using System.Text.Json.Nodes;

internal sealed class ApiDiscoveryException(string code, string message, object? recovery = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Recovery { get; } = recovery;

    public JsonObject ToJson() => new()
    {
        ["code"] = Code,
        ["message"] = Message,
        ["recovery"] = Recovery is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(Recovery),
    };
}
