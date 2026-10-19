using System.Text.Json;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

internal static class ToolInvocation
{
    public static Task<CallToolResult> Run(Func<Task<string>> action) => Run(action, structured: true);

    public static Task<CallToolResult> RunText(Func<Task<string>> action) => Run(action, structured: false);

    private static async Task<CallToolResult> Run(Func<Task<string>> action, bool structured)
    {
        using var invocation = ToolTiming.BeginInvocation();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var text = await action();
            var value = structured ? JsonSerializer.Deserialize<JsonElement>(text) : default;
            var content = structured
                ? value.ValueKind == JsonValueKind.Object ? value : JsonSerializer.SerializeToElement(new { value })
                : JsonSerializer.SerializeToElement(new { value = text });
            ToolTiming.Add("hostTotalMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var timings = ToolTiming.Snapshot();
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = text },
                    new TextContentBlock { Text = "Server timings (ms): " + JsonSerializer.Serialize(timings) },
                ],
                StructuredContent = content,
                Meta = new System.Text.Json.Nodes.JsonObject
                {
                    ["timings"] = JsonSerializer.SerializeToNode(timings),
                },
            };
        }
        catch (OperationCanceledException)
        {
            ToolTiming.Add("hostTotalMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (McpException)
        {
            ToolTiming.Add("hostTotalMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            ToolTiming.Add("hostTotalMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw new McpException(exception.Message, exception);
        }
    }
}
