using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class ApiNavigationTools
{
    [McpServerTool(Name = "api_find_callers", Title = "Find static IL callers", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Find metadata-resolved static call, callvirt, and newobj references to an exact method symbol. Scope is required: assembly searches one assembly; snapshot searches installed assemblies. Results are static IL evidence only and make no claim about runtime dispatch or callbacks.")]
    public static Task<CallToolResult> ApiFindCallers(InstalledApiCatalog catalog, string assemblyId, string symbolId,
        [Description("Required search scope: assembly or snapshot.")] string scope,
        [Description("Zero-based result offset.")] int offset = 0,
        [Description("Maximum results, 1 to 100.")] int limit = 50,
        [Description("Snapshot ID from the previous page.")] string? snapshotId = null,
        [Description("Include the full searched-assembly list and every diagnostic; default returns counts and the first 5 diagnostics.")] bool includeDiagnostics = false,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.FindCallersAsync(assemblyId, symbolId, scope, offset, limit, snapshotId, includeDiagnostics, cancellationToken));

    [McpServerTool(Name = "api_find_callees", Title = "Find static IL callees", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Decode the direct IL operands of an exact method symbol. Targets without an exact installed metadata definition remain unresolved references, not source IDs. Abstract, external, and runtime-provided methods have no IL body. No runtime dispatch or callback guarantee is implied.")]
    public static Task<CallToolResult> ApiFindCallees(InstalledApiCatalog catalog, string assemblyId, string symbolId,
        [Description("Zero-based result offset.")] int offset = 0,
        [Description("Maximum results, 1 to 100.")] int limit = 50,
        [Description("Snapshot ID from the previous page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.FindCalleesAsync(assemblyId, symbolId, offset, limit, snapshotId, cancellationToken));

    [McpServerTool(Name = "api_find_usages", Title = "Find static IL usages", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Find metadata-resolved field/type/method token references and property/event accessor calls. For overrides, also include callvirt references to overridden base methods as potential dispatches, marked matchKind=potentialVirtualDispatch; exact token references are marked direct. These remain static evidence, not proof of runtime dispatch.")]
    public static Task<CallToolResult> ApiFindUsages(InstalledApiCatalog catalog, string assemblyId, string symbolId,
        [Description("Required search scope: assembly or snapshot.")] string scope,
        [Description("Zero-based result offset.")] int offset = 0,
        [Description("Maximum results, 1 to 100.")] int limit = 50,
        [Description("Snapshot ID from the previous page.")] string? snapshotId = null,
        [Description("Include the full searched-assembly list and every diagnostic; default returns counts and the first 5 diagnostics.")] bool includeDiagnostics = false,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.FindUsagesAsync(assemblyId, symbolId, scope, offset, limit, snapshotId, includeDiagnostics, cancellationToken));
}
