using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

internal static class CatalogLocalizedFallbackCheck
{
    public static async Task RunAsync()
    {
        var oldLaunchConfig = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_LAUNCH_CONFIG");
        var oldCacheDirectory = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR");
        var root = Path.Combine(Path.GetTempPath(), "ThunderRoadMcp-localized-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var gameDirectory = Path.Combine(root, "game", "Blade & Sorcery");
            Directory.CreateDirectory(gameDirectory);
            var executablePath = Path.Combine(gameDirectory, "BladeAndSorcery.exe");
            await File.WriteAllBytesAsync(executablePath, Array.Empty<byte>());
            var launchConfigPath = Path.Combine(root, "launch.json");
            await File.WriteAllTextAsync(launchConfigPath,
                JsonSerializer.Serialize(new { executablePath, arguments = Array.Empty<string>() }));
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_LAUNCH_CONFIG", launchConfigPath);
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR", Path.Combine(root, "cache"));

            var archivePath = GameInstallConfiguration.CatalogArchivePath(
                new LaunchOptions(executablePath, Array.Empty<string>()));
            await WriteArchiveAsync(archivePath,
                ("Skills/Skill_Nova.json", """{"$type":"ThunderRoad.SkillData, ThunderRoad","id":"Nova","description":"{NovaDescription}"}"""),
                ("Waves/Wave_Training1.json", """{"$type":"ThunderRoad.WaveData, ThunderRoad","id":"Training1","localizationId":"Training1Localized","title":"Practice","description":"A friendly lesson"}"""),
                ("Texts/Text_English.json", """{"$type":"ThunderRoad.TextData, ThunderRoad","id":"English","textGroups":[{"texts":[{"id":"NovaDescription","text":"collapses into a black hole"}]}],"items":[],"waves":[{"id":"Training1Localized","title":"Duel","description":"friendly opponents in a sparring arena"}]}"""));

            var indexedCatalog = new BaseCatalogDataCatalog();
            using var indexed = JsonDocument.Parse(await indexedCatalog.SearchAsync(
                "black hole", null, null, 0, 25, null, CancellationToken.None));
            var indexedProperties = indexed.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            Assert(ResultIds(indexed.RootElement).SequenceEqual(new[] { "Nova" }) &&
                   indexed.RootElement.GetProperty("localizedSearchComplete").GetBoolean() &&
                   !indexed.RootElement.GetProperty("catalogTruncated").GetBoolean() &&
                   Array.IndexOf(indexedProperties, "total") < Array.IndexOf(indexedProperties, "results"),
                "indexed search resolves English TextData, reports its independent completeness, and summarizes before results");

            var fallbackJson = await SearchUncachedAsync(new BaseCatalogDataCatalog(), archivePath,
                indexed.RootElement.GetProperty("snapshotId").GetString()!, "black hole");
            using var fallback = JsonDocument.Parse(fallbackJson);
            Assert(ResultIds(fallback.RootElement).SequenceEqual(ResultIds(indexed.RootElement)) &&
                   fallback.RootElement.GetProperty("localizedSearchComplete").GetBoolean() &&
                   !fallback.RootElement.GetProperty("catalogTruncated").GetBoolean(),
                "uncached search returns the same localized matches and completeness as the indexed search");

            using var browse = JsonDocument.Parse(await indexedCatalog.SearchAsync(
                null, null, null, 0, 25, null, CancellationToken.None));
            Assert(browse.RootElement.GetProperty("localizedSearchComplete").ValueKind == JsonValueKind.Null,
                "catalog browsing does not claim a localized text search ran");

            using var indexedWave = JsonDocument.Parse(await indexedCatalog.SearchAsync(
                "sparring arena", null, null, 0, 25, null, CancellationToken.None));
            var uncachedWaveJson = await SearchUncachedAsync(new BaseCatalogDataCatalog(), archivePath,
                indexedWave.RootElement.GetProperty("snapshotId").GetString()!, "sparring arena");
            using var uncachedWave = JsonDocument.Parse(uncachedWaveJson);
            Assert(ResultIds(indexedWave.RootElement).SequenceEqual(new[] { "Training1" }) &&
                   ResultIds(uncachedWave.RootElement).SequenceEqual(ResultIds(indexedWave.RootElement)),
                "indexed and uncached search resolve WaveData through its English localizationId");

            AssertRecovery(await CaptureAsync(() => indexedCatalog.SearchAsync(
                "black hole", null, null, 0, 25, "stale-snapshot", CancellationToken.None)),
                "catalog.snapshot_stale", "stale snapshot restarts catalog_search with actionable recovery");
            AssertRecovery(await CaptureAsync(() => indexedCatalog.ReadAsync(
                "stale-entry", indexed.RootElement.GetProperty("snapshotId").GetString()!, 1, 10, CancellationToken.None)),
                "catalog.entry_invalid_or_stale", "stale entry restarts catalog_search with actionable recovery");
            await WriteArchiveAsync(archivePath,
                ("Skills/Skill_Nova.json", """{"$type":"ThunderRoad.SkillData, ThunderRoad","id":"Nova","description":"{NovaDescription}"}"""),
                ("Texts/Text_English.json", """{"$type":"ThunderRoad.TextData, ThunderRoad","id":"English","textGroups":[{"texts":[{"id":"NovaDescription","text":"collapses into a black hole"}]}],"items":{}}"""));
            var malformedCatalog = new BaseCatalogDataCatalog();
            using var malformedIndexed = JsonDocument.Parse(await malformedCatalog.SearchAsync(
                "black hole", null, null, 0, 25, null, CancellationToken.None));
            Assert(ResultIds(malformedIndexed.RootElement).SequenceEqual(new[] { "Nova" }) &&
                   !malformedIndexed.RootElement.GetProperty("localizedSearchComplete").GetBoolean() &&
                   !malformedIndexed.RootElement.GetProperty("catalogTruncated").GetBoolean() &&
                   malformedIndexed.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(diagnostic => diagnostic.GetString()!.Contains("malformed", StringComparison.OrdinalIgnoreCase)),
                "malformed localized data reports incomplete localization without mislabeling archive truncation");
            var malformedUncachedJson = await SearchUncachedAsync(new BaseCatalogDataCatalog(), archivePath,
                malformedIndexed.RootElement.GetProperty("snapshotId").GetString()!, "black hole");
            using var malformedUncached = JsonDocument.Parse(malformedUncachedJson);
            Assert(ResultIds(malformedUncached.RootElement).SequenceEqual(new[] { "Nova" }) &&
                   !malformedUncached.RootElement.GetProperty("localizedSearchComplete").GetBoolean() &&
                   malformedUncached.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(diagnostic => diagnostic.GetString()!.Contains("malformed", StringComparison.OrdinalIgnoreCase)),
                "uncached malformed localized data exposes the same incomplete-search reason");
            await WriteArchiveAsync(archivePath,
                ("Skills/Skill_Nova.json", """{"$type":"ThunderRoad.SkillData, ThunderRoad","id":"Nova","description":"{NovaDescription}"}"""),
                ("Texts/Text_English.json", """{"$type":"ThunderRoad.TextData, ThunderRoad","id":"English","textGroups":{},"items":[]}"""));
            var malformedGroupsCatalog = new BaseCatalogDataCatalog();
            using var malformedGroupsIndexed = JsonDocument.Parse(await malformedGroupsCatalog.SearchAsync(
                "Nova", null, null, 0, 25, null, CancellationToken.None));
            Assert(ResultIds(malformedGroupsIndexed.RootElement).SequenceEqual(new[] { "Nova" }) &&
                   !malformedGroupsIndexed.RootElement.GetProperty("localizedSearchComplete").GetBoolean() &&
                   malformedGroupsIndexed.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(diagnostic => diagnostic.GetString()!.Contains("malformed", StringComparison.OrdinalIgnoreCase)),
                "malformed textGroups data does not claim localized-search completeness");
            var malformedGroupsUncachedJson = await SearchUncachedAsync(new BaseCatalogDataCatalog(), archivePath,
                malformedGroupsIndexed.RootElement.GetProperty("snapshotId").GetString()!, "Nova");
            using var malformedGroupsUncached = JsonDocument.Parse(malformedGroupsUncachedJson);
            Assert(ResultIds(malformedGroupsUncached.RootElement).SequenceEqual(new[] { "Nova" }) &&
                   !malformedGroupsUncached.RootElement.GetProperty("localizedSearchComplete").GetBoolean() &&
                   malformedGroupsUncached.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(diagnostic => diagnostic.GetString()!.Contains("malformed", StringComparison.OrdinalIgnoreCase)),
                "uncached search also reports malformed textGroups as incomplete");
        }
        finally
        {
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_LAUNCH_CONFIG", oldLaunchConfig);
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR", oldCacheDirectory);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> SearchUncachedAsync(BaseCatalogDataCatalog catalog, string archivePath,
        string snapshotId, string query)
    {
        var openedType = typeof(BaseCatalogDataCatalog).GetNestedType("OpenedArchive", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Catalog archive wrapper was not found.");
        var constructor = openedType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 3);
        var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var opened = constructor.Invoke(new object[] { archivePath, stream, snapshotId });
        try
        {
            var method = typeof(BaseCatalogDataCatalog).GetMethod("SearchUncachedAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Uncached catalog search path was not found.");
            var search = (Task<string>)method.Invoke(catalog,
                new object?[] { opened, query, null, null, 0, 25, CancellationToken.None })!;
            return await search;
        }
        finally
        {
            await ((IAsyncDisposable)opened).DisposeAsync();
        }
    }

    private static async Task WriteArchiveAsync(string archivePath, params (string Path, string Json)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        await using var file = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (path, json) in entries)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
            await using var output = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(json);
            await output.WriteAsync(bytes);
        }
    }

    private static string[] ResultIds(JsonElement response) => response.GetProperty("results").EnumerateArray()
        .Select(result => result.GetProperty("id").GetString()!).ToArray();

    private static async Task<Exception> CaptureAsync(Func<Task<string>> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Catalog recovery check expected a typed stale-data error.");
    }

    private static void AssertRecovery(Exception exception, string code, string message)
    {
        Assert(exception is ApiDiscoveryException discovery && discovery.Code == code &&
               discovery.ToJson()["recovery"]?["tool"]?.GetValue<string>() == "catalog_search",
            message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Catalog localization check failed: " + message);
    }
}
