using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ThunderRoad;
using UnityEngine;

namespace ThunderRoadMcp.Bridge
{
    internal sealed class BridgeRequest
    {
        public string id { get; set; }
        public string token { get; set; }
        public string method { get; set; }
        public JObject parameters { get; set; }
        public bool diagnostics { get; set; }
    }
    internal sealed class BridgeJsonResult
    {
        public readonly string Json;
        public BridgeJsonResult(string json) { Json = json; }
    }
    internal sealed class BridgeEvaluationException : Exception
    {
        public readonly string errorCode;
        public readonly string executionOutcome;
        public readonly object diagnostics;
        public BridgeEvaluationException(string errorCode, string message, string executionOutcome, object diagnostics = null,
            Exception innerException = null) : base(message, innerException)
        {
            this.errorCode = errorCode;
            this.executionOutcome = executionOutcome;
            this.diagnostics = diagnostics;
        }
    }
    internal sealed class BridgeResultDepthException : Exception
    {
        public BridgeResultDepthException() : base("Evaluation result exceeds the maximum JSON nesting depth of 62.") { }
    }
    internal sealed class LoadedAssemblyStatus
    {
        public object[] loadedAssemblies { get; set; }
        public int loadedAssemblyCount { get; set; }
        public bool loadedAssembliesTruncated { get; set; }
    }
    internal sealed class BridgeRequestHandler
    {
        internal const int MaxLineBytes = 64 * 1024;
        private const int MaxResultDepth = 62;
        private const int MaxLevelIdLength = 256;
        private const int MaxModeLength = 256;
        private const int MaxCatalogIdLength = 256;
        private const int MaxComponentTypeLength = 256;
        private const int MaxObjectNameFilterLength = 128;
        private const int MaxFoundTextLength = 128;
        private const int MaxFoundFieldNameLength = 64;
        private const int MaxFoundFields = 24;
        private const int MaxFoundPathLevels = 32;
        private const int MaxFoundPathLength = 512;
        private const float ItemSpawnDistance = 1.5f;
        private const float CreatureSpawnDistance = 2f;
        private const float EffectSpawnDistance = 1.5f;
        private const float SpawnCompletionTimeoutSeconds = 8f;
        private const float CreatureSpawnCompletionTimeoutSeconds = 20f;
        private static readonly TimeSpan SpawnResultTimeout = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan CreatureSpawnResultTimeout = TimeSpan.FromSeconds(25);
        private static readonly ConcurrentDictionary<string, Type> ComponentTypes = new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);
        private readonly BridgeConfig config;
        private readonly SemaphoreSlim compiler = new SemaphoreSlim(1, 1);

        public BridgeRequestHandler(BridgeConfig config) { this.config = config; }
        internal static string SerializeResult(object result)
        {
            if (result is Task)
                throw new NotSupportedException("Async evaluation is unsupported; return a completed synchronous value instead.");
            var output = new BoundedStringWriter(MaxLineBytes - 512);
            using (var writer = new JsonTextWriter(output))
            {
                writer.CloseOutput = false;
                JsonSerializer.Create(new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                }).Serialize(writer, result);
                writer.Flush();
            }
            string json = output.ToString();
            EnsureResultDepth(json);
            return json;
        }
        private static void EnsureResultDepth(string json)
        {
            int depth = 0;
            using (var textReader = new JsonTextReader(new StringReader(json)) { MaxDepth = null })
            {
                while (textReader.Read())
                {
                    if (textReader.TokenType == JsonToken.StartArray || textReader.TokenType == JsonToken.StartObject)
                    {
                        if (++depth > MaxResultDepth)
                            throw new BridgeResultDepthException();
                    }
                    else if (textReader.TokenType == JsonToken.EndArray || textReader.TokenType == JsonToken.EndObject)
                    {
                        depth--;
                    }
                }
            }
        }
        internal async Task<object> DispatchAsync(BridgeRequest request, CancellationToken token, BridgeRequestTimings timings)
        {
            JObject args = request.parameters ?? new JObject();
            switch (request.method)
            {
                case "status":
                    bool includeAssemblies = GetBoolean(args, "includeAssemblies", false);
                    return new BridgeJsonResult(await AwaitWithCancellation(
                        MainThreadQueue.Enqueue(() => SerializeResult(CreateStatus(includeAssemblies)), token), token).ConfigureAwait(false));
                case "logs":
                    long since = GetInteger(args, "since", 0);
                    if (since < 0) throw new InvalidDataException("logs.since must be non-negative.");
                    return BridgeLogs.ReadSince(since);
                case "evaluate":
                    try
                    {
                        string code = (string)args["code"];
                        if (string.IsNullOrWhiteSpace(code) || Encoding.UTF8.GetByteCount(code) > MaxLineBytes)
                            throw new InvalidDataException("evaluate.code must be non-empty and at most 64 KiB UTF-8.");
                        long timeout = GetInteger(args, "timeoutMs", 10000);
                        if (timeout < 1 || timeout > 30000)
                            throw new InvalidDataException("evaluate.timeoutMs must be between 1 and 30000.");
                        return new BridgeJsonResult(await Evaluate(code, TimeSpan.FromMilliseconds(timeout), token, timings).ConfigureAwait(false));
                    }
                    catch (BridgeEvaluationException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        throw new BridgeEvaluationException("evaluation.not_started", exception.Message,
                            "not_started", null, exception);
                    }
                case "runtime":
                    return await RuntimeAsync(args, token).ConfigureAwait(false);
                case "load_level":
                    string levelId = GetRequiredString(args, "levelId", MaxLevelIdLength);
                    string mode = GetRequiredString(args, "mode", MaxModeLength);
                    return new BridgeJsonResult(await AwaitWithCancellation(
                        MainThreadQueue.Enqueue(() => SerializeResult(LoadLevel(levelId, mode)), token), token).ConfigureAwait(false));
                case "spawn_item":
                    return await SpawnItemAsync(args, token).ConfigureAwait(false);
                case "spawn_creature":
                    return await SpawnCreatureAsync(args, token).ConfigureAwait(false);
                case "spawn_effect":
                    return await SpawnEffectAsync(args, token).ConfigureAwait(false);
                case "find_objects":
                    return await FindObjectsAsync(args, token).ConfigureAwait(false);
                default:
                    throw new InvalidDataException("Unknown method: " + request.method);
            }
        }
        private static async Task<object> RuntimeAsync(JObject args, CancellationToken token)
        {
            string action = GetRequiredString(args, "action", 16);
            switch (action)
            {
                case "list":
                    return new BridgeJsonResult(await AwaitWithCancellation(
                        MainThreadQueue.Enqueue(() => SerializeResult(Runtime().Describe()), token), token).ConfigureAwait(false));
                case "inspect":
                    string inspectKey = GetRequiredString(args, "key", McpRuntime.MaxKeyLength);
                    return new BridgeJsonResult(await AwaitWithCancellation(
                        MainThreadQueue.Enqueue(() => SerializeResult(Runtime().Inspect(inspectKey)), token), token).ConfigureAwait(false));
                case "stop":
                    string key = GetRequiredString(args, "key", McpRuntime.MaxKeyLength);
                    return new BridgeJsonResult(await AwaitWithCancellation(
                        MainThreadQueue.Enqueue(() => SerializeResult(new
                        {
                            action,
                            key,
                            stopped = Runtime().StopManagedCoroutine(key)
                        }), token), token).ConfigureAwait(false));
                case "clear":
                    return new BridgeJsonResult(await AwaitWithCancellation(
                        MainThreadQueue.Enqueue(() =>
                        {
                            Runtime().Clear();
                            return SerializeResult(new { action, cleared = true });
                        }, token), token).ConfigureAwait(false));
                default:
                    throw new InvalidDataException("runtime.action must be list, inspect, stop, or clear.");
            }
        }

        private static McpRuntime Runtime()
        {
            return McpRuntime.Current ?? throw new InvalidOperationException("McpRuntime is not available.");
        }

        private static object LoadLevel(string levelId, string mode)
        {
            ThunderRoad.LevelManager.State state = ThunderRoad.LevelManager.state;
            string currentLevel = ThunderRoad.LevelManager.GetCurrentLevel();
            ThunderRoad.LevelManager.LoadLevel(levelId, mode);
            return new
            {
                levelId,
                mode,
                currentLevelBefore = currentLevel,
                levelStateBefore = state.ToString(),
                sceneReadyBefore = state == ThunderRoad.LevelManager.State.Loaded
            };
        }

        private static Task<object> SpawnItemAsync(JObject args, CancellationToken token)
        {
            string itemId = GetRequiredString(args, "itemId", MaxCatalogIdLength);
            string requestedKey = GetOptionalString(args, "key", McpRuntime.MaxKeyLength);
            bool disallowDespawn = GetBoolean(args, "disallowDespawn", true);
            Vector3? position = ReadOptionalPosition(args, "spawn_item");
            return RunSpawnAsync("spawn_item", SpawnResultTimeout, (abort, completion) =>
            {
                McpRuntime runtime = Runtime();
                ItemData data = Catalog.GetData<ItemData>(itemId);
                if (data == null) throw new InvalidDataException("Catalog item not found: " + itemId);
                Vector3 spawnPosition = position ?? DefaultSpawnPosition(ItemSpawnDistance);
                string key = requestedKey ?? GenerateKey(runtime, "item-");
                return SpawnItemRoutine(runtime, data, itemId, spawnPosition, key, disallowDespawn, abort, completion);
            }, token);
        }

        private static Task<object> SpawnCreatureAsync(JObject args, CancellationToken token)
        {
            string creatureId = GetRequiredString(args, "creatureId", MaxCatalogIdLength);
            string requestedKey = GetOptionalString(args, "key", McpRuntime.MaxKeyLength);
            Vector3? position = ReadOptionalPosition(args, "spawn_creature");
            bool hasRotation = TryGetNumber(args, "rotationY", out double requestedRotation);
            return RunSpawnAsync("spawn_creature", CreatureSpawnResultTimeout, (abort, completion) =>
            {
                McpRuntime runtime = Runtime();
                CreatureData data = Catalog.GetData<CreatureData>(creatureId);
                if (data == null) throw new InvalidDataException("Catalog creature not found: " + creatureId);
                Vector3 spawnPosition = position ?? DefaultSpawnPosition(CreatureSpawnDistance);
                // A defaulted position is in front of the player, so default the yaw to facing them.
                float rotationY = hasRotation ? (float)requestedRotation
                    : position.HasValue ? 0f
                    : Player.local.head.transform.eulerAngles.y + 180f;
                string key = requestedKey ?? GenerateKey(runtime, "creature-");
                return SpawnCreatureRoutine(runtime, data, creatureId, spawnPosition, rotationY, key, abort, completion);
            }, token);
        }

        private static async Task<object> SpawnEffectAsync(JObject args, CancellationToken token)
        {
            string effectId = GetRequiredString(args, "effectId", MaxCatalogIdLength);
            string requestedKey = GetOptionalString(args, "key", McpRuntime.MaxKeyLength);
            Vector3? position = ReadOptionalPosition(args, "spawn_effect");
            return new BridgeJsonResult(await AwaitWithCancellation(
                MainThreadQueue.Enqueue(() => SpawnEffect(effectId, requestedKey, position, token).Json, token), token).ConfigureAwait(false));
        }

        private static BridgeJsonResult SpawnEffect(string effectId, string requestedKey, Vector3? position, CancellationToken token)
        {
            McpRuntime runtime = Runtime();
            EffectData data = Catalog.GetData<EffectData>(effectId);
            if (data == null) throw new InvalidDataException("Catalog effect not found: " + effectId);
            Vector3 spawnPosition = position ?? DefaultSpawnPosition(EffectSpawnDistance);
            string key = requestedKey ?? GenerateKey(runtime, "effect-");
            token.ThrowIfCancellationRequested();

            EffectInstance instance = null;
            bool committed = false;
            try
            {
                instance = data.Spawn(spawnPosition, Quaternion.identity, null, null, true, null, false, 1f, 1f, new Type[0]);
                if (instance == null) throw new InvalidOperationException("EffectData.Spawn returned no EffectInstance.");
                token.ThrowIfCancellationRequested();
                instance.Play();
                token.ThrowIfCancellationRequested();

                var result = new BridgeJsonResult(SerializeResult(new
                {
                    key,
                    effectId,
                    position = new { x = spawnPosition.x, y = spawnPosition.y, z = spawnPosition.z }
                }));
                token.ThrowIfCancellationRequested();
                runtime.Set(key, instance);
                committed = true;
                return result;
            }
            catch
            {
                RollbackSpawnIfUncommitted(instance, committed, EndEffectSafely);
                throw;
            }
        }

        // Runs a spawn on the main thread inside a bridge-managed coroutine and waits for its completion source.
        private static async Task<object> RunSpawnAsync(string method, TimeSpan resultTimeout,
            Func<CancellationToken, TaskCompletionSource<object>, IEnumerator> createRoutine, CancellationToken token)
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            string coroutineKey = "spawn-" + Guid.NewGuid().ToString("N");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(resultTimeout);
                try
                {
                    await AwaitWithCancellation(MainThreadQueue.Enqueue(() =>
                    {
                        if (deadline.IsCancellationRequested) return string.Empty;
                        try
                        {
                            Runtime().StartManagedCoroutine(coroutineKey, createRoutine(deadline.Token, completion));
                        }
                        catch (Exception exception)
                        {
                            completion.TrySetException(exception);
                        }
                        return string.Empty;
                    }, deadline.Token), deadline.Token).ConfigureAwait(false);
                    return await AwaitWithCancellation(completion.Task, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException(method + " timed out after " + resultTimeout.TotalSeconds.ToString("0") + " seconds.");
                }
            }
        }

        private static Vector3? ReadOptionalPosition(JObject args, string method)
        {
            bool hasX = TryGetNumber(args, "x", out double x);
            bool hasY = TryGetNumber(args, "y", out double y);
            bool hasZ = TryGetNumber(args, "z", out double z);
            if (hasX != hasY || hasY != hasZ)
                throw new InvalidDataException(method + " x, y, and z must be supplied together.");
            return hasX ? new Vector3((float)x, (float)y, (float)z) : (Vector3?)null;
        }

        private static Vector3 DefaultSpawnPosition(float distance)
        {
            Player player = Player.local;
            PlayerHead head = player == null ? null : player.head;
            if (head == null)
                throw new InvalidOperationException("Player head is unavailable; supply x, y, and z explicitly.");
            return head.transform.position + head.transform.forward * distance;
        }

        private static string GenerateKey(McpRuntime runtime, string prefix)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                string candidate = prefix + Guid.NewGuid().ToString("N").Substring(0, 12);
                if (!runtime.TryGet<object>(candidate, out _)) return candidate;
            }
            throw new InvalidOperationException("Could not allocate a unique runtime key.");
        }

        internal static void RollbackSpawnIfUncommitted<T>(T spawned, bool committed, Action<T> rollback) where T : class
        {
            if (committed || ReferenceEquals(spawned, null)) return;
            try { rollback(spawned); } catch { }
        }

        internal static void SettleAbandonedSpawn(TaskCompletionSource<object> completion)
        {
            if (!completion.Task.IsCompleted) completion.TrySetCanceled();
        }
        private static void DespawnItemSafely(Item item)
        {
            if (item == null) return;
            try { item.Despawn(); } catch { }
        }

        private static void DespawnCreatureSafely(Creature creature)
        {
            if (creature == null) return;
            try { creature.Despawn(); } catch { }
        }

        private static void EndEffectSafely(EffectInstance instance)
        {
            if (instance == null) return;
            try { instance.End(); } catch { }
        }

        private static IEnumerator SpawnItemRoutine(McpRuntime runtime, ItemData data, string itemId, Vector3 position,
            string key, bool disallowDespawn, CancellationToken abort, TaskCompletionSource<object> completion)
        {
            object callbackGate = new object();
            Item spawned = null;
            bool called = false;
            bool abandoned = false;
            bool committed = false;
            try
            {
                try
                {
                    data.SpawnAsync(item =>
                    {
                        bool despawn;
                        lock (callbackGate)
                        {
                            despawn = abandoned
                                ? !ReferenceEquals(item, spawned)
                                : called && !ReferenceEquals(item, spawned);
                            if (!abandoned && !called)
                            {
                                spawned = item;
                                called = true;
                            }
                        }
                        if (despawn) DespawnItemSafely(item);
                    }, position, Quaternion.identity, null, false);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                    yield break;
                }

                float deadline = Time.realtimeSinceStartup + SpawnCompletionTimeoutSeconds;
                while (true)
                {
                    lock (callbackGate)
                        if (called) break;
                    if (abort.IsCancellationRequested || Time.realtimeSinceStartup >= deadline) break;
                    yield return null;
                }

                Item completedItem;
                bool callbackCompleted;
                lock (callbackGate)
                {
                    callbackCompleted = called;
                    completedItem = spawned;
                }
                if (abort.IsCancellationRequested || !callbackCompleted)
                {
                    if (!abort.IsCancellationRequested)
                        completion.TrySetException(new TimeoutException(
                            "spawn_item did not complete within " + SpawnCompletionTimeoutSeconds.ToString("0") + " seconds."));
                    yield break;
                }
                if (completedItem == null)
                {
                    completion.TrySetException(new InvalidOperationException("Item spawn completed without an Item."));
                    yield break;
                }

                try
                {
                    completedItem.DisallowDespawn = disallowDespawn;
                    Vector3 actual = completedItem.transform.position;
                    var result = new BridgeJsonResult(SerializeResult(new
                    {
                        key,
                        itemId,
                        position = new { x = actual.x, y = actual.y, z = actual.z },
                        instanceId = completedItem.GetInstanceID(),
                        name = completedItem.name
                    }));
                    abort.ThrowIfCancellationRequested();
                    lock (callbackGate)
                    {
                        runtime.Set(key, completedItem);
                        committed = true;
                    }
                    completion.TrySetResult(result);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }
            finally
            {
                Item uncommitted = null;
                lock (callbackGate)
                {
                    abandoned = true;
                    if (!committed) uncommitted = spawned;
                }
                RollbackSpawnIfUncommitted(uncommitted, false, DespawnItemSafely);
                SettleAbandonedSpawn(completion);
            }
        }

        private static IEnumerator SpawnCreatureRoutine(McpRuntime runtime, CreatureData data, string creatureId,
            Vector3 position, float rotationY, string key, CancellationToken abort, TaskCompletionSource<object> completion)
        {
            object callbackGate = new object();
            Creature spawned = null;
            bool called = false;
            bool abandoned = false;
            bool committed = false;
            try
            {
                try
                {
                    data.SpawnAsync(position, rotationY, null, true, null, creature =>
                    {
                        bool despawn;
                        lock (callbackGate)
                        {
                            despawn = abandoned
                                ? !ReferenceEquals(creature, spawned)
                                : called && !ReferenceEquals(creature, spawned);
                            if (!abandoned && !called)
                            {
                                spawned = creature;
                                called = true;
                            }
                        }
                        if (despawn) DespawnCreatureSafely(creature);
                    });
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                    yield break;
                }

                float deadline = Time.realtimeSinceStartup + CreatureSpawnCompletionTimeoutSeconds;
                while (true)
                {
                    lock (callbackGate)
                        if (called) break;
                    if (abort.IsCancellationRequested || Time.realtimeSinceStartup >= deadline) break;
                    yield return null;
                }

                Creature completedCreature;
                bool callbackCompleted;
                lock (callbackGate)
                {
                    callbackCompleted = called;
                    completedCreature = spawned;
                }
                if (abort.IsCancellationRequested || !callbackCompleted)
                {
                    if (!abort.IsCancellationRequested)
                        completion.TrySetException(new TimeoutException(
                            "spawn_creature did not complete within " + CreatureSpawnCompletionTimeoutSeconds.ToString("0") + " seconds."));
                    yield break;
                }
                if (completedCreature == null)
                {
                    completion.TrySetException(new InvalidOperationException("Creature spawn completed without a Creature."));
                    yield break;
                }

                try
                {
                    Vector3 actual = completedCreature.transform.position;
                    var result = new BridgeJsonResult(SerializeResult(new
                    {
                        key,
                        creatureId,
                        position = new { x = actual.x, y = actual.y, z = actual.z },
                        rotationY = completedCreature.transform.eulerAngles.y,
                        instanceId = completedCreature.GetInstanceID(),
                        name = completedCreature.name
                    }));
                    abort.ThrowIfCancellationRequested();
                    lock (callbackGate)
                    {
                        runtime.Set(key, completedCreature);
                        committed = true;
                    }
                    completion.TrySetResult(result);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }
            finally
            {
                Creature uncommitted = null;
                lock (callbackGate)
                {
                    abandoned = true;
                    if (!committed) uncommitted = spawned;
                }
                RollbackSpawnIfUncommitted(uncommitted, false, DespawnCreatureSafely);
                SettleAbandonedSpawn(completion);
            }
        }

        private sealed class FoundObject
        {
            public int instanceId { get; set; }
            public string type { get; set; }
            public string name { get; set; }
            public string path { get; set; }
            public string scene { get; set; }
            public bool activeInHierarchy { get; set; }
            public bool? enabled { get; set; }
            public object position { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public object[] fields { get; set; }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? fieldsTruncated { get; set; }
        }

        private static async Task<object> FindObjectsAsync(JObject args, CancellationToken token)
        {
            string componentType = GetRequiredString(args, "componentType", MaxComponentTypeLength);
            string name = GetOptionalString(args, "name", MaxObjectNameFilterLength);
            bool includeInactive = GetBoolean(args, "includeInactive", false);
            bool includeFields = GetBoolean(args, "includeFields", false);
            long offset = GetInteger(args, "offset", 0);
            long limit = GetInteger(args, "limit", 25);
            long maxLimit = includeFields ? 10 : 50;
            if (offset < 0 || offset > 100000) throw new InvalidDataException("find_objects.offset must be between 0 and 100000.");
            if (limit < 1 || limit > maxLimit)
                throw new InvalidDataException("find_objects.limit must be between 1 and " + maxLimit + (includeFields ? " when includeFields is true." : "."));
            Type type = ResolveComponentType(componentType);
            return new BridgeJsonResult(await AwaitWithCancellation(MainThreadQueue.Enqueue(
                () => SerializeResult(FindObjects(type, name, includeInactive, includeFields, (int)offset, (int)limit)), token), token).ConfigureAwait(false));
        }

        // Runs on the network thread before the request is queued, so the type scan never touches the Unity main thread.
        internal static Type ResolveComponentType(string name)
        {
            Type cached;
            if (ComponentTypes.TryGetValue(name, out cached)) return cached;
            var exact = new List<Type>();
            var simple = new List<Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception) { types = exception.Types; }
                foreach (Type type in types)
                {
                    if (type == null || !typeof(UnityEngine.Component).IsAssignableFrom(type)) continue;
                    if (type.FullName == name) exact.Add(type);
                    else if (type.Name == name) simple.Add(type);
                }
            }
            List<Type> matches = exact.Count > 0 ? exact : simple;
            if (matches.Count == 0)
                throw new InvalidDataException("componentType '" + name + "' is not a loaded UnityEngine.Component type. Use api_search(query:\"" + name + "\", kind:\"type\") to find its full name.");
            if (matches.Count > 1)
                throw new InvalidDataException("componentType '" + name + "' is ambiguous: " +
                    string.Join(", ", matches.Select(match => match.FullName).OrderBy(fullName => fullName, StringComparer.Ordinal).Take(10)) +
                    ". Pass a full type name.");
            ComponentTypes[name] = matches[0];
            return matches[0];
        }

        private static object FindObjects(Type type, string name, bool includeInactive, bool includeFields, int offset, int limit)
        {
            UnityEngine.Object[] found = UnityEngine.Object.FindObjectsByType(type,
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.InstanceID);
            var matches = new List<UnityEngine.Component>(found.Length);
            foreach (UnityEngine.Object candidate in found)
            {
                UnityEngine.Component component = candidate as UnityEngine.Component;
                if (component == null) continue;
                if (name != null && component.gameObject.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matches.Add(component);
            }
            int end = Math.Min(matches.Count, offset + limit);
            var results = new List<FoundObject>(Math.Max(0, end - offset));
            for (int index = offset; index < end; index++)
                results.Add(DescribeFoundObject(matches[index], includeFields));
            bool hasMore = end < matches.Count;
            return new
            {
                componentType = type.FullName,
                total = matches.Count,
                offset,
                limit,
                hasMore,
                nextOffset = hasMore ? (int?)end : null,
                results
            };
        }

        private static FoundObject DescribeFoundObject(UnityEngine.Component component, bool includeFields)
        {
            Type runtimeType = component.GetType();
            GameObject owner = component.gameObject;
            UnityEngine.Behaviour behaviour = component as UnityEngine.Behaviour;
            Vector3 position = component.transform.position;
            var found = new FoundObject
            {
                instanceId = component.GetInstanceID(),
                type = Bound(runtimeType.FullName ?? runtimeType.Name, MaxFoundTextLength),
                name = Bound(owner.name, MaxFoundTextLength),
                path = BuildHierarchyPath(component.transform),
                scene = Bound(owner.scene.name, MaxFoundTextLength),
                activeInHierarchy = owner.activeInHierarchy,
                enabled = behaviour != null ? behaviour.enabled : (bool?)null,
                position = new { x = position.x, y = position.y, z = position.z }
            };
            if (includeFields)
            {
                bool truncated;
                found.fields = DescribeFields(component, runtimeType, out truncated);
                found.fieldsTruncated = truncated;
            }
            return found;
        }

        private static string BuildHierarchyPath(Transform transform)
        {
            var names = new List<string>();
            bool truncated = false;
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (names.Count == MaxFoundPathLevels)
                {
                    truncated = true;
                    break;
                }
                names.Add(current.name);
            }
            names.Reverse();
            string path = string.Join("/", names.ToArray());
            if (truncated) path = "…/" + path;
            if (path.Length > MaxFoundPathLength)
            {
                int start = path.Length - (MaxFoundPathLength - 1);
                if (char.IsLowSurrogate(path[start])) start++;
                path = "…" + path.Substring(start);
            }
            return path;
        }

        // Reads public instance fields only: a field read never runs game code, unlike a property getter.
        private static object[] DescribeFields(UnityEngine.Component component, Type type, out bool truncated)
        {
            var fields = new List<FieldInfo>();
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string declaringNamespace = field.DeclaringType == null ? null : field.DeclaringType.Namespace;
                if (declaringNamespace != null && declaringNamespace.StartsWith("UnityEngine", StringComparison.Ordinal)) continue;
                fields.Add(field);
            }
            fields.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            truncated = fields.Count > MaxFoundFields;
            var described = new object[Math.Min(fields.Count, MaxFoundFields)];
            for (int index = 0; index < described.Length; index++)
            {
                FieldInfo field = fields[index];
                string fieldType = null;
                object value;
                try
                {
                    fieldType = Bound(field.FieldType.FullName ?? field.FieldType.Name, MaxFoundTextLength);
                    value = McpRuntime.DescribeValue(field.GetValue(component), 64, false);
                }
                catch (Exception)
                {
                    // Mod types can reference assemblies that are not loaded; report the field instead of failing the query.
                    value = new { kind = "unreadable" };
                }
                described[index] = new { name = Bound(field.Name, MaxFoundFieldNameLength), type = fieldType, value };
            }
            return described;
        }

        private static string Bound(string value, int maxLength)
        {
            bool truncated;
            return value == null ? null : McpRuntime.BoundedText(value, maxLength, out truncated);
        }

        private object CreateStatus(bool includeAssemblies)
        {
            ThunderRoad.LevelManager.State state = ThunderRoad.LevelManager.state;
            LoadedAssemblyStatus assemblyStatus = includeAssemblies
                ? BuildLoadedAssemblyStatus(AppDomain.CurrentDomain.GetAssemblies(),
                    GetAssemblyDirectory(typeof(ThunderRoad.ThunderScript).Assembly), FindModsRoot())
                : null;
            return BuildStatusPayload(Process.GetCurrentProcess().Id, Application.productName, Application.version,
                Application.unityVersion, ThunderRoad.LevelManager.GetCurrentLevel(), state.ToString(),
                state == ThunderRoad.LevelManager.State.Loaded, includeAssemblies, assemblyStatus);
        }

        internal static object BuildStatusPayload(int processId, string productName, string gameVersion, string unityVersion,
            string currentLevel, string levelState, bool sceneReady, bool includeAssemblies, LoadedAssemblyStatus assemblyStatus)
        {
            JObject status = JObject.FromObject(new
            {
                bridgeReady = true,
                processId,
                productName,
                gameVersion,
                unityVersion,
                currentLevel,
                levelState,
                sceneReady,
            });
            if (includeAssemblies)
            {
                status["loadedAssemblyCount"] = assemblyStatus.loadedAssemblyCount;
                status["loadedAssembliesTruncated"] = assemblyStatus.loadedAssembliesTruncated;
                status["loadedAssemblies"] = JArray.FromObject(assemblyStatus.loadedAssemblies);
            }
            return status;
        }

        internal static LoadedAssemblyStatus BuildLoadedAssemblyStatus(IEnumerable<Assembly> assemblies, string managedRoot, string modsRoot)
        {
            var entries = new List<Tuple<string, string, string, object>>();
            string managed = NormalizeRoot(managedRoot);
            string mods = NormalizeRoot(modsRoot);
            foreach (Assembly assembly in assemblies)
            {
                try
                {
                    if (assembly == null || assembly.IsDynamic) continue;
                    string location = assembly.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    string fullPath = Path.GetFullPath(location);
                    string origin;
                    string root;
                    if (IsUnderRoot(fullPath, managed)) { origin = "game"; root = managed; }
                    else if (IsUnderRoot(fullPath, mods)) { origin = "mod"; root = mods; }
                    else continue;
                    string relativePath = fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    AssemblyName name = assembly.GetName();
                    Guid mvid = assembly.ManifestModule.ModuleVersionId;
                    object entry = new
                    {
                        name = name.Name,
                        version = name.Version == null ? null : name.Version.ToString(),
                        moduleVersionId = mvid,
                        origin,
                        relativePath
                    };
                    entries.Add(Tuple.Create(origin, relativePath, name.Name + "\0" + mvid.ToString("D"), entry));
                }
                catch (Exception ex) when (ex is NotSupportedException || ex is IOException || ex is ArgumentException ||
                    ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    // Assemblies without accessible file locations are not reportable.
                }
            }
            entries.Sort((a, b) =>
            {
                int comparison = string.CompareOrdinal(a.Item1, b.Item1);
                if (comparison == 0) comparison = string.CompareOrdinal(a.Item2, b.Item2);
                return comparison != 0 ? comparison : string.CompareOrdinal(a.Item3, b.Item3);
            });
            int count = entries.Count;
            return new LoadedAssemblyStatus
            {
                loadedAssemblies = entries.Take(128).Select(entry => entry.Item4).ToArray(),
                loadedAssemblyCount = count,
                loadedAssembliesTruncated = count > 128
            };
        }

        private static string NormalizeRoot(string root)
        {
            return string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        private static string GetAssemblyDirectory(Assembly assembly)
        {
            try { return Path.GetDirectoryName(assembly.Location); }
            catch (Exception ex) when (ex is NotSupportedException || ex is IOException || ex is ArgumentException ||
                ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { return null; }
        }

        private static bool IsUnderRoot(string path, string root)
        {
            return root != null && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string FindModsRoot()
        {
            string assemblyDirectory = GetAssemblyDirectory(Assembly.GetExecutingAssembly());
            if (assemblyDirectory == null) return null;
            DirectoryInfo directory = new DirectoryInfo(assemblyDirectory);
            while (directory != null && !string.Equals(directory.Name, "Mods", StringComparison.OrdinalIgnoreCase))
                directory = directory.Parent;
            return directory == null ? null : directory.FullName;
        }

        private async Task<string> Evaluate(string code, TimeSpan timeout, CancellationToken shutdownToken, BridgeRequestTimings timings)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
            {
                deadline.CancelAfter(timeout);
                CancellationToken token = deadline.Token;
                try
                {
                    using (timings.Measure("compilerQueueWaitMs"))
                        await compiler.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                {
                    throw new BridgeEvaluationException("evaluation.not_started",
                        "Evaluation timed out before invocation began.", "not_started", null, exception);
                }

                Task<Func<object>> compile = null;
                bool releaseDeferred = false;
                Func<object> evaluation;
                try
                {
                    compile = Task.Run(() =>
                    {
                        using (timings.Measure("compilationMs"))
                            return CSharpEvaluator.Compile(code, token, timings);
                    }, token);
                    try { evaluation = await AwaitWithCancellation(compile, token).ConfigureAwait(false); }
                    catch (Exception exception)
                    {
                        if (!compile.IsCompleted)
                        {
                            releaseDeferred = true;
                            _ = compile.ContinueWith(t =>
                            {
                                if (t.IsFaulted) { var ignored = t.Exception; }
                                compiler.Release();
                            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                        var diagnostics = CompilationDiagnostics(exception);
                        if (exception is OperationCanceledException)
                            throw new BridgeEvaluationException("evaluation.not_started",
                                "Evaluation timed out before invocation began.", "not_started", diagnostics, exception);
                        throw new BridgeEvaluationException("evaluation.compilation_failed", exception.Message,
                            "not_started", diagnostics, exception);
                    }
                }
                finally
                {
                    if (!releaseDeferred) compiler.Release();
                }

                var invocationReturned = 0;
                var queueWait = Stopwatch.StartNew();
                var queueWaitRecorded = 0;
                void RecordQueueWait()
                {
                    if (Interlocked.Exchange(ref queueWaitRecorded, 1) == 0)
                        timings.Add("mainThreadQueueWaitMs", queueWait.Elapsed.TotalMilliseconds);
                }

                Task<string> execution;
                try
                {
                    execution = MainThreadQueue.Enqueue(() =>
                    {
                        RecordQueueWait();
                        object value;
                        try
                        {
                            using (timings.Measure("invocationMs"))
                                value = evaluation();
                            Interlocked.Exchange(ref invocationReturned, 1);
                        }
                        catch (Exception exception)
                        {
                            throw new BridgeEvaluationException("evaluation.invocation_failed", exception.ToString(),
                                "unknown", null, exception);
                        }
                        try
                        {
                            using (timings.Measure("serializationMs"))
                                return SerializeResult(value);
                        }
                        catch (BridgeResultDepthException exception)
                        {
                            throw new BridgeEvaluationException("evaluation.result_depth_exceeded", exception.Message,
                                "completed_result_unavailable", null, exception);
                        }
                        catch (Exception exception)
                        {
                            throw new BridgeEvaluationException("evaluation.result_unavailable", exception.Message,
                                "completed_result_unavailable", null, exception);
                        }
                    }, token);
                }
                catch (OperationCanceledException exception)
                {
                    throw new BridgeEvaluationException("evaluation.not_started",
                        "Evaluation timed out before invocation began.", "not_started", null, exception);
                }
                catch (Exception exception)
                {
                    throw new BridgeEvaluationException("evaluation.not_started", exception.Message, "not_started", null, exception);
                }

                try
                {
                    return await AwaitWithCancellation(execution, token).ConfigureAwait(false);
                }
                catch (BridgeEvaluationException)
                {
                    throw;
                }
                catch (OperationCanceledException exception)
                {
                    if (Volatile.Read(ref invocationReturned) != 0)
                        throw new BridgeEvaluationException("evaluation.result_unavailable",
                            "Evaluation completed, but result serialization did not finish before the timeout.",
                            "completed_result_unavailable", null, exception);
                    if (Volatile.Read(ref queueWaitRecorded) == 0)
                        throw new BridgeEvaluationException("evaluation.not_started",
                            "Evaluation timed out before invocation began.", "not_started", null, exception);
                    throw new BridgeEvaluationException("evaluation.outcome_unknown",
                        "Evaluation timed out after invocation may have begun; do not retry automatically.", "unknown", null, exception);
                }
                catch (Exception exception)
                {
                    throw new BridgeEvaluationException("evaluation.outcome_unknown", exception.Message, "unknown", null, exception);
                }
                finally
                {
                    RecordQueueWait();
                }
            }
        }

        private static object[] CompilationDiagnostics(Exception exception)
        {
            var property = exception.GetType().GetProperty("Diagnostics", BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !(property.GetValue(exception, null) is IEnumerable values))
                return null;
            var diagnostics = new List<object>();
            foreach (var value in values)
            {
                if (value == null) continue;
                var type = value.GetType();
                string id = ReadDiagnosticString(type, value, "Id", 64);
                string severity = ReadDiagnosticString(type, value, "Severity", 16);
                string message = ReadDiagnosticString(type, value, "Message", 256);
                int? line = ReadDiagnosticInt(type, value, "Line");
                int? column = ReadDiagnosticInt(type, value, "Column");
                var spanValue = type.GetProperty("Span", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value, null);
                object span = null;
                if (spanValue != null)
                {
                    var spanType = spanValue.GetType();
                    span = new
                    {
                        start = ReadDiagnosticInt(spanType, spanValue, "Start"),
                        length = ReadDiagnosticInt(spanType, spanValue, "Length")
                    };
                }
                diagnostics.Add(new { id, severity, message, line, column, span });
                if (diagnostics.Count == 20) break;
            }
            return diagnostics.ToArray();
        }

        private static string ReadDiagnosticString(Type type, object value, string name, int maxLength)
        {
            string text = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value, null) as string;
            return text != null && text.Length > maxLength ? text.Substring(0, maxLength) : text;
        }

        private static int? ReadDiagnosticInt(Type type, object value, string name)
        {
            var propertyValue = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value, null);
            return propertyValue is int number ? (int?)number : propertyValue as int?;
        }
        private static async Task<T> AwaitWithCancellation<T>(Task<T> task, CancellationToken token)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetResult(true)))
            {
                if (task != await Task.WhenAny(task, canceled.Task).ConfigureAwait(false))
                    token.ThrowIfCancellationRequested();
            }
            return await task.ConfigureAwait(false);
        }

        internal bool IsAuthenticated(string token)
        {
            if (token == null || token.Length != config.Token.Length) return false;
            int diff = 0;
            for (int i = 0; i < token.Length; i++) diff |= token[i] ^ config.Token[i];
            return diff == 0;
        }

        private static long GetInteger(JObject args, string name, long fallback)
        {
            JToken value = args[name];
            if (value == null) return fallback;
            if (value.Type != JTokenType.Integer) throw new InvalidDataException(name + " must be an integer.");
            return (long)value;
        }

        private static string GetRequiredString(JObject args, string name, int maxLength)
        {
            string text = GetOptionalString(args, name, maxLength);
            if (text == null) throw new InvalidDataException(name + " is required.");
            return text;
        }

        private static string GetOptionalString(JObject args, string name, int maxLength)
        {
            JToken value = args[name];
            if (value == null || value.Type == JTokenType.Null) return null;
            if (value.Type != JTokenType.String) throw new InvalidDataException(name + " must be a string.");
            string text = (string)value;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException(name + " must be non-empty.");
            if (text.Length > maxLength)
                throw new InvalidDataException(name + " must be at most " + maxLength + " characters.");
            return text;
        }

        private static bool GetBoolean(JObject args, string name, bool fallback)
        {
            JToken value = args[name];
            if (value == null || value.Type == JTokenType.Null) return fallback;
            if (value.Type != JTokenType.Boolean) throw new InvalidDataException(name + " must be a boolean.");
            return (bool)value;
        }

        private static bool TryGetNumber(JObject args, string name, out double value)
        {
            JToken token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                value = 0;
                return false;
            }
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new InvalidDataException(name + " must be a number.");
            value = (double)token;
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1_000_000)
                throw new InvalidDataException(name + " must be finite and within ±1000000.");
            return true;
        }

    }
}
