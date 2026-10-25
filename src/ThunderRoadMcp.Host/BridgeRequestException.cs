using System.Text.Json;

internal sealed class BridgeRequestException(string message, string? errorCode, string? executionOutcome,
    JsonElement? diagnostics, bool messageTruncated = false, bool diagnosticsTruncated = false,
    int? omittedDiagnosticCount = null) : InvalidOperationException(message)
{
    public string? ErrorCode { get; } = errorCode;
    public string? ExecutionOutcome { get; } = executionOutcome;
    public JsonElement? Diagnostics { get; } = diagnostics;
    public bool MessageTruncated { get; } = messageTruncated;
    public bool DiagnosticsTruncated { get; } = diagnosticsTruncated;
    public int? OmittedDiagnosticCount { get; } = omittedDiagnosticCount;
}
