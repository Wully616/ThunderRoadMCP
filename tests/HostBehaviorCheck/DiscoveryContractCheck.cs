using System.Reflection;
using System.Text.Json;

internal static class DiscoveryContractCheck
{
    public static void Run()
    {
        AssertDefault(typeof(GameTools), nameof(GameTools.GameStatus), "includeAssemblies", false);
        AssertDefault(typeof(GameTools), nameof(GameTools.GameFindObjects), "includeFields", false);
        AssertDefault(typeof(GameTools), nameof(GameTools.GameFindObjects), "limit", 25);
        AssertDefault(typeof(ApiDiscoveryTools), nameof(ApiDiscoveryTools.ApiLookup), "maxResults", 3);
        AssertDefault(typeof(ApiDiscoveryTools), nameof(ApiDiscoveryTools.ApiLookup), "maxChars", 8000);
        AssertDefault(typeof(ApiDiscoveryTools), nameof(ApiDiscoveryTools.ApiLookup), "includeExamples", false);
        AssertDefault(typeof(ApiNavigationTools), nameof(ApiNavigationTools.ApiFindCallers), "limit", 20);
        AssertDefault(typeof(ApiNavigationTools), nameof(ApiNavigationTools.ApiFindCallees), "limit", 20);
        AssertDefault(typeof(ApiNavigationTools), nameof(ApiNavigationTools.ApiFindUsages), "limit", 20);
        AssertDefault(typeof(ApiNavigationTools), nameof(ApiNavigationTools.ApiFindUsages), "includeDiagnostics", false);

        var launchArguments = typeof(GameTools).GetMethod(nameof(GameTools.GameLaunch))!.GetParameters()
            .Single(parameter => parameter.Name == "arguments");
        var argumentNullability = new NullabilityInfoContext().Create(launchArguments);
        Assert(launchArguments.HasDefaultValue && launchArguments.DefaultValue is null &&
               argumentNullability.ReadState == NullabilityState.Nullable,
            "game_launch keeps its optional argument array nullable");
        Assert(argumentNullability.ElementType?.ReadState == NullabilityState.NotNull,
            "game_launch schema declares non-null argument-array items");
        var rejectsNullArgument = false;
        try
        {
            GameProcess.ValidateLaunchArguments(new string[] { null! });
        }
        catch (ArgumentException exception)
        {
            rejectsNullArgument = exception.Message.Contains("index 0", StringComparison.Ordinal);
        }
        Assert(rejectsNullArgument, "game_launch runtime validation rejects JSON null argument items");
        var evaluationTool = GameTools.EvaluateCSharp(null!, "return 1;").GetAwaiter().GetResult();
        var evaluationStructured = evaluationTool.GetType().GetProperty("StructuredContent")?.GetValue(evaluationTool);
        Assert(evaluationTool.IsError == true && evaluationStructured is JsonElement structured &&
               structured.GetProperty("executionOutcome").GetString() == "unknown" &&
               structured.GetProperty("errorCode").GetString() == "evaluation.failed",
            "evaluate_csharp routes bridge-call failures through its structured unknown-outcome mapper");

        var direct = new InstalledApiCatalog.NavigationEdge(2, 0x06000001, "callvirt", 17, default);
        var potential = direct with { potentialVirtualDispatch = true };
        var otherCallSite = direct with { ilOffset = 18 };
        var unique = InstalledApiCatalog.DeduplicateCallSites([direct, potential, otherCallSite]);
        Assert(unique.Count == 2 && !unique[0].potentialVirtualDispatch && unique[0].ilOffset == 17 &&
               unique[1].ilOffset == 18,
            "navigation returns each IL call site once and preserves the direct match over its duplicate potential match");
    }

    private static void AssertDefault(Type toolType, string methodName, string parameterName, object expected)
    {
        var parameter = toolType.GetMethod(methodName)!.GetParameters()
            .Single(candidate => candidate.Name == parameterName);
        Assert(parameter.HasDefaultValue && Equals(parameter.DefaultValue, expected),
            $"{methodName}.{parameterName} defaults to {expected}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Discovery contract check failed: " + message);
    }
}
