using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public sealed record ApiReuseContext(
    string HostAssemblyPath,
    string BridgeConfigPath,
    string LaunchConfigPath,
    string LogDirectory,
    string CacheDirectory,
    string GameExecutablePath,
    string GameManagedDirectory,
    string GameSharedAssemblyPath,
    string ModSharedAssemblyPath,
    string BaseAssemblyPath,
    string MissingDependencyBackupPath,
    string ChangedFixturePath,
    string FixtureOutputDirectory,
    string WorkDirectory,
    Func<Task<JsonDocument>> NextDiagnosticsAsync)
{
    public JsonDocument? LastDiagnostics { get; set; }
}

public static class ApiReuseCheck
{
    public static async Task RunAsync(Func<string, object, Task<JsonDocument>> apiToolJsonAsync,
        Action<bool, string> assert, ApiReuseContext context)
    {
        async Task<JsonDocument> CaptureDiagnosticsAsync(string name, object arguments)
        {
            try
            {
                var response = await apiToolJsonAsync(name, arguments);
                ReplaceDiagnostics(await context.NextDiagnosticsAsync());
                return response;
            }
            catch
            {
                ReplaceDiagnostics(await context.NextDiagnosticsAsync());
                throw;
            }
        }

        void ReplaceDiagnostics(JsonDocument diagnostics)
        {
            context.LastDiagnostics?.Dispose();
            context.LastDiagnostics = diagnostics;
        }

        static long Count(JsonDocument? diagnostics, string name)
        {
            if (diagnostics is null ||
                !diagnostics.RootElement.TryGetProperty("counts", out var counts) ||
                !counts.TryGetProperty(name, out var value))
                return 0;
            return value.GetInt64();
        }

        try
        {
            await ApiPersistentCacheCheck.RunAsync(CaptureDiagnosticsAsync, assert, context);
            await ApiIndexAdmissionCheck.RunAsync(assert, context);
            await ApiIdentityFreshnessCheck.RunAsync(CaptureDiagnosticsAsync, assert, context);
            await ApiLookupRegressionCheck.RunAsync(CaptureDiagnosticsAsync, assert);
            await CheckSourceReuseAndSiblingChangeAsync(CaptureDiagnosticsAsync, assert, context, Count);
            await CheckDependencyRestorationAsync(CaptureDiagnosticsAsync, assert, context, Count);
            await ApiAttributeCheck.RunAsync(CaptureDiagnosticsAsync, assert, context);
        }
        finally
        {
            context.LastDiagnostics?.Dispose();
            context.LastDiagnostics = null;
        }
    }


    private static async Task CheckSourceReuseAndSiblingChangeAsync(
        Func<string, object, Task<JsonDocument>> apiToolJsonAsync, Action<bool, string> assert,
        ApiReuseContext context, Func<JsonDocument?, string, long> count)
    {
        using var assemblies = await apiToolJsonAsync("api_assemblies", new
        {
            name = "Fixture.Shared", origin = "game", limit = 50,
        });
        var assemblyId = assemblies.RootElement.GetProperty("assemblies").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Fixture.Shared")
            .GetProperty("assemblyId").GetString()!;
        using var search = await apiToolJsonAsync("api_search", new
        {
            query = "SignatureSurface", kind = "type", assemblyId,
        });
        var symbolId = search.RootElement.GetProperty("results").EnumerateArray()
            .Single(item => item.GetProperty("qualifiedName").GetString() == "Fixture.Shared.SignatureSurface")
            .GetProperty("symbolId").GetString()!;
        using var first = await apiToolJsonAsync("api_decompile", new
        {
            assemblyId, symbolId, startLine = 1, lineCount = 1,
        });
        var firstSnapshot = first.RootElement.GetProperty("snapshotId").GetString()!;
        var nextLine = first.RootElement.GetProperty("nextLine").GetInt32();
        assert(first.RootElement.GetProperty("hasMore").GetBoolean(), "API source first page offers a continuation");
        assert(count(context.LastDiagnostics, "api.source.decompileBuild") == 1,
            "cold API source request reconstructs and caches the complete source document once");

        using (var next = await apiToolJsonAsync("api_decompile", new
        {
            assemblyId, symbolId, startLine = nextLine, lineCount = 1, snapshotId = firstSnapshot,
        }))
            assert(next.RootElement.GetProperty("startLine").GetInt32() == nextLine &&
                   count(context.LastDiagnostics, "api.source.cacheHit") > 0,
                "source continuation pages reuse the same immutable cached source document");

        var siblingPath = Path.Combine(context.GameManagedDirectory, "ApiReuseSibling.dll");
        try
        {
            File.Copy(context.GameSharedAssemblyPath, siblingPath);
            using var siblingSearch = await apiToolJsonAsync("api_search", new
            {
                query = "SignatureSurface", assemblyId,
            });
            assert(siblingSearch.RootElement.GetProperty("results").GetArrayLength() > 0 &&
                   count(context.LastDiagnostics, "api.index.memoryHit") > 0 &&
                   count(context.LastDiagnostics, "api.index.build") == 0,
                "changing a sibling assembly reuses the unchanged per-assembly API index");

            using var changedSnapshotSource = await apiToolJsonAsync("api_decompile", new
            {
                assemblyId, symbolId, startLine = 1, lineCount = 1,
            });
            assert(changedSnapshotSource.RootElement.GetProperty("snapshotId").GetString() != firstSnapshot &&
                   count(context.LastDiagnostics, "api.source.decompileBuild") == 1,
                "dependency-bound source cache is rebuilt when the assembly snapshot changes");
        }
        finally
        {
            if (File.Exists(siblingPath))
                File.Delete(siblingPath);
        }
        using var restoredAssemblies = await apiToolJsonAsync("api_assemblies", new
        {
            name = "Fixture.Shared", origin = "game", limit = 50,
        });
        assert(restoredAssemblies.RootElement.GetProperty("snapshotId").GetString() == firstSnapshot,
            "removing the temporary sibling returns the exact prior installed snapshot identity");
    }

    private static async Task CheckDependencyRestorationAsync(
        Func<string, object, Task<JsonDocument>> apiToolJsonAsync, Action<bool, string> assert,
        ApiReuseContext context, Func<JsonDocument?, string, long> count)
    {
        using var modAssemblies = await apiToolJsonAsync("api_assemblies", new
        {
            name = "Fixture.Shared", origin = "mod", limit = 50,
        });
        var modAssemblyId = modAssemblies.RootElement.GetProperty("assemblies").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Fixture.Shared")
            .GetProperty("assemblyId").GetString()!;
        using var derived = await apiToolJsonAsync("api_search", new
        {
            query = "DerivedSurface", kind = "type", assemblyId = modAssemblyId,
        });
        var derivedHit = derived.RootElement.GetProperty("results").EnumerateArray().Single();
        var derivedSymbolId = derivedHit.GetProperty("symbolId").GetString()!;
        using var missingSource = await apiToolJsonAsync("api_decompile", new
        {
            assemblyId = modAssemblyId, symbolId = derivedSymbolId, startLine = 1, lineCount = 40,
        });
        var missingSnapshot = missingSource.RootElement.GetProperty("snapshotId").GetString()!;
        var missingWarnings = missingSource.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(item => item.GetString() ?? "").ToArray();
        assert(missingWarnings.Any(warning => warning.Contains("Fixture.Base", StringComparison.OrdinalIgnoreCase) ||
                                              warning.Contains("missing", StringComparison.OrdinalIgnoreCase)),
            "missing dependency warning is present before restoration");

        File.Move(context.MissingDependencyBackupPath, context.BaseAssemblyPath);
        try
        {
            using var restoredLookup = await apiToolJsonAsync("api_lookup", new
            {
                query = "derived surface", assemblyId = modAssemblyId, maxResults = 10, maxChars = 16000, includeExamples = false,
            });
            var baseResolved = restoredLookup.RootElement.GetProperty("relatedTypes").EnumerateArray()
                .Any(type => type.GetProperty("qualifiedName").GetString() == "Fixture.Base.BaseSurface");
            assert(baseResolved, "restoring a previously missing dependency resolves the related base type");

            using var restoredSource = await apiToolJsonAsync("api_decompile", new
            {
                assemblyId = modAssemblyId, symbolId = derivedSymbolId, startLine = 1, lineCount = 40,
            });
            var restoredWarnings = restoredSource.RootElement.GetProperty("warnings").EnumerateArray()
                .Select(item => item.GetString() ?? "").ToArray();
            assert(restoredSource.RootElement.GetProperty("snapshotId").GetString() != missingSnapshot &&
                   !restoredWarnings.Any(warning => warning.Contains("Fixture.Base", StringComparison.OrdinalIgnoreCase) ||
                                                    warning.Contains("missing", StringComparison.OrdinalIgnoreCase)) &&
                   count(context.LastDiagnostics, "api.source.decompileBuild") == 1,
                "restoring a dependency invalidates snapshot-bound source and removes the missing-reference warning");
            await ApiDecompileBatchCheck.RunAsync(apiToolJsonAsync, assert);
            await ApiNavigationCheck.RunAsync(apiToolJsonAsync, assert);
        }
        finally
        {
            File.Move(context.BaseAssemblyPath, context.MissingDependencyBackupPath);
        }
    }
}
