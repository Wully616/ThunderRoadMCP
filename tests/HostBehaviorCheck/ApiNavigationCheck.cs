using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public static class ApiNavigationCheck
{
    public static async Task RunAsync(Func<string, object, Task<JsonDocument>> apiToolJsonAsync, Action<bool, string> assert)
    {
        using var baseAssemblies = await apiToolJsonAsync("api_assemblies", new { name = "Fixture.Base", origin = "game", limit = 20 });
        using var gameSharedAssemblies = await apiToolJsonAsync("api_assemblies", new { name = "Fixture.Shared", origin = "game", limit = 20 });
        using var modSharedAssemblies = await apiToolJsonAsync("api_assemblies", new { name = "Fixture.Shared", origin = "mod", limit = 20 });
        var gameBase = baseAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single();
        var gameShared = gameSharedAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single();
        var modShared = modSharedAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single();
        var baseId = gameBase.GetProperty("assemblyId").GetString()!;
        var gameSharedId = gameShared.GetProperty("assemblyId").GetString()!;
        var modId = modShared.GetProperty("assemblyId").GetString()!;

        using var gamePingSearch = await apiToolJsonAsync("api_search", new { query = "Ping", assemblyId = gameSharedId, kind = "method" });
        using var modPingSearch = await apiToolJsonAsync("api_search", new { query = "Ping", assemblyId = modId, kind = "method" });
        var gamePingId = gamePingSearch.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("symbolId").GetString()!;
        var modPingId = modPingSearch.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("symbolId").GetString()!;
        using var gamePingCallers = await apiToolJsonAsync("api_find_callers", new { assemblyId = gameSharedId, symbolId = gamePingId, scope = "snapshot" });
        using var modPingCallers = await apiToolJsonAsync("api_find_callers", new { assemblyId = modId, symbolId = modPingId, scope = "snapshot" });
        assert(gamePingId != modPingId &&
               gamePingCallers.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("assemblyId").GetString() == gameSharedId &&
               modPingCallers.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("assemblyId").GetString() == modId,
            "same-name assemblies and same-token targets resolve callers only to the exact assembly identity");
        using var methods = await apiToolJsonAsync("api_search", new { query = "Overload", assemblyId = baseId, kind = "method" });
        var overload = methods.RootElement.GetProperty("results").EnumerateArray()
            .Single(item => item.GetProperty("signature").GetString()!.Contains("int", StringComparison.Ordinal));
        var overloadId = overload.GetProperty("symbolId").GetString()!;
        using var callers = await apiToolJsonAsync("api_find_callers", new { assemblyId = baseId, symbolId = overloadId, scope = "snapshot", limit = 1 });
        var callerRoot = callers.RootElement;
        assert(callerRoot.GetProperty("total").GetInt32() == 2 &&
               callerRoot.GetProperty("results")[0].GetProperty("assemblyId").GetString() == modId &&
               callerRoot.GetProperty("hasMore").GetBoolean(),
            "cross-assembly exact overload callers are identified and paged with their own assembly provenance");
        var caller = callerRoot.GetProperty("results")[0];
        using var nextCallers = await apiToolJsonAsync("api_find_callers", new
        {
            assemblyId = baseId,
            symbolId = overloadId,
            scope = "snapshot",
            offset = callerRoot.GetProperty("nextOffset").GetInt32(),
            limit = 1,
            snapshotId = callerRoot.GetProperty("snapshotId").GetString(),
        });
        assert(nextCallers.RootElement.GetProperty("results")[0].GetProperty("symbolId").GetString() !=
               caller.GetProperty("symbolId").GetString() &&
               !nextCallers.RootElement.GetProperty("hasMore").GetBoolean(),
            "caller continuation returns the other exact caller and terminates without duplicates");
        using var stringOverload = await apiToolJsonAsync("api_search", new { query = "Overload", assemblyId = baseId, kind = "method" });
        var stringOverloadId = stringOverload.RootElement.GetProperty("results").EnumerateArray()
            .Single(item => item.GetProperty("signature").GetString()!.Contains("string", StringComparison.Ordinal))
            .GetProperty("symbolId").GetString()!;
        using var stringCallers = await apiToolJsonAsync("api_find_callers", new
        {
            assemblyId = baseId, symbolId = stringOverloadId, scope = "snapshot", limit = 10,
            snapshotId = callerRoot.GetProperty("snapshotId").GetString(),
        });
        assert(stringCallers.RootElement.GetProperty("total").GetInt32() == 1 &&
               stringCallers.RootElement.GetProperty("results")[0].GetProperty("method").GetString() == "NavigationCaller.StringCall",
            "string overload caller is distinct from same-name, same-arity integer overload callers");
        using var source = await apiToolJsonAsync("api_decompile", new
        {
            assemblyId = caller.GetProperty("assemblyId").GetString(),
            symbolId = caller.GetProperty("symbolId").GetString(),
            startLine = 1,
            lineCount = 80,
        });
        assert(source.RootElement.GetProperty("source").GetString()!.Contains(
                caller.GetProperty("method").GetString()!.Split('.').Last(), StringComparison.Ordinal),
            "navigation result symbolId hands off to reconstructed source for its actual caller method");

        using var callees = await apiToolJsonAsync("api_find_callees", new { assemblyId = modId, symbolId = caller.GetProperty("symbolId").GetString(), limit = 100 });
        assert(callees.RootElement.GetProperty("results").EnumerateArray().Any(item => item.GetProperty("targetSymbolId").GetString() == overloadId),
            "callee IL resolves the exact overloaded game definition to its source symbolId");
        using var genericSearch = await apiToolJsonAsync("api_search", new { query = "Generic", assemblyId = baseId, kind = "method" });
        var genericId = genericSearch.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("symbolId").GetString()!;
        using var callerMethods = await apiToolJsonAsync("api_search", new { query = "NavigationCaller.Call", assemblyId = modId, kind = "method" });
        var callerMethodId = callerMethods.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("symbolId").GetString()!;
        using var genericCallees = await apiToolJsonAsync("api_find_callees", new { assemblyId = modId, symbolId = callerMethodId, limit = 100 });
        assert(genericCallees.RootElement.GetProperty("results").EnumerateArray()
                .Any(item => item.GetProperty("targetSymbolId").GetString() == genericId),
            "generic MethodSpec resolves to its exact installed generic method definition");

        using var echoSearch = await apiToolJsonAsync("api_search", new { query = "Echo", assemblyId = baseId, kind = "method" });
        var echoId = echoSearch.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("symbolId").GetString()!;
        using var genericTypeCaller = await apiToolJsonAsync("api_search", new { query = "NavigationCaller.GenericTypeCall", assemblyId = modId, kind = "method" });
        var genericTypeCallerId = genericTypeCaller.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("symbolId").GetString()!;
        using var genericTypeCallees = await apiToolJsonAsync("api_find_callees", new { assemblyId = modId, symbolId = genericTypeCallerId, limit = 100 });
        assert(genericTypeCallees.RootElement.GetProperty("results").EnumerateArray()
                .Any(item => item.GetProperty("targetSymbolId").GetString() == echoId),
            "TypeSpec and MethodSpec resolution returns the open generic declaring type's exact method ID");

        using var abstractSearch = await apiToolJsonAsync("api_search", new { query = "NoBody", assemblyId = baseId, kind = "method" });
        var abstractId = abstractSearch.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("symbolId").GetString()!;
        using var abstractCallees = await apiToolJsonAsync("api_find_callees", new { assemblyId = baseId, symbolId = abstractId });
        assert(abstractCallees.RootElement.GetProperty("total").GetInt32() == 0 &&
               abstractCallees.RootElement.GetProperty("complete").GetBoolean() &&
               !abstractCallees.RootElement.GetProperty("hasBody").GetBoolean() &&
               abstractCallees.RootElement.GetProperty("noBodyReason").GetString() == "no_il_body",
            "abstract method returns a complete empty no-body static graph with a stable reason code");
        foreach (var (kind, name, expectedOpcodes) in new[]
        {
            ("field", "Value", new[] { "ldfld", "stfld" }),
            ("property", "Number", new[] { "callvirt" }),
            ("event", "Changed", new[] { "callvirt" }),
        })
        {
            using var search = await apiToolJsonAsync("api_search", new { query = name, assemblyId = baseId, kind });
            var symbolId = search.RootElement.GetProperty("results").EnumerateArray().Single()
                .GetProperty("symbolId").GetString()!;
            using var usages = await apiToolJsonAsync("api_find_usages", new { assemblyId = baseId, symbolId, scope = "snapshot", limit = 100 });
            var usageResults = usages.RootElement.GetProperty("results").EnumerateArray().ToArray();
            assert(expectedOpcodes.All(opcode => usageResults.Any(item => item.GetProperty("opcode").GetString() == opcode)),
                $"{kind} usage resolves actual IL semantics through its metadata definition");
            if (kind == "field")
            {
                var getter = usageResults.Single(item => item.GetProperty("method").GetString() == "NavigationTarget.get_Number");
                using var getterSource = await apiToolJsonAsync("api_decompile", new
                {
                    assemblyId = getter.GetProperty("assemblyId").GetString(),
                    symbolId = getter.GetProperty("symbolId").GetString(),
                    startLine = 1,
                    lineCount = 80,
                });
                assert(getterSource.RootElement.GetProperty("source").GetString()!.Contains("Value", StringComparison.Ordinal),
                    "field usage caller representing the property getter hands off to api_decompile");
            }
        }
        using var callbackSearch = await apiToolJsonAsync("api_search", new { query = "CallbackWork", assemblyId = baseId, kind = "method" });
        var callbackId = callbackSearch.RootElement.GetProperty("results").EnumerateArray().Single().GetProperty("symbolId").GetString()!;
        using var callbackCallers = await apiToolJsonAsync("api_find_callers", new { assemblyId = baseId, symbolId = callbackId, scope = "snapshot" });
        var lambda = callbackCallers.RootElement.GetProperty("results").EnumerateArray().Single();
        using var lambdaSource = await apiToolJsonAsync("api_decompile", new
        {
            assemblyId = lambda.GetProperty("assemblyId").GetString(),
            symbolId = lambda.GetProperty("symbolId").GetString(),
            startLine = 1,
            lineCount = 80,
        });
        using var lambdaCallees = await apiToolJsonAsync("api_find_callees", new
        {
            assemblyId = lambda.GetProperty("assemblyId").GetString(),
            symbolId = lambda.GetProperty("symbolId").GetString(),
        });
        assert(lambdaSource.RootElement.GetProperty("source").GetString()!.Contains("CallbackWork", StringComparison.Ordinal) &&
               lambdaCallees.RootElement.GetProperty("results").EnumerateArray().Any(item => item.GetProperty("targetSymbolId").GetString() == callbackId),
            "compiler-generated lambda caller IDs round-trip through decompile and callee navigation");

    }
}
