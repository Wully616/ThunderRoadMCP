using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class BaseCatalogDataTools
{
    private const string ModGuidance = "The shipped bas.jsondb is immutable reference data; never edit, replace, or delete it. Create new CatalogData JSON under your mod's own folder; same-ID mod data overlays shipped data. Every mod requires manifest.json.";

    [McpServerTool(Name = "catalog_search", Title = "Search shipped game catalog data", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Browse the shipped bas.jsondb by query, record type, or ID. Results are deterministic and paged; carry snapshotId and nextOffset to continue and use entryId with catalog_read. Records with a usable $type name include an apiLookup query for api_lookup(kind=type). The archive is read-only. " + ModGuidance)]
    public static Task<CallToolResult> CatalogSearch(BaseCatalogDataCatalog catalog,
        [Description("Optional case-insensitive search. Matches the phrase or all of its words across record strings, IDs, names, paths, and the record's English display text; results are ranked by ID/name matches first. TextData tables are excluded unless catalogType=TextData or id is given.")] string? query = null,
        [Description("Optional catalog record type filter, such as LevelData or ItemData.")] string? catalogType = null,
        [Description("Optional case-insensitive record ID filter.")] string? id = null,
        [Description("Zero-based result offset.")] int offset = 0,
        [Description("Maximum records in this page.")] int limit = 25,
        [Description("Snapshot identifier from the previous page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.SearchAsync(query, catalogType, id, offset, limit, snapshotId, cancellationToken));

    [McpServerTool(Name = "catalog_read", Title = "Read a shipped catalog record", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(JsonObject))]
    [Description("Read exact JSON text for a catalog_search result in bounded line pages. Pass the returned entryId and snapshotId, then use nextLine to continue. Results include the record's $type-derived apiLookup query when available. The archive is read-only. " + ModGuidance)]
    public static Task<CallToolResult> CatalogRead(BaseCatalogDataCatalog catalog,
        [Description("Entry identifier returned by catalog_search.")] string entryId,
        [Description("Snapshot identifier returned by catalog_search.")] string snapshotId,
        [Description("One-based first JSON line.")] int startLine = 1,
        [Description("Number of JSON lines to return.")] int lineCount = 200,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => catalog.ReadAsync(entryId, snapshotId, startLine, lineCount, cancellationToken));
}
