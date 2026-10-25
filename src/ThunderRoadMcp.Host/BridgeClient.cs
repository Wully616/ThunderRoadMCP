using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

public sealed class BridgeClient
{
    private const int MaxLineBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(35);
    private static string BridgeConfigPath => Environment.GetEnvironmentVariable("THUNDERROAD_MCP_CONFIG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThunderRoadMcp", "bridge.json");
    private static readonly JsonSerializerOptions ConfigJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions BridgeResponseJsonOptions = new() { MaxDepth = 64 };
    private const double MaxBridgeTimingMs = 60_000;
    private static readonly byte[] LineEnding = "\n"u8.ToArray();
    private static readonly HashSet<string> BridgeTimingNames = new(StringComparer.Ordinal)
    {
        "bridgeRequestTotalMs",
        "compilerQueueWaitMs",
        "referenceInitializationMs",
        "compilationMs",
        "parseMs",
        "emitMs",
        "assemblyLoadMs",
        "mainThreadQueueWaitMs",
        "invocationMs",
        "serializationMs",
    };

    public async Task<string> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        try
        {
            using var timing = ToolTiming.Measure("bridgeRoundTripMs");
            return await RequestCoreAsync(method, parameters, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var requestTimeout = method == "status" ? TimeSpan.FromSeconds(3) : RequestTimeout;
            throw new TimeoutException($"Bridge {method} request exceeded {requestTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<string> RequestCoreAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var requestMayHaveBeenSent = false;
        try
        {
            BridgeConfig config;
            using (ToolTiming.Measure("bridgeConfigMs"))
                config = await ReadBridgeConfigAsync(cancellationToken);
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var requestTimeout = method == "status" ? TimeSpan.FromSeconds(3) : RequestTimeout;
            timeout.CancelAfter(requestTimeout);
            var token = timeout.Token;
            using (ToolTiming.Measure("bridgeConnectMs"))
                await client.ConnectAsync(IPAddress.Loopback, config.Port, token);
            await using var stream = client.GetStream();
            var id = Guid.NewGuid().ToString("N");
            ToolTiming.SetBridgeRequestId(id);
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id,
                token = config.Token,
                method,
                parameters,
                diagnostics = ToolTiming.Enabled,
            });
            if (request.Length + LineEnding.Length > MaxLineBytes)
                throw new InvalidOperationException("Bridge request exceeds the 64 KiB line limit.");
            using (ToolTiming.Measure("bridgeWriteMs"))
            {
                await stream.WriteAsync(request, token);
                requestMayHaveBeenSent = true;
                await stream.WriteAsync(LineEnding, token);
            }
            ToolTiming.Count("bridge.requests");
            ToolTiming.Count("bridge.requestBytes", request.Length + LineEnding.Length);

            var responseBytes = new ArrayBufferWriter<byte>(1024);
            var buffer = new byte[4096];
            var hasLineEnd = false;
            using (ToolTiming.Measure("bridgeReadMs"))
            {
                while (responseBytes.WrittenCount < MaxLineBytes)
                {
                    var read = await stream.ReadAsync(buffer, token);
                    if (read == 0)
                        throw new IOException("Bridge closed the connection before responding.");
                    var lineEnd = Array.IndexOf(buffer, (byte)'\n', 0, read);
                    var count = lineEnd < 0 ? read : lineEnd;
                    var lineLength = responseBytes.WrittenCount + count + (lineEnd >= 0 ? 1 : 0);
                    if (lineLength > MaxLineBytes)
                        throw new IOException("Bridge response exceeds the 64 KiB line limit.");
                    buffer.AsSpan(0, count).CopyTo(responseBytes.GetSpan(count));
                    responseBytes.Advance(count);
                    if (lineEnd >= 0)
                    {
                        hasLineEnd = true;
                        break;
                    }
                }
            }
            if (!hasLineEnd)
                throw new IOException("Bridge response exceeds the 64 KiB line limit.");
            ToolTiming.Count("bridge.responseBytes", responseBytes.WrittenCount + LineEnding.Length);
            string result;
            using (ToolTiming.Measure("bridgeResponseParseMs"))
                result = ParseResponse(responseBytes.WrittenMemory, id);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (method == "evaluate")
        {
            var requestTimeout = RequestTimeout;
            throw new BridgeRequestException($"Bridge {method} request exceeded {requestTimeout.TotalSeconds:0} seconds.",
                "evaluation.failed", requestMayHaveBeenSent ? "unknown" : "not_started", null);
        }
        catch (Exception exception) when (method == "evaluate" && exception is not BridgeRequestException)
        {
            throw new BridgeRequestException(exception.Message, "evaluation.failed",
                requestMayHaveBeenSent ? "unknown" : "not_started", null);
        }
    }

    internal static string ParseResponse(ReadOnlyMemory<byte> responseBytes, string id)
    {
        using var document = JsonDocument.Parse(responseBytes, BridgeResponseJsonOptions);
        var root = document.RootElement;
        if (root.GetProperty("id").GetString() != id)
            throw new IOException("Bridge response ID did not match the request.");
        if (ToolTiming.Enabled &&
            root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
            foreach (var timing in timings.EnumerateObject())
                if (BridgeTimingNames.Contains(timing.Name) &&
                    timing.Value.ValueKind == JsonValueKind.Number &&
                    timing.Value.TryGetDouble(out var milliseconds) &&
                    double.IsFinite(milliseconds) && milliseconds is >= 0 and <= MaxBridgeTimingMs)
                    ToolTiming.Add(timing.Name, milliseconds);
        if (!root.GetProperty("success").GetBoolean())
        {
            var errorCode = root.TryGetProperty("errorCode", out var errorCodeElement) &&
                errorCodeElement.ValueKind == JsonValueKind.String ? errorCodeElement.GetString() : null;
            var executionOutcome = root.TryGetProperty("executionOutcome", out var outcomeElement) &&
                outcomeElement.ValueKind == JsonValueKind.String ? outcomeElement.GetString() : null;
            JsonElement? diagnostics = root.TryGetProperty("diagnostics", out var diagnosticElement)
                ? diagnosticElement.Clone()
                : null;
            var messageTruncated = root.TryGetProperty("messageTruncated", out var messageTruncatedElement) &&
                messageTruncatedElement.ValueKind == JsonValueKind.True;
            var diagnosticsTruncated = root.TryGetProperty("diagnosticsTruncated", out var diagnosticsTruncatedElement) &&
                diagnosticsTruncatedElement.ValueKind == JsonValueKind.True;
            int? omittedDiagnosticCount = root.TryGetProperty("omittedDiagnosticCount", out var omittedDiagnosticCountElement) &&
                omittedDiagnosticCountElement.TryGetInt32(out var count) && count >= 0 ? count : null;
            throw new BridgeRequestException(root.GetProperty("error").GetString() ?? "Bridge request failed.",
                errorCode, executionOutcome, diagnostics, messageTruncated, diagnosticsTruncated, omittedDiagnosticCount);
        }
        return root.GetProperty("result").GetRawText();
    }


    private static async Task<BridgeConfig> ReadBridgeConfigAsync(CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(BridgeConfigPath);
        var config = await JsonSerializer.DeserializeAsync<BridgeConfig>(input, ConfigJsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Bridge configuration '{BridgeConfigPath}' is empty.");
        if (!config.Enabled || config.Host != "127.0.0.1" || config.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(config.Token) || config.Token.Length != 64 || !config.Token.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Bridge configuration must be enabled with host 127.0.0.1, a valid port, and a 64-character hex token.");
        return config;
    }


    private sealed record BridgeConfig(bool Enabled, string Host, int Port, string Token);
}
