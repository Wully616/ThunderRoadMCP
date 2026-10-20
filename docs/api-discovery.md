# API discovery and evaluation

The host can inspect installed game and mod assemblies without a separate game-source checkout. Use the offline discovery tools to find declarations, then use the bridge's `evaluate_csharp` only when a live game probe is needed.

`game_status` reports bridge `currentLevel`, `levelState`, and `sceneReady` at top level when available, while retaining the bridge payload as `status`. Status is compact by default; use `game_status(includeAssemblies=true)` when loaded assembly inventory is needed. Readiness fields remain null when disconnected, missing, invalid, or using an older bridge.

The MCP initialize response identifies the `ThunderRoadMcp` server and provides concise discovery-first, readiness, pagination, and evaluation-safety guidance. Tool results retain their text content and expose JSON object `structuredContent`; scalar and plain-text results use a `value` property.

## Offline installed-assembly discovery

The host resolves the game executable from an existing `launch.json` (or `THUNDERROAD_MCP_LAUNCH_CONFIG` override). When the default config is absent, it discovers Steam and Meta Horizon Link installs; one candidate is selected automatically, while none or multiple candidates produce an actionable error. `game_installations` lists detected paths without launching or changing the game. The host CLI's `--list-installations` mode returns the same JSON envelope without starting MCP. Set `executablePath` in `launch.json` to select an install explicitly.

The host derives these roots from the selected executable:

- Game assemblies (`origin: "game"`): `<exe directory>/<exe stem>_Data/Managed`
- Mod assemblies (`origin: "mod"`): `<exe directory>/<exe stem>_Data/StreamingAssets/Mods`
- Base catalog archive: `<exe directory>/<exe stem>_Data/StreamingAssets/Default/bas.jsondb`

No arbitrary assembly or archive paths are accepted from callers. Results include the assembly origin and root-relative path.

API discovery derives symbol data from installed assembly metadata and reconstructs code from IL. It intentionally ignores adjacent XML documentation sidecars because they are not version-verified and may be stale or misleading.

## API index freshness and cache

The versioned `api-index-v5` cache is stored under `%LocalAppData%/ThunderRoadMcp/cache` (or `THUNDERROAD_MCP_CACHE_DIR`); its process budget is 384 MiB of conservative estimated metadata/index payload (not a measured working-set limit; the final installed cache occupied about 305 MB on disk, including the symbol directory), and its disk budget is 384 MiB. Writes use unique temporary files and atomic replacement under a cross-process cache lock; corrupt, stale, or oversized cache entries are ignored and rebuilt. Each cache index stores symbol records once and type-to-symbol IDs, not duplicated member records. ILSpy's synthetic parameterless struct/enum constructors (dummy `FakeMethod` entities that expose no metadata token and are neither callable nor decompilable) are skipped as non-addressable and counted separately; other declarations that expose no stable metadata token are counted and mark the index incomplete when public. An assembly that exceeds the 50,000-indexed-declaration safety cap is likewise stored and reported as an explicit partial index instead of failing every query; narrow `api_search` runs a bounded query-filtered scan for unindexed declarations, `api_type` resolves an unindexed type on demand, and diagnostics tell the caller that the common index was capped.
A snapshot-keyed public symbol directory and a normalized type-name-to-assembly map are published with the indexes. Warm `api_lookup` ranks from the single directory and loads only the assemblies needed for selected results and related types; it does not parse every per-assembly index merely to resolve a type name.
At most two API scan snapshots are retained concurrently (64 MiB each); a detached shared index/source build keeps its snapshot lease until completion, so canceled callers cannot exceed the 128 MiB transient scan-buffer bound.

The persistent common index keeps all type declarations and public members; non-public members remain inspectable through a query-filtered fallback when `includeNonPublic` is requested, and `api_type` enumerates non-public or inherited members on demand. Task relevant-member records include a simple `name`; related type records preserve raw metadata generic arity (for example ``Namespace.Type`1``) while comparisons use normalized names. This keeps large private implementation surfaces from consuming the shared index budget.

Dependency-resolved relationships, inherited generic substitutions, and reconstructed source are bound to the full installed-assembly snapshot, so a changed or newly restored dependency invalidates those results even when the target assembly is unchanged. Source reuse is exact-symbol and snapshot keyed, memory-only, and bounded to 32 MiB of conservative estimated source-document payload; source line indexes are sparse to keep large files within the bound. The byte-scan budget is transient and distinct from process metadata/source caches; the memory gauges report conservative estimates, not process allocations or working set. Metadata inspection reads installed files but never loads or executes target code or attribute constructors. An opt-in `THUNDERROAD_MCP_DIAGNOSTICS=1` setting writes timing and deterministic cache/work counters to stderr; normal MCP responses do not include detailed timing blocks or source text in diagnostics.

This is installed-file provenance, not a claim about what is loaded in the running process; the game may still have an older mod build loaded after its DLL is replaced. `api_assemblies` returns each assembly's metadata `moduleVersionId` (MVID), which can be compared with the MVIDs in `game_status(includeAssemblies=true).status.loadedAssemblies` to identify a matching loaded build. The live list includes only file-backed assemblies under the game's Managed directory or Mods root; dynamic assemblies and files outside those roots are excluded. It is deterministic and capped at 128 entries; `loadedAssemblyCount` reports the full eligible count and `loadedAssembliesTruncated` indicates whether entries were omitted. Dependencies resolve only from the configured roots, without network retrieval; unavailable references are reported as warnings.

- `api_assemblies(name?, origin?, offset=0, limit=50, snapshotId?)`: enumerate installed managed assemblies, whether or not they are loaded, and return `assemblyId`, name, version, `moduleVersionId`, origin, relative path, totals, `hasMore`/`nextOffset`, `snapshotId`, and bounded invalid/native-file diagnostics.
- `api_search(query, assemblyId?, kind?, origin?, includeNonPublic=false, offset=0, limit=50, snapshotId?)`: search types and members by name or qualified name, case-insensitively. Public results are the default. Game assemblies are always searchable; mod results are limited to assemblies reported loaded by the connected bridge, and unloaded mods and underscore-prefixed top-level mod directories are never searched. If the bridge is unavailable or its inventory is missing, all mod results are omitted and a diagnostic explains why. Use `kind="type"` for types; assembly IDs disambiguate same-name assemblies and symbol IDs identify exact overloads.
- `api_type(assemblyId, typeSymbolId, includeInherited=false, includeNonPublic=false, offset=0, limit=100, snapshotId?)`: show a type declaration, bases/interfaces, constraints, relevant attribute metadata, and paged members. Inherited results identify their actual declaring assembly and type.
`api_decompile(assemblyId, symbolId, startLine=1, lineCount=200, snapshotId?)` returns bounded, paged reconstructed C# with identity/provenance and warnings. It is reconstructed from IL, not original source; unavailable dependencies or a missing managed body can limit output. Use `hasMore`/`nextLine` for source continuation. `api_decompile_batch(items, snapshotId?, maxChars=32000)` reads 1–20 exact `{assemblyId, symbolId, startLine?, lineCount?}` requests against one validated snapshot and returns ordered per-entry success/errors. Invalid source pages fail only their entry with a line-1 recovery request. The serialized JSON envelope, including escaping and metadata, is capped by `maxChars` (2,000–64,000); `partial`, `truncated`, and `nextItems` preserve source/request continuations rather than dropping them.

`api_find_callers(assemblyId, symbolId, scope, offset=0, limit=50, snapshotId?)` and `api_find_usages(...)` return metadata-resolved static references from either one assembly or the installed snapshot. `api_find_callees(assemblyId, symbolId, offset=0, limit=50, snapshotId?)` decodes a method's direct IL operands. These report static IL evidence, not runtime dispatch. Results include caller/callee identities, provenance, opcode/token/IL offset, paging, and completeness diagnostics. The shared reference index stores at most 100,000 edges; when a query needs references beyond that index, a target-filtered scan is bounded to 2,000,000 methods, 5,000,000 decoded references, or 50,000,000 instructions. Hitting a bound or encountering unresolved references is reported with `complete: false`. Callee exact references that do not resolve to installed definitions remain references without source symbol IDs.
`api_find_callers`/`api_find_usages` return `searchedAssemblyCount`, `diagnosticCount`, and the first 5 diagnostics by default; pass `includeDiagnostics=true` for the full searched-assembly list and every diagnostic. `api_search` ranks exact simple name, exact qualified name, prefix, substring, then qualified-name matches (types before members, game before mods) and accepts `origin="game"|"mod"`. `api_type` accepts `name` (member substring) and `kind` filters and omits member fields identical to the type's own provenance. Tools reject unknown argument names.

- `api_lookup(query?, assemblyId?, symbolId?, mode?, kind?, maxResults=5, maxChars=16000, includeExamples=true, continuation?, startLine=1, lineCount=200, snapshotId?)`: natural-language `query` defaults to metadata-only task mode, ranking public declarations by deterministic tokenization, common inflections, and auditable lexical synonyms. It returns bounded signatures, parameter/return types, relevant attribute metadata, related types, selected inherited-member context, optional query-ranked examples, base-catalog records, and explicit confidence/completeness/uncertainty. It returns at most one highest-ranked curated example. It does not eagerly decompile source. `maxChars` bounds serialized logical JSON including the `logicalChars` field, names, and escaping (3000–32000). Continuations bind the query, options, ranking version, and snapshot. Use `mode="symbol"` for legacy exact/lexical lookup with source, or pass the exact `assemblyId`+`symbolId` pair (which selects symbol mode) to retrieve a paged source result; exact qualified names retain deterministic overload handling. More than 16 distinct non-stop catalog terms returns partial catalog context marked incomplete with omitted terms disclosed.
  `api_lookup` query modes (task and symbol) apply the same rule as `api_search`: only mod assemblies the bridge reports as loaded are considered, including for related-type context. Exact `assemblyId`+`symbolId` lookups, `api_type`, and `api_decompile` act on the IDs you supply (from `api_assemblies`, which lists every installed assembly).

`THUNDERROAD_MCP_DIAGNOSTICS=1` emits opt-in stderr timings for inventory enumeration, file validation, index build/load, API search, task ranking, and type expansion, plus deterministic bytes-hashed, index/source-cache, query-work, and estimated-cache-size counters. Detailed diagnostics are not added to normal MCP responses.

Use `includeNonPublic` only to inspect implementation details; private or protected members are not necessarily callable from ordinary mod code.

IDs are opaque and tied to assembly origin, path, and content version. Use returned IDs rather than constructing them. A changed/replaced assembly receives a fresh identity, and an old or invalid ID is an error. Results and source are ordered and bounded; inspect continuation fields before assuming a result is complete.

Pass a type hit's `symbolId` as `typeSymbolId` to `api_type`. For source, use `api_lookup(mode="symbol", query=...)` or the exact `assemblyId`/`symbolId` pair; inherited members use their reported declaring assembly ID. Carry the returned `snapshotId` into later pages. If installed files change, no stale page is served: restart the search without `snapshotId`, then use the current IDs from its result.

Discovery uses ICSharpCode.Decompiler 11.1.0.9782 under the [ILSpy MIT license](https://github.com/icsharpcode/ILSpy/blob/master/LICENSE); its required MIT notice is included with the host output.

## Read-only base catalog data

`catalog_search(query?, catalogType?, id?, offset=0, limit=25, snapshotId?)` searches JSON records in the selected installation's `StreamingAssets/Default/bas.jsondb`. It matches type and ID filters case-insensitively and searches record string values, IDs, names, and archive paths. Results include an opaque `entryId`, record type/ID, archive-relative path, size, and snapshot/page fields. If `$type` has a safely usable API type name, `apiLookup` contains it as an `api_lookup` `query` with `kind="type"`; use it directly, then select returned IDs if the type search is ambiguous. Nested non-generic CLR names normalize `+` to `.`, matching API qualified names; generic/array CLR forms that cannot be represented safely are omitted rather than emitted as malformed queries. This link is derived from `$type`, never from the record ID.
Query matching: the phrase, or all of its words, may match across record strings, IDs, names, paths, and the record's English display text (`{Token}` strings resolve through the English `TextData`). Results are ranked ID/name first. `TextData` records are excluded unless `catalogType="TextData"` or `id` is given, and an unknown `catalogType` is rejected with the known type names.


`catalog_read(entryId, snapshotId, startLine=1, lineCount=200)` returns exact JSON lines from a search result in bounded pages and includes the same `apiLookup` type handoff when available. For example, search `catalogType="LevelData", query="Survival"` to find levels whose data contains Survival mode details, or filter `catalogType="ItemData"` to inspect item records. Use `entryId` and `snapshotId` as returned; do not construct them. Snapshot IDs derive from archive bytes, so unchanged archive content retains the same snapshot. If a stale-snapshot error occurs, rerun `catalog_search`, then use its current `entryId` and `snapshotId` together. `catalogReadMs` in `_meta.timings` measures catalog record reads when available.

The shipped `bas.jsondb` is immutable reference data: the host opens it read-only, and agents MUST NOT edit, modify, replace, or delete it. Mods add new CatalogData JSON files in their own mod folder; a record with the same ID merges over the shipped record at load time. Every mod must include its own `manifest.json`.

## Use discovered declarations in a live probe

For example, after discovery confirms `Creature.holders` and `Holder.name`, a mod helper can read holder names from a supplied creature:

```csharp
using System;
using System.Linq;
using ThunderRoad;

public static class HolderNameHelper
{
    public static string[] GetHolderNames(Creature creature)
    {
        if (creature == null || creature.holders == null)
            return Array.Empty<string>();
        return creature.holders.Select(holder => holder.name).ToArray();
    }
}
```

To check live state, use the same declaration information to write a small typed statement body for `evaluate_csharp`. This avoids guessing member names or using reflection for routine API discovery.

For example, after confirming `Player.currentCreature`, `Creature.holders`, and `Holder.name` in installed declarations, this read-only statement returns the holder names and distinguishes a missing creature from zero holders:

```csharp
var creature = Player.currentCreature;
if (creature == null)
    return new { playerFound = false, holderNames = Array.Empty<string>() };
return new
{
    playerFound = true,
    holderNames = creature.holders == null
        ? Array.Empty<string>()
        : creature.holders.Select(holder => holder.name).ToArray()
};
```

The member names above illustrate the workflow; inspect the installed declarations before using them. Do not infer that zero holders means the player creature is absent, or hard-code holder names/counts.

The safety boundary differs: offline discovery reads metadata/IL and does not load or execute installed target assemblies; `evaluate_csharp` compiles and executes trusted arbitrary code inside the game, synchronously on Unity's main thread, with full game-process and filesystem access and no sandbox. A timeout cannot stop code already running. Keep live probes read-only unless mutation is intended. Discovery works without the bridge and needs no game restart. After updating the host, restart the MCP host and reconnect/restart the MCP client to expose the new tools. See [security guidance](../README.md#security) and [bridge protocol](protocol.md).
