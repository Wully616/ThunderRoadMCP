# Blade & Sorcery game guide

How to drive a live Blade & Sorcery game through ThunderRoadMcp: start it, wait for the bridge, load a level, spawn and track objects, run managed coroutines, and read logs. Use it with [API and catalog discovery](api-discovery.md) and the [bridge protocol](protocol.md). Tool parameter details are in [Usage](usage.md).

The harness skill at [`skills/blade-and-sorcery/SKILL.md`](../skills/blade-and-sorcery/SKILL.md) is the short form of this guide; [Unity 2021.3 references](../skills/blade-and-sorcery/references/unity-2021.3.md) holds the engine documentation links.

## Reading this guide through MCP

`game_guide(topic?)` returns one section of this document from the copy embedded in the host, as `{topic, content}` plus text, without a game or bridge. The default `bootstrap` topic returns the intro, version stamp, and Bootstrap sections.

- Topics: `bootstrap`, `unity`, `runtime`, `spawning`, `levels`, `api`, `logs`, `eval`, `example`, `troubleshooting`, `version`.
- Aliases: `overview` → `bootstrap`, `console`/`workflow` → `api`, `worked-example` → `example`.
- An unknown topic is a tool error that lists the canonical topics.

The embedded copy is built with the host. Edit this file as the source of truth, then republish the host and reconnect the MCP client before expecting the new text from the tool.

## Version stamp

The tracked bridge package is built against public PC **1.3.2**. Steam and Meta Horizon Link installs are expected to use the same game version; ThunderRoad APIs can change between game releases. Never assume a member exists because a snippet or an old mod used it: run `api_search` and `api_type` against the selected installation, and compare `game_status.status.gameVersion` with the package you are targeting. Discovery reads installed files, not a source checkout.

## Bootstrap

The host uses the installation selected by `launch.json` (or `THUNDERROAD_MCP_LAUNCH_CONFIG`); with no explicit selection it auto-discovers Steam and Meta Horizon Link installs. One candidate is selected automatically; none or several are an error until `executablePath` is set.

1. `game_installations` confirms the selected installation. It never launches or changes the game.
2. If the game process is not running, launch it with `game_launch(arguments: ["-level", "Home"])` by default. If the user requests another level, pass that level directly as the startup `-level` argument instead. `game_launch` returns the PID; it does not wait for bridge readiness. The bridge becomes available only after the main-menu/Home startup has loaded, so wait with `game_wait_ready` or verify `game_status` before using bridge-backed tools. Do not depend on `game_load_level` for initial startup.
3. `game_wait_ready(level?, timeoutMs)` polls bridge connectivity and readiness. If `level` is given, it also waits for that level. It returns `ready`, `timedOut`, `elapsedMs`, and `status`; a timeout is a normal `ready:false, timedOut:true` result, not a tool error and not proof that the game failed. Inspect `status` or call `game_status` before retrying.
4. `game_status` is always safe to call. By default it reports compact `bridgeConnected`, `bridgeReady`, `currentLevel`, `levelState`, `sceneReady`, and bridge payload fields; pass `includeAssemblies=true` to include loaded assembly inventory and MVIDs. `bridgeReady` is true only when the authenticated bridge answered, and `sceneReady` is true only when the game reports its loaded state.

`game_launch`, `game_stop`, and `game_restart` control only a process launched by this host. Exiting the host does not stop the game.

## Loading a level

`game_load_level(levelId, mode)` asks the game to load a level.

- `levelId` is a catalog `LevelData` id such as `Arena` or `Home`; browse with `catalog_search(catalogType="LevelData")`. The catalogue also contains the level's supported modes.
- `mode` is the game-mode id, for example `Sandbox`.
- Example: `game_load_level("Arena","Sandbox")`.

Loading is a game-side transition, not an instantaneous scene swap. The call returns a snapshot taken **before** it invokes `LevelManager.LoadLevel`: `levelId`, `mode`, `currentLevelBefore`, `levelStateBefore`, and `sceneReadyBefore`. There is no post-load level in the result. Poll `game_status` (or `game_wait_ready` with the target level) until `currentLevel` matches and `sceneReady` is true.

`LevelManager.GetCurrentLevel()` reports **lowercase** ids, so `currentLevelBefore` and the later `game_status.currentLevel` must not be compared case-sensitively with the id you passed. After the load, compare the status value:

```csharp
string.Equals(status.currentLevel, "Arena", StringComparison.OrdinalIgnoreCase)
```

`currentLevelBefore` describes the level that was active when the request was made. Treat it as a pre-load hint, not as confirmation that the new level loaded.

## Managed runtime

Every `evaluate_csharp` call compiles a fresh assembly and runs its body synchronously. Local variables and the generated eval type disappear with the call, so they are not durable storage. A coroutine started on a game-owned or otherwise persistent object can outlive the call, but its lifetime then depends on that owner; `McpRuntime.Current` is the supported owner for coroutines and named references that later eval calls must reach. `McpRuntime` is in `ThunderRoadMcp.Bridge`, and the evaluator imports that namespace, so snippets use `McpRuntime.Current` directly.

- `McpRuntime.Current.StartManagedCoroutine("key", Routine())` starts `Routine()` and records it under `key`.
- `McpRuntime.Current.StopManagedCoroutine("key")` stops that coroutine.
- `McpRuntime.Current.Set("key", value)`, `Get<T>("key")`, and `Remove("key")` store, read, and drop named references.
- `game_runtime(action:"list")` returns `{coroutines:[{key,type}],objects:[{key,type}],coroutineCount,objectCount}`.
- `game_runtime(action:"stop", key:"key")` stops one managed coroutine and returns `{action,key,stopped}`. `key` is required; omitting it is a validation error and stops nothing.
- `game_runtime(action:"clear")` stops all managed coroutines and clears stored object references, returning `{action:"clear",cleared:true}`. Use it before reusing keys or after a level change.

Coroutines run on the main thread and are not threads; a blocking loop still stalls the game. A routine ends naturally when its `IEnumerator` finishes or after `StopManagedCoroutine`.

Never call `StopAllCoroutines` on a game-owned manager such as `Player`, `LevelManager`, or a creature controller. It stops the game's own routines too. Manage only the coroutines you started through `McpRuntime` (or on your own GameObject).

Named references are ordinary managed references: a scene load destroys the GameObject but not the C# reference. Unity's overloaded equality reports a destroyed object as `== null`, so re-check before use and `Remove` stale keys.

## Spawning items

`game_spawn_item(itemId, x?, y?, z?, key?, disallowDespawn?)` resolves `Catalog.GetData<ItemData>(itemId)`, spawns the item, and returns `{key,itemId,position:{x,y,z},instanceId,name}`.

```text
game_spawn_item(itemId: "<ItemData id>", key: "demo-sword")
```

- Find ids with `catalog_search(catalogType="ItemData", query="sword")`. An unknown or empty id is a bridge error: `Catalog item not found: <id>`.
- Omit `x`/`y`/`z` to spawn at the player's head plus 1.5 m forward; explicit coordinates are world-space. The returned `position` is the actual world position.
- `key` is the handle for `McpRuntime.Current.Get<Item>(key)`; keep it stable so later eval calls can find the object. When omitted, the bridge generates `item-<12 hex>`.
- `disallowDespawn` defaults to `true` on the host and sets `Item.DisallowDespawn`; pass `false` to allow normal despawn. Keeping it true is useful for props you intend to inspect over time.
- After a level load, the spawned object is destroyed. Remove the stale key and spawn again.

Creatures do not have a dedicated tool yet. Discover the spawn API first: `api_search("Creature spawn")`, `api_type` for the candidate type, and decompile only when the implementation behavior matters. Then spawn through `evaluate_csharp` and keep the returned reference with `McpRuntime.Current.Set`. Do not guess creature, brain, or spawner members.

## API workflow

1. `api_search` finds types and members by name, including qualified names.
2. `api_type` shows the declaration, base types, interfaces, constraints, and members; follow inherited members to their actual declaring assembly.
3. `api_decompile` shows reconstructed C#. Use it only when behavior matters; signatures usually come from `api_type`.
4. For an existing game action, search `ConsoleCommands` first. Many actions already have a command, and reusing it is safer than reimplementing the logic.

Discovery defaults to public members. `includeNonPublic` is for understanding implementation; a private member is not callable from normal mod or eval code. Pass returned IDs and `snapshotId` values through unchanged. If the installed files change, old IDs are rejected and the query must restart.

## Logs

`game_logs(source:"bridge", since?)` reads the in-game ring. When `since` is omitted or `0`, the bridge returns the **newest** entries rather than an empty page. Each response returns at most three entries; pass the returned `nextSequence` as the next `since` to continue forward. The ring retains 512 entries.

Use `source:"current"` and `source:"previous"` to read `Player.log` and `Player-prev.log` from disk, which works without a ready bridge. Bridge log entries include the Unity log level and a stack trace when the game logged one. Add `Debug.Log` to your own eval code only when a value cannot be returned directly.

## `evaluate_csharp` limits

- The snippet is a **statement body** returning `object`, compiled into a synchronous method and invoked on Unity's main thread.
- Top-level `await` is unsupported, and a returned `Task` is rejected before serialization. Use a managed coroutine or a normal blocking call.
- `timeoutMs` bounds compilation, queue wait, and dispatch. It cannot interrupt a method that is already running; a long loop keeps blocking the game after the error is returned.
- Every successful eval leaves a compiled assembly loaded until the game process restarts. Prefer a small number of purposeful snippets over a stream of one-liners.
- The code is trusted arbitrary C# with full game-process and filesystem access. Prefer read-only probes; mutate only when that is the goal.
- Runtime exceptions come back as errors with the exception message; compile diagnostics refer to `evaluation.cs` line numbers.

## Unity 2021.3 guidance

Blade & Sorcery uses Unity **2021.3.38f1** with Mono/JIT. Verify signatures against the installed assemblies; APIs documented only for later Unity versions are not available.

- **Object lifetime:** a scene load destroys scene objects unless they are marked `DontDestroyOnLoad`. Use Unity's overloaded `== null` check for destroyed objects; the null-conditional operator (`?.`) bypasses it. Do not assume a stored reference survives `game_load_level`. [Object.DontDestroyOnLoad](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Object.DontDestroyOnLoad.html), [Object](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Object.html)
- **MonoBehaviour and coroutines:** `StartCoroutine` needs an active, enabled MonoBehaviour. Coroutines are frame-scheduled on the main thread, not threads. `yield return null` resumes next frame; `WaitForSeconds` uses scaled time and is affected by `Time.timeScale`, while `WaitForSecondsRealtime` is not; `WaitForEndOfFrame` runs after rendering. [Coroutines manual](https://docs.unity3d.com/2021.3/Documentation/Manual/Coroutines.html), [MonoBehaviour.StartCoroutine](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/MonoBehaviour.StartCoroutine.html), [WaitForSeconds](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/WaitForSeconds.html), [WaitForEndOfFrame](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/WaitForEndOfFrame.html)
- **Frame timing:** `Time.deltaTime` is the completed-frame time; do not multiply it by another frame count. Movement in `Update` is frame-rate dependent, while physics runs in `FixedUpdate` with `Time.fixedDeltaTime`. [Time.deltaTime](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Time-deltaTime.html)
- **Rigidbody:** a kinematic body ignores forces and gravity and is not moved by the physics simulation. Drive it with `MovePosition`/`MoveRotation` from `FixedUpdate`; for a non-kinematic body, writing `transform.position` fights the simulation. Set `isKinematic` deliberately before driving a transform. [Rigidbody.isKinematic](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Rigidbody-isKinematic.html), [Rigidbody.MovePosition](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Rigidbody.MovePosition.html), [FixedUpdate](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/MonoBehaviour.FixedUpdate.html)
- **Scenes:** Unity scene loading (`SceneManager.LoadScene`) is not how Blade & Sorcery changes levels. Use `game_load_level`; direct scene loads bypass the game's level and mode setup. [SceneManager.LoadScene](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/SceneManagement.SceneManager.LoadScene.html)
- **Logging:** `Debug.Log`, `Debug.LogWarning`, and `Debug.LogException` are captured in `Player.log` and in the bridge ring. [Debug.Log](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Debug.Log.html), [Log files](https://docs.unity3d.com/2021.3/Documentation/Manual/LogFiles.html)

## Worked example: spawn and orbit

1. Find a prop and spawn it, keeping the key:

   ```text
   catalog_search(catalogType="ItemData", query="sword")
   game_spawn_item(itemId: "<an ItemData id from the search>", key: "orbit-demo")
   ```

2. Start a managed coroutine that circles the item around its spawn point:

   ```csharp
   var item = McpRuntime.Current.Get<Item>("orbit-demo");
   if (item == null) return new { error = "spawn orbit-demo first, then retry" };

   var body = item.GetComponent<Rigidbody>();
   if (body != null) body.isKinematic = true;

   var origin = item.transform.position;
   McpRuntime.Current.StartManagedCoroutine("orbit-demo", Orbit());

   IEnumerator Orbit()
   {
       var angle = 0f;
       while (item != null)
       {
           angle += 90f * Time.deltaTime;
           var offset = new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad), 0f, Mathf.Sin(angle * Mathf.Deg2Rad)) * 1.5f;
           item.transform.position = origin + offset;
           yield return null;
       }
   }

   return new { key = "orbit-demo", origin, physics = body != null };
   ```

3. Inspect or stop it:

   ```text
   game_runtime(action:"list")
   game_runtime(action:"stop", key:"orbit-demo")
   ```

The loop ends by itself when the item is destroyed (for example after a level load), and `StopManagedCoroutine` stops it sooner. The example drives the transform directly, so it forces the body kinematic first; for a physics-driven prop, move the body with forces or `MovePosition` in `FixedUpdate` instead.

## Troubleshooting

- **`game_launch` succeeded but tools are unavailable:** launch is not readiness. Wait with `game_wait_ready` and check `game_status`. Confirm a bridge config exists, is enabled, and the game was restarted after installing the bridge.
- **Level load appears stuck:** loading takes time. The tool result is a pre-call snapshot, so confirm progress with `game_status`; compare lowercase level ids case-insensitively.
- **A runtime key returns `null`:** the object was destroyed by a level load or despawn, or the key was cleared. Remove the stale key and spawn or resolve the object again.
- **A coroutine keeps running after an eval:** it was started on `McpRuntime`; stop it with `game_runtime(action:"stop", key:"…")`. Never clear it with `StopAllCoroutines` on a game manager.
- **A snippet compiles against the wrong build:** the selected installation may differ from the tracked 1.3.2 package. Check `game_status.status.gameVersion` and re-run `api_type` for the member.
