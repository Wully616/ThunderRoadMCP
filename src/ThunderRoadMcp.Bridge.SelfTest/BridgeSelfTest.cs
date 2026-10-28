using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
        BridgeResultContractCheck.Run();
        VerifyExampleCompilation();
        VerifyReferenceSourcePrecedence(managed);
        VerifyCompileOnlyValidation();
        object arithmetic = CSharpEvaluator.Compile("return 6 * 7;", CancellationToken.None)();
        Assert((int)arithmetic == 42, "arithmetic result");

        string gameApi = (string)CSharpEvaluator.Compile("return typeof(ThunderScript).FullName;", CancellationToken.None)();
        Assert(gameApi == "ThunderRoad.ThunderScript", "installed ThunderRoad API metadata");

        string runtimeProbe = (string)CSharpEvaluator.Compile("return McpRuntime.Current == null ? \"none\" : \"present\";", CancellationToken.None)();
        Assert(runtimeProbe == "none", "eval compiles against the bridge runtime host");

        VerifyStatusAndAssemblyIdentity(thunderRoad, managed);
        VerifyLoadedAssemblyPaging();
        VerifyRuntimeValues();
        VerifyRequestTimings();
        VerifyBridgeServerRoundTrip();
        VerifyCoroutineObservability();
        VerifyComponentTypeResolution();

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
        Console.WriteLine("PASS: live status shape, loaded assembly MVID, arithmetic, metadata source precedence, game API metadata, runtime host reference, generic runtime retrieval and inspection, bounded runtime list, bridge request/response round trip with an open client, nested coroutine disposal and recursive-iterator rejection, stop and failure observability, spawn rollback and cancellation settlement, successful and failed bridge timing stages, client-disconnect queue cancellation, scene query component type resolution, newest bridge log page, mapped diagnostic, runtime exception, DTO serialization, pending Task rejection, and canceled queue. CLR " + Environment.Version);
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
        FieldInfo recentFailures = typeof(McpRuntime).GetField("recentFailures", BindingFlags.Instance | BindingFlags.NonPublic);
        recentFailures.SetValue(runtime, Activator.CreateInstance(recentFailures.FieldType));
        typeof(McpRuntime).GetField("mainThreadId", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(runtime, Thread.CurrentThread.ManagedThreadId);
        return runtime;
    }

    private static IEnumerator FailingRoutine()
    {
        yield return null;
        yield return null;
        throw new InvalidOperationException("routine failed");
    }

    private static IEnumerator NestedFinallyRoutine(List<string> disposed, string name, bool fail = false)
    {
        try
        {
            yield return null;
            if (fail) throw new InvalidOperationException("nested routine failed");
        }
        finally
        {
            disposed.Add(name);
        }
    }

    private static IEnumerator ParentFinallyRoutine(List<string> disposed, string name, IEnumerator child)
    {
        try
        {
            yield return child;
            yield return null;
        }
        finally
        {
            disposed.Add(name);
        }
    }

    private static IEnumerator TrackForSelfTest(McpRuntime runtime, string key, IEnumerator routine)
    {
        FieldInfo coroutineField = typeof(McpRuntime).GetField("coroutines", BindingFlags.Instance | BindingFlags.NonPublic);
        IDictionary entries = (IDictionary)coroutineField.GetValue(runtime);
        Type entryType = coroutineField.FieldType.GetGenericArguments()[1];
        object entry = FormatterServices.GetUninitializedObject(entryType);
        entryType.GetField("Routine").SetValue(entry, routine.GetType().FullName);
        entryType.GetField("StartedUtc").SetValue(entry, DateTime.UtcNow);
        entries.Add(key, entry);
        MethodInfo track = typeof(McpRuntime).GetMethod("Track", BindingFlags.Instance | BindingFlags.NonPublic);
        IEnumerator tracked = (IEnumerator)track.Invoke(runtime, new object[] { key, entry, routine });
        entryType.GetField("Tracked").SetValue(entry, tracked);
        return tracked;
    }

    private sealed class TestCustomYieldInstruction : UnityEngine.CustomYieldInstruction
    {
        public override bool keepWaiting => true;
    }

    private sealed class RecursiveEnumerator : IEnumerator, IDisposable
    {
        public object Current => this;
        public bool Disposed { get; private set; }
        public bool MoveNext() => true;
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }

    private static IEnumerator YieldObject(object value)
    {
        yield return value;
    }

    private static void VerifyCoroutineObservability()
    {
        McpRuntime runtime = CreateRuntimeForSelfTest();
        FieldInfo coroutineField = typeof(McpRuntime).GetField("coroutines", BindingFlags.Instance | BindingFlags.NonPublic);
        IDictionary coroutineEntries = (IDictionary)coroutineField.GetValue(runtime);
        Type entryType = coroutineField.FieldType.GetGenericArguments()[1];
        object failing = FormatterServices.GetUninitializedObject(entryType);
        entryType.GetField("Routine").SetValue(failing, "Test.FailingRoutine");
        entryType.GetField("StartedUtc").SetValue(failing, DateTime.UtcNow);
        coroutineEntries.Add("failing", failing);

        MethodInfo track = typeof(McpRuntime).GetMethod("Track", BindingFlags.Instance | BindingFlags.NonPublic);
        IEnumerator tracked = (IEnumerator)track.Invoke(runtime, new object[] { "failing", failing, FailingRoutine() });
        Assert(tracked.MoveNext() && tracked.MoveNext() && (int)entryType.GetField("Steps").GetValue(failing) == 2,
            "tracked coroutine counts each yielded step");
        try
        {
            tracked.MoveNext();
            throw new Exception("Expected the tracked coroutine failure.");
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message == "routine failed", "tracked coroutine rethrows its failure");
        }
        Assert(coroutineEntries.Count == 0, "a failed coroutine leaves the managed list");

        JArray failures = (JArray)JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Describe()))["recentFailures"];
        Assert(failures.Count == 1 && (string)failures[0]["key"] == "failing" &&
            (string)failures[0]["routine"] == "Test.FailingRoutine" && (int)failures[0]["steps"] == 2 &&
            ((string)failures[0]["error"]).StartsWith("System.InvalidOperationException: routine failed", StringComparison.Ordinal),
            "recent failures record the routine, step count, and exception text");

        var normallyDisposed = new List<string>();
        McpRuntime normalRuntime = CreateRuntimeForSelfTest();
        IEnumerator normal = TrackForSelfTest(normalRuntime, "normal",
            ParentFinallyRoutine(normallyDisposed, "parent", NestedFinallyRoutine(normallyDisposed, "nested")));
        Assert(normal.MoveNext() && normal.Current == null && normal.MoveNext() && normal.Current == null && !normal.MoveNext() &&
            normallyDisposed.Count == 2 && normallyDisposed[0] == "nested" && normallyDisposed[1] == "parent",
            "normal completion disposes nested and parent iterators");

        var exceptionDisposed = new List<string>();
        McpRuntime nestedFailureRuntime = CreateRuntimeForSelfTest();
        IEnumerator nestedFailure = TrackForSelfTest(nestedFailureRuntime, "nested-failure",
            ParentFinallyRoutine(exceptionDisposed, "parent", NestedFinallyRoutine(exceptionDisposed, "nested", true)));
        Assert(nestedFailure.MoveNext(), "nested failure test starts its child iterator");
        try
        {
            nestedFailure.MoveNext();
            throw new Exception("Expected the nested coroutine failure.");
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message == "nested routine failed", "nested iterator exceptions propagate");
        }
        bool endedAfterNestedFailure = !nestedFailure.MoveNext();
        JArray nestedFailures = (JArray)JObject.Parse(BridgeRequestHandler.SerializeResult(nestedFailureRuntime.Describe()))["recentFailures"];
        Assert(exceptionDisposed.Count == 2 && exceptionDisposed[0] == "nested" && exceptionDisposed[1] == "parent" &&
            nestedFailures.Count == 1 && (string)nestedFailures[0]["key"] == "nested-failure" && endedAfterNestedFailure,
            "nested exception disposes the whole chain and records one failure");

        var recursiveRuntime = CreateRuntimeForSelfTest();
        var recursive = new RecursiveEnumerator();
        IEnumerator recursiveTracked = TrackForSelfTest(recursiveRuntime, "recursive", recursive);
        try
        {
            recursiveTracked.MoveNext();
            throw new Exception("Expected a recursive nested iterator rejection.");
        }
        catch (InvalidOperationException exception)
        {
            Assert(exception.Message.IndexOf("active in its nested iterator chain", StringComparison.Ordinal) >= 0 &&
                   recursive.Disposed,
                "a recursive nested iterator fails clearly and disposes its chain");
        }
        Assert(!recursiveTracked.MoveNext(), "a failed recursive coroutine stays stopped");

        McpRuntime customYieldRuntime = CreateRuntimeForSelfTest();
        var customYield = new TestCustomYieldInstruction();
        IEnumerator customYieldRoutine = TrackForSelfTest(customYieldRuntime, "custom-yield", YieldObject(customYield));
        Assert(customYieldRoutine.MoveNext() && ReferenceEquals(customYieldRoutine.Current, customYield),
            "Unity custom yield instructions pass through unchanged");
        ((IDisposable)customYieldRoutine).Dispose();

        var stoppedDisposals = new List<string>();
        McpRuntime stopRuntime = CreateRuntimeForSelfTest();
        IEnumerator stopped = TrackForSelfTest(stopRuntime, "stop",
            ParentFinallyRoutine(stoppedDisposals, "parent", NestedFinallyRoutine(stoppedDisposals, "nested")));
        Assert(stopped.MoveNext() && stopRuntime.StopManagedCoroutine("stop") &&
            stoppedDisposals.Count == 2 && stoppedDisposals[0] == "nested" && stoppedDisposals[1] == "parent",
            "explicit stop disposes nested and parent iterators");
        ((IDisposable)stopped).Dispose();
        Assert(stoppedDisposals.Count == 2, "repeated stop cleanup is idempotent");

        var allStoppedDisposals = new List<string>();
        McpRuntime stopAllRuntime = CreateRuntimeForSelfTest();
        IEnumerator firstStopped = TrackForSelfTest(stopAllRuntime, "first",
            ParentFinallyRoutine(allStoppedDisposals, "first-parent", NestedFinallyRoutine(allStoppedDisposals, "first-nested")));
        IEnumerator secondStopped = TrackForSelfTest(stopAllRuntime, "second",
            ParentFinallyRoutine(allStoppedDisposals, "second-parent", NestedFinallyRoutine(allStoppedDisposals, "second-nested")));
        Assert(firstStopped.MoveNext() && secondStopped.MoveNext(), "stop-all test starts both iterator chains");
        stopAllRuntime.StopAllManagedCoroutines();
        JObject stoppedList = JObject.Parse(BridgeRequestHandler.SerializeResult(stopAllRuntime.Describe()));
        Assert(allStoppedDisposals.Count == 4 && (int)stoppedList["coroutineCount"] == 0,
            "stop-all disposes every managed iterator chain and removes every entry");

        var clearedDisposals = new List<string>();
        McpRuntime clearRuntime = CreateRuntimeForSelfTest();
        IEnumerator cleared = TrackForSelfTest(clearRuntime, "clear",
            ParentFinallyRoutine(clearedDisposals, "parent", NestedFinallyRoutine(clearedDisposals, "nested")));
        Assert(cleared.MoveNext(), "clear test starts its iterator chain");
        clearRuntime.Clear();
        Assert(clearedDisposals.Count == 2 && clearedDisposals[0] == "nested" && clearedDisposals[1] == "parent",
            "runtime clear disposes nested and parent iterators");
        ((IDisposable)cleared).Dispose();
        Assert(clearedDisposals.Count == 2, "clear cleanup is idempotent");

        bool rollbackAttempted = false;
        int failedRollbackAttempts = 0;
        BridgeRequestHandler.RollbackSpawnIfUncommitted(new object(), false, _ => rollbackAttempted = true);
        BridgeRequestHandler.RollbackSpawnIfUncommitted(new object(), false, _ =>
        {
            failedRollbackAttempts++;
            throw new InvalidOperationException("cleanup failure");
        });
        BridgeRequestHandler.RollbackSpawnIfUncommitted(new object(), true,
            _ => throw new InvalidOperationException("must not roll back a committed spawn"));
        var abandonedCompletion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        BridgeRequestHandler.SettleAbandonedSpawn(abandonedCompletion);
        var completedSpawn = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        completedSpawn.TrySetResult("committed");
        BridgeRequestHandler.SettleAbandonedSpawn(completedSpawn);
        Assert(rollbackAttempted && failedRollbackAttempts == 1 && abandonedCompletion.Task.IsCanceled &&
            completedSpawn.Task.Result.Equals("committed"),
            "abandoned spawn cleanup attempts rollback, preserves completion, and settles pending requests");

        MethodInfo recordFailure = typeof(McpRuntime).GetMethod("RecordCoroutineFailure", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int index = 0; index < 20; index++)
            recordFailure.Invoke(runtime, new object[] { "k" + index, failing, new InvalidOperationException("failure " + index) });
        failures = (JArray)JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Describe()))["recentFailures"];
        Assert(failures.Count == 16 && (string)failures[0]["key"] == "k4" && (string)failures[15]["key"] == "k19",
            "recent failures keep only the newest 16");

        object live = FormatterServices.GetUninitializedObject(entryType);
        entryType.GetField("Routine").SetValue(live, "Test.LiveRoutine");
        entryType.GetField("StartedUtc").SetValue(live, DateTime.UtcNow);
        entryType.GetField("Steps").SetValue(live, 3);
        coroutineEntries.Add("live", live);
        string liveJson = BridgeRequestHandler.SerializeResult(runtime.Describe());
        JObject listed = (JObject)((JArray)JObject.Parse(liveJson)["coroutines"])[0];
        Assert((string)listed["key"] == "live" && (string)listed["routine"] == "Test.LiveRoutine" && (int)listed["steps"] == 3 &&
            System.Text.RegularExpressions.Regex.IsMatch(liveJson, "\"startedAtUtc\":\"\\d{4}-\\d{2}-\\d{2}T[\\d:.]+Z\""),
            "list reports each coroutine's routine, UTC start time, and steps");

        runtime.Clear();
        Assert(((JArray)JObject.Parse(BridgeRequestHandler.SerializeResult(runtime.Describe()))["recentFailures"]).Count == 0,
            "clear empties the coroutine failure history");
    }

    private static void VerifyComponentTypeResolution()
    {
        Assert(BridgeRequestHandler.ResolveComponentType("Transform") == typeof(UnityEngine.Transform) &&
            BridgeRequestHandler.ResolveComponentType("UnityEngine.Transform") == typeof(UnityEngine.Transform),
            "scene query resolves simple and full Component type names");
        try
        {
            BridgeRequestHandler.ResolveComponentType("NoSuchComponentXyz");
            throw new Exception("Expected an unknown component type rejection.");
        }
        catch (InvalidDataException ex)
        {
            Assert(ex.Message.Contains("api_search"), "unknown component type points to api_search");
        }
    }

    private static void VerifyCompileOnlyValidation()
    {
        var handler = new BridgeRequestHandler(new BridgeConfig());
        int loadedEvaluationAssembliesBefore = CountEvaluationAssemblies();
        string sideEffectPath = Path.Combine(Path.GetTempPath(), "thunderroad-mcp-validation-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            AssertValidationRejected(handler, new { code = " ", timeoutMs = 30000 }, "validate rejects an empty body");
            AssertValidationRejected(handler, new { code = "return null;", timeoutMs = 30001 }, "validate rejects timeout above the limit");
            JObject success = DispatchValidation(handler, "return 42;");
            Assert((bool)success["valid"] && ((JArray)success["diagnostics"]).Count == 0,
                "compile-only validation accepts valid code: " + success.ToString());

            JObject warning = DispatchValidation(handler, "int unused = 1; return null;");
            Assert((bool)warning["valid"] && ((JArray)warning["diagnostics"]).Any(d => (string)d["severity"] == "warning"),
                "compile-only validation keeps warning-only code valid and reports warnings");

            JObject failure = DispatchValidation(handler, "\nreturn missingValidationName;");
            JObject diagnostic = (JObject)((JArray)failure["diagnostics"])[0];
            Assert(!(bool)failure["valid"] && (string)diagnostic["id"] == "CS0103" &&
                   (int)diagnostic["line"] == 2 && (int)diagnostic["column"] == 8 &&
                   (int)diagnostic["span"]["start"] == 8,
                "compile-only validation returns semantic errors mapped to submitted source coordinates");

            JObject syntaxFailure = DispatchValidation(handler, "return (");
            Assert(!(bool)syntaxFailure["valid"] &&
                   ((JArray)syntaxFailure["diagnostics"]).Any(d => (string)d["severity"] == "error"),
                "compile-only validation returns syntax errors as invalid results");

            string escapedPath = sideEffectPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            JObject sideEffect = DispatchValidation(handler,
                "System.IO.File.WriteAllText(\"" + escapedPath + "\", \"executed\"); return null;");
            Assert((bool)sideEffect["valid"] && !File.Exists(sideEffectPath),
                "compile-only validation never invokes the submitted body");
            Assert(CountEvaluationAssemblies() == loadedEvaluationAssembliesBefore,
                "compile-only validation never loads an evaluation assembly");
        }
        finally
        {
            if (File.Exists(sideEffectPath)) File.Delete(sideEffectPath);
        }
    }

    private static JObject DispatchValidation(BridgeRequestHandler handler, string code)
    {
        var result = (BridgeJsonResult)DispatchAndDrain(handler, new BridgeRequest
        {
            method = "validate",
            parameters = JObject.FromObject(new { code, timeoutMs = 30000 })
        }, BridgeRequestTimings.CreateForSelfTest());
        return JObject.Parse(result.Json);
    }
    private static void AssertValidationRejected(BridgeRequestHandler handler, object parameters, string message)
    {
        try
        {
            handler.DispatchAsync(new BridgeRequest
            {
                method = "validate",
                parameters = JObject.FromObject(parameters)
            }, CancellationToken.None, BridgeRequestTimings.CreateForSelfTest()).GetAwaiter().GetResult();
            throw new Exception("Expected validation request rejection.");
        }
        catch (InvalidDataException)
        {
            Assert(true, message);
        }
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
        catch (BridgeEvaluationException ex)
        {
            Assert(ex.errorCode == "evaluation.invocation_failed" && ex.executionOutcome == "unknown" &&
                   ex.Message.Contains("timed failure"), "timed evaluation failure propagation");
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
        catch (BridgeEvaluationException ex)
        {
            Assert(ex.errorCode == "evaluation.compilation_failed" && ex.executionOutcome == "not_started" &&
                   ex.Message.Contains("CS0103"), "timed compilation failure propagation");
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

    private static void VerifyBridgeServerRoundTrip()
    {
        int port;
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        finally
        {
            reservation.Stop();
        }
        var config = new BridgeConfig { Enabled = true, Host = "127.0.0.1", Port = port, Token = new string('a', 64) };
        using (var server = new BridgeServer(config))
        {
            server.Start();
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                NetworkStream stream = client.GetStream();
                byte[] request = System.Text.Encoding.UTF8.GetBytes(
                    "{\"id\":\"server-selftest\",\"token\":\"" + config.Token + "\",\"method\":\"logs\",\"parameters\":{}}\n");
                stream.Write(request, 0, request.Length);
                var buffer = new byte[4096];
                int total = 0;
                while (Array.IndexOf(buffer, (byte)'\n', 0, total) < 0 && total < buffer.Length)
                {
                    Task<int> read = stream.ReadAsync(buffer, total, buffer.Length - total);
                    Assert(read.Wait(TimeSpan.FromSeconds(10)),
                        "bridge returns a response while the client keeps the connection open");
                    if (read.Result == 0) break;
                    total += read.Result;
                }
                JObject response = JObject.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, total));
                Assert((string)response["id"] == "server-selftest" && (bool)response["success"] &&
                       response["result"]["entries"] is JArray,
                    "bridge server completes a full request/response round trip");
            }
        }
    }

    private static void VerifyCanceledQueueWait(BridgeRequestHandler handler)
    {
        var timings = BridgeRequestTimings.CreateForSelfTest();
        using (var requestLifetime = new CancellationTokenSource())
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    using (var accepted = listener.AcceptTcpClient())
                    {
                        MethodInfo monitorMethod = typeof(BridgeServer).GetMethod(
                            "MonitorClientDisconnect", BindingFlags.Static | BindingFlags.NonPublic);
                        Assert(monitorMethod != null, "bridge client-disconnect monitor is available to its self-test");
                        Task monitor = (Task)monitorMethod.Invoke(null, new object[]
                        {
                            accepted.GetStream(), requestLifetime
                        });
                        try
                        {
                            byte[] probe = System.Text.Encoding.ASCII.GetBytes("ok\n");
                            accepted.GetStream().Write(probe, 0, probe.Length);
                            var received = new byte[probe.Length];
                            Task<int> probeRead = client.GetStream().ReadAsync(received, 0, received.Length);
                            Assert(probeRead.Wait(TimeSpan.FromSeconds(5)) && probeRead.Result == probe.Length &&
                                   System.Text.Encoding.ASCII.GetString(received) == "ok\n",
                                "a pending client-disconnect read does not block the response write");

                            Task<object> dispatch = handler.DispatchAsync(new BridgeRequest
                            {
                                method = "evaluate",
                                parameters = JObject.FromObject(new { code = "return 6 * 7;", timeoutMs = 30000 })
                            }, requestLifetime.Token, timings);
                            object gate = typeof(MainThreadQueue).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                            object queue = typeof(MainThreadQueue).GetField("Queue", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                            var count = queue.GetType().GetProperty("Count");
                            Assert(SpinWait.SpinUntil(() =>
                            {
                                lock (gate) return (int)count.GetValue(queue) > 0;
                            }, TimeSpan.FromSeconds(30)), "evaluation reaches the main-thread queue before client disconnect");

                            client.Close();
                            Assert(SpinWait.SpinUntil(() => requestLifetime.IsCancellationRequested, TimeSpan.FromSeconds(5)),
                                "client EOF cancels the bridge request lifetime");
                            MainThreadQueue.Drain();
                            try
                            {
                                dispatch.GetAwaiter().GetResult();
                                throw new Exception("Expected queued evaluation cancellation after client disconnect.");
                            }
                            catch (BridgeEvaluationException exception)
                            {
                                Assert(exception.errorCode == "evaluation.not_started" &&
                                       exception.executionOutcome == "not_started",
                                    "client disconnect cancels queued evaluation before invocation");
                            }
                        }
                        finally
                        {
                            accepted.Close();
                            Assert(monitor.Wait(TimeSpan.FromSeconds(5)),
                                "client-disconnect monitor ends when its socket closes");
                        }
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }
        timings.Complete();
        Assert(timings.Snapshot().ContainsKey("mainThreadQueueWaitMs"),
            "disconnected queued evaluation reports elapsed queue wait");
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
        Assembly byteLoadedBridge = Assembly.Load(File.ReadAllBytes(typeof(McpRuntime).Assembly.Location));
        Assert(!byteLoadedBridge.IsDynamic && string.IsNullOrEmpty(byteLoadedBridge.Location),
            "Assembly.Load(byte[]) fixture has a non-dynamic fileless location");
        string externalManaged = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Managed");
        string externalMods = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Mods");
        Assembly locatedBridge = typeof(McpRuntime).Assembly;
        Assert(!string.IsNullOrEmpty(locatedBridge.Location) &&
               locatedBridge.GetName().Name == byteLoadedBridge.GetName().Name &&
               locatedBridge.GetName().Version == byteLoadedBridge.GetName().Version &&
               locatedBridge.ManifestModule.ModuleVersionId == byteLoadedBridge.ManifestModule.ModuleVersionId,
            "located and byte-loaded bridge fixtures have identical assembly identity");
        LoadedAssemblyStatus locatedInventory = BridgeRequestHandler.BuildLoadedAssemblyStatus(
            new[] { locatedBridge }, externalManaged, externalMods);
        Assert(locatedInventory.loadedAssemblyCount == 0 && locatedInventory.inventoryComplete,
            "located assembly outside selected roots is excluded from inventory");
        LoadedAssemblyStatus byteLoadedInventory = BridgeRequestHandler.BuildLoadedAssemblyStatus(
            new[] { byteLoadedBridge }, externalManaged, externalMods);
        JObject byteLoadedReported = (JObject)JObject.Parse(
            BridgeRequestHandler.SerializeResult(byteLoadedInventory))["loadedAssemblies"][0];
        Assert((string)byteLoadedReported["name"] == byteLoadedBridge.GetName().Name &&
               (string)byteLoadedReported["version"] == byteLoadedBridge.GetName().Version.ToString() &&
               (Guid)byteLoadedReported["moduleVersionId"] == byteLoadedBridge.ManifestModule.ModuleVersionId &&
               byteLoadedReported["origin"].Type == JTokenType.Null &&
               byteLoadedReported["relativePath"].Type == JTokenType.Null &&
               byteLoadedInventory.inventoryComplete,
            "byte-loaded bridge assembly retains exact identity with explicitly unknown provenance");
        Assert(BridgeRequestHandler.BuildLoadedAssemblyStatus(
                   new[] { byteLoadedBridge }, externalManaged, externalMods).inventorySnapshotId ==
               byteLoadedInventory.inventorySnapshotId,
            "fileless assembly inventory snapshot remains stable");
        JObject status = JObject.Parse(BridgeRequestHandler.SerializeResult(BridgeRequestHandler.BuildStatusPayload(
            1, "product", "game", "unity", "test-level", "Loaded", true, true, assemblyStatus)));
        Assert(status["currentLevel"].Type == JTokenType.String && (string)status["currentLevel"] == "test-level",
            "status currentLevel is a string");
        Assert((string)status["levelState"] == "Loaded" && (bool)status["sceneReady"] &&
            status["loadedAssemblies"].Type == JTokenType.Array &&
            (Guid)status["loadedAssemblies"][0]["moduleVersionId"] == thunderRoad.ManifestModule.ModuleVersionId &&
            (int)status["loadedAssemblyCount"] == 1 && !(bool)status["loadedAssembliesTruncated"] &&
            (bool)status["inventoryComplete"],
            "status inventory opt-in retains assembly inventory, MVID, and completeness");
        JObject compactStatus = JObject.Parse(BridgeRequestHandler.SerializeResult(BridgeRequestHandler.BuildStatusPayload(
            1, "product", "game", "unity", "test-level", "Loaded", true, false, assemblyStatus)));
        Assert(compactStatus.Property("loadedAssemblies") == null &&
               compactStatus.Property("loadedAssemblyCount") == null &&
               compactStatus.Property("loadedAssembliesTruncated") == null &&
               compactStatus.Property("inventoryComplete") == null,
            "compact status omits loaded assembly inventory");
        JObject noLevelStatus = JObject.Parse(BridgeRequestHandler.SerializeResult(BridgeRequestHandler.BuildStatusPayload(
            1, "product", "game", "unity", null, "Unloaded", false, false, assemblyStatus)));
        Assert(noLevelStatus["currentLevel"].Type == JTokenType.Null, "status currentLevel can be null");
    }
    private static void VerifyLoadedAssemblyPaging()
    {
        const int assemblyCount = 130;
        var entries = new object[assemblyCount];
        for (int index = 0; index < assemblyCount - 1; index++)
        {
            entries[index] = new
            {
                name = "Game." + index.ToString("D3"),
                version = "1.0.0.0",
                moduleVersionId = Guid.NewGuid(),
                origin = "game",
                relativePath = "Game." + index.ToString("D3") + ".dll"
            };
        }
        entries[assemblyCount - 1] = new
        {
            name = "Late.Mod",
            version = "1.0.0.0",
            moduleVersionId = Guid.NewGuid(),
            origin = "mod",
            relativePath = "ZMod/Late.Mod.dll"
        };
        var inventory = new LoadedAssemblyStatus
        {
            allLoadedAssemblies = entries,
            loadedAssemblies = new object[128],
            loadedAssemblyCount = assemblyCount,
            loadedAssembliesTruncated = true,
            inventoryComplete = true,
            inventorySnapshotId = "loaded1.fixture"
        };

        int offset = 0;
        int pageCount = 0;
        int collected = 0;
        bool foundLateMod = false;
        while (true)
        {
            string json = BridgeRequestHandler.BuildLoadedAssemblyPage(
                inventory, offset, 32, offset == 0 ? null : inventory.inventorySnapshotId);
            Assert(System.Text.Encoding.UTF8.GetByteCount(json) <= BridgeRequestHandler.MaxLineBytes - 512,
                "loaded assembly page stays within the bridge result budget");
            JObject page = JObject.Parse(json);
            JArray assemblies = (JArray)page["loadedAssemblies"];
            Assert((string)page["inventorySnapshotId"] == inventory.inventorySnapshotId &&
                   (int)page["loadedAssemblyCount"] == assemblyCount &&
                   (bool)page["inventoryComplete"],
                "loaded assembly page identity and completeness are stable");
            pageCount++;
            collected += assemblies.Count;
            foreach (var item in assemblies)
            {
                var assembly = (JObject)item;
                if ((string)assembly["origin"] == "mod" && (string)assembly["relativePath"] == "ZMod/Late.Mod.dll")
                    foundLateMod = true;
            }

            JToken next = page["nextOffset"];
            bool hasMore = next.Type != JTokenType.Null;
            Assert((bool)page["loadedAssembliesTruncated"] == hasMore,
                "page truncation metadata agrees with its continuation");
            if (!hasMore) break;
            int nextOffset = (int)next;
            Assert(nextOffset == offset + assemblies.Count && nextOffset > offset,
                "loaded assembly pages advance without gaps");
            offset = nextOffset;
        }
        Assert(pageCount == 5 && collected == assemblyCount && foundLateMod,
            "bounded loaded assembly pages include late-sorted mods beyond the former cap");

        bool staleRejected = false;
        try
        {
            BridgeRequestHandler.BuildLoadedAssemblyPage(inventory, 32, 32, "loaded1.stale");
        }
        catch (InvalidDataException exception)
        {
            staleRejected = exception.Message.Contains("changed");
        }
        Assert(staleRejected, "loaded assembly pages reject changed inventory snapshots");
        const int unicodeAssemblyCount = 4;
        var unicodeEntries = new object[unicodeAssemblyCount];
        for (int index = 0; index < unicodeEntries.Length; index++)
        {
            unicodeEntries[index] = new
            {
                name = "Mod.界." + index,
                version = "1.0.0.0",
                moduleVersionId = Guid.NewGuid(),
                origin = "mod",
                relativePath = "Mods/" + new string('界', 12000) + "/" + index + ".dll"
            };
        }
        var unicodeInventory = new LoadedAssemblyStatus
        {
            allLoadedAssemblies = unicodeEntries,
            loadedAssemblies = new object[0],
            loadedAssemblyCount = unicodeAssemblyCount,
            inventoryComplete = true,
            inventorySnapshotId = "loaded1.unicode"
        };
        int unicodeOffset = 0;
        int unicodePages = 0;
        int unicodeCollected = 0;
        while (unicodeOffset < unicodeAssemblyCount)
        {
            string pageJson = BridgeRequestHandler.BuildLoadedAssemblyPage(
                unicodeInventory, unicodeOffset, 32, unicodeOffset == 0 ? null : unicodeInventory.inventorySnapshotId);
            Assert(System.Text.Encoding.UTF8.GetByteCount(pageJson) <= BridgeRequestHandler.MaxLineBytes - 512,
                "multibyte long-path pages fit the UTF-8 response budget");
            JObject page = JObject.Parse(pageJson);
            int pageItems = ((JArray)page["loadedAssemblies"]).Count;
            Assert(pageItems > 0, "multibyte page always advances by at least one identity");
            unicodeCollected += pageItems;
            unicodePages++;
            JToken next = page["nextOffset"];
            if (next.Type == JTokenType.Null) break;
            unicodeOffset = (int)next;
        }
        Assert(unicodePages > 1 && unicodeCollected == unicodeAssemblyCount,
            "multibyte long paths reduce page item count without omitting identities");

        var oversizedInventory = new LoadedAssemblyStatus
        {
            allLoadedAssemblies = new object[]
            {
                new
                {
                    name = "Oversized.Mod",
                    version = "1.0.0.0",
                    moduleVersionId = Guid.NewGuid(),
                    origin = "mod",
                    relativePath = new string('界', 22000)
                }
            },
            loadedAssemblies = new object[0],
            loadedAssemblyCount = 1,
            inventoryComplete = true,
            inventorySnapshotId = "loaded1.oversized"
        };
        bool oversizedIdentityRejected = false;
        try { BridgeRequestHandler.BuildLoadedAssemblyPage(oversizedInventory, 0, 32, null); }
        catch (InvalidDataException exception)
        {
            oversizedIdentityRejected = exception.Message.Contains("64 KiB UTF-8 response budget");
        }
        Assert(oversizedIdentityRejected, "a single oversized identity fails with actionable response-budget guidance");
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
