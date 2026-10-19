using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

public sealed class GameProcess
{
    private const int MaxLaunchArgumentCount = 64;
    private const int MaxLaunchArgumentLength = 4096;
    private readonly BridgeClient _bridgeClient;
    private Process? _ownedProcess;
    private string[]? _launchedArguments;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    public GameProcess(BridgeClient bridgeClient) => _bridgeClient = bridgeClient;

    public async Task<string> StatusAsync(bool includeAssemblies, CancellationToken cancellationToken)
    {
        bool processRunning;
        int? ownedProcessId;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_ownedProcess is { } process && !process.HasExited)
            {
                processRunning = true;
                ownedProcessId = process.Id;
            }
            else
            {
                processRunning = false;
                ownedProcessId = null;
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        try
        {
            var response = await _bridgeClient.RequestAsync("status", new { includeAssemblies }, cancellationToken);
            using var document = JsonDocument.Parse(response);
            var status = document.RootElement;
            var bridgeProcessId = status.GetProperty("processId").GetInt32();
            if (ownedProcessId is not null && bridgeProcessId != ownedProcessId)
                throw new InvalidOperationException($"Authenticated bridge process {bridgeProcessId} does not match host-launched process {ownedProcessId}.");
            bool? bridgeReady = status.TryGetProperty("bridgeReady", out var ready) && ready.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ready.GetBoolean()
                : null;
            var currentLevel = status.TryGetProperty("currentLevel", out var level) && level.ValueKind == JsonValueKind.String ? level.GetString() : null;
            var levelState = status.TryGetProperty("levelState", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString() : null;
            bool? sceneReady = status.TryGetProperty("sceneReady", out var scene) && scene.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? scene.GetBoolean()
                : null;
            object statusPayload = status.Clone();
            if (!includeAssemblies && status.TryGetProperty("loadedAssemblies", out _))
            {
                var compactStatus = new Dictionary<string, JsonElement>();
                foreach (var property in status.EnumerateObject())
                    if (property.Name != "loadedAssemblies")
                        compactStatus.Add(property.Name, property.Value);
                statusPayload = compactStatus;
            }
            return JsonSerializer.Serialize(new { gameProcessRunning = true, bridgeConnected = true, bridgeReady, currentLevel, levelState, sceneReady, status = statusPayload });
        }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or InvalidOperationException)
        {
            return JsonSerializer.Serialize(new { gameProcessRunning = processRunning ? true : (bool?)null, bridgeConnected = false, bridgeReady = false, currentLevel = (string?)null, levelState = (string?)null, sceneReady = (bool?)null, bridgeError = exception.Message });
        }
    }

    public Task<string> WaitForReadyAsync(string? level, int timeoutMs, CancellationToken cancellationToken) =>
        WaitForReadyAsync(level, timeoutMs, Stopwatch.StartNew(), cancellationToken, CancellationToken.None);

    internal async Task<string> WaitForReadyAsync(string? level, int timeoutMs, Stopwatch stopwatch,
        CancellationToken cancellationToken, CancellationToken deadlineToken = default, bool requireTransition = false)
    {
        if (timeoutMs is < 1 or > 300_000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be between 1 and 300000.");
        if (level is not null && (string.IsNullOrWhiteSpace(level) || level.Length > 256))
            throw new ArgumentException("level must be a non-empty string of at most 256 characters.", nameof(level));
        ArgumentNullException.ThrowIfNull(stopwatch);

        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        JsonElement lastStatus = default;
        var hasStatus = false;
        var transitionObserved = !requireTransition;
        var ready = false;
        while (true)
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineToken);
            attempt.CancelAfter(remaining);
            string statusJson;
            try
            {
                statusJson = await StatusAsync(false, attempt.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            using var document = JsonDocument.Parse(statusJson);
            lastStatus = document.RootElement.Clone();
            hasStatus = true;
            var statusReady = IsReady(lastStatus, level);
            if (!statusReady)
                transitionObserved = true;
            ready = statusReady && transitionObserved;
            if (ready)
                break;
            remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            var delay = remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500);
            try
            {
                await Task.Delay(delay, attempt.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        return JsonSerializer.Serialize(new
        {
            ready,
            timedOut = !ready,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            level,
            status = hasStatus ? (object)lastStatus : null,
        });
    }


    private static bool IsReady(JsonElement status, string? level)
    {
        if (!IsTrue(status, "bridgeConnected") || !IsTrue(status, "bridgeReady") || !IsTrue(status, "sceneReady"))
            return false;
        if (level is null)
            return true;
        return status.TryGetProperty("currentLevel", out var currentLevel) &&
               currentLevel.ValueKind == JsonValueKind.String &&
               string.Equals(currentLevel.GetString(), level, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrue(JsonElement status, string name) =>
        status.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public async Task<string> LaunchAsync(string[]? arguments, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            return await LaunchUnderLockAsync(arguments, cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<string> LaunchUnderLockAsync(string[]? argumentOverride, CancellationToken cancellationToken)
    {
        var validatedArguments = argumentOverride is null ? null : ValidateLaunchArguments(argumentOverride);
        if (_ownedProcess is { } owned)
        {
            if (!owned.HasExited)
                return LaunchPending(owned, launched: false, _launchedArguments ?? []);
            owned.Dispose();
            _ownedProcess = null;
        }
        var options = await GameInstallConfiguration.ReadLaunchOptionsAsync(cancellationToken);
        var arguments = validatedArguments ?? options.Arguments;
        var processName = Path.GetFileNameWithoutExtension(options.ExecutablePath);
        var existingProcesses = Process.GetProcessesByName(processName);
        try
        {
            if (existingProcesses.Length != 0)
                throw new InvalidOperationException("Blade & Sorcery is already running outside this host; refusing to launch a duplicate.");
        }
        finally
        {
            foreach (var existing in existingProcesses)
                existing.Dispose();
        }

        var start = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath)!,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        var configOverride = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_CONFIG");
        if (configOverride is not null)
            start.Environment["THUNDERROAD_MCP_CONFIG"] = configOverride;
        _ownedProcess = Process.Start(start) ?? throw new InvalidOperationException("Game process could not be started.");
        _ownedProcess.BeginOutputReadLine();
        _ownedProcess.BeginErrorReadLine();
        _ownedProcess.StandardInput.Close();
        if (_ownedProcess.HasExited)
            throw new InvalidOperationException($"Game launch process exited with code {_ownedProcess.ExitCode}.");
        _launchedArguments = arguments.ToArray();
        return LaunchPending(_ownedProcess, launched: true, _launchedArguments);
    }

    private static string[] ValidateLaunchArguments(string[] arguments)
    {
        if (arguments.Length > MaxLaunchArgumentCount)
            throw new ArgumentException($"At most {MaxLaunchArgumentCount} launch arguments are supported.");
        var copy = new string[arguments.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index] ?? throw new ArgumentException("Launch arguments must be strings.");
            if (argument.Length > MaxLaunchArgumentLength)
                throw new ArgumentException($"Each launch argument must be at most {MaxLaunchArgumentLength} characters.");
            copy[index] = argument;
        }
        return copy;
    }

    private static string LaunchPending(Process process, bool launched, string[] arguments) => JsonSerializer.Serialize(new
    {
        gameProcessLaunched = launched,
        gameProcessRunning = !process.HasExited,
        processId = process.Id,
        arguments,
        bridgeConnected = (bool?)null,
        bridgeReady = (bool?)null,
        bridgeStatus = "not_checked",
        message = "Bridge readiness was not checked by game_launch; use game_wait_ready or game_status.",
    });


    public async Task<string> StopAsync(bool force, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            var outcome = await StopUnderLockAsync(force, cancellationToken);
            return outcome switch
            {
                StopOutcome.Stopped => force ? "Force-stopped the host-owned game process." : "Stopped the host-owned game process gracefully.",
                StopOutcome.NoOwnedProcess => throw new InvalidOperationException("No host-owned game process is running."),
                StopOutcome.StillRunning => throw new InvalidOperationException("Game did not exit gracefully; call game_stop with force=true to terminate the host-owned process."),
                _ => throw new InvalidOperationException("Unknown stop result."),
            };
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<string> RestartAsync(bool force, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_ownedProcess is null)
                throw new InvalidOperationException("No host-owned game process is running.");
            await GameInstallConfiguration.ReadLaunchOptionsAsync(cancellationToken);
            var outcome = await StopUnderLockAsync(force, cancellationToken);
            if (outcome == StopOutcome.NoOwnedProcess)
                throw new InvalidOperationException("No host-owned game process is running.");
            if (outcome == StopOutcome.StillRunning)
                throw new InvalidOperationException("Game did not exit gracefully; call game_restart with force=true to terminate the host-owned process.");
            return await LaunchUnderLockAsync(_launchedArguments, cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<StopOutcome> StopUnderLockAsync(bool force, CancellationToken cancellationToken)
    {
        if (_ownedProcess is not { } process)
            return StopOutcome.NoOwnedProcess;
        if (process.HasExited)
        {
            process.Dispose();
            _ownedProcess = null;
            return StopOutcome.Stopped;
        }
        if (force)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }
        else
        {
            process.CloseMainWindow();
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (TimeoutException)
            {
                return StopOutcome.StillRunning;
            }
        }
        process.Dispose();
        _ownedProcess = null;
        return StopOutcome.Stopped;
    }

    private enum StopOutcome
    {
        NoOwnedProcess,
        Stopped,
        StillRunning,
    }

}
