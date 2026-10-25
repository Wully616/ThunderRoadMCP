using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public sealed record ApiDecompileBatchItem(
    [property: Description("Exact assemblyId from api_assemblies/api_search.")] string AssemblyId,
    [property: Description("Exact symbolId from api_search/api_type.")] string SymbolId,
    [property: Description("One-based source line.")] int StartLine = 1,
    [property: Description("Source lines for this member, 1 to 500.")] int LineCount = 200);

internal sealed record ResolvedApiScope(string Requested, string Effective,
    IReadOnlySet<InstalledApiCatalog.LoadedAssemblyIdentity>? LoadedAssemblies, string Diagnostic,
    bool InventoryTruncated, string ScopeSnapshotId);

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
    [Description("Deterministic offline search of installed type/member identifiers or matched XML documentation; no model generates API answers. Public results are the default. Identifier ordering remains exact simple, exact qualified, prefix, substring, then qualified-name matches; XML summary/remarks/parameter/return/example matches follow all identifiers and return their xmlDocumentation snippets. Types precede members and game assemblies precede mods within a rank. Disabled (underscore-prefixed) mod folders are never searched. scope selects installed, loaded, or auto assembly scope. Use api_search for exact names or distinctive documentation phrases; use api_lookup task mode for task intent, then use returned IDs with api_type/api_decompile. Carry snapshotId and nextOffset for remaining pages.")]
    public static Task<CallToolResult> ApiSearch(InstalledApiCatalog catalog, GameProcess gameProcess,
        [Description("Case-insensitive type/member/qualified-name identifier or phrase from XML summary, remarks, parameters, returns, or examples. Identifier hits rank before documentation hits.")] string query,
        [Description("Restrict search to an assemblyId returned by api_assemblies.")] string? assemblyId = null,
        [Description("Optional result kind, such as type, method, property, or field.")] string? kind = null,
        [Description("Optional origin filter: game or mod.")] string? origin = null,
        [Description("Include non-public declarations only when implementation details are needed.")] bool includeNonPublic = false,
        [Description("Zero-based result offset.")] int offset = 0,
        [Description("Maximum results in this page.")] int limit = 50,
        [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
        [Description("Assembly scope: auto (default), installed (game plus enabled mods; offline modding), or loaded (game APIs plus installed mods matching the running game's paged assembly inventory).")] string? scope = null,
        [Description("Pagination contract version: 1 is legacy best-effort; 2 requires scopeSnapshotId for offset pages.")] int paginationVersion = 1,
        [Description("Scope identity from the first page; required for offset>0 when paginationVersion=2.")] string? scopeSnapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(async () =>
        {
            var resolvedScope = await ResolveScopeAsync(gameProcess, scope, cancellationToken);
            return await catalog.SearchAsync(query, assemblyId, kind, includeNonPublic, offset, limit, snapshotId,
                paginationVersion, scopeSnapshotId, cancellationToken, resolvedScope, origin);
        });

    /// <summary>
    /// Resolves the assembly scope. Returns the bridge's loaded mod set for loaded scope, or null for installed scope
    /// (game assemblies plus every enabled mod on disk), plus a diagnostic naming the scope applied.
    /// </summary>
    internal static async Task<ResolvedApiScope> ResolveScopeAsync(
        GameProcess gameProcess, string? scope, CancellationToken cancellationToken)
    {
        var requested = (scope ?? "auto").Trim().ToLowerInvariant();
        if (requested is not ("auto" or "installed" or "loaded"))
            throw new ArgumentException("scope must be auto, installed, or loaded.", nameof(scope));
        if (requested == "installed")
            return CreateScope(requested, null, false, "Assembly scope: installed (game assemblies and enabled mods on disk).");
        string? unavailable = null;
        using var inventory = JsonDocument.Parse(await gameProcess.LoadedAssemblyInventoryAsync(cancellationToken));
        var root = inventory.RootElement;
        var bridgeConnected = root.TryGetProperty("bridgeConnected", out var connected) &&
                              connected.ValueKind == JsonValueKind.True;
        var loaded = InstalledApiCatalog.CreateLoadedAssemblyIdentitySet();
        var truncatedInventory = false;
        if (!bridgeConnected)
            unavailable = "the game bridge is not connected";
        else if (!root.TryGetProperty("inventoryAvailable", out var inventoryAvailable) ||
                 inventoryAvailable.ValueKind != JsonValueKind.True)
        {
            string? inventoryErrorText = root.TryGetProperty("inventoryError", out var inventoryError) &&
                                         inventoryError.ValueKind == JsonValueKind.String
                ? inventoryError.GetString()
                : null;
            unavailable = string.IsNullOrWhiteSpace(inventoryErrorText)
                ? "the connected bridge does not report a consistent loaded assembly inventory"
                : inventoryErrorText;
        }
        else if (!root.TryGetProperty("loadedAssemblies", out var assemblies) ||
                 assemblies.ValueKind != JsonValueKind.Array)
            unavailable = "the connected bridge returned an invalid loaded assembly inventory";
        else
        {
            truncatedInventory = !root.TryGetProperty("inventoryComplete", out var complete) ||
                                 complete.ValueKind != JsonValueKind.True;
            foreach (var assembly in assemblies.EnumerateArray())
            {
                if (assembly.ValueKind != JsonValueKind.Object ||
                    !assembly.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(nameElement.GetString()) ||
                    !assembly.TryGetProperty("version", out var versionElement) ||
                    versionElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(versionElement.GetString()) ||
                    !assembly.TryGetProperty("moduleVersionId", out var moduleVersionId) ||
                    moduleVersionId.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(moduleVersionId.GetString(), out var parsedMvid) ||
                    !assembly.TryGetProperty("origin", out var assemblyOrigin) ||
                    !assembly.TryGetProperty("relativePath", out var relativePath))
                {
                    truncatedInventory = true;
                    continue;
                }
                if (assemblyOrigin.ValueKind == JsonValueKind.String &&
                    string.Equals(assemblyOrigin.GetString(), "game", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (assemblyOrigin.ValueKind == JsonValueKind.String &&
                    string.Equals(assemblyOrigin.GetString(), "mod", StringComparison.OrdinalIgnoreCase) &&
                    relativePath.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(relativePath.GetString()))
                {
                    loaded.Add(InstalledApiCatalog.CreateLoadedAssemblyIdentity(relativePath.GetString()!,
                        nameElement.GetString()!, versionElement.GetString()!, parsedMvid));
                    continue;
                }
                if (assemblyOrigin.ValueKind == JsonValueKind.Null && relativePath.ValueKind == JsonValueKind.Null)
                {
                    loaded.Add(InstalledApiCatalog.CreateLoadedAssemblyIdentity(
                        nameElement.GetString()!, versionElement.GetString()!, parsedMvid));
                    continue;
                }
                truncatedInventory = true;
            }
        }
        if (unavailable is null)
        {
            var diagnostic = truncatedInventory
                ? "Assembly scope: loaded (mods reported by the running game); the bridge inventory was incomplete, so some loaded mods may be missing."
                : "Assembly scope: loaded (mods reported by the running game).";
            return CreateScope(requested, loaded, truncatedInventory, diagnostic);
        }
        if (requested == "loaded")
            throw new ApiDiscoveryException("scope.unavailable",
                $"scope=\"loaded\" needs the running game's assembly inventory, but {unavailable}. Start the game and call game_wait_ready, or use scope=\"installed\".",
                new { tool = "game_wait_ready", arguments = new { timeoutMs = 30000 } });
        return CreateScope(requested, null, false,
            $"Assembly scope: installed because {unavailable}; enabled mods on disk are included even if the game would not load them. Use scope=\"loaded\" with a running game before writing evaluate_csharp code.");
    }

    private static ResolvedApiScope CreateScope(string requested,
        IReadOnlySet<InstalledApiCatalog.LoadedAssemblyIdentity>? loaded, bool inventoryTruncated, string diagnostic)
    {
        var effective = loaded is null ? "installed" : "loaded";
        var assemblies = loaded is null
            ? Array.Empty<ScopeAssembly>()
            : loaded.Select(identity => new ScopeAssembly(
                    identity.RelativePath is null ? null :
                        (OperatingSystem.IsWindows() ? identity.RelativePath.ToUpperInvariant() : identity.RelativePath),
                    identity.Name,
                    identity.Version,
                    identity.ModuleVersionId))
                .OrderBy(identity => identity.RelativePath, StringComparer.Ordinal)
                .ThenBy(identity => identity.Name, StringComparer.Ordinal)
                .ThenBy(identity => identity.Version, StringComparer.Ordinal)
                .ThenBy(identity => identity.ModuleVersionId)
                .ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ScopeFingerprint(requested, effective, inventoryTruncated, assemblies));
        var scopeSnapshotId = "scope1." + Convert.ToHexString(SHA256.HashData(bytes));
        return new ResolvedApiScope(requested, effective, loaded, diagnostic, inventoryTruncated, scopeSnapshotId);
    }

    private sealed record ScopeFingerprint(string Requested, string Effective, bool InventoryTruncated,
        ScopeAssembly[] LoadedAssemblies);
    private sealed record ScopeAssembly(string? RelativePath, string? Name, string? Version, Guid ModuleVersionId);

    [McpServerTool(Name = "api_type", Title = "Inspect an installed API type", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Inspect a discovered type's declaration, bases, interfaces, generic constraints, and paged members. Use the type symbolId and assemblyId from api_search; inherited members report their declaring assembly and type. Carry snapshotId and nextOffset to continue. Members omit fields identical to the type's own provenance; inherited members from other assemblies keep assemblyId and declaringType.")]
    public static Task<CallToolResult> ApiType(InstalledApiCatalog catalog,
        [Description("Assembly identifier returned by api_search.")] string assemblyId,
        [Description("Type symbolId returned by api_search.")] string typeSymbolId,
        [Description("Include public inherited members and their declaring types.")] bool includeInherited = false,
        [Description("Include non-public declarations only when needed.")] bool includeNonPublic = false,
        [Description("Optional case-insensitive member-name substring filter.")] string? name = null,
        [Description("Optional member kind: constructor, method, field, property, or event.")] string? kind = null,
        [Description("Zero-based member offset.")] int offset = 0,
        [Description("Maximum members in this page.")] int limit = 100,
        [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.TypeAsync(assemblyId, typeSymbolId, includeInherited, includeNonPublic, name, kind, offset, limit, snapshotId, cancellationToken));
    [McpServerTool(Name = "api_lookup", Title = "Find an installed API for a task", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Task-intent retrieval: ranks installed identifiers, signatures, matched XML documentation, selected-install catalog evidence, and curated concept hints deterministically offline; it is not model-generated. Use api_search for exact names or distinctive XML documentation phrases, and use mode=\"symbol\" or exact IDs for source. Returns bounded metadata/context, optional curated examples, and confidence/completeness; never executes installed code. Continuations are bound to the exact query, options, ranking version, and installed snapshot. Query modes use installed, loaded, or auto assembly scope. Disabled mod folders are never included.")]
    public static Task<CallToolResult> ApiLookup(InstalledApiCatalog catalog, BaseCatalogDataCatalog baseCatalog, GameProcess gameProcess,
    [Description("Natural-language task intent for task mode, up to 2048 characters. For exact names or distinctive XML-documentation phrases use api_search. Omit when passing the exact assemblyId and symbolId pair.")] string? query = null,
    [Description("Optional assemblyId restriction from api_assemblies or a previous lookup.")] string? assemblyId = null,
    [Description("Exact symbolId returned by api_search, api_lookup, or api_type. Requires assemblyId and no query.")] string? symbolId = null,
    [Description("Lookup mode: task (default for query; ranked metadata/context) or symbol (query plus reconstructed source). Exact assemblyId+symbolId uses symbol mode.")] string? mode = null,
    [Description("Optional task/symbol query kind, such as type, method, field, or property.")] string? kind = null,
    [Description("Maximum task-mode result declarations, 1 to 20; default 3.")] int maxResults = 3,
    [Description("Maximum serialized JSON response length in characters, 3000 to 32000; default 8000.")] int maxChars = 8000,
    [Description("Include the highest-ranked relevant curated example with explicit evidence labels; opt in for additional context.")] bool includeExamples = false,
    [Description("Stable task-mode continuation token. Repeat the same query, options, and snapshot.")] string? continuation = null,
    [Description("One-based first source line in symbol mode.")] int startLine = 1,
    [Description("Number of source lines to return in symbol mode.")] int lineCount = 200,
    [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
    [Description("Assembly scope for query modes: auto (default), installed (game plus enabled mods; offline modding), or loaded (game APIs plus installed mods matching the running game's paged assembly inventory).")] string? scope = null,
    CancellationToken cancellationToken = default) =>
    ToolInvocation.Run(async () =>
    {
        var resolvedScope = query is null
            ? null
            : await ResolveScopeAsync(gameProcess, scope, cancellationToken);
        return await catalog.LookupAsync(query, assemblyId, symbolId, kind, mode, startLine, lineCount, snapshotId,
            maxResults, maxChars, includeExamples, continuation, baseCatalog, cancellationToken, resolvedScope);
    });
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
