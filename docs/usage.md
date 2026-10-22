# Usage

The host is a local stdio MCP server. Offline tools work without the game running and without the bridge. Bridge-backed tools require the game, an installed bridge mod, an enabled bridge config, and in-game readiness.

## Offline workflow

These tools work without launching the game or enabling the bridge:

- `game_installations` lists detected Steam and Meta Horizon Link installs.
- `game_guide(topic?)` is a read-only tool that returns one section of the embedded game guide as `{topic, content}` plus text; it needs no game or bridge. The default topic is `bootstrap`. Topics are `bootstrap`, `unity`, `runtime`, `spawning`, `levels`, `api`, `logs`, `eval`, `example`, `troubleshooting`, and `version`; aliases are `overview` (bootstrap), `console`/`workflow` (api), and `worked-example` (example). An unknown topic is a tool error listing the canonical topics. The guide is embedded in the host DLL, so guide edits take effect only after the host is published again and the MCP client reconnects.
- `api_lookup` is the recommended first discovery call: a natural-language query returns ranked signatures and bounded related context, while exact IDs or `mode:"symbol"` retrieve reconstructed source. `api_assemblies`, `api_search`, `api_type`, and `api_decompile` remain available for browsing and exact implementation questions. See [API and catalog discovery](api-discovery.md).
- `api_find_callers`, `api_find_callees`, and `api_find_usages` navigate static IL references from exact `assemblyId`/`symbolId` pairs. Callers and usages require `scope:"assembly"` or `"snapshot"`; responses report searched assemblies and partial analysis. Their shared index caps at 500,000 methods, 1,000,000 decoded references, 100,000 indexed references, and 10,000,000 IL instructions; a capped index triggers an exact-target overflow scan bounded at 2,000,000 methods, 5,000,000 references, and 50,000,000 instructions. Pages allow 1–100 results, with at most 50,000 matches; limit hits and unreadable/unresolved references make `complete:false`. Carry `snapshotId` and `nextOffset` between pages. Results include opcode and IL offset; static metadata does not guarantee runtime dispatch, reflection, delegate, or event-callback paths. Resolved method and member IDs can be passed to `api_decompile`; type IDs can be passed to `api_type`.
- `catalog_search` and `catalog_read` browse the read-only shipped catalog. Mods add their own catalog JSON and require `manifest.json`.
- `game_logs` with the `current` or `previous` source reads log files from disk.
- `game_status` is safe to call and reports `bridgeConnected: false` with a `bridgeError` when the bridge is unavailable. Its status is compact by default; pass `includeAssemblies=true` only when the loaded assembly inventory is needed.

Offline discovery reads metadata and IL and does not load or execute installed target assemblies. It needs no game restart. After updating the host, restart the MCP host and reconnect or restart the MCP client so the new tools are exposed.

## Host-only installs

A release install is host-only when no game is found or `-SkipBridge` is passed. A source install can also be host-only when no game is found. The host starts, `game_status` reports the bridge as unavailable, and `game_logs` reads existing logs. API and catalog discovery require an installed game. Bridge-backed tools stay unavailable until the bridge mod is installed and a bridge config is enabled. No bridge config is created on a host-only install.

To add the bridge later, install the release `bridge/ThunderRoadMcp.zip` with `game_install_mod` and create a bridge config, or rerun the installer with the game available and a new `-InstallDirectory`.

## Bridge workflow

Bridge-backed tools require the game, an installed bridge mod, an enabled bridge config, and in-game readiness. `game_install_mod` and `game_launch` are setup and process tools and do not require an enabled bridge.

- `game_install_mod` installs a `ThunderRoadMcp.zip` package into the selected game and refuses an occupied destination. Restart the game after installation.
- `game_launch` starts the selected installation and returns its PID without waiting for bridge readiness. It accepts an optional `arguments` array, passed without a shell. For a cold start with no requested level, pass `["-level", "Home"]`; when the user requests a level, pass that target as the startup `-level` argument. The bridge initializes only after the game reaches the main menu; wait with `game_wait_ready` or check `game_status` before bridge-backed calls. `game_launch`, `game_stop`, and `game_restart` control only a process launched and owned by this host; force termination for stop and restart is opt-in. Exiting the host does not stop the game.
- `game_wait_ready` polls the bridge and optionally waits for a specific `level`; `timeoutMs` bounds the wait. It returns `ready`, `timedOut`, `elapsedMs`, and `status`; `ready:false, timedOut:true` is a normal result, not a tool error or proof that the game failed.
- `game_status` reports bridge connectivity and readiness with a compact status by default. Pass `includeAssemblies=true` to include loaded assembly inventory and MVIDs. A successful launch does not mean the bridge is ready.
- `game_load_level` requests a level load through an already-ready bridge; it is not a way to complete initial startup. It accepts a catalog `LevelData` id and game-mode id. By default it returns the pre-request snapshot (`levelId`, `mode`, `currentLevelBefore`, `levelStateBefore`, `sceneReadyBefore`) immediately. Set `waitUntilReady=true` to wait for the target level; `timeoutMs` (1–300000, default 60000) bounds the full operation from before the load request, not just status polling. After an acknowledged request, the result preserves the snapshot and adds `requestAcknowledged:true`, `completed`, `timedOut`, `elapsedMs`, and the last `status`. If the request does not receive an acknowledgement before the deadline, the normal timeout result has `requestAcknowledged:false`, `completed:false`, and no pre-request snapshot fields because the host cannot know whether the bridge received the request; check `game_status` before retrying. When the target is already current and scene-ready before the request, completion requires an observed nonmatching or unready status followed by readiness; if the game exposes no transition between polls, the call may time out despite the level having appeared ready before the request.
- `game_spawn_item` resolves a catalog `ItemData` id and spawns it at the player's head plus 1.5 m forward or at explicit coordinates. It returns `{key,itemId,position,instanceId,name}`; `disallowDespawn` defaults to true and sets `Item.DisallowDespawn`. See [live game runtime](#live-game-runtime).
- `game_spawn_creature` (`creatureId`, optional `x/y/z/rotationY/key`) and `game_spawn_effect` (`effectId`, optional `x/y/z/key`) spawn catalog `CreatureData`/`EffectData` and store the result under a runtime key. `game_find_objects` (`componentType`, optional `name/includeInactive/includeFields/offset/limit`) is a read-only scene query returning instance ids, hierarchy paths, positions, and optional public field values. These three need a redeployed bridge; an older bridge returns a redeploy error.
- Tools reject unknown argument names with `request.invalid`, listing the valid names. `evaluate_csharp` `timeoutMs` is 1–30000; for longer work start a managed coroutine and poll `game_runtime`. `game_wait_ready` and `game_load_level(waitUntilReady=true)` send MCP progress notifications when the client supplies a `progressToken`.
- `game_runtime` lists bridge-owned coroutines and named references with safe value summaries, inspects one stored value by required `key`, stops one coroutine by `key`, or clears all bridge-owned runtime state.
- `evaluate_csharp` requires a ready bridge. See [Live C# evaluation safety](#live-c-evaluation-safety).
- `game_logs` with the `bridge` source reads the authenticated in-game log ring.
Detailed timings are opt-in with `THUNDERROAD_MCP_DIAGNOSTICS=1` in the host environment; the host passes an explicit opt-in flag to the bridge request. Direct authenticated bridge clients may set the request's optional `diagnostics:true` field, and setting the variable in the game process also enables bridge diagnostics. Normal MCP responses omit detailed timing text and `_meta.timings` when disabled. When enabled, `hostTotalMs` measures host tool execution, `bridgeRoundTripMs` covers the host bridge-client path, and `bridgeRequestTotalMs` measures bridge dispatch. Evaluate stages include `compilerQueueWaitMs`, aggregate `compilationMs`, `referenceInitializationMs`, `parseMs`, `emitMs`, `assemblyLoadMs`, `mainThreadQueueWaitMs`, `invocationMs`, and `serializationMs`. Only observed stages are reported. These server-side values are not client-observed MCP latency.

To enable host timings for an MCP session, set `THUNDERROAD_MCP_DIAGNOSTICS=1` in the MCP client's server-launch environment, then restart the MCP host. This is not an MCP tool or runtime setting, so a connected LLM cannot turn it on during a session. Setting the variable in the game process is a separate bridge-side opt-in.


## Live game runtime

`evaluate_csharp` runs synchronously and each call compiles its own assembly, so a variable or a coroutine local to one snippet does not survive it. The bridge provides a persistent `McpRuntime` host for work that must outlive the call. `McpRuntime` is in `ThunderRoadMcp.Bridge`, and the evaluator imports that namespace, so snippets use `McpRuntime.Current` directly:

- `McpRuntime.Current.StartManagedCoroutine("key", routine)` starts a coroutine on the bridge's runtime object; `StopManagedCoroutine("key")` stops it.
- `McpRuntime.Current.Set("key", value)`, `Get<T>("key")`/`TryGet<T>("key", out value)`, and `Remove("key")` are the legacy reference-type APIs. For value types use `GetValue<T>("key")`/`TryGetValue<T>("key", out value)`; `TryGetValue<int>` distinguishes stored zero from missing, null, or mismatched values. `Get` methods return `default(T)` when no compatible stored value exists.
- `game_runtime(action:"list")` and `action:"inspect"` expose bounded detached summaries. `inspect` distinguishes missing (`found:false`) from stored null (`found:true`, `value.kind:"null"`); it supports scalar primitives, enums, strings, common Unity vectors/quaternions/colors, and exact one-dimensional arrays or `List<T>` of supported scalar/value types, limited to 16 elements. Large integers/decimals and NaN/infinities use labeled strings where JSON could lose fidelity. Unity objects report destroyed state; other objects remain opaque. No user getters or arbitrary enumerators run, and all runtime access stays on the Unity main thread. Inspect output, including UTF-8 escaping and bridge envelope, is bounded to 64 KiB.
- `game_runtime(action:"stop", key:"key")` requires `key` and stops one managed coroutine; `action:"clear"` stops all bridge-managed coroutines and clears stored references. Neither operation unsubscribes events nor despawns objects.

`game_spawn_item` resolves a catalog `ItemData` id and returns a runtime key; later calls retrieve the `Item` with `McpRuntime.Current.Get<Item>(key)`. A level load destroys the object, so remove the stale key and spawn again. For custom creature spawning use `api_lookup(query:"spawn a creature", includeExamples:true)` to resolve the installed API and full configuration/callback path; do not guess a spawn method. See the [game guide](game-guide.md).

Event handlers must be retained and explicitly unsubscribed: keep the same delegate in `McpRuntime`, remove an earlier subscription before repeating setup, unsubscribe before removing its runtime key, and do not rely on `clear` to remove game events. See the guide's [managed runtime event example](game-guide.md#managed-runtime).

### Compile-checking examples without execution

Developers can compile the catalog's C# examples against the actual evaluator wrapper and installed references without loading or executing the emitted evaluation assembly:

```text
BridgeSelfTest.exe --validate-examples <Roslyn-payload-directory> <evidence-report.json>
```

Set `THUNDERROAD_MANAGED_DIR` to the installed game's `Managed` directory. For each example, the report records `sourceSha256`, ordered `cleanupSha256` values, and `compileInputSha256` over language plus source and cleanup hashes. It distinguishes the self-test process's `loadedBridgeMvid` and optional `loadedBridgeAssemblySha256` from the payload-adjacent `referencedBridgeAssemblySha256` used by the evaluator. It also records the compiler MVID and payload hash, every packaged Roslyn dependency hash and `compilerPayloadSetId`, the content-hashed compiler reference set, and composite `evaluatorIdentity`. `compile-checked` proves only compilation against that identity; it is not a live gameplay test. Revalidate after the selected API assemblies, bridge, compiler, compiler payload dependencies, or evaluator wrapper change.

## Prebuilt bridge compatibility

Release installs use the tracked prebuilt bridge built against Blade & Sorcery 1.3.2. The installer prints the provenance from `bridge/versions.json`, for example `Using prebuilt bridge for game 1.3.2`.

Compatibility is informational. It does not compare or block against the locally detected game version. Newer game versions may require a new tracked package or a local source build. See [tracked bridge packages](building.md#tracked-bridge-packages).

An older bridge that predates `game_runtime(action:"inspect")` reports `runtime.action must be list, stop, or clear.` The host converts only that known error into upgrade guidance. Install/redeploy the current bridge and restart the game; the older bridge's `list`, `stop`, and `clear` actions remain usable.

## Live C# evaluation safety

`evaluate_csharp` compiles a C# statement body into a method returning `object`, invokes it synchronously on Unity's main thread, and returns a JSON-safe result. Compilation runs off-thread. The compiler builds references from the game's `Managed` directory, the bridge DLL beside the configured runtime payload, and file-backed loaded process assemblies; dynamic and evaluation assemblies without a file location are excluded. Its process-local reference cache is keyed by the full bridge DLL path, not by the content identities or MVIDs of every reference: it does not refresh when a same-path file changes or when a file-backed mod loads after first initialization. Unity does not hot-reload a replaced DLL, so refreshing that metadata would not replace the live game code. Opt-in `referenceInitializationMs` measures cache access/initialization; no live timing was collected here to justify automatic invalidation. Restart the game after changing or installing assemblies that need to be referenced.

The code is trusted arbitrary C# with full game-process, game API, and filesystem access. There is no sandbox. Only enable the bridge for agents and prompts you trust.

`timeoutMs` covers compilation, compiler-queue wait, and dispatch, bounded to 1 to 30000 ms. Expired queued work is skipped. A running C# method or result serialization cannot be forcibly interrupted. Compiler diagnostics and runtime exception details are errors, bounded to 4 KiB; snippet diagnostics refer to `evaluation.cs` line numbers.

Top-level `await`, returned `Task` values, and frame-aware or asynchronous evaluation are unsupported. A returned `Task` is rejected before serialization rather than blocked on. Compiled evaluation assemblies remain loaded until the game process restarts. Evaluation does not patch methods or preserve or reload mod state. Keep live probes read-only unless mutation is intended.

The result object is serialized on Unity's main thread before it is detached from game objects and sent over TCP. No Unity API calls are made by the network or compiler workers. A running evaluation cannot be preempted.

## Logging

`game_logs` sources:

- `current` reads `Player.log`.
- `previous` reads `Player-prev.log`.
- `bridge` reads the authenticated in-game ring and accepts `since`. When `since` is omitted or `0`, it returns the newest entries; pass the returned `nextSequence` as the next `since`.

By default the log directory is `%USERPROFILE%\AppData\LocalLow\WarpFrog\BladeAndSorcery`. `THUNDERROAD_MCP_LOG_DIR` overrides it for the host. Log files can be read directly from disk even if the bridge is unavailable, and they may contain private data.

File pages accept `tailLines` from 1 to 2000. To continue backward, pass the returned `nextBeforeByteOffset` as the next `beforeByteOffset` with the same `snapshotId`; a changed log session invalidates the snapshot and requires restarting pagination. The bridge ring retains 512 entries and returns at most 3 entries per response. See [protocol and limits](protocol.md) for the wire shapes.

Host diagnostics are written to standard error so MCP messages on standard output stay valid. Check your harness's server log if the host starts but the agent reports no tools.

## Bridge configuration

The bridge uses authenticated loopback TCP and bounded NDJSON messages. It is disabled unless a bridge config exists, parses, and sets `enabled` to `true`. Installing the mod alone does not open a listener. The default config path is `%LOCALAPPDATA%\ThunderRoadMcp\bridge.json`. `THUNDERROAD_MCP_CONFIG` overrides it, and the host and game must use the same path. Keep this file out of source control.

The host accepts only a loopback host, a valid port, and a 64-character hexadecimal token. Generate a fresh 32-byte cryptographically random secret; do not use a password or reuse another credential. This Windows PowerShell 5.1 recipe creates the file without overwriting an existing config:

```powershell
$bridgePath = if ($env:THUNDERROAD_MCP_CONFIG) { [IO.Path]::GetFullPath($env:THUNDERROAD_MCP_CONFIG) } else { Join-Path $env:LOCALAPPDATA 'ThunderRoadMcp\bridge.json' }
$configDirectory = Split-Path -Parent $bridgePath
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
if (Test-Path -LiteralPath $bridgePath) { throw "Preserve the existing config; choose a separate THUNDERROAD_MCP_CONFIG path." }
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$token = [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
$json = @{ enabled = $true; host = '127.0.0.1'; port = 47831; token = $token } | ConvertTo-Json
[IO.File]::WriteAllText($bridgePath, $json, [System.Text.UTF8Encoding]::new($false))
```

The resulting config has this shape:

```json
{
  "enabled": true,
  "host": "127.0.0.1",
  "port": 47831,
  "token": "<64 hexadecimal characters; keep private>"
}
```

The installer creates this config by default when it installs the bridge mod and no config exists, so a normal install leaves the bridge enabled. It never enables, disables, or rewrites an existing config; if that config is disabled, the installer warns and preserves it. Pass `-NoBridgeConfig` to install the bridge mod without creating a config, which leaves the bridge offline. Setting `enabled` to `false` keeps the mod offline. A placeholder token is not a usable credential; generate a new random token instead of copying one. Never put the token in agent configuration, source control, or prompts. Restart the game after changing the bridge config.

## Host commands

Run the installed `ThunderRoadMcp.Host.exe` with one of these arguments to use a command mode instead of MCP startup:

```text
--help
--list-installations
--install-bridge <absolute-zip> --game-executable <absolute-exe>
```

`--list-installations` returns the same JSON envelope as `game_installations`, and `--install-bridge` reuses the same discovery and staged package validation as `game_install_mod`. Both emit JSON on standard output and do not write either local config or launch the game. `--help` prints usage text, not JSON. Run the host without arguments to start the stdio MCP server.

## Runtime troubleshooting

- **Host-only install has no bridge tools:** this is expected without a game or with `-SkipBridge`. Install the game and bridge, then enable the bridge config. See [host-only installs](#host-only-installs).
- **Release install asks for the .NET SDK:** run `scripts/install.ps1` from the extracted release root so the sibling `host/` directory is found. Otherwise the installer treats the folder as a source checkout.
- **Bridge reports offline or not ready:** confirm the bridge config exists, is enabled, and is at the same path for host and game. Restart the game after installing or changing the bridge. On cold start, pass `["-level", "Home"]` or the requested target as the startup `-level` argument; the bridge initializes after the main menu. Then check `game_status`. Inspect the config without sharing its token.
- **Bridge config is rejected:** the host requires `enabled: true`, host `127.0.0.1`, a valid port, and a 64-character hexadecimal token. Preserve and fix the existing file instead of overwriting it.
- **`evaluate_csharp` fails:** compile diagnostics point at `evaluation.cs` line numbers. Runtime exceptions are returned as errors. Correct the snippet and retry; a timeout does not stop code that is already running.
- **Launch succeeds but the bridge never becomes ready:** launch returns before readiness. Confirm the bridge config is enabled and, on cold start, that launch used `["-level", "Home"]` or the user-requested startup level. The bridge initializes after the main menu; do not rely on `game_load_level` to reach the initial level before readiness. A `ready:false, timedOut:true` result is not proof the game failed; inspect its `status` or call `game_status` and the logs.
- **`game_load_level` returns the old level:** the result is a pre-call snapshot (`currentLevelBefore`), not the loaded level. Loading is asynchronous; poll `game_status` until `currentLevel` (lowercase) matches case-insensitively and `sceneReady` is true. Confirm the level and mode ids with `catalog_search`.
- **A spawned item or managed reference is `null`:** a level load destroys scene objects. Clear the stale key and spawn or resolve the object again. Coroutines started on `McpRuntime` keep running until the item is gone or you stop them.
- **Logs are missing:** check `game_logs` with the `current` or `previous` source, verify the log directory, or set `THUNDERROAD_MCP_LOG_DIR`. The `bridge` source also requires a ready bridge.
- **The prebuilt bridge targets another game version:** the installer's provenance line is informational and does not block a different local version. Use a new tracked package or a local source build if the bridge fails on a newer game version. See [prebuilt bridge compatibility](#prebuilt-bridge-compatibility).
- **The game reports an older mod build:** compare `game_status(includeAssemblies=true).status.loadedAssemblies` module version IDs with `api_assemblies`. Restart the game after replacing the mod DLL.
- **The agent has no tools or the host will not start:** use the absolute EXE `command` with empty `args` from `mcp-config.json`, confirm the .NET 10 runtime is installed, then restart or reconnect the agent. See [installation troubleshooting](installation.md#installation-troubleshooting).
