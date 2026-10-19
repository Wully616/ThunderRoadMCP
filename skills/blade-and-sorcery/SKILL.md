---
name: blade-and-sorcery
description: Drive a live Blade & Sorcery game through ThunderRoadMcp. Use for launching the game, waiting for bridge readiness, loading levels, spawning items, managed coroutines and runtime references, log reading, and safe evaluate_csharp probes against Unity 2021.3 / PC Mono.
---

# Blade & Sorcery

Read the full workflow in [`docs/game-guide.md`](../../docs/game-guide.md) and installed API details in [`docs/api-discovery.md`](../../docs/api-discovery.md). Inside the harness, `game_guide(topic?)` returns the embedded guide by section (default `bootstrap`; topics include `unity`, `runtime`, `spawning`, `levels`, `api`, `logs`, `eval`, `example`). Engine APIs are pinned to Unity 2021.3; see [`references/unity-2021.3.md`](references/unity-2021.3.md).

## Rules

- **Load the embedded guide first.** Before live work, call `game_guide("bootstrap")` and `game_guide("unity")`; use the other topics as needed.
- **Discover before writing code.** `api_search` → `api_type`; `api_decompile` only when behavior matters. Search `ConsoleCommands` first for actions the game already exposes. Never guess ThunderRoad members.
- **Launch is not readiness.** If the game is not running, launch with `arguments: ["-level", "Home"]` unless the user requests another level; pass a requested level directly as startup `-level`. Wait for `game_wait_ready` or verify `game_status` before bridge tools. The bridge loads after main-menu/Home startup; do not rely on `game_load_level` for initial startup.
- **Levels:** `game_load_level(levelId, mode)` is asynchronous and returns a **pre-call** snapshot (`currentLevelBefore`, `levelStateBefore`, `sceneReadyBefore`), not the loaded level. Poll `game_status` until the new `currentLevel` (lowercase, compare case-insensitively) and `sceneReady` match. Find ids with `catalog_search(catalogType="LevelData")`.
- **Readiness waits are structured.** `game_wait_ready` returns `ready`, `timedOut`, `elapsedMs`, and `status`; `ready:false, timedOut:true` is a normal result, not a tool error.
- **Eval is synchronous.** `evaluate_csharp` compiles a statement body returning `object`, runs it on Unity's main thread, and rejects `Task`/top-level `await`. `timeoutMs` cannot stop code that is already running. Eval assemblies stay loaded until the game restarts; prefer few purposeful snippets.
- **Long work goes through `McpRuntime.Current`.** `StartManagedCoroutine("key", Routine())`, `StopManagedCoroutine("key")`, `Set("key", value)`/`Get<T>`/`Remove`. The evaluator imports `ThunderRoadMcp.Bridge`. Inspect and manage with `game_runtime`: `list` reports coroutines/objects, `stop` requires `key`, `clear` stops all managed coroutines and clears references. Never call `StopAllCoroutines` on `Player`, `LevelManager`, creatures, or other game-owned managers.
- **Spawn:** `game_spawn_item(itemId, key: "…")` resolves `Catalog.GetData<ItemData>`, spawns at the player's head plus 1.5 m forward by default, and returns `{key,itemId,position,instanceId,name}`. An unknown id fails with `Catalog item not found: <id>`; `disallowDespawn` defaults to true. Retrieve with `McpRuntime.Current.Get<Item>(key)`; drop stale keys after a level load. Creatures have no dedicated tool: discover the spawn API, then use eval and store the reference.
- **Logs:** `game_logs(source:"bridge", since?)` returns the newest entries when `since` is omitted or `0`; continue with `nextSequence`. File sources work without the bridge.
- **Version stamp:** Steam and Meta Horizon Link are expected to use the same game version; the tracked bridge package targets public PC 1.3.2. Verify signatures against the selected installation and `game_status.status.gameVersion`; do not assume newer Unity or ThunderRoad APIs exist.
- **Mutation is opt-in.** Prefer read-only probes. The bridge has full process and filesystem access and no sandbox.

## Minimal sequence

```text
game_installations
game_launch(arguments: ["-level", "Home"])
game_wait_ready(timeoutMs: 60000)
game_status
catalog_search(catalogType="ItemData", query="sword")
game_spawn_item(itemId: "<id>", key: "demo")
game_runtime(action: "list")
```

For a requested level, use `game_launch(arguments: ["-level", "<level>"])` instead.

For a worked orbit example and the full readiness, level, spawn, and logging details, use [`docs/game-guide.md`](../../docs/game-guide.md). Tool parameters and runtime limits are in [`docs/usage.md`](../../docs/usage.md).
