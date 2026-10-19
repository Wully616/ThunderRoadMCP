using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.Serialization;
using Newtonsoft.Json.Linq;
using ThunderRoadMcp.Bridge;

internal static class BridgeSelfTest
{
    private static void Main(string[] args)
    {
        string managed = Environment.GetEnvironmentVariable("THUNDERROAD_MANAGED_DIR");
        if (string.IsNullOrWhiteSpace(managed) || !Directory.Exists(managed) || !File.Exists(Path.Combine(managed, "ThunderRoad.dll")))
            throw new InvalidOperationException("Set THUNDERROAD_MANAGED_DIR to the installed game's Managed directory (the same path passed with -p:GameManagedDir).");
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            string path = Path.Combine(managed, new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        Assembly thunderRoad = Assembly.LoadFrom(Path.Combine(managed, "ThunderRoad.dll"));
        if (args.Length != 1) throw new ArgumentException("Pass the packaged Roslyn payload directory.");
        CSharpEvaluator.Configure(Path.GetFullPath(args[0]));
        VerifyReferenceSourcePrecedence(managed);

        object arithmetic = CSharpEvaluator.Compile("return 6 * 7;", CancellationToken.None)();
        Assert((int)arithmetic == 42, "arithmetic result");

        string gameApi = (string)CSharpEvaluator.Compile("return typeof(ThunderScript).FullName;", CancellationToken.None)();
        Assert(gameApi == "ThunderRoad.ThunderScript", "installed ThunderRoad API metadata");

        string runtimeProbe = (string)CSharpEvaluator.Compile("return McpRuntime.Current == null ? \"none\" : \"present\";", CancellationToken.None)();
        Assert(runtimeProbe == "none", "eval compiles against the bridge runtime host");

        VerifyStatusAndAssemblyIdentity(thunderRoad, managed);
        VerifyRuntimeValues();
        VerifyRequestTimings();

        try
        {
            CSharpEvaluator.Compile("return missingSymbol;", CancellationToken.None);
            throw new Exception("Expected a compiler diagnostic.");
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message.Contains("CS0103") && ex.Message.Contains("evaluation.cs(1,"), "snippet compiler diagnostic location");
        }

        try
        {
            CSharpEvaluator.Compile("throw new InvalidOperationException(\"runtime diagnostic\"); return null;", CancellationToken.None)();
            throw new Exception("Expected the evaluation exception.");
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message == "runtime diagnostic", "runtime exception propagation");
        }
        string dtoJson = BridgeRequestHandler.SerializeResult(CSharpEvaluator.Compile("return new { version = \"test\" };", CancellationToken.None)());
        Assert(dtoJson.Contains("\"version\":\"test\""), "synchronous DTO serialization");

        VerifyLogPagination();

        try
        {
            BridgeRequestHandler.SerializeResult(new TaskCompletionSource<int>().Task);
            throw new Exception("Expected async evaluation rejection.");
        }
        catch (NotSupportedException ex)
        {
            Assert(ex.Message.Contains("Async evaluation is unsupported"), "pending Task rejected without waiting");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            bool executed = false;
            Task<string> queued = MainThreadQueue.Enqueue(() => { executed = true; return "unexpected"; }, cancellation.Token);
            cancellation.Cancel();
            MainThreadQueue.Drain();
            Assert(!executed && queued.IsCanceled, "canceled queued work is skipped");
        }
        Console.WriteLine("PASS: live status shape, loaded assembly MVID, arithmetic, metadata source precedence, game API metadata, runtime host reference, generic runtime retrieval and inspection, bounded runtime list, successful and failed bridge timing stages, newest bridge log page, mapped diagnostic, runtime exception, DTO serialization, pending Task rejection, and canceled queue. CLR " + Environment.Version);
    }

    private static void VerifyRuntimeValues()
    {
        McpRuntime runtime = CreateRuntimeForSelfTest();
        runtime.Set("counter", 0);
        Assert(runtime.TryGet<int>("counter", out int counter) && counter == 0 && runtime.Get<int>("counter") == 0,
            "typed runtime retrieval preserves stored zero");
        Assert(!runtime.TryGet<string>("counter", out string mismatch) && mismatch == null,
            "runtime type mismatch returns default");
        Assert(!runtime.TryGet<int>("missing", out int missing) && missing == 0 && runtime.Get<int>("missing") == 0,
            "missing runtime values return default and TryGet reports absence");
        runtime.Set("null", null);
        Assert(runtime.TryGet<string>("null", out string storedNull) && storedNull == null,
            "TryGet reports a stored null as present for reference types");
        Assert(runtime.Get<int>("null") == 0, "runtime type mismatch returns default value type");

        JObject zero = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("counter")));
        Assert((bool)zero["found"] && (string)zero["type"] == "System.Int32" &&
            (string)zero["value"]["kind"] == "scalar" && (int)zero["value"]["value"] == 0,
            "runtime inspection returns typed scalar value");
        JObject absent = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("missing")));
        JObject nullValue = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("null")));
        Assert(!(bool)absent["found"] && absent["value"].Type == JTokenType.Null &&
            (bool)nullValue["found"] && (string)nullValue["value"]["kind"] == "null",
            "runtime inspection distinguishes missing from stored null");

        runtime.Set("text", new string('x', 1024));
        JObject bounded = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("text")));
        string inspectedText = (string)bounded["value"]["value"];
        Assert((bool)bounded["value"]["truncated"] && inspectedText.Length <= 256 &&
            inspectedText.EndsWith("[truncated]", StringComparison.Ordinal),
            "runtime inspection bounds strings and reports truncation");
        runtime.Set("opaque", new object());
        JObject opaque = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("opaque")));
        Assert((string)opaque["type"] == "System.Object" && (string)opaque["value"]["kind"] == "opaque" &&
            opaque["value"]["value"] == null,
            "unknown runtime objects expose type metadata only");

        McpRuntime fullRuntime = CreateRuntimeForSelfTest();
        for (int index = 0; index < McpRuntime.MaxEntries; index++)
        {
            string prefix = index.ToString("D3");
            string key = prefix + new string('k', McpRuntime.MaxKeyLength - prefix.Length);
            fullRuntime.Set(key, new string('v', 1024));
        }
        string listed = BridgeRequestHandler.SerializeResult(fullRuntime.Describe());
        JObject list = JObject.Parse(listed);
        JObject first = (JObject)((JArray)list["objects"])[0];
        string preview = (string)first["value"]["value"];
        Assert((int)list["objectCount"] == McpRuntime.MaxEntries &&
            ((JArray)list["objects"]).Count == McpRuntime.MaxEntries &&
            (string)first["value"]["kind"] == "scalar" && (bool)first["value"]["truncated"] && preview.Length <= 32 &&
            listed.Length < BridgeRequestHandler.MaxLineBytes - 512,
            "runtime list has bounded value previews and stays within the response bound at maximum entries, keys, and strings");
        McpRuntime pathologicalRuntime = CreateRuntimeForSelfTest();
        FieldInfo coroutineField = typeof(McpRuntime).GetField("coroutines", BindingFlags.Instance | BindingFlags.NonPublic);
        System.Collections.IDictionary coroutineEntries = (System.Collections.IDictionary)coroutineField.GetValue(pathologicalRuntime);
        Type coroutineEntryType = coroutineField.FieldType.GetGenericArguments()[1];
        for (int index = 0; index < McpRuntime.MaxEntries; index++)
        {
            string key = index.ToString("D3") + new string('\0', McpRuntime.MaxKeyLength - 3);
            pathologicalRuntime.Set(key, new string('\0', 1024));
            coroutineEntries.Add(key, FormatterServices.GetUninitializedObject(coroutineEntryType));
        }
        string pathologicalJson = BridgeRequestHandler.SerializeResult(pathologicalRuntime.Describe());
        JObject pathologicalList = JObject.Parse(pathologicalJson);
        Assert((int)pathologicalList["coroutineCount"] == McpRuntime.MaxEntries &&
            (int)pathologicalList["objectCount"] == McpRuntime.MaxEntries &&
            (bool)pathologicalList["coroutinesTruncated"] && (bool)pathologicalList["objectsTruncated"] &&
            ((JArray)pathologicalList["coroutines"]).Count < McpRuntime.MaxEntries &&
            ((JArray)pathologicalList["objects"]).Count < McpRuntime.MaxEntries &&
            pathologicalJson.Length < BridgeRequestHandler.MaxLineBytes - 512,
            "runtime list truncates pathological escaped keys and value previews with truthful totals");
    }

    private static McpRuntime CreateRuntimeForSelfTest()
    {
        var runtime = (McpRuntime)FormatterServices.GetUninitializedObject(typeof(McpRuntime));
        typeof(McpRuntime).GetField("values", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(runtime, new Dictionary<string, object>(StringComparer.Ordinal));
        FieldInfo coroutines = typeof(McpRuntime).GetField("coroutines", BindingFlags.Instance | BindingFlags.NonPublic);
        coroutines.SetValue(runtime, Activator.CreateInstance(coroutines.FieldType));
        typeof(McpRuntime).GetField("mainThreadId", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(runtime, Thread.CurrentThread.ManagedThreadId);
        return runtime;
    }
    private static void VerifyRequestTimings()
    {
        var handler = new BridgeRequestHandler(new BridgeConfig());
        var successfulTimings = new BridgeRequestTimings();
        var response = (BridgeJsonResult)DispatchAndDrain(handler, new BridgeRequest
        {
            method = "evaluate",
            parameters = JObject.FromObject(new { code = "return 6 * 7;", timeoutMs = 30000 })
        }, successfulTimings);
        successfulTimings.Complete();
        Assert(response.Json == "42", "timed bridge evaluation result");
        var success = successfulTimings.Snapshot();
        foreach (string name in new[]
        {
            "compilerQueueWaitMs", "compilationMs", "mainThreadQueueWaitMs",
            "invocationMs", "serializationMs", "bridgeRequestTotalMs"
        })
            Assert(success.ContainsKey(name) && success[name] >= 0, "successful evaluation reports " + name);

        var failedTimings = new BridgeRequestTimings();
        try
        {
            DispatchAndDrain(handler, new BridgeRequest
            {
                method = "evaluate",
                parameters = JObject.FromObject(new
                {
                    code = "throw new InvalidOperationException(\"timed failure\"); return null;",
                    timeoutMs = 30000
                })
            }, failedTimings);
            throw new Exception("Expected the timed evaluation failure.");
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message == "timed failure", "timed evaluation failure propagation");
        }
        finally
        {
            failedTimings.Complete();
        }
        var failure = failedTimings.Snapshot();
        Assert(failure.ContainsKey("compilationMs") && failure.ContainsKey("invocationMs") &&
            failure.ContainsKey("bridgeRequestTotalMs"),
            "failed evaluation retains elapsed timing stages");
        var compilationTimings = new BridgeRequestTimings();
        try
        {
            handler.DispatchAsync(new BridgeRequest
            {
                method = "evaluate",
                parameters = JObject.FromObject(new { code = "return missingTimingSymbol;", timeoutMs = 30000 })
            }, CancellationToken.None, compilationTimings).GetAwaiter().GetResult();
            throw new Exception("Expected the timed compilation failure.");
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message.Contains("CS0103"), "timed compilation failure propagation");
        }
        finally
        {
            compilationTimings.Complete();
        }
        var compilationFailure = compilationTimings.Snapshot();
        Assert(compilationFailure.ContainsKey("compilerQueueWaitMs") &&
            compilationFailure.ContainsKey("compilationMs") &&
            compilationFailure.ContainsKey("bridgeRequestTotalMs") &&
            !compilationFailure.ContainsKey("invocationMs"),
            "compilation failure reports only elapsed stages");

        VerifyCanceledQueueWait(handler);
        VerifyRawTimingEnvelopeBounds();
    }

    private static void VerifyCanceledQueueWait(BridgeRequestHandler handler)
    {
        var timings = new BridgeRequestTimings();
        using (var cancellation = new CancellationTokenSource())
        {
            Task<object> dispatch = handler.DispatchAsync(new BridgeRequest
            {
                method = "evaluate",
                parameters = JObject.FromObject(new { code = "return 6 * 7;", timeoutMs = 30000 })
            }, cancellation.Token, timings);
            object gate = typeof(MainThreadQueue).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            object queue = typeof(MainThreadQueue).GetField("Queue", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var count = queue.GetType().GetProperty("Count");
            Assert(SpinWait.SpinUntil(() =>
            {
                lock (gate) return (int)count.GetValue(queue) > 0;
            }, TimeSpan.FromSeconds(30)), "evaluation reaches the main-thread queue");

            cancellation.Cancel();
            try
            {
                dispatch.GetAwaiter().GetResult();
                throw new Exception("Expected queued evaluation cancellation.");
            }
            catch (OperationCanceledException) { }
            MainThreadQueue.Drain();
        }
        timings.Complete();
        Assert(timings.Snapshot().ContainsKey("mainThreadQueueWaitMs"),
            "canceled queued evaluation reports elapsed queue wait");
    }

    private static void VerifyRawTimingEnvelopeBounds()
    {
        var timings = new Dictionary<string, double> { ["bridgeRequestTotalMs"] = 0.5 };
        string nearLimitJson = "{\"value\":\"" + new string('a', 64562) + new string('é', 450) + "\"}";
        byte[] fallbackBytes = BridgeServer.BuildRawSuccessResponse("x", nearLimitJson, timings);
        Assert(fallbackBytes.Length <= BridgeRequestHandler.MaxLineBytes, "timing fallback stays within bridge response limit");
        JObject fallback = JObject.Parse(System.Text.Encoding.UTF8.GetString(fallbackBytes));
        Assert((bool)fallback["success"] &&
            fallback["timings"] == null &&
            (string)fallback["result"]["value"] == new string('a', 64562) + new string('é', 450),
            "oversized optional timing envelope preserves raw result");

        string oversizedJson = "{\"value\":\"" + new string('x', 65530) + "\"}";
        byte[] errorBytes = BridgeServer.BuildRawSuccessResponse("x", oversizedJson, timings);
        JObject error = JObject.Parse(System.Text.Encoding.UTF8.GetString(errorBytes));
        Assert(errorBytes.Length <= BridgeRequestHandler.MaxLineBytes &&
            !(bool)error["success"] &&
            (string)error["error"] == "Response exceeds 64 KiB.",
            "oversized raw result returns a bounded error");
    }

    private static object DispatchAndDrain(BridgeRequestHandler handler, BridgeRequest request, BridgeRequestTimings timings)
    {
        Task<object> dispatch = handler.DispatchAsync(request, CancellationToken.None, timings);
        while (!dispatch.IsCompleted)
        {
            MainThreadQueue.Drain();
            if (!dispatch.IsCompleted) Thread.Sleep(1);
        }
        return dispatch.GetAwaiter().GetResult();
    }
    private static void VerifyLogPagination()
    {
        for (int index = 0; index < 4; index++)
            BridgeLogs.Record("Log", "entry-" + index);
        JObject latestLogs = JObject.Parse(BridgeRequestHandler.SerializeResult(BridgeLogs.ReadSince(0)));
        Assert(((JArray)latestLogs["entries"]).Count == 3 &&
               (string)latestLogs["entries"][0]["message"] == "entry-1" &&
               (string)latestLogs["entries"][2]["message"] == "entry-3",
            "zero since returns the newest log entries");
        long newestSequence = (long)latestLogs["nextSequence"];
        JObject nextLogs = JObject.Parse(BridgeRequestHandler.SerializeResult(BridgeLogs.ReadSince(newestSequence - 1)));
        Assert(((JArray)nextLogs["entries"]).Count == 1 &&
               (string)nextLogs["entries"][0]["message"] == "entry-3" &&
               (long)nextLogs["nextSequence"] == newestSequence,
            "positive since continues forward from the newest page");
    }

    private static void VerifyStatusAndAssemblyIdentity(Assembly thunderRoad, string managed)
    {
        LoadedAssemblyStatus assemblyStatus = BridgeRequestHandler.BuildLoadedAssemblyStatus(
            new[] { thunderRoad }, managed, Path.Combine(managed, "..", "Mods"));
        JObject inventory = JObject.Parse(BridgeRequestHandler.SerializeResult(assemblyStatus));
        JObject reported = (JObject)inventory["loadedAssemblies"][0];
        Assert((string)reported["name"] == "ThunderRoad", "ThunderRoad assembly reported");
        Assert((string)reported["origin"] == "game" && (string)reported["relativePath"] == "ThunderRoad.dll",
            "ThunderRoad assembly origin and relative path");
        Assert((Guid)reported["moduleVersionId"] == thunderRoad.ManifestModule.ModuleVersionId, "ThunderRoad actual MVID reported");
        JObject status = JObject.Parse(BridgeRequestHandler.SerializeResult(BridgeRequestHandler.BuildStatusPayload(
            1, "product", "game", "unity", "test-level", "Loaded", true, true, assemblyStatus)));
        Assert(status["currentLevel"].Type == JTokenType.String && (string)status["currentLevel"] == "test-level",
            "status currentLevel is a string");
        Assert((string)status["levelState"] == "Loaded" && (bool)status["sceneReady"] &&
            status["loadedAssemblies"].Type == JTokenType.Array &&
            (Guid)status["loadedAssemblies"][0]["moduleVersionId"] == thunderRoad.ManifestModule.ModuleVersionId &&
            (int)status["loadedAssemblyCount"] == 1 && !(bool)status["loadedAssembliesTruncated"],
            "status inventory opt-in retains assembly inventory and MVID");
        JObject compactStatus = JObject.Parse(BridgeRequestHandler.SerializeResult(BridgeRequestHandler.BuildStatusPayload(
            1, "product", "game", "unity", "test-level", "Loaded", true, false, assemblyStatus)));
        Assert(compactStatus.Property("loadedAssemblies") == null &&
               compactStatus.Property("loadedAssemblyCount") == null &&
               compactStatus.Property("loadedAssembliesTruncated") == null,
            "compact status omits loaded assembly inventory");
        JObject noLevelStatus = JObject.Parse(BridgeRequestHandler.SerializeResult(BridgeRequestHandler.BuildStatusPayload(
            1, "product", "game", "unity", null, "Unloaded", false, false, assemblyStatus)));
        Assert(noLevelStatus["currentLevel"].Type == JTokenType.Null, "status currentLevel can be null");
    }

    private static void VerifyReferenceSourcePrecedence(string managed)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string framework = Path.Combine(windows, Environment.Is64BitProcess ? "Microsoft.NET\\Framework64" : "Microsoft.NET\\Framework", "v4.0.30319");
        string shadowPath = Path.GetFullPath(Path.Combine(framework, "System.Security.dll"));
        string managedPath = Path.GetFullPath(Path.Combine(managed, "System.Security.dll"));
        Assert(File.Exists(shadowPath) && !string.Equals(shadowPath, managedPath, StringComparison.OrdinalIgnoreCase),
            "external framework duplicate available");
        Assembly shadow = Assembly.LoadFile(shadowPath);
        Assert(shadow.GetName().Name == "System.Security", "external duplicate assembly loaded");

        object arithmetic = CSharpEvaluator.Compile("return 1 + 2;", CancellationToken.None)();
        Assert((int)arithmetic == 3, "compile with external duplicate assembly loaded");

        Assembly compiler = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == "ThunderRoadMcp.Compiler")
            {
                compiler = assembly;
                break;
            }
        }
        Assert(compiler != null, "private compiler loaded");
        MethodInfo getPaths = compiler.GetType("ThunderRoadMcp.Compiler.CSharpEvaluator", true)
            .GetMethod("GetReferencePaths", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
        Assert(getPaths != null, "reference path selector available");
        MethodInfo unityNameFilter = compiler.GetType("ThunderRoadMcp.Compiler.CSharpEvaluator", true)
            .GetMethod("IsUnityEngineAssemblyName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert(unityNameFilter != null, "UnityEngine assembly name filter available");
        Assert((bool)unityNameFilter.Invoke(null, new object[] { "UnityEngine.PhysicsModuleUnityEngine.SharedI" }),
            "malformed UnityEngine assembly identity is treated as engine metadata");
        string bridgePath = Path.GetFullPath(typeof(McpRuntime).Assembly.Location);
        var paths = (string[])getPaths.Invoke(null, new object[] { bridgePath });
        Assert(Array.Exists(paths, path => string.Equals(path, managedPath, StringComparison.OrdinalIgnoreCase)),
            "installed Managed assembly retained");
        Assert(Array.Exists(paths, path => string.Equals(path, bridgePath, StringComparison.OrdinalIgnoreCase)),
            "bridge assembly explicitly referenced");
        Assert(!Array.Exists(paths, path => string.Equals(path, shadowPath, StringComparison.OrdinalIgnoreCase)),
            "same-name external metadata path excluded");
    }

    private static void Assert(bool condition, string check)
    {
        if (!condition) throw new Exception("FAIL: " + check);
    }
}
