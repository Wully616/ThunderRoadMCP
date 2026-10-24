using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

internal static class EvaluationContractCheck
{
    public static async Task RunAsync()
    {
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
               compilerDiagnostics.GetProperty("span").GetProperty("length").GetInt32() == 13,
            "host error shaping forwards structured compiler diagnostics and preserves the compilation error code");

        var success = await ToolInvocation.RunEvaluation(() => Task.FromResult("{\"value\":42}"));
        Assert(success.IsError != true && Structured(success).GetProperty("value").GetInt32() == 42 &&
               success.Content.OfType<TextContentBlock>().Single().Text == "{\"value\":42}",
            "successful evaluations preserve their structured and text result shapes");
    }

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
