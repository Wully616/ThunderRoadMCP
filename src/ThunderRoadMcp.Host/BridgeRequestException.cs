using System.Text.Json;

internal sealed class BridgeRequestException(string message, string? errorCode, string? executionOutcome,
    JsonElement? diagnostics) : InvalidOperationException(message)
{
    public string? ErrorCode { get; } = errorCode;
    public string? ExecutionOutcome { get; } = executionOutcome;
    public JsonElement? Diagnostics { get; } = diagnostics;
}
