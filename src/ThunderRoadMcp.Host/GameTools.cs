using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class GameTools
{
    [McpServerTool(Name = "game_status", Title = "Check game and bridge status", ReadOnly = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Report game process, bridge connectivity, and in-game readiness. Status is compact by default; set includeAssemblies=true to include loaded assembly inventory and MVIDs.")]
    public static Task<CallToolResult> GameStatus(GameProcess gameProcess,
        [Description("Include the loaded assembly inventory and MVIDs in the nested status payload.")] bool includeAssemblies = false,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => gameProcess.StatusAsync(includeAssemblies, cancellationToken));

    [McpServerTool(Name = "game_installations", Title = "Find game installations", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("List detected Steam and Meta Horizon Link installations without launching or changing the game. If multiple candidates exist, select one by setting executablePath in launch.json.")]
    public static Task<CallToolResult> GameInstallations(CancellationToken cancellationToken) =>
        ToolInvocation.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GameInstallConfiguration.ListInstallationsJson());
        });

    [McpServerTool(Name = "game_launch", Title = "Launch Blade & Sorcery", OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Start the selected game installation and return its PID without waiting for bridge readiness. On cold start, explicitly pass arguments [\"-level\", \"Home\"] unless the user requested another level; pass that target directly as the startup -level argument. The bridge initializes only after main-menu/Home startup, so use game_wait_ready or game_status before bridge-backed calls. Optional raw arguments override the launch.json array and are preserved by game_restart; every argument must be non-null.")]
    public static Task<CallToolResult> GameLaunch(GameProcess gameProcess,
        [Description("Optional raw process arguments, at most 64 non-null strings of 4096 characters each; omit to use launch.json.")] string[]? arguments = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => gameProcess.LaunchAsync(arguments, cancellationToken));

    [McpServerTool(Name = "game_wait_ready", Title = "Wait for game and bridge readiness", ReadOnly = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Poll until the bridge is connected and ready, the scene is loaded, and optionally the requested level is current. Returns ready, timedOut, elapsedMs, and the last status; a timeout is a normal result, not a tool error. Sends MCP progress notifications about every 5 s when the client supplies a progressToken; if the client's own request timeout is shorter than timeoutMs, use a smaller timeoutMs and call again.")]
    public static Task<CallToolResult> GameWaitReady(GameProcess gameProcess,
        IProgress<ProgressNotificationValue> progress,
        [Description("Optional level id to wait for; compared case-insensitively.")] string? level = null,
        [Description("Maximum wait in milliseconds, from 1 to 300000.")] int timeoutMs = 60000,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => gameProcess.WaitForReadyAsync(level, timeoutMs, cancellationToken, progress));

    [McpServerTool(Name = "game_load_level", Title = "Load a Blade & Sorcery level", Destructive = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Requires a ready bridge. Do not use for initial startup; use game_launch's -level argument. By default returns the pre-request state immediately. waitUntilReady=true applies a bounded total deadline from before the load request through readiness; an unacknowledged timeout omits pre-request state. With waitUntilReady=true it sends MCP progress notifications about every 5 s when the client supplies a progressToken; if the client's own request timeout is shorter than timeoutMs, use a smaller timeoutMs and continue with game_wait_ready instead of repeating the load.")]
    public static Task<CallToolResult> GameLoadLevel(GameProcess gameProcess, BridgeClient bridgeClient,
        IProgress<ProgressNotificationValue> progress,
        [Description("Catalog LevelData id, for example Arena or Home.")] string levelId,
        [Description("Game mode id, for example Sandbox.")] string mode,
        [Description("Wait for the requested level to become ready after the load request.")] bool waitUntilReady = false,
        [Description("Total time from before the load request through readiness, from 1 to 300000 milliseconds.")] int timeoutMs = 60000,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(async () =>
        {
            var id = RequireText(levelId, nameof(levelId), 256);
            var gameMode = RequireText(mode, nameof(mode), 256);
            if (!waitUntilReady)
                return await bridgeClient.RequestAsync("load_level", new { levelId = id, mode = gameMode }, cancellationToken);
            if (timeoutMs is < 1 or > 300_000)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be between 1 and 300000.");

            var elapsed = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            string loadResult;
            try
            {
                loadResult = await bridgeClient.RequestAsync("load_level", new { levelId = id, mode = gameMode }, deadline.Token);
            }
            catch (Exception exception) when (
                (exception is OperationCanceledException or TimeoutException) &&
                !cancellationToken.IsCancellationRequested)
            {
                return JsonSerializer.Serialize(new
                {
                    levelId = id,
                    mode = gameMode,
                    requestAcknowledged = false,
                    completed = false,
                    timedOut = true,
                    elapsedMs = elapsed.ElapsedMilliseconds,
                });
            }

            bool requireTransition;
            using (var loadDocument = JsonDocument.Parse(loadResult))
            {
                var loadRoot = loadDocument.RootElement;
                requireTransition = loadRoot.TryGetProperty("currentLevelBefore", out var before) &&
                    before.ValueKind == JsonValueKind.String &&
                    string.Equals(before.GetString(), id, StringComparison.OrdinalIgnoreCase) &&
                    loadRoot.TryGetProperty("sceneReadyBefore", out var sceneReadyBefore) &&
                    sceneReadyBefore.ValueKind == JsonValueKind.True;
            }

            var readiness = await gameProcess.WaitForReadyAsync(
                id, timeoutMs, elapsed, cancellationToken, deadline.Token, requireTransition, progress);
            using var readinessDocument = JsonDocument.Parse(readiness);
            var readinessRoot = readinessDocument.RootElement;
            var result = JsonNode.Parse(loadResult)!.AsObject();
            result["requestAcknowledged"] = true;
            result["completed"] = readinessRoot.GetProperty("ready").GetBoolean();
            result["timedOut"] = readinessRoot.GetProperty("timedOut").GetBoolean();
            result["elapsedMs"] = readinessRoot.GetProperty("elapsedMs").GetInt64();
            result["status"] = readinessRoot.GetProperty("status").ValueKind == JsonValueKind.Null
                ? null
                : JsonNode.Parse(readinessRoot.GetProperty("status").GetRawText());
            return result.ToJsonString();
        });

    [McpServerTool(Name = "game_spawn_item", Title = "Spawn a catalog item", OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Resolve an ItemData catalog id, spawn it in front of the player or at explicit world coordinates, and store the Item under a runtime key retrievable with McpRuntime.Current.Get<Item>(key). Returns key, itemId, actual position, instanceId, and name.")]
    public static Task<CallToolResult> GameSpawnItem(BridgeClient bridgeClient,
        [Description("Catalog ItemData id.")] string itemId,
        [Description("World-space X; supply together with y and z, or omit all three.")] double? x = null,
        [Description("World-space Y; supply together with x and z, or omit all three.")] double? y = null,
        [Description("World-space Z; supply together with x and y, or omit all three.")] double? z = null,
        [Description("Runtime key for later retrieval; generated when omitted.")] string? key = null,
        [Description("Exempt the spawned item from the game's despawn cleanup.")] bool disallowDespawn = true,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() =>
        {
            var id = RequireText(itemId, nameof(itemId), 256);
            ValidatePosition(x, y, z);
            var runtimeKey = key is null ? null : RequireText(key, nameof(key), 128);
            return bridgeClient.RequestAsync("spawn_item", new { itemId = id, x, y, z, key = runtimeKey, disallowDespawn }, cancellationToken);
        });

    [McpServerTool(Name = "game_spawn_creature", Title = "Spawn a catalog creature", OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Resolve a CreatureData catalog id, spawn it in front of the player facing them (or at explicit coordinates and rotationY degrees), and store the Creature under a runtime key retrievable with McpRuntime.Current.Get<Creature>(key). Cleanup: Creature.Despawn() then McpRuntime.Current.Remove(key). Returns key, creatureId, actual position, rotationY, instanceId, and name.")]
    public static Task<CallToolResult> GameSpawnCreature(BridgeClient bridgeClient,
        [Description("Catalog CreatureData id.")] string creatureId,
        [Description("World-space X; supply together with y and z, or omit all three.")] double? x = null,
        [Description("World-space Y; supply together with x and z, or omit all three.")] double? y = null,
        [Description("World-space Z; supply together with x and y, or omit all three.")] double? z = null,
        [Description("World-space yaw in degrees; defaults to facing the player when x, y, and z are omitted, otherwise 0.")] double? rotationY = null,
        [Description("Runtime key for later retrieval; generated when omitted.")] string? key = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() =>
        {
            var id = RequireText(creatureId, nameof(creatureId), 256);
            ValidatePosition(x, y, z);
            ValidateFinite(nameof(rotationY), rotationY);
            var runtimeKey = key is null ? null : RequireText(key, nameof(key), 128);
            return RequestBridgeFeatureAsync(bridgeClient, "spawn_creature", new { creatureId = id, x, y, z, rotationY, key = runtimeKey }, "game_spawn_creature", cancellationToken);
        });

    [McpServerTool(Name = "game_spawn_effect", Title = "Spawn a catalog effect", OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Spawn and play a catalog EffectData at the player view or explicit coordinates; the EffectInstance is stored under key. Stop with McpRuntime.Current.Get<EffectInstance>(key).End() then Remove(key). Returns key, effectId, and position.")]
    public static Task<CallToolResult> GameSpawnEffect(BridgeClient bridgeClient,
        [Description("Catalog EffectData id.")] string effectId,
        [Description("World-space X; supply together with y and z, or omit all three.")] double? x = null,
        [Description("World-space Y; supply together with x and z, or omit all three.")] double? y = null,
        [Description("World-space Z; supply together with x and y, or omit all three.")] double? z = null,
        [Description("Runtime key for later retrieval; generated when omitted.")] string? key = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() =>
        {
            var id = RequireText(effectId, nameof(effectId), 256);
            ValidatePosition(x, y, z);
            var runtimeKey = key is null ? null : RequireText(key, nameof(key), 128);
            return RequestBridgeFeatureAsync(bridgeClient, "spawn_effect", new { effectId = id, x, y, z, key = runtimeKey }, "game_spawn_effect", cancellationToken);
        });

    [McpServerTool(Name = "game_find_objects", Title = "Find loaded scene components", ReadOnly = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Find loaded scene components by Unity component type (simple or full name, for example LightningStriker, ThunderRoad.Creature, or Transform with name to find GameObjects). Returns instanceId, hierarchy path, scene, active/enabled state, and position; includeFields adds public instance field values read without invoking game code. Use api_search to find type names; use evaluate_csharp only for actions.")]
    public static Task<CallToolResult> GameFindObjects(BridgeClient bridgeClient,
        [Description("UnityEngine.Component type to find: a simple name such as LightningStriker or a full name such as ThunderRoad.Creature.")] string componentType,
        [Description("Optional case-insensitive substring filter on the GameObject name, at most 128 characters.")] string? name = null,
        [Description("Include components on inactive GameObjects.")] bool includeInactive = false,
        [Description("Add up to 24 public instance fields per component with bounded value summaries; limit is then at most 10.")] bool includeFields = false,
        [Description("Number of matches to skip, from 0 to 100000; results are ordered by instance id.")] int offset = 0,
        [Description("Maximum results, from 1 to 50, or 1 to 10 when includeFields is true.")] int limit = 25,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() =>
        {
            var type = RequireText(componentType, nameof(componentType), 256);
            var nameFilter = name is null ? null : RequireText(name, nameof(name), 128);
            if (offset is < 0 or > 100_000)
                throw new ArgumentOutOfRangeException(nameof(offset), "offset must be between 0 and 100000.");
            var maxLimit = includeFields ? 10 : 50;
            if (limit < 1 || limit > maxLimit)
                throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be between 1 and {maxLimit}{(includeFields ? " when includeFields is true" : "")}.");
            return RequestBridgeFeatureAsync(bridgeClient, "find_objects", new { componentType = type, name = nameFilter, includeInactive, includeFields, offset, limit }, "game_find_objects", cancellationToken);
        });

    [McpServerTool(Name = "game_runtime", Title = "Manage bridge-owned runtime state", Destructive = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("List bridge-owned coroutines and safe value summaries, inspect one stored runtime value without traversing it, stop one managed coroutine, or clear all bridge-owned runtime state. inspect and stop require key; list reports full counts and truncation flags when the 64 KiB response budget omits entries. clear stops all managed coroutines and clears stored objects. Game-owned coroutines are never touched. list includes each managed coroutine's routine type, UTC start time, and step count, plus the last 16 coroutine failures with exception text.")]
    public static Task<CallToolResult> GameRuntime(BridgeClient bridgeClient,
        [Description("Action: list, inspect, stop, or clear.")] string action,
        [Description("Runtime value key for inspect or managed coroutine key for stop.")] string? key = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(async () =>
        {
            var normalized = action?.Trim().ToLowerInvariant() ?? "";
            try
            {
                return await (normalized switch
                {
                    "list" or "clear" => bridgeClient.RequestAsync("runtime", new { action = normalized }, cancellationToken),
                    "inspect" or "stop" => bridgeClient.RequestAsync("runtime", new { action = normalized, key = RequireText(key ?? "", nameof(key), 128) }, cancellationToken),
                    _ => throw new ArgumentException("action must be list, inspect, stop, or clear."),
                });
            }
            catch (InvalidOperationException exception) when (normalized == "inspect" &&
                FirstLineIs(exception.Message, "runtime.action must be list, stop, or clear."))
            {
                throw new InvalidOperationException("The installed bridge does not support runtime inspection. Redeploy the updated ThunderRoadMcp bridge and restart the game; list, stop, and clear remain available.", exception);
            }
        });

    private static bool FirstLineIs(string error, string expected)
    {
        var firstLine = error.AsSpan();
        var newline = firstLine.IndexOfAny('\r', '\n');
        if (newline >= 0) firstLine = firstLine[..newline];
        return firstLine.SequenceEqual(expected) ||
            firstLine.SequenceEqual("System.IO.InvalidDataException: " + expected);
    }

    // An older bridge answers an unknown method with "Unknown method: <name>"; turn that into redeploy guidance.
    private static async Task<string> RequestBridgeFeatureAsync(BridgeClient client, string method, object parameters,
        string toolName, CancellationToken cancellationToken)
    {
        try
        {
            return await client.RequestAsync(method, parameters, cancellationToken);
        }
        catch (InvalidOperationException exception) when (FirstLineIs(exception.Message, "Unknown method: " + method))
        {
            throw new InvalidOperationException($"The installed bridge does not support {toolName}. Redeploy the updated ThunderRoadMcp bridge and restart the game.", exception);
        }
    }

    private static void ValidatePosition(double? x, double? y, double? z)
    {
        if ((x is null) != (y is null) || (y is null) != (z is null))
            throw new ArgumentException("x, y, and z must be supplied together.");
        ValidateFinite(nameof(x), x);
        ValidateFinite(nameof(y), y);
        ValidateFinite(nameof(z), z);
    }

    private static void ValidateFinite(string name, double? value)
    {
        if (value is { } number && (!double.IsFinite(number) || Math.Abs(number) > 1_000_000))
            throw new ArgumentException($"{name} must be finite and within ±1000000.");
    }

    private static string RequireText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException($"{name} must be a non-empty string of at most {maxLength} characters.");
        return value;
    }

    [McpServerTool(Name = "game_install_mod", Title = "Install the bridge mod", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Install a ThunderRoadMcp package into the selected game's Mods directory. Refuses to overwrite an existing installation; the game must be restarted manually.")]
    public static Task<CallToolResult> GameInstallMod(
        [Description("Absolute path to the ThunderRoadMcp ZIP package.")] string packagePath,
        CancellationToken cancellationToken) =>
        ToolInvocation.Run(async () =>
        {
            var options = await GameInstallConfiguration.ReadLaunchOptionsAsync(cancellationToken);
            return await BridgeModInstaller.InstallAsync(packagePath, options, cancellationToken);
        });

    [McpServerTool(Name = "game_stop", Title = "Stop the host-launched game", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Gracefully stop only the game process launched by this host; set force=true to terminate it if graceful shutdown fails.")]
    public static Task<CallToolResult> GameStop(GameProcess gameProcess,
        [Description("Force termination instead of graceful shutdown.")] bool force = false,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.RunText(() => gameProcess.StopAsync(force, cancellationToken));

    [McpServerTool(Name = "game_restart", Title = "Restart the host-launched game", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Restart only the game process launched by this host; set force=true to terminate the old process if graceful shutdown fails.")]
    public static Task<CallToolResult> GameRestart(GameProcess gameProcess,
        [Description("Force termination of the old process instead of graceful shutdown.")] bool force = false,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => gameProcess.RestartAsync(force, cancellationToken));

    [McpServerTool(Name = "evaluate_csharp", Title = "Evaluate trusted C# in the game", Destructive = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Compile and run trusted arbitrary C# in the game, without a sandbox and with full game-process and filesystem access. Runs synchronously on Unity's main thread; timeout cannot stop code already running. Error results report executionOutcome: not_started, completed_result_unavailable, or unknown; never automatically retry an unknown outcome.")]
    public static Task<CallToolResult> EvaluateCSharp(BridgeClient bridgeClient,
        [Description("Synchronous C# statement body that returns a JSON-serializable value.")] string code,
        [Description("Evaluation deadline in milliseconds, from 1 to 30000; a running snippet cannot be interrupted.")] int timeoutMs = 30000,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.RunEvaluation(() =>
        {
            if (timeoutMs is < 1 or > 30_000)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be between 1 and 30000; for longer work start a managed coroutine with McpRuntime.Current.StartManagedCoroutine and poll game_runtime(action:\"list\").");
            return bridgeClient.RequestAsync("evaluate", new { code, timeoutMs }, cancellationToken);
        });

    [McpServerTool(Name = "validate_csharp", Title = "Validate C# without executing", ReadOnly = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Compile a C# statement body against the game's loaded references without invoking it or loading the emitted evaluation assembly. Returns valid plus bounded Roslyn diagnostics; compilation success does not prove runtime behavior.")]
    public static Task<CallToolResult> ValidateCSharp(BridgeClient bridgeClient,
        [Description("C# statement body to compile; never executed by this tool.")] string code,
        [Description("Compilation deadline in milliseconds, from 1 to 30000.")] int timeoutMs = 30000,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() =>
        {
            if (timeoutMs is < 1 or > 30_000)
                throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be between 1 and 30000.");
            return RequestBridgeFeatureAsync(bridgeClient, "validate", new { code, timeoutMs }, "validate_csharp", cancellationToken);
        });

    [McpServerTool(Name = "game_logs", Title = "Read Unity game logs", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Read bounded pages from the current Player.log, previous Player-prev.log, or the authenticated in-game bridge log ring. File pagination uses beforeByteOffset plus snapshotId; bridge pagination uses since, where 0 returns the newest entries and nextSequence advances forward.")]
    public static Task<CallToolResult> GameLogs(BridgeClient bridgeClient,
        [Description("Log source: current, previous, or bridge.")] string source = "current",
        [Description("Number of lines to return for file sources, from 1 to 2000.")] int tailLines = 200,
        [Description("For bridge source, return entries after this sequence number; 0 returns the newest entries.")] long since = 0,
        [Description("For file sources, continue before this byte offset.")] long? beforeByteOffset = null,
        [Description("Snapshot identifier returned by the previous file page.")] string? snapshotId = null,
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() => source switch
        {
            "current" => Task.FromResult(PlayerLogReader.Read("current", "Player.log", tailLines, beforeByteOffset, snapshotId)),
            "previous" => Task.FromResult(PlayerLogReader.Read("previous", "Player-prev.log", tailLines, beforeByteOffset, snapshotId)),
            "bridge" when beforeByteOffset is null && snapshotId is null => bridgeClient.RequestAsync("logs", new { since }, cancellationToken),
            "bridge" => throw new ArgumentException("File pagination parameters apply only to file sources."),
            _ => throw new ArgumentException("source must be current, previous, or bridge.")
        });
}
