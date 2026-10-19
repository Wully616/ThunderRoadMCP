using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

internal static class CatalogReuseCheck
{
    private static readonly DateTimeOffset EntryTimestamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        var priorLaunchConfig = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_LAUNCH_CONFIG");
        var priorCacheDir = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR");
        var priorDiagnostics = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_DIAGNOSTICS");
        var root = Path.Combine(Path.GetTempPath(), "ThunderRoadMcp-catalog-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("THUNDERROAD_MCP_DIAGNOSTICS", "1");
        var cacheDirectory = Path.Combine(root, "cache");
        Environment.SetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR", cacheDirectory);

        try
        {
            var install = CreateInstall(root, "primary");
            await SelectInstallAsync(install);
            var archivePath = GameInstallConfiguration.CatalogArchivePath(
                new LaunchOptions(install.ExecutablePath, Array.Empty<string>()));
            var first = "{\r\n\"$type\":\"ThunderRoad.ItemData, ThunderRoad\",\r\n\"id\":\"dup-one\",\r\n\"name\":\"Twin A\",\r\n\"extra\":{\"text\":\"deep green phrase\",\"greeting\":\"café\"}\r\n}\r\n";
            var second = first.Replace("dup-one", "dup-two", StringComparison.Ordinal)
                .Replace("Twin A", "Twin B", StringComparison.Ordinal)
                .Replace("deep green phrase", "deep blue phrase", StringComparison.Ordinal);
            var uncachedText = new string('q', 1_100_000) + "needlephrase";
            var uncached = "{\"id\":\"big-record\",\"name\":\"Large\",\"payload\":\"" + uncachedText + "\"}";
            var largeName = "{\"$type\":\"ThunderRoad.ItemData, ThunderRoad\",\"id\":\"long-name\",\"name\":\"" + new string('n', 1024) + "\"}";
            var oversized = "{\"id\":\"oversized\",\"payload\":\"" + new string('z', 4 * 1024 * 1024 + 128) + "\"}";
            await WriteArchiveAsync(archivePath,
                ("Records/Twin.json", first),
                ("Records/Twin.json", second),
                ("Records/Broken.json", "{"),
                ("Records/Uncached.json", uncached),
                ("Records/LargeName.json", largeName),
                ("Records/Large.json", oversized));
            var originalLength = new FileInfo(archivePath).Length;
            var fixedTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(archivePath, fixedTime);

            var catalog = new BaseCatalogDataCatalog();
            var cold = await MeasureSearchAsync(catalog, "deep green phrase");
            using var coldJson = JsonDocument.Parse(cold.Json);
            var coldRoot = coldJson.RootElement;
            Assert(coldRoot.GetProperty("total").GetInt32() == 1 &&
                   coldRoot.GetProperty("results")[0].GetProperty("id").GetString() == "dup-one",
                "cold index preserves nested substring search");
            Assert(Count(cold, "catalogIndexBuildCount") == 1 && Count(cold, "catalogJsonParseCount") == 6 &&
                   Count(cold, "catalogUncachedStringRecords") == 1 &&
                   Count(cold, "catalogArchiveBytesHashed") == originalLength,
                "cold build parses records once, marks uncached coverage, and hashes the full archive");
            var handoff = await catalog.SearchTaskCatalogAsync("ItemData", 5, null, CancellationToken.None);
            Assert(handoff.SnapshotId == coldRoot.GetProperty("snapshotId").GetString() &&
                   handoff.Records.Any(record => record.DeclaredApiType == "ThunderRoad.ItemData" &&
                                                 record.ArchivePath == "Records/Twin.json") &&
                   handoff.Records.Any(record => record.Id == "long-name" && record.Name is null) &&
                   !handoff.IsComplete,
                "task handoff keeps bounded direct $type evidence and its independent catalog snapshot");

            var warm = await MeasureSearchAsync(catalog, "deep green phrase");
            Assert(Count(warm, "catalogCacheHit") == 1 && Count(warm, "catalogJsonParseCount") == 1 &&
                   Count(warm, "catalogSearchFallbackParses") == 1 &&
                   Count(warm, "catalogArchiveBytesHashed") == originalLength,
                "warm search reuses per-record metadata, falls back exactly, and retains full-hash freshness");

            var twins = await MeasureSearchAsync(catalog, "Twin");
            using (var twinsJson = JsonDocument.Parse(twins.Json))
            {
                var records = twinsJson.RootElement.GetProperty("results").EnumerateArray().ToArray();
                Assert(twinsJson.RootElement.GetProperty("total").GetInt32() == 2 &&
                       records[0].GetProperty("id").GetString() == "dup-one" &&
                       records[1].GetProperty("id").GetString() == "dup-two" &&
                       records[0].GetProperty("entryId").GetString() != records[1].GetProperty("entryId").GetString(),
                    "duplicate archive paths retain order and distinct entry identity");
            }
            using (var firstPage = JsonDocument.Parse(await catalog.SearchAsync("Twin", null, null, 0, 1, null, CancellationToken.None)))
            {
                var pageRoot = firstPage.RootElement;
                var secondOffset = pageRoot.GetProperty("nextOffset").GetInt32();
                using var secondPage = JsonDocument.Parse(await catalog.SearchAsync("Twin", null, null, secondOffset, 1,
                    pageRoot.GetProperty("snapshotId").GetString(), CancellationToken.None));
                Assert(pageRoot.GetProperty("total").GetInt32() == 2 &&
                       pageRoot.GetProperty("results").GetArrayLength() == 1 &&
                       pageRoot.GetProperty("results")[0].GetProperty("id").GetString() == "dup-one" &&
                       pageRoot.GetProperty("hasMore").GetBoolean() && secondOffset == 1 &&
                       secondPage.RootElement.GetProperty("results")[0].GetProperty("id").GetString() == "dup-two",
                    "catalog search preserves deterministic offset and limit pagination");
            }
            using (var allJson = JsonDocument.Parse((await catalog.SearchAsync(null, null, null, 0, 25, null, CancellationToken.None))))
            {
                var all = allJson.RootElement;
                var diagnostics = all.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
                Assert(all.GetProperty("catalogTruncated").GetBoolean() &&
                       diagnostics.Any(message => message.Contains("Broken.json", StringComparison.Ordinal)) &&
                       diagnostics.Any(message => message.Contains("member exceeds", StringComparison.Ordinal)),
                    "malformed and over-limit entries preserve diagnostics and truncation behavior");
            }
            var needle = await MeasureSearchAsync(catalog, "needlephrase");
            using (var needleJson = JsonDocument.Parse(needle.Json))
                Assert(needleJson.RootElement.GetProperty("results")[0].GetProperty("id").GetString() == "big-record" &&
                       Count(needle, "catalogSearchFallbackParses") == 1,
                    "uncached oversized strings remain fully searchable by fallback");

            using var twinsAgain = JsonDocument.Parse(twins.Json);
            var firstEntry = twinsAgain.RootElement.GetProperty("results")[0].GetProperty("entryId").GetString()!;
            var snapshot = twinsAgain.RootElement.GetProperty("snapshotId").GetString()!;
            var exact = new StringBuilder();
            var nextLine = 1;
            while (true)
            {
                using var page = JsonDocument.Parse(await catalog.ReadAsync(firstEntry, snapshot, nextLine, 1, CancellationToken.None));
                var pageRoot = page.RootElement;
                exact.Append(pageRoot.GetProperty("text").GetString());
                if (!pageRoot.GetProperty("hasMore").GetBoolean()) break;
                nextLine = pageRoot.GetProperty("nextLine").GetInt32();
            }
            Assert(exact.ToString() == first, "record pages reconstruct exact CRLF JSON text");

            var restarted = await MeasureSearchAsync(new BaseCatalogDataCatalog(), "deep green phrase");
            using var restartedJson = JsonDocument.Parse(restarted.Json);
            Assert(Count(restarted, "catalogPersistentHit") == 1 &&
                   Count(restarted, "catalogIndexBuildCount") == 0 &&
                   Count(restarted, "catalogJsonParseCount") == 1 &&
                   Count(restarted, "catalogSearchFallbackParses") == 1 &&
                   restartedJson.RootElement.GetProperty("snapshotId").GetString() == snapshot,
                "a new service reloads indexed metadata without rebuilding and keeps the installation snapshot stable; " +
                "the intentionally uncached oversized record still requires one exact fallback parse");

            var cacheFile = Directory.GetFiles(Path.Combine(cacheDirectory, "catalog"), "catalog-*.idx").Single();
            var corrupted = await File.ReadAllBytesAsync(cacheFile);
            corrupted[0] ^= 0xff;
            await File.WriteAllBytesAsync(cacheFile, corrupted);
            var recovered = await MeasureSearchAsync(new BaseCatalogDataCatalog(), "deep green phrase");
            using var recoveredJson = JsonDocument.Parse(recovered.Json);
            Assert(Count(recovered, "catalogCacheCorrupt") == 1 && Count(recovered, "catalogJsonParseCount") > 0 &&
                   recoveredJson.RootElement.GetProperty("total").GetInt32() == 1,
                "a corrupt persistent index is discarded and rebuilt without losing results");

            var replacementPath = Path.Combine(root, "replacement.idx");
            var changedFirst = first.Replace("deep green phrase", "deep brown phrase", StringComparison.Ordinal);
            await WriteArchiveAsync(replacementPath,
                ("Records/Twin.json", changedFirst),
                ("Records/Twin.json", second),
                ("Records/Broken.json", "{"),
                ("Records/Uncached.json", uncached),
                ("Records/LargeName.json", largeName),
                ("Records/Large.json", oversized));
            var replacementBytes = await File.ReadAllBytesAsync(replacementPath);
            Assert(replacementBytes.LongLength == originalLength, "same-size replacement fixture has identical archive length");
            var oldTimestamp = File.GetLastWriteTimeUtc(archivePath);
            await File.WriteAllBytesAsync(archivePath, replacementBytes);
            File.SetLastWriteTimeUtc(archivePath, fixedTime);
            Assert(File.GetLastWriteTimeUtc(archivePath) == oldTimestamp && new FileInfo(archivePath).Length == originalLength,
                "replacement fixture retains the prior size and timestamp");
            var staleRejected = false;
            try { await catalog.ReadAsync(firstEntry, snapshot, 1, 1, CancellationToken.None); }
            catch (InvalidOperationException exception)
            {
                staleRejected = exception.Message.Contains("snapshotId is not for the current bas.jsondb archive", StringComparison.Ordinal);
            }
            Assert(staleRejected, "full content hashing rejects a same-size, same-timestamp archive replacement");
            var changed = await MeasureSearchAsync(new BaseCatalogDataCatalog(), "deep brown phrase");
            string changedSnapshot;
            string changedEntry;
            using (var changedJson = JsonDocument.Parse(changed.Json))
            {
                changedSnapshot = changedJson.RootElement.GetProperty("snapshotId").GetString()!;
                changedEntry = changedJson.RootElement.GetProperty("results")[0].GetProperty("entryId").GetString()!;
                Assert(changedSnapshot != snapshot &&
                       changedJson.RootElement.GetProperty("total").GetInt32() == 1 &&
                       Count(changed, "catalogJsonParseCount") > 0 &&
                       Count(changed, "catalogArchiveBytesHashed") == originalLength,
                    "changed archive content creates a distinct snapshot and fresh parsed index after full hashing");
            }

            var otherInstall = CreateInstall(root, "other-install");
            var otherArchive = GameInstallConfiguration.CatalogArchivePath(
                new LaunchOptions(otherInstall.ExecutablePath, Array.Empty<string>()));
            Directory.CreateDirectory(Path.GetDirectoryName(otherArchive)!);
            await File.WriteAllBytesAsync(otherArchive, replacementBytes);
            await SelectInstallAsync(otherInstall);
            var isolated = await MeasureSearchAsync(new BaseCatalogDataCatalog(), "deep brown phrase");
            using var isolatedJson = JsonDocument.Parse(isolated.Json);
            var crossInstallStaleRejected = false;
            try { await new BaseCatalogDataCatalog().ReadAsync(changedEntry, changedSnapshot, 1, 1, CancellationToken.None); }
            catch (InvalidOperationException exception)
            {
                crossInstallStaleRejected = exception.Message.Contains("snapshotId is not for the current bas.jsondb archive", StringComparison.Ordinal);
            }
            Assert(Count(isolated, "catalogPersistentHit") == 0 && Count(isolated, "catalogJsonParseCount") > 0 &&
                   isolatedJson.RootElement.GetProperty("snapshotId").GetString() != changedSnapshot &&
                   crossInstallStaleRejected,
                "identical archive bytes at another installation use a separate cache and reject prior IDs");
            var termsInstall = CreateInstall(root, "term-overflow-install");
            var termsArchive = GameInstallConfiguration.CatalogArchivePath(
                new LaunchOptions(termsInstall.ExecutablePath, Array.Empty<string>()));
            await WriteArchiveAsync(termsArchive,
                ("Records/Twin.json", "{\"$type\":\"ThunderRoad.ItemData, ThunderRoad\",\"id\":\"twin\",\"name\":\"Twin\"}"));
            await SelectInstallAsync(termsInstall);
            var boundedHandoff = await new BaseCatalogDataCatalog().SearchTaskCatalogAsync(
                "Twin " + string.Join(' ', Enumerable.Range(0, 16).Select(index => "term" + index)),
                5, null, CancellationToken.None);
            Assert(!boundedHandoff.IsComplete && boundedHandoff.OmittedTerms.SequenceEqual(new[] { "term15" }) &&
                   boundedHandoff.Records.Count == 1 &&
                   boundedHandoff.Records[0].MatchedTerms.Contains("Twin", StringComparer.OrdinalIgnoreCase),
                "task catalog lookup returns partial matches, discloses omitted terms, and marks the handoff incomplete");


            var concurrentInstall = CreateInstall(root, "concurrent-install");
            var concurrentArchive = GameInstallConfiguration.CatalogArchivePath(
                new LaunchOptions(concurrentInstall.ExecutablePath, Array.Empty<string>()));
            var concurrentEntries = Enumerable.Range(0, 16)
                .Select(index => ($"Records/{index:D2}.json", "{\"id\":\"" + index + "\",\"payload\":\"" + new string('x', 1_900_000) + "\"}"))
                .ToArray();
            await WriteArchiveAsync(concurrentArchive, concurrentEntries);
            await SelectInstallAsync(concurrentInstall);
            var concurrentCatalog = new BaseCatalogDataCatalog();
            var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstConcurrent = MeasureSearchAsync(concurrentCatalog, null, CancellationToken.None, metrics =>
            {
                if (Count(metrics, "catalogIndexBuildCount") > 0 && Count(metrics, "catalogJsonParseCount") > 0)
                    buildStarted.TrySetResult();
            });
            await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var canceledWaiter = new CancellationTokenSource();
            var waiterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondConcurrent = MeasureSearchAsync(concurrentCatalog, null, canceledWaiter.Token,
                started: () => waiterStarted.TrySetResult());
            await waiterStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            canceledWaiter.Cancel();
            var canceledResult = await secondConcurrent;
            var firstResult = await firstConcurrent;
            Assert(canceledResult.Canceled && Count(firstResult.Metrics, "catalogIndexBuildCount") == 1,
                "a canceled operation waiting for bounded catalog admission does not cancel the active build");
            var afterCancellation = await MeasureSearchAsync(concurrentCatalog, null);
            using var afterCancellationJson = JsonDocument.Parse(afterCancellation.Json);
            Assert(Count(afterCancellation, "catalogJsonParseCount") == 0 &&
                   afterCancellationJson.RootElement.GetProperty("total").GetInt32() == concurrentEntries.Length,
                "the surviving build publishes a complete warm index after waiter cancellation");
            await CheckConcurrentPersistentAdmissionAsync(root, cacheDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_LAUNCH_CONFIG", priorLaunchConfig);
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR", priorCacheDir);
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_DIAGNOSTICS", priorDiagnostics);
            try { Directory.Delete(root, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<Measurement> MeasureSearchAsync(BaseCatalogDataCatalog catalog, string? query,
        CancellationToken cancellationToken = default, Action<Dictionary<string, object>>? observe = null, Action? started = null)
    {
        using var invocation = ToolTiming.BeginInvocation();
        var pending = catalog.SearchAsync(query, null, null, 0, 100, null, cancellationToken);
        started?.Invoke();
        while (!pending.IsCompleted)
        {
            observe?.Invoke(ToolTiming.Snapshot());
            await Task.Delay(1);
        }
        try
        {
            var json = await pending;
            return new Measurement(json, ToolTiming.Snapshot(), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new Measurement("", ToolTiming.Snapshot(), true);
        }
    }

    private static async Task SelectInstallAsync((string Directory, string ExecutablePath, string LaunchConfigPath) install)
    {
        await File.WriteAllTextAsync(install.LaunchConfigPath,
            JsonSerializer.Serialize(new { executablePath = install.ExecutablePath, arguments = Array.Empty<string>() }));
        Environment.SetEnvironmentVariable("THUNDERROAD_MCP_LAUNCH_CONFIG", install.LaunchConfigPath);
    }

    private static (string Directory, string ExecutablePath, string LaunchConfigPath) CreateInstall(string root, string name)
    {
        var game = Path.Combine(root, name, "Blade & Sorcery");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "BladeAndSorcery.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        var launchConfigPath = Path.Combine(root, name, "launch.json");
        File.WriteAllText(launchConfigPath,
            JsonSerializer.Serialize(new { executablePath = executable, arguments = Array.Empty<string>() }));
        return (game, executable, launchConfigPath);
    }

    private static async Task WriteArchiveAsync(string path, params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (entryPath, content) in entries)
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
            entry.LastWriteTime = EntryTimestamp;
            await using var entryStream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            await entryStream.WriteAsync(bytes);
        }
    }
    private static async Task CheckConcurrentPersistentAdmissionAsync(string root, string cacheDirectory)
    {
        const long maxDiskBytes = 128L * 1024 * 1024;
        var catalogDirectory = Path.Combine(cacheDirectory, "catalog");
        var budgetJson = "{\"id\":\"budget-record\",\"name\":\"Budget fixture\"}";
        var calibration = CreateInstall(root, "disk-calibration");
        var calibrationArchive = GameInstallConfiguration.CatalogArchivePath(
            new LaunchOptions(calibration.ExecutablePath, Array.Empty<string>()));
        await WriteArchiveAsync(calibrationArchive, ("Records/Budget.json", budgetJson));
        var existingNames = CacheArtifacts(catalogDirectory).Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await RunCatalogCacheChildAsync(calibration, cacheDirectory);
        var afterCalibration = CacheArtifacts(catalogDirectory);
        var calibrationFile = afterCalibration.Single(path => !existingNames.Contains(Path.GetFileName(path)));
        var expectedFileBytes = new FileInfo(calibrationFile).Length;
        File.Delete(calibrationFile);

        var baselineFiles = CacheArtifacts(catalogDirectory);
        var baselineBytes = baselineFiles.Sum(path => new FileInfo(path).Length);
        var fillerBytes = maxDiskBytes - baselineBytes - expectedFileBytes * 3 / 2;
        Assert(fillerBytes > 0, "fixture leaves room for a cache-admission boundary");
        var fillerPath = Path.Combine(catalogDirectory, "catalog-" + new string('A', 64) + ".idx");
        await using (var filler = new FileStream(fillerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            filler.SetLength(fillerBytes);
        File.SetLastWriteTimeUtc(fillerPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var beforeNames = CacheArtifacts(catalogDirectory).Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var first = CreateInstall(root, "disk-writer-one");
        var second = CreateInstall(root, "disk-writer-two");
        foreach (var install in new[] { first, second })
        {
            var archive = GameInstallConfiguration.CatalogArchivePath(
                new LaunchOptions(install.ExecutablePath, Array.Empty<string>()));
            await WriteArchiveAsync(archive, ("Records/Budget.json", budgetJson));
        }
        await Task.WhenAll(RunCatalogCacheChildAsync(first, cacheDirectory),
            RunCatalogCacheChildAsync(second, cacheDirectory));

        var artifacts = CacheArtifacts(catalogDirectory);
        var totalBytes = artifacts.Sum(path => new FileInfo(path).Length);
        var newIndexes = artifacts.Where(path => path.EndsWith(".idx", StringComparison.OrdinalIgnoreCase) &&
                                                 !beforeNames.Contains(Path.GetFileName(path)))
            .ToArray();
        var noTemporaryFiles = artifacts.All(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert(totalBytes <= maxDiskBytes && !File.Exists(fillerPath) && newIndexes.Length == 2 && noTemporaryFiles,
            "concurrent processes serialize exact-size admission, evict before writing, and stay within the disk budget");
    }

    private static async Task RunCatalogCacheChildAsync(
        (string Directory, string ExecutablePath, string LaunchConfigPath) install, string cacheDirectory)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("HostBehaviorCheck process path is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(CatalogReuseCheck).Assembly.Location);
        start.ArgumentList.Add("--catalog-cache-child");
        start.ArgumentList.Add(install.LaunchConfigPath);
        start.ArgumentList.Add(cacheDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start catalog cache child.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Catalog cache child failed: " + error);
        using var result = JsonDocument.Parse(output);
        Assert(result.RootElement.GetProperty("results").GetArrayLength() == 1,
            "catalog cache child returned its fixture record");
    }

    private static string[] CacheArtifacts(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "catalog-*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".idx", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();

    private static long Count(Measurement measurement, string name) => Count(measurement.Metrics, name);

    private static long Count(Dictionary<string, object> metrics, string name)
    {
        var counts = (Dictionary<string, long>)metrics["counts"];
        return counts.GetValueOrDefault(name);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Catalog reuse check failed: " + message);
    }

    private sealed record Measurement(string Json, Dictionary<string, object> Metrics, bool Canceled);
}
