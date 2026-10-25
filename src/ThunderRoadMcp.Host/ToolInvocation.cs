using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

internal static class ToolInvocation
{
    public static Task<CallToolResult> Run(Func<Task<string>> action) => Run(action, structured: true);

    public static Task<CallToolResult> RunText(Func<Task<string>> action) => Run(action, structured: false);

    public static Task<CallToolResult> RunEvaluation(Func<Task<string>> action) =>
        Run(action, structured: true, evaluation: true);

    private static async Task<CallToolResult> Run(Func<Task<string>> action, bool structured, bool evaluation = false)
    {
        using var invocation = ToolTiming.BeginInvocation();
        var started = ToolTiming.Enabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            var text = await action();
            if (ToolTiming.Enabled)
                ToolTiming.Add("hostHandlerMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            CallToolResult result;
            using (ToolTiming.Measure("responseConstructionMs"))
            {
                var value = structured ? JsonSerializer.Deserialize<JsonElement>(text) : default;
                var content = structured
                    ? value.ValueKind == JsonValueKind.Object ? value : JsonSerializer.SerializeToElement(new { value })
                    : JsonSerializer.SerializeToElement(new { value = text });
                result = new CallToolResult
                {
                    Content = [new TextContentBlock { Text = text }],
                    StructuredContent = content,
                };
                if (ToolTiming.Enabled)
                {
                    ToolTiming.Count("host.textBytes", Encoding.UTF8.GetByteCount(text));
                    ToolTiming.Count("host.responseContentBlocks");
                }
            }

            if (ToolTiming.Enabled)
            {
                ToolTiming.Add("hostTotalMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                ToolTiming.WriteDiagnostics();
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            RecordFailure(started);
            throw;
        }
        catch (ArgumentException exception)
        {
            if (evaluation)
                return EvaluationFailure(exception.Message, "request.invalid", "not_started", null, started);
            if (ToolTiming.Enabled)
            {
                ToolTiming.Add("hostTotalMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                ToolTiming.WriteDiagnostics();
            }
            return InvalidRequest(exception.Message);
        }
        catch (ApiDiscoveryException exception)
        {
            if (ToolTiming.Enabled)
            {
                ToolTiming.Add("hostTotalMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                ToolTiming.WriteDiagnostics();
            }
            var error = new JsonObject { ["error"] = exception.ToJson() };
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = error.ToJsonString() }],
                StructuredContent = JsonSerializer.SerializeToElement(error),
            };
        }
        catch (McpException exception)
        {
            if (evaluation)
                return EvaluationFailure(exception.Message, "evaluation.failed", "unknown", null, started);
            RecordFailure(started);
            throw;
        }
        catch (Exception exception)
        {
            if (evaluation)
            {
                var bridgeFailure = exception as BridgeRequestException;
                return EvaluationFailure(exception.Message, bridgeFailure?.ErrorCode ?? "evaluation.failed",
                    bridgeFailure?.ExecutionOutcome ?? "unknown", bridgeFailure?.Diagnostics, started,
                    bridgeFailure?.MessageTruncated ?? false, bridgeFailure?.DiagnosticsTruncated ?? false,
                    bridgeFailure?.OmittedDiagnosticCount);
            }
            RecordFailure(started);
            throw new McpException(exception.Message, exception);
        }
    }

    private static CallToolResult EvaluationFailure(string message, string errorCode, string executionOutcome,
        JsonElement? diagnostics, long started, bool messageTruncated = false, bool diagnosticsTruncated = false,
        int? omittedDiagnosticCount = null)
    {
        RecordFailure(started);
        var truncatedMessage = message.Length > 4096;
        var boundedMessage = truncatedMessage ? message[..4096] : message;
        var structured = new JsonObject
        {
            ["errorCode"] = errorCode,
            ["executionOutcome"] = executionOutcome,
            ["error"] = new JsonObject
            {
                ["code"] = errorCode,
                ["message"] = boundedMessage,
                ["messageTruncated"] = messageTruncated || truncatedMessage,
                ["recovery"] = null,
            },
            ["diagnosticsTruncated"] = diagnosticsTruncated,
        };
        if (omittedDiagnosticCount is { } count)
            structured["omittedDiagnosticCount"] = count;
        if (diagnostics is { } value)
            structured["diagnostics"] = JsonNode.Parse(value.GetRawText());
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = boundedMessage }],
            StructuredContent = JsonSerializer.SerializeToElement(structured),
        };
    }

    internal static CallToolResult InvalidRequest(string message)
    {
        var error = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = "request.invalid",
                ["message"] = message.Length <= 512 ? message : message[..512],
                ["messageTruncated"] = message.Length > 512,
                ["recovery"] = null,
            },
        };
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = error.ToJsonString() }],
            StructuredContent = JsonSerializer.SerializeToElement(error),
        };
    }

    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> RejectUnknownArguments(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var name = context.Params?.Name;
            var tool = context.MatchedPrimitive as McpServerTool;
            if (tool is null && name is not null)
                tool = context.Server.ServerOptions.ToolCollection?.FirstOrDefault(candidate => candidate.ProtocolTool.Name == name);
            if (tool is null)
                return await next(context, cancellationToken);

            var valid = new SortedSet<string>(StringComparer.Ordinal);
            if (tool.ProtocolTool.InputSchema.ValueKind == JsonValueKind.Object
                && tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                    valid.Add(property.Name);
            }

            var unknown = context.Params?.Arguments?.Keys.Where(key => !valid.Contains(key)).Order(StringComparer.Ordinal).ToArray() ?? [];
            if (unknown.Length == 0)
                return await next(context, cancellationToken);
            return InvalidRequest($"Unknown argument(s) for {name}: {string.Join(", ", unknown)}. Valid arguments: {(valid.Count == 0 ? "none" : string.Join(", ", valid))}.");
        };

    private static void RecordFailure(long started)
    {
        if (!ToolTiming.Enabled)
            return;
        ToolTiming.Add("hostTotalMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        ToolTiming.WriteDiagnostics();
    }
}
