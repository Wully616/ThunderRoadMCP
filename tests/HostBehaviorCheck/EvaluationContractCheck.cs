using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal static class EvaluationContractCheck
{
    public static async Task RunAsync()
    {
        await VerifyTransportOutcomesAsync();

        foreach (var depth in new[] { 20, 60, 61, 62 })
        {
            var result = BridgeClient.ParseResponse(Encoding.UTF8.GetBytes(SuccessResponse(depth)), "depth-check");
            using var parsedResult = JsonDocument.Parse(result);
            Assert(parsedResult.RootElement.ValueKind == JsonValueKind.Object,
                $"host bridge response parser accepts a nested-{depth} result");
            var toolResult = await ToolInvocation.RunEvaluation(() => Task.FromResult(result));
            var nestedValue = Structured(toolResult);
            for (var level = 0; level < depth; level++)
                nestedValue = nestedValue.GetProperty("child");
            Assert(toolResult.IsError != true && nestedValue.GetInt32() == 0 &&
                   toolResult.Content.OfType<TextContentBlock>().Single().Text == result,
                $"host evaluation path preserves a nested-{depth} result");
        }

        var unguardedDepth63 = BridgeClient.ParseResponse(Encoding.UTF8.GetBytes(SuccessResponse(63)), "depth-check");
        using (var parsedDepth63 = JsonDocument.Parse(unguardedDepth63))
            Assert(parsedDepth63.RootElement.ValueKind == JsonValueKind.Object,
                "host parser accepts an unguarded nested-63 response for boundary regression evidence");

        foreach (var depth in new[] { 64, 70 })
        {
            try
            {
                BridgeClient.ParseResponse(Encoding.UTF8.GetBytes(SuccessResponse(depth)), "depth-check");
                throw new InvalidOperationException($"Evaluation contract check expected a nested-{depth} parse failure.");
            }
            catch (JsonException)
            {
                Assert(true, $"host parser rejects an unguarded nested-{depth} response");
            }
        }

        var depthFailure = CaptureBridgeFailure("evaluation.result_depth_exceeded", "completed_result_unavailable");
        Assert(depthFailure.ErrorCode == "evaluation.result_depth_exceeded" &&
               depthFailure.ExecutionOutcome == "completed_result_unavailable",
            "bridge depth rejection is parseable as a completed evaluation with unavailable result");
        var depthFailureResult = await ToolInvocation.RunEvaluation(() => Task.FromException<string>(depthFailure));
        AssertOutcome(depthFailureResult, "completed_result_unavailable", "evaluation.result_depth_exceeded");
        Assert(depthFailureResult.Content.OfType<TextContentBlock>().Single().Text == depthFailure.Message,
            "host preserves the bridge's readable depth-failure message");
        var oldBridgeFailure = CaptureBridgeFailure(null, null);
        Assert(oldBridgeFailure.ErrorCode is null && oldBridgeFailure.ExecutionOutcome is null,
            "old bridge errors preserve absent execution metadata for host-side unknown classification");
        var oldBridgeResult = await ToolInvocation.RunEvaluation(() => Task.FromException<string>(oldBridgeFailure));
        AssertOutcome(oldBridgeResult, "unknown", "evaluation.failed");

        var invalid = await ToolInvocation.RunEvaluation(() =>
            Task.FromException<string>(new ArgumentException("invalid request")));
        AssertOutcome(invalid, "not_started", "request.invalid");
        var diagnostics = JsonDocument.Parse("""[{"id":"CS0103","severity":"error","message":"missing name","line":2,"column":8,"span":{"start":15,"length":13}}]""");
        var compileFailure = await ToolInvocation.RunEvaluation(() => Task.FromException<string>(
            new BridgeRequestException("C# compilation failed", "evaluation.compilation_failed", "not_started",
                diagnostics.RootElement.Clone())));
        var compilerDiagnostics = Structured(compileFailure).GetProperty("diagnostics")[0];
        Assert(compilerDiagnostics.GetProperty("id").GetString() == "CS0103" &&
               Structured(compileFailure).GetProperty("errorCode").GetString() == "evaluation.compilation_failed" &&
               compilerDiagnostics.GetProperty("line").GetInt32() == 2 &&
               compilerDiagnostics.GetProperty("span").GetProperty("length").GetInt32() == 13 &&
               !Structured(compileFailure).GetProperty("error").GetProperty("messageTruncated").GetBoolean() &&
               !Structured(compileFailure).GetProperty("diagnosticsTruncated").GetBoolean(),
            "host error shaping forwards structured compiler diagnostics and preserves untruncated compilation metadata");

        var warningFloodDiagnostics = new List<object>
        {
            new
            {
                id = "CS0103", severity = "error", message = "missing final symbol", messageTruncated = false,
                line = (int?)30, column = (int?)8, span = (object)new { start = 300, length = 7 }
            }
        };
        warningFloodDiagnostics.AddRange(Enumerable.Range(0, 19).Select(index => (object)new
        {
            id = "CS0219",
            severity = "warning",
            message = index == 0 ? new string('w', 256) : "unused local " + index,
            messageTruncated = index == 0,
            line = (int?)(index + 1),
            column = (int?)5,
            span = (object)new { start = index, length = 1 }
        }));
        string warningFloodResponse = JsonSerializer.Serialize(new
        {
            id = "depth-check",
            success = false,
            error = new string('x', 4096),
            messageTruncated = true,
            errorCode = "evaluation.compilation_failed",
            executionOutcome = "not_started",
            diagnostics = warningFloodDiagnostics,
            diagnosticsTruncated = true,
            omittedDiagnosticCount = 6
        });
        var warningFloodFailure = CaptureBridgeFailureResponse(warningFloodResponse);
        var warningFloodResult = await ToolInvocation.RunEvaluation(() => Task.FromException<string>(warningFloodFailure));
        var warningFloodEnvelope = Structured(warningFloodResult);
        var prioritizedDiagnostics = warningFloodEnvelope.GetProperty("diagnostics");
        Assert(warningFloodFailure.MessageTruncated && warningFloodEnvelope.GetProperty("error").GetProperty("messageTruncated").GetBoolean() &&
               warningFloodResult.Content.OfType<TextContentBlock>().Single().Text.Length == 4096 &&
               prioritizedDiagnostics.GetArrayLength() == 20 &&
               prioritizedDiagnostics[0].GetProperty("id").GetString() == "CS0103" &&
               prioritizedDiagnostics[1].GetProperty("messageTruncated").GetBoolean() &&
               warningFloodEnvelope.GetProperty("diagnosticsTruncated").GetBoolean() &&
               warningFloodEnvelope.GetProperty("omittedDiagnosticCount").GetInt32() == 6,
            "host preserves an upstream-clipped message, the late error ahead of warning flood, per-diagnostic truncation, and omitted count");
        var success = await ToolInvocation.RunEvaluation(() => Task.FromResult("{\"value\":42}"));
        Assert(success.IsError != true && Structured(success).GetProperty("value").GetInt32() == 42 &&
               success.Content.OfType<TextContentBlock>().Single().Text == "{\"value\":42}",
            "successful evaluations preserve their structured and text result shapes");
    }
    private static async Task VerifyTransportOutcomesAsync()
    {
        var previousConfigPath = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_CONFIG");
        var configPath = Path.Combine(Path.GetTempPath(), "thunderroad-mcp-evaluation-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var refusedPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            await File.WriteAllTextAsync(configPath, BridgeConfig(refusedPort));
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_CONFIG", configPath);

            var refused = await ToolInvocation.RunEvaluation(() =>
                new BridgeClient().RequestAsync("evaluate", new { code = "return 1;" }, CancellationToken.None));
            AssertOutcome(refused, "not_started", "evaluation.failed");

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await File.WriteAllTextAsync(configPath, BridgeConfig(port));
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                var stream = accepted.GetStream();
                var buffer = new byte[1024];
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    if (Array.IndexOf(buffer, (byte)'\n', 0, read) >= 0)
                        break;
                }
            });
            var disconnected = await ToolInvocation.RunEvaluation(() =>
                new BridgeClient().RequestAsync("evaluate", new { code = "return 1;" }, CancellationToken.None));
            await server;
            AssertOutcome(disconnected, "unknown", "evaluation.failed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_CONFIG", previousConfigPath);
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    private static string BridgeConfig(int port) =>
        $$"""{"enabled":true,"host":"127.0.0.1","port":{{port}},"token":"{{new string('0', 64)}}" }""";

    private static string SuccessResponse(int resultDepth)
    {
        var nested = "0";
        for (var depth = 0; depth < resultDepth; depth++)
            nested = "{\"child\":" + nested + "}";
        return "{\"id\":\"depth-check\",\"success\":true,\"result\":" + nested + "}";
    }

    private static BridgeRequestException CaptureBridgeFailure(string? errorCode, string? executionOutcome)
    {
        var metadata = string.Empty;
        if (errorCode is not null)
            metadata += ",\"errorCode\":\"" + errorCode + "\"";
        if (executionOutcome is not null)
            metadata += ",\"executionOutcome\":\"" + executionOutcome + "\"";
        try
        {
            BridgeClient.ParseResponse(Encoding.UTF8.GetBytes(
                "{\"id\":\"depth-check\",\"success\":false,\"error\":\"result unavailable\"" + metadata + "}"), "depth-check");
        }
        catch (BridgeRequestException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Evaluation contract check expected a bridge failure response.");
    }
    private static BridgeRequestException CaptureBridgeFailureResponse(string response)
    {
        try
        {
            BridgeClient.ParseResponse(Encoding.UTF8.GetBytes(response), "depth-check");
        }
        catch (BridgeRequestException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Evaluation contract check expected a bridge failure response.");
    }


    private static void AssertOutcome(CallToolResult result, string outcome, string errorCode)
    {
        var structured = Structured(result);
        Assert(result.IsError == true && structured.GetProperty("executionOutcome").GetString() == outcome &&
               structured.GetProperty("errorCode").GetString() == errorCode &&
               result.Content.OfType<TextContentBlock>().Single().Text.Length > 0,
            $"evaluation failure reports {outcome} and preserves readable text");
    }

    private static JsonElement Structured(CallToolResult result)
    {
        var value = typeof(CallToolResult).GetProperty("StructuredContent")?.GetValue(result);
        return value is JsonElement element
            ? element
            : throw new InvalidOperationException("Evaluation contract check expected structured MCP content.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Evaluation contract check failed: " + message);
    }
}
