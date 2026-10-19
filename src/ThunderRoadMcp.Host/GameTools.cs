using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class GameTools
{
    [McpServerTool(Name = "game_status", Title = "Check game and bridge status", ReadOnly = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Report game process, bridge connectivity, and in-game readiness. Status is compact by default; set includeAssemblies=true to include loaded assembly inventory and MVIDs.")]
    public static Task<CallToolResult> GameStatus(GameProcess gameProcess,
        [Description("Include the loaded assembly inventory and MVIDs in the nested status payload.")] bool includeAssemblies = false,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => gameProcess.StatusAsync(includeAssemblies, cancellationToken));

    [McpServerTool(Name = "game_installations", Title = "Find game installations", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("List detected Steam and Meta Horizon Link installations without launching or changing the game. If multiple candidates exist, select one by setting executablePath in launch.json.")]
    public static Task<CallToolResult> GameInstallations(CancellationToken cancellationToken) =>
        ToolInvocation.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GameInstallConfiguration.ListInstallationsJson());
        });

    [McpServerTool(Name = "game_launch", Title = "Launch Blade & Sorcery", OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Start the selected game installation and return its PID without waiting for bridge readiness. On cold start, explicitly pass arguments [\"-level\", \"Home\"] unless the user requested another level; pass that target directly as the startup -level argument. The bridge initializes only after main-menu/Home startup, so use game_wait_ready or game_status before bridge-backed calls. Optional raw arguments override the launch.json array and are preserved by game_restart.")]
    public static Task<CallToolResult> GameLaunch(GameProcess gameProcess,
        [Description("Optional raw process arguments, at most 64 strings of 4096 characters each; omit to use launch.json.")] string[]? arguments = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => gameProcess.LaunchAsync(arguments, cancellationToken));

    [McpServerTool(Name = "game_wait_ready", Title = "Wait for game and bridge readiness", ReadOnly = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Poll until the bridge is connected and ready, the scene is loaded, and optionally the requested level is current. Returns ready, timedOut, elapsedMs, and the last status; a timeout is a normal result, not a tool error.")]
    public static Task<CallToolResult> GameWaitReady(GameProcess gameProcess,
        [Description("Optional level id to wait for; compared case-insensitively.")] string? level = null,
        [Description("Maximum wait in milliseconds, from 1 to 300000.")] int timeoutMs = 60000,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => gameProcess.WaitForReadyAsync(level, timeoutMs, cancellationToken));

    [McpServerTool(Name = "game_load_level", Title = "Load a Blade & Sorcery level", Destructive = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Requires a ready bridge. Do not use for initial startup; use game_launch's -level argument. By default returns the pre-request state immediately. waitUntilReady=true applies a bounded total deadline from before the load request through readiness; an unacknowledged timeout omits pre-request state.")]
    public static Task<CallToolResult> GameLoadLevel(GameProcess gameProcess, BridgeClient bridgeClient,
        [Description("Catalog LevelData id, for example Arena or Home.")] string levelId,
        [Description("Game mode id, for example Sandbox.")] string mode,
        [Description("Wait for the requested level to become ready after the load request.")] bool waitUntilReady = false,
        [Description("Total time from before the load request through readiness, from 1 to 300000 milliseconds.")] int timeoutMs = 60000,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(async () =>
        {
            var id = RequireText(levelId, nameof(levelId), 256);
            var gameMode = RequireText(mode, nameof(mode), 256);
            if (!waitUntilReady)
                return await bridgeClient.RequestAsync("load_level", new { levelId = id, mode = gameMode }, cancellationToken);
            if (timeoutMs is < 1 or > 300_000)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be between 1 and 300000.");

            var elapsed = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            string loadResult;
            try
            {
                loadResult = await bridgeClient.RequestAsync("load_level", new { levelId = id, mode = gameMode }, deadline.Token);
            }
            catch (Exception exception) when (
                (exception is OperationCanceledException or TimeoutException) &&
                !cancellationToken.IsCancellationRequested)
            {
                return JsonSerializer.Serialize(new
                {
                    levelId = id,
                    mode = gameMode,
                    requestAcknowledged = false,
                    completed = false,
                    timedOut = true,
                    elapsedMs = elapsed.ElapsedMilliseconds,
                });
            }

            bool requireTransition;
            using (var loadDocument = JsonDocument.Parse(loadResult))
            {
                var loadRoot = loadDocument.RootElement;
                requireTransition = loadRoot.TryGetProperty("currentLevelBefore", out var before) &&
                    before.ValueKind == JsonValueKind.String &&
                    string.Equals(before.GetString(), id, StringComparison.OrdinalIgnoreCase) &&
                    loadRoot.TryGetProperty("sceneReadyBefore", out var sceneReadyBefore) &&
                    sceneReadyBefore.ValueKind == JsonValueKind.True;
            }

            var readiness = await gameProcess.WaitForReadyAsync(
                id, timeoutMs, elapsed, cancellationToken, deadline.Token, requireTransition);
            using var readinessDocument = JsonDocument.Parse(readiness);
            var readinessRoot = readinessDocument.RootElement;
            var result = JsonNode.Parse(loadResult)!.AsObject();
            result["requestAcknowledged"] = true;
            result["completed"] = readinessRoot.GetProperty("ready").GetBoolean();
            result["timedOut"] = readinessRoot.GetProperty("timedOut").GetBoolean();
            result["elapsedMs"] = readinessRoot.GetProperty("elapsedMs").GetInt64();
            result["status"] = readinessRoot.GetProperty("status").ValueKind == JsonValueKind.Null
                ? null
                : JsonNode.Parse(readinessRoot.GetProperty("status").GetRawText());
            return result.ToJsonString();
        });

    [McpServerTool(Name = "game_spawn_item", Title = "Spawn a catalog item", OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Resolve an ItemData catalog id, spawn it in front of the player or at explicit world coordinates, and store the Item under a runtime key retrievable with McpRuntime.Current.Get<Item>(key). Returns key, itemId, actual position, instanceId, and name.")]
    public static Task<CallToolResult> GameSpawnItem(BridgeClient bridgeClient,
        [Description("Catalog ItemData id.")] string itemId,
        [Description("World-space X; supply together with y and z, or omit all three.")] double? x = null,
        [Description("World-space Y; supply together with x and z, or omit all three.")] double? y = null,
        [Description("World-space Z; supply together with x and y, or omit all three.")] double? z = null,
        [Description("Runtime key for later retrieval; generated when omitted.")] string? key = null,
        [Description("Exempt the spawned item from the game's despawn cleanup.")] bool disallowDespawn = true,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() =>
        {
            var id = RequireText(itemId, nameof(itemId), 256);
            if ((x is null) != (y is null) || (y is null) != (z is null))
                throw new ArgumentException("x, y, and z must be supplied together.");
            foreach (var (name, coordinate) in new[] { (nameof(x), x), (nameof(y), y), (nameof(z), z) })
                if (coordinate is { } value && (!double.IsFinite(value) || Math.Abs(value) > 1_000_000))
                    throw new ArgumentException($"{name} must be finite and within ±1000000.");
            var runtimeKey = key is null ? null : RequireText(key, nameof(key), 128);
            return bridgeClient.RequestAsync("spawn_item", new { itemId = id, x, y, z, key = runtimeKey, disallowDespawn }, cancellationToken);
        });

    [McpServerTool(Name = "game_runtime", Title = "Manage bridge-owned runtime state", Destructive = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("List bridge-owned coroutines and safe value summaries, inspect one stored runtime value without traversing it, stop one managed coroutine, or clear all bridge-owned runtime state. inspect and stop require key; list reports full counts and truncation flags when the 64 KiB response budget omits entries. clear stops all managed coroutines and clears stored objects. Game-owned coroutines are never touched.")]
    public static Task<CallToolResult> GameRuntime(BridgeClient bridgeClient,
        [Description("Action: list, inspect, stop, or clear.")] string action,
        [Description("Runtime value key for inspect or managed coroutine key for stop.")] string? key = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() =>
        {
            var normalized = action?.Trim().ToLowerInvariant() ?? "";
            return normalized switch
            {
                "list" or "clear" => bridgeClient.RequestAsync("runtime", new { action = normalized }, cancellationToken),
                "inspect" or "stop" => bridgeClient.RequestAsync("runtime", new { action = normalized, key = RequireText(key ?? "", nameof(key), 128) }, cancellationToken),
                _ => throw new ArgumentException("action must be list, inspect, stop, or clear."),
            };
        });

    private static string RequireText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException($"{name} must be a non-empty string of at most {maxLength} characters.");
        return value;
    }

    [McpServerTool(Name = "game_install_mod", Title = "Install the bridge mod", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Install a ThunderRoadMcp package into the selected game's Mods directory. Refuses to overwrite an existing installation; the game must be restarted manually.")]
    public static Task<CallToolResult> GameInstallMod(
        [Description("Absolute path to the ThunderRoadMcp ZIP package.")] string packagePath,
        CancellationToken cancellationToken) =>
        ToolInvocation.Run(async () =>
        {
            var options = await GameInstallConfiguration.ReadLaunchOptionsAsync(cancellationToken);
            return await BridgeModInstaller.InstallAsync(packagePath, options, cancellationToken);
        });

    [McpServerTool(Name = "game_stop", Title = "Stop the host-launched game", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Gracefully stop only the game process launched by this host; set force=true to terminate it if graceful shutdown fails.")]
    public static Task<CallToolResult> GameStop(GameProcess gameProcess,
        [Description("Force termination instead of graceful shutdown.")] bool force = false,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.RunText(() => gameProcess.StopAsync(force, cancellationToken));

    [McpServerTool(Name = "game_restart", Title = "Restart the host-launched game", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Restart only the game process launched by this host; set force=true to terminate the old process if graceful shutdown fails.")]
    public static Task<CallToolResult> GameRestart(GameProcess gameProcess,
        [Description("Force termination of the old process instead of graceful shutdown.")] bool force = false,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => gameProcess.RestartAsync(force, cancellationToken));

    [McpServerTool(Name = "evaluate_csharp", Title = "Evaluate trusted C# in the game", Destructive = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Compile and run trusted arbitrary C# in the game, without a sandbox and with full game-process and filesystem access. Runs synchronously on Unity's main thread; timeout cannot stop code already running.")]
    public static Task<CallToolResult> EvaluateCSharp(BridgeClient bridgeClient,
        [Description("Synchronous C# statement body that returns a JSON-serializable value.")] string code,
        [Description("Evaluation deadline in milliseconds, from 1 to 30000; a running snippet cannot be interrupted.")] int timeoutMs = 30000,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => bridgeClient.RequestAsync("evaluate", new { code, timeoutMs }, cancellationToken));

    [McpServerTool(Name = "game_logs", Title = "Read Unity game logs", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Read bounded pages from the current Player.log, previous Player-prev.log, or the authenticated in-game bridge log ring. File pagination uses beforeByteOffset plus snapshotId; bridge pagination uses since, where 0 returns the newest entries and nextSequence advances forward.")]
    public static Task<CallToolResult> GameLogs(BridgeClient bridgeClient,
        [Description("Log source: current, previous, or bridge.")] string source = "current",
        [Description("Number of lines to return for file sources, from 1 to 2000.")] int tailLines = 200,
        [Description("For bridge source, return entries after this sequence number; 0 returns the newest entries.")] long since = 0,
        [Description("For file sources, continue before this byte offset.")] long? beforeByteOffset = null,
        [Description("Snapshot identifier returned by the previous file page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => source switch
        {
            "current" => Task.FromResult(PlayerLogReader.Read("current", "Player.log", tailLines, beforeByteOffset, snapshotId)),
            "previous" => Task.FromResult(PlayerLogReader.Read("previous", "Player-prev.log", tailLines, beforeByteOffset, snapshotId)),
            "bridge" when beforeByteOffset is null && snapshotId is null => bridgeClient.RequestAsync("logs", new { since }, cancellationToken),
            "bridge" => throw new ArgumentException("File pagination parameters apply only to file sources."),
            _ => throw new ArgumentException("source must be current, previous, or bridge.")
        });
}
