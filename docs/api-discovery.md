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

This is installed-file provenance, not a claim about what is loaded in the running process; the game may still have an older mod build loaded after its DLL is replaced. `api_assemblies` returns each assembly's metadata `moduleVersionId` (MVID), which can be compared with the MVIDs in `game_status(includeAssemblies=true).status.loadedAssemblies` to identify a matching loaded build. The live list includes only file-backed assemblies under the game's Managed directory or Mods root; dynamic assemblies and files outside those roots are excluded. It is deterministic and capped at 128 entries; `loadedAssemblyCount` reports the full eligible count and `loadedAssembliesTruncated` indicates whether entries were omitted. Dependencies resolve only from the configured roots, without network retrieval; unavailable references are reported as warnings.

- `api_assemblies(name?, origin?, offset=0, limit=50, snapshotId?)`: enumerate installed managed assemblies and return `assemblyId`, name, version, `moduleVersionId`, origin, relative path, totals, `hasMore`/`nextOffset`, `snapshotId`, and bounded invalid/native-file diagnostics.
- `api_search(query, assemblyId?, kind?, includeNonPublic=false, offset=0, limit=50, snapshotId?)`: search types and members by name or qualified name, case-insensitively. Public results are the default. Hits include `symbolId`, kind, qualified name, declaring type, signature, accessibility, static status, and provenance. Use `kind="type"` for types; assembly IDs disambiguate same-name assemblies and symbol IDs identify exact overloads.
- `api_type(assemblyId, typeSymbolId, includeInherited=false, includeNonPublic=false, offset=0, limit=100, snapshotId?)`: show a type declaration, bases/interfaces, constraints, and paged members. Inherited results identify their actual declaring assembly and type.
- `api_decompile(assemblyId, symbolId, startLine=1, lineCount=200, snapshotId?)`: return bounded, paged reconstructed C# with identity/provenance and warnings. It is reconstructed from IL, not original source; unavailable dependencies or a missing managed body can limit output. Use `hasMore`/`nextLine` for source continuation.

Use `includeNonPublic` only to inspect implementation details; private or protected members are not necessarily callable from ordinary mod code.

IDs are opaque and tied to assembly origin, path, and content version. Use returned IDs rather than constructing them. A changed/replaced assembly receives a fresh identity, and an old or invalid ID is an error. Results and source are ordered and bounded; inspect continuation fields before assuming a result is complete.

Pass a type hit's `symbolId` as `typeSymbolId` to `api_type`. To decompile a hit, pass its `assemblyId` and `symbolId`; inherited members use their reported declaring assembly ID. Carry the returned `snapshotId` into later pages; if installed files change, the snapshot is rejected and the query must restart.

Discovery uses ICSharpCode.Decompiler 11.1.0.9782 under the [ILSpy MIT license](https://github.com/icsharpcode/ILSpy/blob/master/LICENSE); its required MIT notice is included with the host output.

## Read-only base catalog data

`catalog_search(query?, catalogType?, id?, offset=0, limit=25, snapshotId?)` searches JSON records in the selected installation's `StreamingAssets/Default/bas.jsondb`. It matches type and ID filters case-insensitively and searches record string values, IDs, names, and archive paths. Results include an opaque `entryId`, record type/ID, archive-relative path, size, and snapshot/page fields.

`catalog_read(entryId, snapshotId, startLine=1, lineCount=200)` returns exact JSON lines from a search result in bounded pages. For example, search `catalogType="LevelData", query="Survival"` to find levels whose data contains Survival mode details, or filter `catalogType="ItemData"` to inspect item records. Use `entryId` and `snapshotId` as returned; do not construct them. Snapshot IDs derive from archive bytes, so unchanged archive content retains the same snapshot. If a stale-snapshot error occurs, rerun `catalog_search`, then use its current `entryId` and `snapshotId` together.

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
