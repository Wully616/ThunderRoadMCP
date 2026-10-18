using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
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
        Console.WriteLine("PASS: live status shape, loaded assembly MVID, arithmetic, metadata source precedence, game API metadata, runtime host reference, newest bridge log page, mapped diagnostic, runtime exception, DTO serialization, pending Task rejection, and canceled queue. CLR " + Environment.Version);
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
