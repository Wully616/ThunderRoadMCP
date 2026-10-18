using System;
using System.Collections;
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
    }
    internal sealed class BridgeJsonResult
    {
        public readonly string Json;
        public BridgeJsonResult(string json) { Json = json; }
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
        private const int MaxLevelIdLength = 256;
        private const int MaxModeLength = 256;
        private const int MaxItemIdLength = 256;
        private const float SpawnCompletionTimeoutSeconds = 8f;
        private static readonly TimeSpan SpawnResultTimeout = TimeSpan.FromSeconds(12);
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
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    MaxDepth = 16
                }).Serialize(writer, result);
                writer.Flush();
            }
            return output.ToString();
        }
        internal async Task<object> DispatchAsync(BridgeRequest request, CancellationToken token)
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
                    string code = (string)args["code"];
                    if (string.IsNullOrWhiteSpace(code) || Encoding.UTF8.GetByteCount(code) > MaxLineBytes)
                        throw new InvalidDataException("evaluate.code must be non-empty and at most 64 KiB UTF-8.");
                    long timeout = GetInteger(args, "timeoutMs", 10000);
                    if (timeout < 1 || timeout > 30000) throw new InvalidDataException("evaluate.timeoutMs must be between 1 and 30000.");
                    return new BridgeJsonResult(await Evaluate(code, TimeSpan.FromMilliseconds(timeout), token).ConfigureAwait(false));
                case "runtime":
                    return await RuntimeAsync(args, token).ConfigureAwait(false);
                case "load_level":
                    string levelId = GetRequiredString(args, "levelId", MaxLevelIdLength);
                    string mode = GetRequiredString(args, "mode", MaxModeLength);
                    return new BridgeJsonResult(await AwaitWithCancellation(
                        MainThreadQueue.Enqueue(() => SerializeResult(LoadLevel(levelId, mode)), token), token).ConfigureAwait(false));
                case "spawn_item":
                    return await SpawnItemAsync(args, token).ConfigureAwait(false);
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
                    throw new InvalidDataException("runtime.action must be list, stop, or clear.");
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

        private static async Task<object> SpawnItemAsync(JObject args, CancellationToken token)
        {
            string itemId = GetRequiredString(args, "itemId", MaxItemIdLength);
            string requestedKey = GetOptionalString(args, "key", McpRuntime.MaxKeyLength);
            bool disallowDespawn = GetBoolean(args, "disallowDespawn", true);
            bool hasX = TryGetNumber(args, "x", out double x);
            bool hasY = TryGetNumber(args, "y", out double y);
            bool hasZ = TryGetNumber(args, "z", out double z);
            if (hasX != hasY || hasY != hasZ)
                throw new InvalidDataException("spawn_item x, y, and z must be supplied together.");
            Vector3? position = hasX ? new Vector3((float)x, (float)y, (float)z) : (Vector3?)null;

            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            string coroutineKey = "spawn-" + Guid.NewGuid().ToString("N");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(SpawnResultTimeout);
                try
                {
                    await AwaitWithCancellation(MainThreadQueue.Enqueue(() =>
                    {
                        if (deadline.IsCancellationRequested) return string.Empty;
                        try
                        {
                            McpRuntime runtime = Runtime();
                            ItemData data = Catalog.GetData<ItemData>(itemId);
                            if (data == null) throw new InvalidDataException("Catalog item not found: " + itemId);
                            Vector3 spawnPosition = position ?? DefaultSpawnPosition();
                            string key = requestedKey ?? GenerateItemKey(runtime);
                            runtime.StartManagedCoroutine(coroutineKey,
                                SpawnItemRoutine(runtime, data, itemId, spawnPosition, key, disallowDespawn, deadline.Token, completion));
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
                    throw new TimeoutException("spawn_item timed out after " + SpawnResultTimeout.TotalSeconds.ToString("0") + " seconds.");
                }
            }
        }

        private static Vector3 DefaultSpawnPosition()
        {
            Player player = Player.local;
            PlayerHead head = player == null ? null : player.head;
            if (head == null)
                throw new InvalidOperationException("Player head is unavailable; supply x, y, and z explicitly.");
            return head.transform.position + head.transform.forward * 1.5f;
        }

        private static string GenerateItemKey(McpRuntime runtime)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                string candidate = "item-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                if (!runtime.TryGet<object>(candidate, out _)) return candidate;
            }
            throw new InvalidOperationException("Could not allocate a unique runtime key.");
        }

        private static IEnumerator SpawnItemRoutine(McpRuntime runtime, ItemData data, string itemId, Vector3 position,
            string key, bool disallowDespawn, CancellationToken abort, TaskCompletionSource<object> completion)
        {
            Item spawned = null;
            bool called = false;
            try
            {
                data.SpawnAsync(item => { spawned = item; called = true; }, position, Quaternion.identity, null, false);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + SpawnCompletionTimeoutSeconds;
            while (!called && !abort.IsCancellationRequested && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (abort.IsCancellationRequested)
                yield break;
            if (!called)
            {
                completion.TrySetException(new TimeoutException(
                    "spawn_item did not complete within " + SpawnCompletionTimeoutSeconds.ToString("0") + " seconds."));
                yield break;
            }
            if (spawned == null)
            {
                completion.TrySetException(new InvalidOperationException("Item spawn completed without an Item."));
                yield break;
            }
            if (abort.IsCancellationRequested)
                yield break;

            try
            {
                spawned.DisallowDespawn = disallowDespawn;
                runtime.Set(key, spawned);
                Vector3 actual = spawned.transform.position;
                completion.TrySetResult(new
                {
                    key,
                    itemId,
                    position = new { x = actual.x, y = actual.y, z = actual.z },
                    instanceId = spawned.GetInstanceID(),
                    name = spawned.name
                });
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
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
                status["loadedAssemblies"] = JArray.FromObject(assemblyStatus.loadedAssemblies);
                status["loadedAssemblyCount"] = assemblyStatus.loadedAssemblyCount;
                status["loadedAssembliesTruncated"] = assemblyStatus.loadedAssembliesTruncated;
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

        private async Task<string> Evaluate(string code, TimeSpan timeout, CancellationToken shutdownToken)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
            {
                deadline.CancelAfter(timeout);
                CancellationToken token = deadline.Token;
                await compiler.WaitAsync(token).ConfigureAwait(false);
                Task<Func<object>> compile = null;
                bool releaseDeferred = false;
                Func<object> evaluation;
                try
                {
                    compile = Task.Run(() => CSharpEvaluator.Compile(code, token), token);
                    try { evaluation = await AwaitWithCancellation(compile, token).ConfigureAwait(false); }
                    catch
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
                        throw;
                    }
                }
                finally
                {
                    if (!releaseDeferred) compiler.Release();
                }

                Task<string> execution = MainThreadQueue.Enqueue(() => SerializeResult(evaluation()), token);
                return await AwaitWithCancellation(execution, token).ConfigureAwait(false);
            }
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
