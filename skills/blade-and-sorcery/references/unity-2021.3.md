# Unity 2021.3 reference

Blade & Sorcery uses Unity **2021.3.38f1** on Windows x64 with Mono/JIT. All engine documentation links below are pinned to the 2021.3 manual and scripting API. Do not substitute newer-version pages: the signatures can differ.

The companion workflow is in [`docs/game-guide.md`](../../../docs/game-guide.md).

## Engine behavior that breaks eval snippets

### Object lifetime and destroyed objects

- A scene load destroys every scene object that is not marked `DontDestroyOnLoad`. A C# reference to a destroyed GameObject or component survives, but Unity reports it as `== null` through the overloaded equality operator.
- `obj?.member` bypasses that overload and can call into a destroyed object. Use `if (obj == null) …` for `UnityEngine.Object` values.
- Do not carry references across `game_load_level`. Remove runtime keys and resolve objects again after the load.
- `DontDestroyOnLoad` only works for root GameObjects, or components on root GameObjects; children are preserved with their transform.

Docs: [Object](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Object.html), [Object.DontDestroyOnLoad](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Object.DontDestroyOnLoad.html)

### Coroutines and frame timing

- `StartCoroutine` needs an active, enabled `MonoBehaviour`. A coroutine is frame-scheduled on the main thread; it is not a thread and does not make blocking work safe.
- `yield return null` resumes on the next frame. `WaitForSeconds` uses scaled time and is affected by `Time.timeScale`; `WaitForSecondsRealtime` ignores time scale; `WaitForEndOfFrame` runs after rendering.
- `Time.deltaTime` is the time of the frame that just finished. Multiply per-frame rates by `deltaTime` once; do not combine it with an additional frame count.
- Use `McpRuntime` to host coroutines started from eval snippets; a coroutine started on a temporary object disappears with that object.

Docs: [Coroutines manual](https://docs.unity3d.com/2021.3/Documentation/Manual/Coroutines.html), [MonoBehaviour.StartCoroutine](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/MonoBehaviour.StartCoroutine.html), [WaitForSeconds](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/WaitForSeconds.html), [WaitForSecondsRealtime](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/WaitForSecondsRealtime.html), [WaitForEndOfFrame](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/WaitForEndOfFrame.html), [Time.deltaTime](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Time-deltaTime.html), [Time.timeScale](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Time-timeScale.html)

### Physics, Rigidbody, and transforms

- Physics runs in `FixedUpdate`, on the physics clock (`Time.fixedDeltaTime`), not once per rendered frame. Reads like `GetComponent<Rigidbody>()` are fine anywhere, but forces and `MovePosition` belong in `FixedUpdate`.
- A kinematic Rigidbody ignores forces, gravity, and collision response. Set `isKinematic` deliberately before driving it with `MovePosition`/`MoveRotation`.
- Writing `transform.position` on a non-kinematic body fights the simulation and can produce jitter; use forces, `velocity`, or `MovePosition` instead.
- `Rigidbody.velocity` is the property name in 2021.3. Later Unity versions renamed it; do not copy newer snippets.

Docs: [Rigidbody](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Rigidbody.html), [Rigidbody.isKinematic](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Rigidbody-isKinematic.html), [Rigidbody.MovePosition](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Rigidbody.MovePosition.html), [Rigidbody.velocity](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Rigidbody-velocity.html), [Transform.position](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Transform-position.html), [MonoBehaviour.FixedUpdate](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/MonoBehaviour.FixedUpdate.html)

### Scene loading and the game's level flow

- Unity's `SceneManager.LoadScene` is not how Blade & Sorcery changes levels. It skips the game's level, mode, and spawn setup. Use `game_load_level` and wait for `currentLevel`/`sceneReady`.
- `LevelManager.GetCurrentLevel()` reports lowercase ids; compare case-insensitively and expect the previous value during a transition.
- Load-order assumptions from the Unity execution-order page do not describe the game's own initialization; use the readiness fields instead.

Docs: [SceneManager.LoadScene](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/SceneManagement.SceneManager.LoadScene.html), [Execution order manual](https://docs.unity3d.com/2021.3/Documentation/Manual/ExecutionOrder.html)

### Logging

- `Debug.Log`/`LogWarning`/`LogError`/`LogException` go to `Player.log`. Stack traces are captured for errors and exceptions.
- The bridge ring shows at most three entries per response and retains 512; continue with `nextSequence`.
- `Application.logMessageReceived` runs on the main thread; `logMessageReceivedThreaded` receives messages from any thread.

Docs: [Debug.Log](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Debug.Log.html), [Application.logMessageReceived](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Application-logMessageReceived.html), [Log files manual](https://docs.unity3d.com/2021.3/Documentation/Manual/LogFiles.html)

### Threading and serialization

- Unity APIs are main-thread only. A bridge eval body already runs on the main thread, so a background worker must marshal back before touching `Transform`, `Rigidbody`, `GameObject`, or other engine objects.
- Returned values are serialized to JSON. Unity structs (`Vector3`, `Quaternion`, `Color`) serialize as public fields; a returned `UnityEngine.Object` is detached and serialized, not sent live. Return small `new { … }` shapes of primitives instead of large object graphs.

## APIs that do not exist in 2021.3

Check these common newer-version APIs before using them:

| Newer API | 2021.3 alternative |
| --- | --- |
| `Awaitable` (2023.1+) | coroutine or managed coroutine |
| `Object.FindObjectsByType` / `FindFirstObjectByType` / `FindAnyObjectByType` (2022.2+) | `Object.FindObjectsOfType` / `FindObjectOfType` |
| `Rigidbody.linearVelocity` / `linearDamping` (Unity 6 rename) | `Rigidbody.velocity` / `drag` |

The installed game assemblies are the final authority: run `api_search` and `api_type` for ThunderRoad types, and `api_decompile` when implementation behavior matters.

## Game-version drift

The tracked bridge package targets public PC **1.3.2**. Steam and Meta Horizon Link are expected to use the same game version. ThunderRoad API members, level data, and item ids can change between game releases. Always confirm `game_status.status.gameVersion` and re-discover declarations for the selected installation. Do not assume a member from one build exists in another, and do not hard-code holder, creature, level, or item ids.

## Quick checklist

1. `game_status` shows the expected `gameVersion` and `unityVersion`.
2. The declaration exists in the installed assemblies (`api_type`).
3. The probe is read-only unless mutation is the goal.
4. Long-running work is anchored on `McpRuntime`, and the key is recorded.
5. Logs are read with `nextSequence`, not guessed offsets.
