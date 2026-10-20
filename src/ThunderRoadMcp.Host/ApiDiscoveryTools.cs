using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed record ApiDecompileBatchItem(
    [property: Description("Exact assemblyId from api_assemblies/api_search.")] string AssemblyId,
    [property: Description("Exact symbolId from api_search/api_type.")] string SymbolId,
    [property: Description("One-based source line.")] int StartLine = 1,
    [property: Description("Source lines for this member, 1 to 500.")] int LineCount = 200);


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
    [Description("Search installed type and member names; public results are the default. By default, mod results are limited to assemblies reported loaded by the game bridge and exclude underscore-prefixed mod directories. Set includeUnloadedMods=true to search installed mod DLLs regardless of load state or when offline. Use api_lookup to combine an unambiguous search hit with paged source; otherwise use the returned assemblyId and symbolId with api_type or api_decompile. Carry snapshotId and nextOffset for remaining pages. This reads metadata and does not execute game code.")]
    public static Task<CallToolResult> ApiSearch(InstalledApiCatalog catalog, GameProcess gameProcess,
        [Description("Case-insensitive type, member, or qualified-name search text.")] string query,
        [Description("Restrict search to an assemblyId returned by api_assemblies.")] string? assemblyId = null,
        [Description("Optional result kind, such as type, method, property, or field.")] string? kind = null,
        [Description("Include non-public declarations only when implementation details are needed.")] bool includeNonPublic = false,
        [Description("Include installed mod DLLs regardless of load state, including underscore-prefixed folders.")] bool includeUnloadedMods = false,
        [Description("Zero-based result offset.")] int offset = 0,
        [Description("Maximum results in this page.")] int limit = 50,
        [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(async () =>
        {
            IReadOnlySet<InstalledApiCatalog.LoadedAssemblyIdentity>? loadedModAssemblies = null;
            string? loadedAssemblyDiagnostic = null;
            if (!includeUnloadedMods)
            {
                using var status = JsonDocument.Parse(await gameProcess.StatusAsync(true, cancellationToken));
                var root = status.RootElement;
                var bridgeConnected = root.TryGetProperty("bridgeConnected", out var connected) &&
                                      connected.ValueKind == JsonValueKind.True;
                var loaded = InstalledApiCatalog.CreateLoadedAssemblyIdentitySet();
                if (!bridgeConnected)
                    loadedAssemblyDiagnostic = "Game bridge is not connected; mod assemblies are excluded. Set includeUnloadedMods=true to search installed mod DLLs.";
                else if (!root.TryGetProperty("status", out var bridgeStatus) ||
                         !bridgeStatus.TryGetProperty("loadedAssemblies", out var assemblies) ||
                         assemblies.ValueKind != JsonValueKind.Array)
                    loadedAssemblyDiagnostic = "Connected bridge does not report loaded assemblies; mod assemblies are excluded. Set includeUnloadedMods=true to search installed mod DLLs.";
                else
                {
                    foreach (var assembly in assemblies.EnumerateArray())
                    {
                        if (!assembly.TryGetProperty("origin", out var origin) ||
                            origin.ValueKind != JsonValueKind.String ||
                            !string.Equals(origin.GetString(), "mod", StringComparison.OrdinalIgnoreCase) ||
                            !assembly.TryGetProperty("relativePath", out var relativePath) ||
                            relativePath.ValueKind != JsonValueKind.String ||
                            !assembly.TryGetProperty("moduleVersionId", out var moduleVersionId) ||
                            moduleVersionId.ValueKind != JsonValueKind.String ||
                            !Guid.TryParse(moduleVersionId.GetString(), out var parsedMvid))
                            continue;
                        loaded.Add(InstalledApiCatalog.CreateLoadedAssemblyIdentity(relativePath.GetString()!, parsedMvid));
                    }
                    if (bridgeStatus.TryGetProperty("loadedAssembliesTruncated", out var truncated) &&
                        truncated.ValueKind == JsonValueKind.True)
                        loadedAssemblyDiagnostic = "Bridge loaded-assembly inventory was truncated; some loaded mod assemblies may be omitted.";
                }
                loadedModAssemblies = loaded;
            }
            return await catalog.SearchAsync(query, assemblyId, kind, includeNonPublic, offset, limit, snapshotId,
                cancellationToken, loadedModAssemblies, loadedAssemblyDiagnostic);
        });

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
[McpServerTool(Name = "api_lookup", Title = "Find an installed API for a task", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
[Description("Recommended first discovery tool. A natural-language query defaults to ranked task mode: it returns metadata-only declarations, bounded signatures/type context, related catalog records, and evidence-labelled examples without eager decompilation. Use mode=\"symbol\" for the legacy exact-symbol/source lookup, or pass exact assemblyId and symbolId to receive its source directly. Task mode never executes installed code; its executionContract explains the separate evaluator constraints. Continuations are bound to the exact query, options, ranking version, and full installed snapshot.")]
public static Task<CallToolResult> ApiLookup(InstalledApiCatalog catalog, BaseCatalogDataCatalog baseCatalog,
    [Description("Natural-language task or exact type/member text. Task mode accepts up to 2048 characters. Omit when passing the exact assemblyId and symbolId pair.")] string? query = null,
    [Description("Optional assemblyId restriction from api_assemblies or a previous lookup.")] string? assemblyId = null,
    [Description("Exact symbolId returned by api_search, api_lookup, or api_type. Requires assemblyId and no query.")] string? symbolId = null,
    [Description("Lookup mode: task (default for query; ranked metadata/context) or symbol (query plus reconstructed source). Exact assemblyId+symbolId uses symbol mode.")] string? mode = null,
    [Description("Optional task/symbol query kind, such as type, method, field, or property.")] string? kind = null,
    [Description("Maximum task-mode result declarations, 1 to 20.")] int maxResults = 5,
    [Description("Maximum serialized JSON response length in characters, 3000 to 32000.")] int maxChars = 16000,
    [Description("Include the highest-ranked relevant curated example with explicit evidence labels.")] bool includeExamples = true,
    [Description("Stable task-mode continuation token. Repeat the same query, options, and snapshot.")] string? continuation = null,
    [Description("One-based first source line in symbol mode.")] int startLine = 1,
    [Description("Number of source lines to return in symbol mode.")] int lineCount = 200,
    [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
    CancellationToken cancellationToken = default) =>
    ToolInvocation.Run(() => catalog.LookupAsync(query, assemblyId, symbolId, kind, mode, startLine, lineCount, snapshotId,
        maxResults, maxChars, includeExamples, continuation, baseCatalog, cancellationToken));


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
    [McpServerTool(Name = "api_decompile_batch", Title = "Read multiple reconstructed API sources", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Reconstruct source for 1 to 20 exact assemblyId/symbolId pairs in one installed snapshot. Each item may page source independently. The total serialized response is bounded by maxChars; partial results return nextItems to continue. Installed code is never executed.")]
    public static Task<CallToolResult> ApiDecompileBatch(InstalledApiCatalog catalog,
        [Description("Nonempty bounded list of exact assemblyId/symbolId pairs with optional source page controls.")] ApiDecompileBatchItem[] items,
        [Description("Snapshot identifier shared by all members.")] string? snapshotId = null,
        [Description("Hard maximum serialized response size in characters, 2000 to 64000.")] int maxChars = 32000,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.DecompileBatchAsync(items, snapshotId, maxChars, cancellationToken));
}
