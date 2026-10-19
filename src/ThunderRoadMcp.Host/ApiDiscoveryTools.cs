using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class ApiDiscoveryTools
{
    [McpServerTool(Name = "api_assemblies", Title = "List installed game assemblies", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Discover managed DLLs under the selected game's Managed and Mods roots. Use assemblyId with api_search, and carry snapshotId with nextOffset to continue a page. To compare an installed moduleVersionId with the running build, call game_status(includeAssemblies=true) and compare with status.loadedAssemblies.")]
    public static Task<CallToolResult> ApiAssemblies(InstalledApiCatalog catalog,
        [Description("Optional case-insensitive assembly name filter.")] string? name = null,
        [Description("Optional origin filter: game or mod.")] string? origin = null,
        [Description("Zero-based result offset.")] int offset = 0,
        [Description("Maximum assemblies in this page, from 1 to 100.")] int limit = 50,
        [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.AssembliesAsync(name, origin, offset, limit, snapshotId, cancellationToken));

    [McpServerTool(Name = "api_search", Title = "Search installed game APIs", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Search installed type and member names; public results are the default. Use api_lookup to combine an unambiguous search hit with paged source; otherwise use the returned assemblyId and symbolId with api_type or api_decompile. Carry snapshotId and nextOffset for remaining pages. This reads metadata and does not execute game code.")]
    public static Task<CallToolResult> ApiSearch(InstalledApiCatalog catalog,
        [Description("Case-insensitive type, member, or qualified-name search text.")] string query,
        [Description("Restrict search to an assemblyId returned by api_assemblies.")] string? assemblyId = null,
        [Description("Optional result kind, such as type, method, property, or field.")] string? kind = null,
        [Description("Include non-public declarations only when implementation details are needed.")] bool includeNonPublic = false,
        [Description("Zero-based result offset.")] int offset = 0,
        [Description("Maximum results in this page.")] int limit = 50,
        [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.SearchAsync(query, assemblyId, kind, includeNonPublic, offset, limit, snapshotId, cancellationToken));

    [McpServerTool(Name = "api_type", Title = "Inspect an installed API type", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Inspect a discovered type's declaration, bases, interfaces, generic constraints, and paged members. Use the type symbolId and assemblyId from api_search; inherited members report their declaring assembly and type. Carry snapshotId and nextOffset to continue.")]
    public static Task<CallToolResult> ApiType(InstalledApiCatalog catalog,
        [Description("Assembly identifier returned by api_search.")] string assemblyId,
        [Description("Type symbolId returned by api_search.")] string typeSymbolId,
        [Description("Include public inherited members and their declaring types.")] bool includeInherited = false,
        [Description("Include non-public declarations only when needed.")] bool includeNonPublic = false,
        [Description("Zero-based member offset.")] int offset = 0,
        [Description("Maximum members in this page.")] int limit = 100,
        [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.TypeAsync(assemblyId, typeSymbolId, includeInherited, includeNonPublic, offset, limit, snapshotId, cancellationToken));
    [McpServerTool(Name = "api_lookup", Title = "Find and read an installed API", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Search for one installed type or member and return its exact signature and paged reconstructed source. Fully qualified exact-name matches take precedence over longer substring matches; same-name overloads remain ambiguous. Select a candidate using its assemblyId and symbolId. For more than 20 candidates, continue with the returned nextSearch api_search handoff. For source continuation, pass the exact pair with snapshotId and next startLine. Alternatively provide exact assemblyId and symbolId directly. This reads metadata and does not execute game code.")]
    public static Task<CallToolResult> ApiLookup(InstalledApiCatalog catalog,
        [Description("Search text. Use this mode instead of symbolId; public declarations are the default.")] string? query = null,
        [Description("Optional assemblyId restriction from api_assemblies or a previous lookup.")] string? assemblyId = null,
        [Description("Exact symbolId returned by api_search, api_lookup, or api_type. Requires assemblyId and no query.")] string? symbolId = null,
        [Description("Optional search kind, such as type, method, field, or property.")] string? kind = null,
        [Description("One-based first source line.")] int startLine = 1,
        [Description("Number of source lines to return.")] int lineCount = 200,
        [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.LookupAsync(query, assemblyId, symbolId, kind, startLine, lineCount, snapshotId, cancellationToken));


    [McpServerTool(Name = "api_decompile", Title = "Read reconstructed API source", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Reconstruct C# for a catalogued type or exact member symbolId, including a selected overload. Output is bounded and paged reconstructed source, not original source, and installed code is never executed. Carry snapshotId and nextLine to continue.")]
    public static Task<CallToolResult> ApiDecompile(InstalledApiCatalog catalog,
        [Description("Assembly identifier returned by api_search, or by inherited-member metadata.")] string assemblyId,
        [Description("Type or exact member symbolId returned by api_search or api_type.")] string symbolId,
        [Description("One-based first source line.")] int startLine = 1,
        [Description("Number of source lines to return.")] int lineCount = 200,
        [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.DecompileAsync(assemblyId, symbolId, startLine, lineCount, snapshotId, cancellationToken));
}
