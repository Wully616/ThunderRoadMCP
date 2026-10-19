using System;
using System.IO;
using System.Reflection;
using System.Collections;
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
        if (args.Length == 3 && args[0] == "--validate-examples")
        {
            CSharpEvaluator.Configure(Path.GetFullPath(args[1]));
            WriteExampleValidationReport(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            return;
        }
        if (args.Length != 1) throw new ArgumentException("Pass the packaged Roslyn payload directory, or --validate-examples <payload-directory> <report-path>.");
        CSharpEvaluator.Configure(Path.GetFullPath(args[0]));
        VerifyExampleCompilation();
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

    private static void VerifyExampleCompilation()
    {
        int before = CountEvaluationAssemblies();
        foreach (ApiExample example in GameplayExamples.All)
        {
            if (!string.Equals(example.Language, "csharp", StringComparison.Ordinal)) continue;
            CSharpEvaluator.Validate(example.Code, CancellationToken.None);
            foreach (string cleanup in example.Cleanup)
                CSharpEvaluator.Validate(cleanup, CancellationToken.None);
        }
        Assert(CountEvaluationAssemblies() == before, "example validation emits without loading evaluation assemblies");
    }

    private static void WriteExampleValidationReport(string payloadDirectory, string reportPath)
    {
        int before = CountEvaluationAssemblies();
        var validatedExamples = new JArray();
        foreach (ApiExample example in GameplayExamples.All)
        {
            string sourceHash = HashText(example.Code);
            var cleanupHashes = new JArray();
            var evidenceHashParts = new List<string> { example.Language, sourceHash };
            foreach (string cleanup in example.Cleanup)
            {
                string cleanupHash = HashText(cleanup);
                cleanupHashes.Add(cleanupHash);
                evidenceHashParts.Add(cleanupHash);
            }
            string exampleInputHash = HashText(string.Join("\n", evidenceHashParts));
            bool compileChecked = string.Equals(example.Language, "csharp", StringComparison.Ordinal);
            if (compileChecked)
            {
                CSharpEvaluator.Validate(example.Code, CancellationToken.None);
                foreach (string cleanup in example.Cleanup)
                    CSharpEvaluator.Validate(cleanup, CancellationToken.None);
            }
            validatedExamples.Add(new JObject
            {
                ["id"] = example.Id,
                ["sourceSha256"] = sourceHash,
                ["cleanupSha256"] = cleanupHashes,
                ["compileInputSha256"] = exampleInputHash,
                ["evidence"] = compileChecked ? "compile-checked" : example.Evidence,
                ["language"] = example.Language
            });
        }
        Assert(CountEvaluationAssemblies() == before, "example validation does not load evaluation assemblies");

        string modRoot = Directory.GetParent(payloadDirectory)?.FullName;
        if (modRoot == null) throw new InvalidOperationException("Roslyn payload directory must be inside the mod root.");
        string bridgePath = Path.GetFullPath(Path.Combine(modRoot, "ThunderRoadMcp.Bridge.dll"));
        string compilerPayloadPath = Path.Combine(payloadDirectory, "ThunderRoadMcp.Compiler.dll.payload");
        string compilerPayloadHash = null;
        string[] compilerPayloadPaths = Directory.GetFiles(payloadDirectory, "*.dll.payload", SearchOption.TopDirectoryOnly);
        Array.Sort(compilerPayloadPaths, StringComparer.Ordinal);
        var compilerPayloadFiles = new JArray();
        var compilerPayloadFingerprints = new List<string>(compilerPayloadPaths.Length);
        foreach (string path in compilerPayloadPaths)
        {
            string fileName = Path.GetFileName(path);
            string contentHash = HashFile(path);
            compilerPayloadFingerprints.Add(fileName + "=" + contentHash);
            compilerPayloadFiles.Add(new JObject { ["fileName"] = fileName, ["sha256"] = contentHash });
            if (string.Equals(fileName, "ThunderRoadMcp.Compiler.dll.payload", StringComparison.Ordinal))
                compilerPayloadHash = contentHash;
        }
        if (compilerPayloadHash == null) throw new FileNotFoundException("Roslyn compiler payload not found.", compilerPayloadPath);
        compilerPayloadFingerprints.Sort(StringComparer.Ordinal);
        string compilerPayloadSetId = HashText(string.Join("\n", compilerPayloadFingerprints));
        string referencedBridgeAssemblyHash = HashFile(bridgePath);
        Assembly compiler = FindCompilerAssembly();
        MethodInfo getPaths = compiler.GetType("ThunderRoadMcp.Compiler.CSharpEvaluator", true)
            .GetMethod("GetReferencePaths", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
        Assert(getPaths != null, "compiler reference path selector available");
        string[] paths = (string[])getPaths.Invoke(null, new object[] { bridgePath });
        var referenceFiles = new JArray();
        var referenceFingerprints = new List<string>(paths.Length);
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            foreach (string path in paths)
            {
                string contentHash;
                using (FileStream stream = File.OpenRead(path))
                    contentHash = Hex(sha256.ComputeHash(stream));
                string fileName = Path.GetFileName(path);
                referenceFingerprints.Add(fileName + "=" + contentHash);
                referenceFiles.Add(new JObject { ["fileName"] = fileName, ["sha256"] = contentHash });
            }
            referenceFingerprints.Sort(StringComparer.Ordinal);
            string referenceSetId = Hex(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", referenceFingerprints))));
            Assembly loadedBridge = typeof(McpRuntime).Assembly;
            string loadedBridgeMvid = loadedBridge.ManifestModule.ModuleVersionId.ToString("D");
            string loadedBridgeAssemblyHash = null;
            if (!loadedBridge.IsDynamic)
            {
                string loadedBridgePath = null;
                try { loadedBridgePath = loadedBridge.Location; }
                catch (NotSupportedException) { }
                if (!string.IsNullOrEmpty(loadedBridgePath) && File.Exists(loadedBridgePath))
                    loadedBridgeAssemblyHash = HashFile(loadedBridgePath);
            }
            string compilerMvid = compiler.ManifestModule.ModuleVersionId.ToString("D");
            string evaluatorIdentity = HashText("loadedBridgeMvid=" + loadedBridgeMvid +
                "\nloadedBridgeSha256=" + (loadedBridgeAssemblyHash ?? "unavailable") +
                "\nreferencedBridgeSha256=" + referencedBridgeAssemblyHash +
                "\ncompilerMvid=" + compilerMvid + "\ncompilerPayloadSetId=" + compilerPayloadSetId +
                "\nreferenceSetId=" + referenceSetId);
            var report = new JObject
            {
                ["schemaVersion"] = 3,
                ["scope"] = "Compile-only evaluator-wrapper validation; emitted assemblies are not loaded or executed; gameplay is not live-tested.",
                ["evaluatorIdentity"] = evaluatorIdentity,
                ["referenceSetId"] = referenceSetId,
                ["referenceFiles"] = referenceFiles,
                ["loadedBridgeMvid"] = loadedBridgeMvid,
                ["loadedBridgeAssemblySha256"] = loadedBridgeAssemblyHash,
                ["loadedBridgeFileBacked"] = loadedBridgeAssemblyHash != null,
                ["referencedBridgeAssemblySha256"] = referencedBridgeAssemblyHash,
                ["compilerMvid"] = compilerMvid,
                ["compilerAssemblySha256"] = compilerPayloadHash,
                ["compilerPayloadSetId"] = compilerPayloadSetId,
                ["compilerPayloadFiles"] = compilerPayloadFiles,
                ["examples"] = validatedExamples
            };
            string directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(reportPath, report.ToString(Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
        }
        Console.WriteLine("Wrote compile-only example evidence to " + reportPath);
    }

    private static Assembly FindCompilerAssembly()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (string.Equals(assembly.GetName().Name, "ThunderRoadMcp.Compiler", StringComparison.Ordinal))
                return assembly;
        throw new InvalidOperationException("The evaluator compiler assembly was not loaded.");
    }

    private static int CountEvaluationAssemblies()
    {
        int count = 0;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetName().Name.StartsWith("ThunderRoadMcpEvaluation_", StringComparison.Ordinal))
                count++;
        return count;
    }

    private static string HashText(string value)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
            return Hex(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    private static string Hex(byte[] bytes)
    {
        var text = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) text.Append(value.ToString("X2"));
        return text.ToString();
    }

    private static string HashFile(string path)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return Hex(sha256.ComputeHash(stream));
    }

    private static void VerifyRuntimeValues()
    {
        McpRuntime runtime = CreateRuntimeForSelfTest();
        runtime.Set("counter", 0);
        Assert(runtime.TryGetValue<int>("counter", out int counter) && counter == 0 && runtime.GetValue<int>("counter") == 0,
            "value retrieval preserves a stored zero");
        Assert(!runtime.TryGet<string>("counter", out string mismatch) && mismatch == null,
            "legacy reference retrieval reports a type mismatch");
        Assert(!runtime.TryGetValue<string>("counter", out mismatch) && mismatch == null,
            "unconstrained retrieval reports a type mismatch");
        Assert(!runtime.TryGetValue<int>("missing", out int missing) && missing == 0 && runtime.GetValue<int>("missing") == 0,
            "missing runtime values return default and TryGetValue reports absence");
        runtime.Set("null", null);
        Assert(runtime.TryGet<string>("null", out string storedNull) && storedNull == null &&
            runtime.TryGetValue<string>("null", out storedNull) && storedNull == null,
            "reference retrieval distinguishes stored null from missing");
        Assert(!runtime.TryGetValue<int>("null", out missing) && runtime.GetValue<int>("null") == 0,
            "stored null is a type mismatch for value retrieval");

        JObject zero = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("counter")));
        Assert((bool)zero["found"] && (string)zero["type"] == "System.Int32" &&
            (string)zero["value"]["kind"] == "integer" && (int)zero["value"]["value"] == 0,
            "runtime inspection returns an exact integer scalar");
        JObject absent = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("missing")));
        JObject nullValue = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("null")));
        Assert(!(bool)absent["found"] && absent["value"].Type == JTokenType.Null &&
            (bool)nullValue["found"] && (string)nullValue["value"]["kind"] == "null",
            "runtime inspection distinguishes missing from stored null");

        runtime.Set("boolean", true);
        runtime.Set("decimal", 1234567890.0123456789012345678m);
        runtime.Set("largeInteger", long.MaxValue);
        runtime.Set("scaledDecimal", 1.2300m);
        runtime.Set("nan", double.NaN);
        runtime.Set("positiveInfinity", float.PositiveInfinity);
        runtime.Set("negativeInfinity", double.NegativeInfinity);
        runtime.Set("enum", InspectionSample.Second);
        JObject boolean = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("boolean")));
        JObject decimalValue = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("decimal")));
        JObject scaledDecimal = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("scaledDecimal")));
        JObject largeInteger = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("largeInteger")));
        JObject nan = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("nan")));
        JObject positiveInfinity = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("positiveInfinity")));
        JObject negativeInfinity = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("negativeInfinity")));
        JObject enumValue = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("enum")));
        Assert((string)boolean["value"]["kind"] == "boolean" && (bool)boolean["value"]["value"],
            "boolean inspection preserves boolean type");
        Assert((string)decimalValue["value"]["value"] == "1234567890.0123456789012345678" &&
            (string)decimalValue["value"]["representation"] == "decimalString",
            "decimal inspection preserves decimal precision");
        Assert((string)scaledDecimal["value"]["value"] == "1.2300",
            "decimal inspection preserves the stored decimal scale");
        Assert((string)largeInteger["value"]["value"] == long.MaxValue.ToString() &&
            (string)largeInteger["value"]["representation"] == "decimalString",
            "large integer inspection avoids JSON consumer precision loss");
        Assert((string)nan["value"]["value"] == "NaN" && (string)nan["value"]["representation"] == "string" &&
            (string)positiveInfinity["value"]["value"] == "+Infinity" &&
            (string)negativeInfinity["value"]["value"] == "-Infinity",
            "non-finite floating-point values use labeled string representations");
        Assert((string)enumValue["value"]["kind"] == "enum" &&
            (string)enumValue["value"]["name"] == "Second" && (string)enumValue["value"]["numericValue"] == "2",
            "enum inspection returns a name and exact underlying numeric value");

        runtime.Set("text", new string('x', 1024));
        JObject bounded = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("text")));
        string inspectedText = (string)bounded["value"]["value"];
        Assert((bool)bounded["value"]["truncated"] && inspectedText.Length <= 256 &&
            inspectedText.EndsWith("[truncated]", StringComparison.Ordinal),
            "runtime inspection bounds strings and reports truncation");

        runtime.Set("surrogate", new string('x', 243) + "\U0001F600" + new string('y', 20));
        string surrogatePreview = (string)JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("surrogate")))["value"]["value"];
        string surrogatePrefix = surrogatePreview.Substring(0, surrogatePreview.Length - "…[truncated]".Length);
        Assert(!char.IsHighSurrogate(surrogatePrefix[surrogatePrefix.Length - 1]),
            "string truncation does not split a UTF-16 surrogate pair");

        UnityEngine.ScriptableObject destroyedObject =
            (UnityEngine.ScriptableObject)FormatterServices.GetUninitializedObject(typeof(UnityEngine.ScriptableObject));
        runtime.Set("destroyed", destroyedObject);
        JObject destroyed = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("destroyed")));
        Assert((string)destroyed["value"]["kind"] == "unityObject" && (bool)destroyed["value"]["destroyed"] &&
            destroyed["value"]["name"].Type == JTokenType.Null && destroyed["value"]["instanceId"].Type == JTokenType.Null,
            "destroyed Unity objects are identified without reading their name or instance ID");

        var items = new List<int>();
        for (int index = 0; index < 20; index++) items.Add(index);
        runtime.Set("collection", items);
        JObject collection = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("collection")));
        Assert((string)collection["value"]["kind"] == "collection" && (int)collection["value"]["count"] == 20 &&
            (bool)collection["value"]["truncated"] && ((JArray)collection["value"]["elements"]).Count == 16,
            "exact List<T> inspection is bounded by element count");
        runtime.Set("vector", new UnityEngine.Vector3(float.NaN, 2f, 3f));
        JObject vector = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("vector")));
        Assert((string)vector["value"]["kind"] == "unityValue" &&
            (string)vector["value"]["value"]["x"] == "NaN" && (bool)vector["value"]["nonFiniteComponentsAreStrings"],
            "Unity value type inspection labels non-finite components without failing JSON serialization");

        HostileInspectable.GetterCalls = 0;
        HostileInspectable.EnumerationCalls = 0;
        runtime.Set("hostile", new HostileInspectable());
        JObject hostile = JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Inspect("hostile")));
        Assert((string)hostile["value"]["kind"] == "opaque" && HostileInspectable.GetterCalls == 0 &&
            HostileInspectable.EnumerationCalls == 0,
            "unsupported objects expose metadata without invoking getters or enumerators");

        Exception threadError = null;
        var background = new Thread(() =>
        {
            try { runtime.TryGetValue<int>("counter", out _); }
            catch (Exception exception) { threadError = exception; }
        });
        background.Start();
        background.Join();
        Assert(threadError is InvalidOperationException && threadError.Message.Contains("Unity main thread"),
            "runtime retrieval remains main-thread only");

        Exception inspectThreadError = null;
        var inspectBackground = new Thread(() =>
        {
            try { runtime.Inspect("counter"); }
            catch (Exception exception) { inspectThreadError = exception; }
        });
        inspectBackground.Start();
        inspectBackground.Join();
        Assert(inspectThreadError is InvalidOperationException && inspectThreadError.Message.Contains("Unity main thread"),
            "runtime inspection remains main-thread only");

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

        string worstInspect = BridgeRequestHandler.SerializeResult(pathologicalRuntime.Inspect("000" + new string('\0', McpRuntime.MaxKeyLength - 3)));
        byte[] response = BridgeServer.BuildRawSuccessResponse(new string('i', 128), worstInspect, null);
        Assert(response.Length <= BridgeRequestHandler.MaxLineBytes,
            "runtime inspect DTO plus UTF-8 escaping and response envelope fit the 64 KiB bridge limit");
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
        var successfulTimings = BridgeRequestTimings.CreateForSelfTest();
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

        var failedTimings = BridgeRequestTimings.CreateForSelfTest();
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
        var compilationTimings = BridgeRequestTimings.CreateForSelfTest();
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
        var timings = BridgeRequestTimings.CreateForSelfTest();
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

    private enum InspectionSample
    {
        First = 1,
        Second = 2
    }

    private sealed class HostileInspectable : IEnumerable
    {
        public static int GetterCalls;
        public static int EnumerationCalls;

        public object ExplodingProperty
        {
            get
            {
                GetterCalls++;
                throw new InvalidOperationException("Getter must not be invoked.");
            }
        }

        public IEnumerator GetEnumerator()
        {
            EnumerationCalls++;
            throw new InvalidOperationException("Enumerator must not be invoked.");
        }
    }

    private static void Assert(bool condition, string check)
    {
        if (!condition) throw new Exception("FAIL: " + check);
    }
}
