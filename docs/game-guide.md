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

`game_load_level(levelId, mode)` asks the game to load a level. Add `waitUntilReady:true` and an optional `timeoutMs` to wait in the same call; the waited result includes completion/timeout state and `elapsedMs` from before the load request through the readiness check. This is observed server-side elapsed time, not a frame-accurate engine loading duration.

- `levelId` is a catalog `LevelData` id such as `Arena` or `Home`; browse with `catalog_search(catalogType="LevelData")`. The catalogue also contains the level's supported modes.
- `mode` is the game-mode id, for example `Sandbox`.
- Example: `game_load_level("Arena","Sandbox")`.

Loading is a game-side transition, not an instantaneous scene swap. By default the call returns a snapshot taken **before** it invokes `LevelManager.LoadLevel`: `levelId`, `mode`, `currentLevelBefore`, `levelStateBefore`, and `sceneReadyBefore`. These fields remain pre-load state even with `waitUntilReady:true`; inspect the completion and final status fields for the outcome. Without the optional wait, poll `game_status` (or `game_wait_ready` with the target level) until `currentLevel` matches and `sceneReady` is true.

`LevelManager.GetCurrentLevel()` reports **lowercase** ids, so `currentLevelBefore` and the later `game_status.currentLevel` must not be compared case-sensitively with the id you passed. After the load, compare the status value:

```csharp
string.Equals(status.currentLevel, "Arena", StringComparison.OrdinalIgnoreCase)
```

`currentLevelBefore` describes the level that was active when the request was made. Treat it as a pre-load hint, not as confirmation that the new level loaded.

With `waitUntilReady:true`, check `requestAcknowledged` as well as `completed`/`timedOut`. If the total deadline expires before acknowledgement, no pre-request snapshot can be reported and the bridge may still have received the request; inspect status before retrying. Reloading an already-ready target requires an observed unready or different-level state before readiness counts as completion. A transition missed between polls can therefore produce a conservative timeout.

## Managed runtime

Every `evaluate_csharp` call compiles a fresh assembly and runs its body synchronously. Local variables and the generated eval type disappear with the call, so they are not durable storage. A coroutine started on a game-owned or otherwise persistent object can outlive the call, but its lifetime then depends on that owner; `McpRuntime.Current` is the supported owner for coroutines and named references that later eval calls must reach. `McpRuntime` is in `ThunderRoadMcp.Bridge`, and the evaluator imports that namespace, so snippets use `McpRuntime.Current` directly.

- `McpRuntime.Current.StartManagedCoroutine("key", Routine())` starts `Routine()` and records it under `key`.
- `McpRuntime.Current.StopManagedCoroutine("key")` stops that coroutine.
- `McpRuntime.Current.Set("key", value)`, `Get<T>("key")`/`TryGet<T>("key", out value)` are reference-type retrieval APIs; `Get<T>` returns `default(T)` for a missing or mismatched key, and `TryGet<T>` reports whether a compatible value (including stored null) exists. Value types use the distinct `GetValue<T>`/`TryGetValue<T>` APIs, for example `TryGetValue<int>("counter", out count)` distinguishes stored zero from absence or mismatch.
- `game_runtime(action:"list")` lists managed keys, types, and bounded summaries; `action:"inspect"` requires a key and returns a detached DTO without compiling C#. It preserves booleans, strings, numeric primitives, enums, common Unity vectors/quaternions/colors, and exact bounded one-dimensional arrays or `List<T>` of supported scalar/value elements. Collections include at most 16 elements. Large integers/decimal and non-finite floating-point values use labeled strings where JSON consumers could lose fidelity. Unity objects include a destroyed flag; arbitrary objects remain opaque. Inspection invokes no user getters or enumerators and runs on Unity's main thread. The complete result fits the 64 KiB UTF-8 bridge limit.
- `list` also reports each managed coroutine's `routine` (the iterator's type name), `startedAtUtc`, and `steps` (how many times it has yielded), so bridge-started work such as an in-flight `spawn-<guid>` coroutine is visible. `recentFailures` holds the last 16 exceptions thrown by managed coroutines as `{key,routine,error,steps,startedAtUtc,failedAtUtc}`. A routine that throws is removed from `coroutines`, so check `recentFailures` (and `game_logs(source:"bridge")`) when a coroutine disappears. `clear` empties the failure history.
- `game_runtime(action:"stop", key:"key")` stops one managed coroutine and returns `{action,key,stopped}`. `key` is required; omitting it is a validation error and stops nothing.
- `game_runtime(action:"clear")` stops all managed coroutines and clears stored object references, returning `{action:"clear",cleared:true}`. Use it before reusing keys or after a level change.

Coroutines run on the main thread and are not threads; a blocking loop still stalls the game. A routine ends naturally when its `IEnumerator` finishes or after `StopManagedCoroutine`.

Never call `StopAllCoroutines` on a game-owned manager such as `Player`, `LevelManager`, or a creature controller. It stops the game's own routines too. Manage only the coroutines you started through `McpRuntime` (or on your own GameObject).

Named references are ordinary managed references: a scene load destroys the GameObject but not the C# reference. Unity's overloaded equality reports a destroyed object as `== null`, so re-check before use and `Remove` stale keys.

Game event subscriptions need explicit ownership: keep the exact delegate in `McpRuntime`, remove any prior registration before repeating the setup, and unsubscribe before dropping the stored delegate. `game_runtime(action:"clear")` clears references but does not unsubscribe game or Unity events.

```csharp
var runtime = McpRuntime.Current;
const string key = "scene-change-handler";
var previous = runtime.Get<UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene>>(key);
if (previous != null) UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= previous;
UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene> handler =
    (oldScene, newScene) => UnityEngine.Debug.Log("Active scene changed to " + newScene.name);
runtime.Set(key, handler);
UnityEngine.SceneManagement.SceneManager.activeSceneChanged += handler;
return new { subscribed = true, key };
```

Unsubscribe in a later eval:

```csharp
var runtime = McpRuntime.Current;
const string key = "scene-change-handler";
var handler = runtime.Get<UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene>>(key);
if (handler != null) UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= handler;
runtime.Remove(key);
return new { unsubscribed = handler != null };
```

Reusable helpers: `McpRuntime` stores any object, including delegates, and every evaluation assembly stays loaded until the game process restarts, so a delegate stored by one `evaluate_csharp` call stays callable from later calls without resending its code. Keys are process-local: a stored delegate survives level loads but not `game_runtime(action:"clear")` or a game restart, and it must be stored again after its code changes.

```csharp
var runtime = McpRuntime.Current;
const string key = "demo.helper.distance";
Func<Creature, float> distanceToPlayer = creature =>
    creature == null || Player.local == null || Player.local.head == null
        ? -1f
        : Vector3.Distance(Player.local.head.transform.position, creature.transform.position);
runtime.Set(key, distanceToPlayer);
return new { stored = key, usage = "McpRuntime.Current.Get<Func<Creature, float>>(\"demo.helper.distance\")(creature)" };
```

Call it from a later eval with `McpRuntime.Current.Get<Func<Creature, float>>("demo.helper.distance")(creature)` and remove it with `McpRuntime.Current.Remove("demo.helper.distance")`.

## Finding live objects

`game_find_objects(componentType, name?, includeInactive?, includeFields?, offset?, limit?)` is a read-only scene query. It lists loaded `UnityEngine.Component` instances of one type, so you can locate a `LightningStriker`, a creature's `Brain`, or a named GameObject without writing an `evaluate_csharp` scan first. It needs a ready bridge; an older bridge without it returns a redeploy-the-bridge error.

```text
game_find_objects(componentType: "LightningStriker")
game_find_objects(componentType: "Locomotion", includeFields: true, limit: 3)
game_find_objects(componentType: "Transform", name: "Arena", includeInactive: true)
```

- `componentType` is a simple name (`LightningStriker`) or a full name (`ThunderRoad.Creature`) of a loaded `UnityEngine.Component` type. An unknown name is an error that points at `api_search(query, kind:"type")`, and an ambiguous simple name lists the full names to choose from. Use `Transform` with `name` to find GameObjects by name.
- `name` is an optional case-insensitive substring of the GameObject name. `includeInactive` (default false) also searches inactive GameObjects.
- Results are ordered by instance id and paged: `offset` is 0 to 100000, and `limit` defaults to 25 with a maximum of 50 (10 with `includeFields`). The response reports `total`, `hasMore`, and `nextOffset`.
- Each result has `instanceId`, the runtime `type`, the GameObject `name`, a hierarchy `path` (at most 32 levels and 512 characters, prefixed with `…` when cut), `scene`, `activeInHierarchy`, `enabled` (null unless the component is a `Behaviour`), and the world `position`.
- `includeFields:true` adds up to 24 public instance fields per component, sorted by name and excluding fields declared by Unity types, as `{name,type,value}` with `value` summarized like `game_runtime` inspect; `fieldsTruncated` says whether more existed. Only fields are read, never properties, because a property getter can run game code.

The query only reads. To act on a result, resolve its `instanceId` in `evaluate_csharp`, for example with `Resources.InstanceIDToObject(instanceId)` (confirm the member with `api_type` on the selected installation; the result is null once the object is destroyed), or keep what you need in `McpRuntime`. The scan runs on Unity's main thread over every object of the type, so prefer a specific type, or a `name` filter and small `limit` for broad types such as `Transform`.

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

`game_spawn_creature(creatureId, x?, y?, z?, rotationY?, key?)` resolves `Catalog.GetData<CreatureData>(creatureId)` and runs `CreatureData.SpawnAsync(position, rotationY, parent, pooled, waypoints, callback)` on a bridge-managed coroutine; the game configures the creature before its callback. It returns `{key,creatureId,position:{x,y,z},rotationY,instanceId,name}` and stores the `Creature` under `key`, retrievable with `McpRuntime.Current.Get<Creature>(key)`.

```text
game_spawn_creature(creatureId: "<CreatureData id>", key: "demo-creature")
```

- Find ids with `catalog_search(catalogType="CreatureData")`. An unknown id is a bridge error: `Catalog creature not found: <id>`.
- Omit `x`/`y`/`z` to spawn 2 m in front of the player, facing them; explicit coordinates are world-space. `rotationY` is yaw in degrees: it defaults to facing the player for the default position and to 0 for explicit coordinates. When omitted, `key` is generated as `creature-<12 hex>`.
- The spawn is asynchronous: the call waits up to 20 seconds for the game to finish, and a creature that arrives after the call gave up is despawned instead of being left untracked. This is `CreatureData.SpawnAsync`, not `Creature.SpawnAsync`, and raw prefab instantiation does not produce a configured creature.
- Clean up with `evaluate_csharp`: `var creature = McpRuntime.Current.Get<Creature>("demo-creature"); if (creature != null) creature.Despawn(); McpRuntime.Current.Remove("demo-creature");`. `game_runtime(action:"clear")` only drops references; it does not despawn creatures.

`game_spawn_effect(effectId, x?, y?, z?, key?)` resolves `Catalog.GetData<EffectData>(effectId)`, spawns it with `EffectData.Spawn`, calls `EffectInstance.Play()`, stores the `EffectInstance` under `key` (generated as `effect-<12 hex>` when omitted), and returns `{key,effectId,position:{x,y,z}}`. Omit `x`/`y`/`z` to play it 1.5 m in front of the player. Find ids with `catalog_search(catalogType="EffectData")`. Stop it with `McpRuntime.Current.Get<EffectInstance>(key).End()` (or `Despawn()`), then `McpRuntime.Current.Remove(key)`.

## API workflow

Choose the workflow by task:

- Existing game action: use its dedicated tool, such as `game_spawn_item`, `game_spawn_creature`, or `game_spawn_effect`, and `game_find_objects` to locate live scene components; search catalog data only if the required ID is unknown. Readiness checks are needed only before bridge-backed actions.
- Custom script: one `api_lookup(query:"...")` call returns ranked signatures, bounded related declarations, relevant examples, and execution/cleanup context; then write a purposeful statement-body snippet and call `evaluate_csharp`.
- Exact implementation question: use `api_lookup(mode:"symbol", query:"...")` or the lower-level `api_search`, `api_type`, and `api_decompile` tools. Do not fetch exhaustive inventory, inspect unrelated types, or read the guide twice by default.

The API tools inspect installed files without executing them. `api_search` and `api_lookup` always include game assemblies and include only mod DLLs whose path and MVID match the bridge's loaded-assembly inventory outside underscore-prefixed mod folders; unloaded mods are never searched, and with the bridge offline no mods are searched. Base catalog records are shipped data, not proof that an object exists in the mod-merged runtime. Snapshot IDs reject stale symbols after files change.

## Logs

`game_logs(source:"bridge", since?)` reads the in-game ring. When `since` is omitted or `0`, the bridge returns the **newest** entries rather than an empty page. Each response returns at most three entries; pass the returned `nextSequence` as the next `since` to continue forward. The ring retains 512 entries.

Use `source:"current"` and `source:"previous"` to read `Player.log` and `Player-prev.log` from disk, which works without a ready bridge. Bridge log entries include the Unity log level and a stack trace when the game logged one. Add `Debug.Log` to your own eval code only when a value cannot be returned directly.

## `evaluate_csharp` limits

- The snippet is a **statement body** returning `object`, compiled into a synchronous method and invoked on Unity's main thread.
- Top-level `await` is unsupported, and a returned `Task` is rejected before serialization. Use a managed coroutine or a normal blocking call.
- `timeoutMs` (1 to 30000, default 30000; the host rejects other values as `request.invalid`) bounds compilation, queue wait, and dispatch. It cannot interrupt a method that is already running; a long loop keeps blocking the game after the error is returned. For longer work, start a managed coroutine and poll `game_runtime(action:"list")`. An MCP client can enforce its own request timeout that is shorter than `timeoutMs`, so keep each call short. `game_wait_ready` and `game_load_level` with `waitUntilReady` send MCP progress notifications about every 5 seconds when the client supplies a progress token; if the client times out first, poll again with `game_wait_ready` and a smaller `timeoutMs` rather than repeating a load.
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

   System.Collections.IEnumerator Orbit()
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

### Common action recipe: spawn, activate, throw, replace, clean up

**Target:** public PC 1.3.2 / Unity 2021.3. The signatures below were inspected in the installed `ThunderRoad.dll` (MVID `7c3f68b0-6dd9-431c-b97d-3ea4fdbc55ec`); the C# bodies can be compiled without a live scene. Scene behavior still needs a live game check. For another build, confirm `Item.physicBody`, `PhysicBody.velocity`, `Item.Throw`, and `Item.Despawn` with `api_lookup` before using this recipe. Activation here means enabling the GameObject, not triggering an item's spell or custom use action.

1. **Spawn** a fresh, unheld prop away from the player. Select an actual `ItemData` id first:

   ```text
   catalog_search(catalogType:"ItemData", query:"sword")
   game_spawn_item(itemId:"<returned id>", key:"action-demo")
   ```

2. **Activate** the tracked prop and enable its physics:

   ```csharp
   var item = McpRuntime.Current.Get<Item>("action-demo");
   if (item == null) return new { error = "spawn action-demo first" };
   if (item.physicBody == null) return new { error = "item has no physics body" };
   item.gameObject.SetActive(true);
   item.physicBody.isKinematic = false;
   return new { activated = true };
   ```

3. **Throw** that unheld prop along its forward axis, after checking the path is clear:

   ```csharp
   var item = McpRuntime.Current.Get<Item>("action-demo");
   if (item == null) return new { error = "spawn action-demo first" };
   if (item.physicBody == null) return new { error = "item has no physics body" };
   item.gameObject.SetActive(true);
   item.physicBody.isKinematic = false;
   item.physicBody.velocity = item.transform.forward * 6f;
   item.Throw();
   return new { thrown = true, speedMetersPerSecond = 6f };
   ```

   `Item.Throw()` marks the item as thrown and configures flight/collision handling; it does **not** impart the initial velocity. This example supplies velocity explicitly. Do not use it on an item held by a player or NPC.

4. **Replace** without losing the original if spawning fails. Spawn the replacement under a different key first:

   ```text
   game_spawn_item(itemId:"<replacement ItemData id>", key:"action-replacement")
   ```

   Once that succeeds, transfer the stable key and despawn the old item:

   ```csharp
   var runtime = McpRuntime.Current;
   var replacement = runtime.Get<Item>("action-replacement");
   if (replacement == null) return new { error = "spawn action-replacement first" };
   var previous = runtime.Get<Item>("action-demo");
   if (previous != null && previous != replacement) previous.Despawn();
   runtime.Set("action-demo", replacement);
   runtime.Remove("action-replacement");
   return new { replaced = true };
   ```

   The new item keeps its spawn position. Supply explicit `x`/`y`/`z` to the spawn tool when a particular replacement position is needed.

5. **Clean up** only this recipe's objects and routines:

   ```csharp
   var runtime = McpRuntime.Current;
   foreach (var key in new[] { "action-demo", "action-replacement" })
   {
       runtime.StopManagedCoroutine(key);
       var item = runtime.Get<Item>(key);
       if (item != null) item.Despawn();
       runtime.Remove(key);
   }
   return new { cleaned = true };
   ```

   `game_runtime(action:"clear")` drops references and stops managed coroutines; it does not despawn objects. Use `Item.Despawn()` for spawned items instead of destroying their GameObjects directly.


## Troubleshooting

- **`game_launch` succeeded but tools are unavailable:** launch is not readiness. Wait with `game_wait_ready` and check `game_status`. Confirm a bridge config exists, is enabled, and the game was restarted after installing the bridge.
- **Level load appears stuck:** loading takes time. The tool result is a pre-call snapshot, so confirm progress with `game_status`; compare lowercase level ids case-insensitively.
- **A runtime key returns `null`:** the object was destroyed by a level load or despawn, or the key was cleared. Remove the stale key and spawn or resolve the object again.
- **A coroutine keeps running after an eval:** it was started on `McpRuntime`; stop it with `game_runtime(action:"stop", key:"…")`. Never clear it with `StopAllCoroutines` on a game manager.
- **A snippet compiles against the wrong build:** the selected installation may differ from the tracked 1.3.2 package. Check `game_status.status.gameVersion` and re-run `api_type` for the member.
