# Building and testing

Build from a Windows source checkout. See [Requirements](../README.md#requirements) for the .NET 10 SDK, .NET 10 runtime, and installed game. Release ZIP installs use a prebuilt host and bridge and do not need the SDK.

The host targets `net10.0`. The in-game bridge runs under the game's Mono runtime. The bridge, compiler, and self-test target `net472` and reference the selected game's `BladeAndSorcery_Data\Managed` assemblies. Builds of those projects require `GameManagedDir`; `Directory.Build.targets` rejects a missing or incomplete directory.

## Build

Set the game's `Managed` directory, then build the solution:

```powershell
$game = Read-Host 'Absolute path to a detected BladeAndSorcery.exe'
$managed = Join-Path (Split-Path $game) 'BladeAndSorcery_Data\Managed'
dotnet build ThunderRoadMcp.sln -c Release -p:GameManagedDir="$managed"
```

### IDE builds (Rider/Visual Studio)

IDEs do not pass `-p:` properties, so keep the game path in the untracked local props file:

1. Copy `GameManagedDir.props.example` to `GameManagedDir.props` in the repository root.
2. Set `GameManagedDir` to the installed game's `BladeAndSorcery_Data\Managed` directory, escaping XML special characters such as `&` as `&amp;`.
3. Reload the solution and build.

`GameManagedDir.props` is ignored by Git. A command-line `-p:GameManagedDir="..."` or the `GAME_MANAGED_DIR` environment variable overrides it.

The solution builds the host, bridge, compiler, and `src/ThunderRoadMcp.Bridge.SelfTest`. The host check under `tests/HostBehaviorCheck` is built separately by `dotnet run`.

The published `ThunderRoadMcp.Host.exe` is a framework-dependent apphost, not self-contained. It requires the .NET 10 runtime and is launched directly rather than through `dotnet`.

## Tracked bridge packages

`scripts/package-bridge.ps1` publishes the bridge and compiler, rejects game dependency assemblies, and writes `ThunderRoadMcp.zip`:

```powershell
$packageOutput = Join-Path $env:TEMP ('ThunderRoadMcp-package-' + [guid]::NewGuid().ToString('N'))
.\scripts\package-bridge.ps1 -GameManagedDir $managed -OutputDirectory $packageOutput
```

`package-bridge.ps1` uses the first `dotnet` on `PATH` unless you pass `-DotnetPath`; the installer passes the SDK it selected.

`-OutputDirectory` must be a new directory. The script does not delete shared `artifacts` or existing files. The package contains the root bridge DLL, `manifest.json`, and `runtime/*.dll.payload` files. It contains no game, Unity, or Newtonsoft assemblies.

Tracked packages live in `dist/bridge`. The current package is `dist/bridge/ThunderRoadMcp-1.3.2.zip`, built against the current public Steam 1.3.2 game. Metadata is `dist/bridge/versions.json`:

```json
{
  "latest": "1.3.2",
  "versions": [
    {
      "gameVersion": "1.3.2",
      "package": "ThunderRoadMcp-1.3.2.zip"
    }
  ]
}
```

Maintainer flow for a new game version:

1. Build a package against that public game's `Managed` directory with `scripts/package-bridge.ps1 -GameManagedDir ... -OutputDirectory ...`.
2. Add the ZIP under `dist/bridge` and add its `gameVersion` and `package` entry to `dist/bridge/versions.json`.
3. Commit both files. CI validates every entry and its package structure.

Compatibility is informational. The installer does not compare or block against the locally detected game version, so a newer game may require a new tracked package or a local source build. See [dist/bridge/README.md](../dist/bridge/README.md).

`scripts/install.ps1` stages into an isolated temporary directory and cleans it when it finishes. A source checkout publishes the host and, when a game is found, uses this packaging path for the bridge. A release layout uses the prebuilt host and tracked bridge. Both refuse to overwrite an existing host directory or bridge mod and preserve existing configs.

## CI and release workflows

Both workflows use hosted Windows runners. There is no self-hosted bridge job and no game DLL download.

`ci.yml` publishes the host, runs `tests/HostBehaviorCheck`, validates every entry in `dist/bridge/versions.json` and its package structure, and parses the PowerShell scripts.

`release.yml` runs for tags matching `v*` or `workflow_dispatch`. It publishes the release ZIP as both an Actions artifact and a GitHub release asset.

A tag run (including manual dispatch on a tag) creates or updates a normal release. A branch dispatch creates or updates a prerelease tagged `v0.0.0-dev.<run number>` at the selected commit.

The release ZIP contains:

- `host/` with the prebuilt host
- `bridge/ThunderRoadMcp.zip` with the resolved latest tracked bridge
- `bridge/versions.json`
- `scripts/`, `src/`, `docs/`, and `skills/`
- `README.md`, `Directory.Build.props`, `Directory.Build.targets`, `GameManagedDir.props.example`, and `ThunderRoadMcp.sln`

Release installs use the prebuilt host and bridge and require the .NET 10 runtime. Source checkout installs build the host and, when a game is found, the bridge, and require the .NET 10 SDK and runtime.

## Checks

The production compiler and queue check uses actual game assembly metadata. Set `THUNDERROAD_MANAGED_DIR` to the same `Managed` directory and pass the packaged runtime directory:

```powershell
$bridgePackage = Join-Path $packageOutput 'ThunderRoadMcp'
$env:THUNDERROAD_MANAGED_DIR = $managed
dotnet run --project src/ThunderRoadMcp.Bridge.SelfTest -c Release -p:GameManagedDir="$managed" -- "$bridgePackage/runtime"
```

The self-test also compile-checks the bundled C# gameplay examples with the actual evaluator wrapper and installed reference set, without loading an evaluation assembly. To save an identity-bound evidence report separately:

```powershell
dotnet run --project src/ThunderRoadMcp.Bridge.SelfTest -c Release -- --validate-examples "$bridgePackage/runtime" "$packageOutput/example-validation.json"
```

Keep `THUNDERROAD_MANAGED_DIR` set as above. Evidence is `compile-checked`, not live-tested; the report binds code hashes, reference content hashes, and bridge/compiler MVIDs. Regenerate it after changing those inputs. MCP-operation examples are not C# and remain unverified by this command.


The isolated evaluator check loads the bridge DLL from bytes, without a file-backed location, and verifies basic evaluation and `McpRuntime` resolution:

```powershell
.\tests\BridgeNamespaceIsolationCheck.ps1 -BridgeDirectory $bridgePackage -GameManagedDirectory $managed
```

The isolated host boundary check runs against a published host DLL:

```powershell
dotnet run --project tests/HostBehaviorCheck -c Release -- "$env:LOCALAPPDATA/ThunderRoadMcp/host/ThunderRoadMcp.Host.dll"
```

The installer behavior check uses a real installed game's `Managed` directory. It copies the bridge's actual game reference assemblies into temporary synthetic game trees, covers source and release-layout installs, host-only `-SkipBridge`, a missing `Mods` directory, starts the exact generated stdio command, verifies initialize and `tools/list`, and confirms refused installs leave existing data untouched. It does not change the live game:

```powershell
.\tests\InstallerBehaviorCheck.ps1 -GameManagedDir $managed
```

Installer checks use isolated synthetic game trees and do not claim a live Unity run.

The isolated installer behavior check also upgrades source and release-layout installs using real installed game `Managed` assemblies copied into synthetic games. It verifies byte-preserved configs, replacement of obsolete host/mod files, MCP initialize and `tools/list`, host-only mode, and untouched unrelated mods; it never touches the live installation:

```powershell
.\tests\InstallerBehaviorCheck.ps1 -GameManagedDir $managed
```

## Discovery performance measurements

Set `THUNDERROAD_MCP_DIAGNOSTICS=1` on a benchmark host to emit bounded request metrics to stderr. Normal MCP text/structured results omit detailed metrics. Diagnostics do not contain bridge secrets, snippets, or full source/catalog bodies.

`scripts/benchmark_mcp.py --help` lists runner inputs; it captures cold, persisted, warm, and invalidation cases without changing the selected game's files. Keep generated cache/measurement directories and machine-specific reports local, isolated from normal user caches.
