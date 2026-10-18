using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

internal static class ToolInvocation
{
    public static Task<CallToolResult> Run(Func<Task<string>> action) => Run(action, structured: true);

    public static Task<CallToolResult> RunText(Func<Task<string>> action) => Run(action, structured: false);

    private static async Task<CallToolResult> Run(Func<Task<string>> action, bool structured)
    {
        try
        {
            var text = await action();
            var value = structured ? JsonSerializer.Deserialize<JsonElement>(text) : default;
            var content = structured
                ? value.ValueKind == JsonValueKind.Object ? value : JsonSerializer.SerializeToElement(new { value })
                : JsonSerializer.SerializeToElement(new { value = text });
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text }],
                StructuredContent = content,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }
}
