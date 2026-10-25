using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class ApiPersistentCacheCheck
{
    private const string FixtureQualifiedName = "Fixture.Shared.SharedSurface";
    private const string TaskFixtureQualifiedName = "Fixture.Shared.XmlDocumentationSurface";

    public static async Task RunAsync(Func<string, object, Task<JsonDocument>> apiToolJsonAsync,
        Action<bool, string> assert, ApiReuseContext context)
    {
        using var assemblies = await apiToolJsonAsync("api_assemblies",
            new { name = "Fixture.Shared", origin = "game", limit = 50 });
        var assemblyId = assemblies.RootElement.GetProperty("assemblies").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Fixture.Shared")
            .GetProperty("assemblyId").GetString()!;
        var taskCacheDirectory = Path.Combine(context.CacheDirectory, "fresh-task-lookup");
        var coldTaskLookup = await RunFreshHostSearchAsync(context, assemblyId, assert,
            "fresh-host cold task lookup", build: -1, memoryHit: -1, diskHit: -1,
            taskLookup: true, cacheDirectory: taskCacheDirectory);
        assert(coldTaskLookup.Build > 0 && coldTaskLookup.DiskHit == 0,
            "fresh-host cold task lookup builds metadata without loading a persisted API index");
        var persistedTaskLookup = await RunFreshHostSearchAsync(context, assemblyId, assert,
            "fresh-host persisted task lookup", build: -1, memoryHit: -1, diskHit: -1,
            taskLookup: true, cacheDirectory: taskCacheDirectory);
        assert(persistedTaskLookup.Build == 0 && persistedTaskLookup.DiskHit > 0,
            "fresh-host persisted task lookup reuses cached metadata without rebuilding an API index");


        async Task<JsonDocument> SearchAsync() => await apiToolJsonAsync("api_search", new
        {
            query = "SharedSurface", kind = "type", assemblyId, offset = 0, limit = 50
        });

        using (var cold = await SearchAsync())
        {
            AssertCompleteFixtureResult(cold, assert, "current-host cold search");
            AssertCounters(context.LastDiagnostics ?? throw new InvalidOperationException("Current-host diagnostics were not captured."),
                assert, "current-host cold search", build: 1, memoryHit: 0, diskHit: 0);
        }

        var indexPath = FindApiIndexPath(context.CacheDirectory, assemblyId, assert);
        var validCache = await File.ReadAllBytesAsync(indexPath);
        AssertCacheDocument(validCache, assemblyId, assert);
        try
        {
            File.Delete(indexPath);
            var concurrentResults = await Task.WhenAll(
                RunFreshHostSearchAsync(context, assemblyId, assert, "concurrent host writer A", build: -1, memoryHit: -1, diskHit: -1),
                RunFreshHostSearchAsync(context, assemblyId, assert, "concurrent host writer B", build: -1, memoryHit: -1, diskHit: -1));
            assert(concurrentResults.All(result => result.Build + result.DiskHit == 1 && result.MemoryHit == 0),
                "concurrent hosts either build or load the shared API cache entry without memory-only false hits");
            AssertCacheDocument(await File.ReadAllBytesAsync(indexPath), assemblyId, assert);
            assert(!Directory.GetFiles(Path.GetDirectoryName(indexPath)!, Path.GetFileName(indexPath) + ".*.tmp").Any(),
                "concurrent host publication leaves no partial temporary API index");

            using (var warm = await SearchAsync())
            {
                AssertCompleteFixtureResult(warm, assert, "current-host warm search");
                AssertCounters(context.LastDiagnostics ?? throw new InvalidOperationException("Current-host diagnostics were not captured."),
                    assert, "current-host warm search", build: 0, memoryHit: 1, diskHit: 0);
            }

            await RunFreshHostSearchAsync(context, assemblyId, assert,
                "second-host disk hit", build: 0, memoryHit: 0, diskHit: 1);

            await WriteIndexPropertiesAsync(indexPath, validCache,
                new KeyValuePair<string, JsonNode?>("Complete", JsonValue.Create(false)),
                new KeyValuePair<string, JsonNode?>("OmittedDeclarations", JsonValue.Create(1)),
                new KeyValuePair<string, JsonNode?>("OmittedPublicDeclarations", JsonValue.Create(1)));
            await RunFreshHostSearchAsync(context, assemblyId, assert,
                "explicit partial-index reuse", build: 0, memoryHit: 0, diskHit: 1);

            await File.WriteAllTextAsync(indexPath, "{ corrupt api index");
            await RunFreshHostSearchAsync(context, assemblyId, assert,
                "corrupt-index recovery", build: 1, memoryHit: 0, diskHit: 0);

            await WriteMutatedIndexAsync(indexPath, validCache, "Schema",
                JsonValue.Create(ReadSchema(validCache) + 1));
            await RunFreshHostSearchAsync(context, assemblyId, assert,
                "schema-version recovery", build: 1, memoryHit: 0, diskHit: 0);

            await WriteMutatedIndexAsync(indexPath, validCache, "Complete", JsonValue.Create(false));
            await RunFreshHostSearchAsync(context, assemblyId, assert,
                "incomplete-index recovery", build: 1, memoryHit: 0, diskHit: 0);
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            await File.WriteAllBytesAsync(indexPath, validCache);
            foreach (var temporary in Directory.GetFiles(Path.GetDirectoryName(indexPath)!,
                         Path.GetFileName(indexPath) + ".*.tmp"))
                File.Delete(temporary);
        }
    }

    private static void AssertCompleteFixtureTaskLookup(JsonDocument lookup, Action<bool, string> assert, string scenario)
    {
        var root = lookup.RootElement;
        var fixtureResult = root.GetProperty("results").EnumerateArray().FirstOrDefault(result =>
            result.GetProperty("qualifiedName").GetString() == TaskFixtureQualifiedName);
        assert(root.GetProperty("mode").GetString() == "task" &&
               root.GetProperty("effectiveScope").GetString() == "installed" &&
               root.GetProperty("found").GetBoolean() &&
               fixtureResult.ValueKind == JsonValueKind.Object &&
               fixtureResult.GetProperty("relevance").GetProperty("documentationMatch").GetBoolean() &&
               fixtureResult.GetProperty("relevance").GetProperty("matchedTokens").GetInt32() == 3 &&
               fixtureResult.GetProperty("relevance").GetProperty("coveragePercent").GetInt32() == 100 &&
               fixtureResult.GetProperty("relevance").GetProperty("confidence").GetString() == "supported",
            $"{scenario} returns the XML-only moonstone Fixture.Shared task result with documentation evidence and supported confidence");
    }
    private static void AssertCompleteFixtureResult(JsonDocument search, Action<bool, string> assert, string scenario)
    {
        var root = search.RootElement;
        var results = root.GetProperty("results");
        assert(root.GetProperty("total").GetInt32() == 1 && results.GetArrayLength() == 1 &&
               !root.GetProperty("hasMore").GetBoolean() &&
               results[0].GetProperty("qualifiedName").GetString() == FixtureQualifiedName,
            $"{scenario} returns the complete Fixture.Shared result");
    }


    private static void AssertCounters(JsonDocument diagnostics, Action<bool, string> assert, string scenario,
        long build, long memoryHit, long diskHit)
    {
        if (build >= 0)
            assert(Counter(diagnostics, "api.index.build") == build,
                $"{scenario} reports api.index.build={build}");
        if (memoryHit >= 0)
            assert(Counter(diagnostics, "api.index.memoryHit") == memoryHit,
                $"{scenario} reports api.index.memoryHit={memoryHit}");
        if (diskHit >= 0)
            assert(Counter(diagnostics, "api.index.diskHit") == diskHit,
                $"{scenario} reports api.index.diskHit={diskHit}");
    }

    private static long Counter(JsonDocument diagnostics, string name)
    {
        var counts = diagnostics.RootElement.GetProperty("counts");
        return counts.TryGetProperty(name, out var count) ? count.GetInt64() : 0;
    }

    private static string FindApiIndexPath(string cacheDirectory, string assemblyId, Action<bool, string> assert)
    {
        var directory = Path.Combine(cacheDirectory, "api-index-v6");
        var matches = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Where(path =>
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                    return document.RootElement.TryGetProperty("AssemblyId", out var id) &&
                           id.GetString() == assemblyId;
                }
                catch (JsonException) { return false; }
            }).ToArray()
            : Array.Empty<string>();
        assert(matches.Length == 1, "one api-index-v6 JSON entry exists for Fixture.Shared game assembly");
        return matches.Single();
    }

    private static void AssertCacheDocument(byte[] bytes, string assemblyId, Action<bool, string> assert)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var symbols = root.GetProperty("Symbols").EnumerateArray().ToArray();
        var types = root.GetProperty("Types").EnumerateArray().ToArray();
        var omitted = root.GetProperty("OmittedDeclarations").GetInt32();
        var omittedPublic = root.GetProperty("OmittedPublicDeclarations").GetInt32();
        var limitReached = root.GetProperty("LimitReached").GetBoolean();
        assert(root.GetProperty("Schema").GetInt32() == 6 &&
               root.GetProperty("AssemblyId").GetString() == assemblyId &&
               root.GetProperty("Complete").GetBoolean() == (omittedPublic == 0 && !limitReached) &&
               omittedPublic == 0 && omittedPublic <= omitted &&
               symbols.Length > 0 && types.Length > 0,
            "cache entry uses the complete api-index-v6 JSON schema with honest public-completeness flags");
        assert(symbols.Any(symbol =>
                   symbol.GetProperty("Hit").TryGetProperty("xmlDocumentation", out var documentation) &&
                   documentation.TryGetProperty("summary", out var summary) &&
                   summary.GetString()!.StartsWith("XML_DOC_", StringComparison.Ordinal)),
            "persistent API index retains normalized XML documentation");
        assert(!symbols.Any(symbol => symbol.GetProperty("Hit").GetProperty("qualifiedName").GetString()!
                   .Contains("PrivateIndexFallbackMarker", StringComparison.Ordinal)),
            "private member declarations are omitted from the persistent common index");
        var type = types[0];
        assert(type.TryGetProperty("SymbolId", out _) && type.TryGetProperty("MemberSymbolIds", out _) &&
               !type.TryGetProperty("Symbol", out _) && !type.TryGetProperty("Members", out _),
            "persistent type records reference shared symbol IDs instead of duplicating declarations");
    }

    private static int ReadSchema(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.GetProperty("Schema").GetInt32();
    }

    private static async Task WriteMutatedIndexAsync(string path, byte[] validCache, string property, JsonNode? value)
    {
        await WriteIndexPropertiesAsync(path, validCache, new KeyValuePair<string, JsonNode?>(property, value));
    }

    private static async Task WriteIndexPropertiesAsync(string path, byte[] validCache,
        params KeyValuePair<string, JsonNode?>[] properties)
    {
        var document = JsonNode.Parse(validCache)!.AsObject();
        foreach (var property in properties)
            document[property.Key] = property.Value;
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(document));
    }

    private static async Task<(long Build, long MemoryHit, long DiskHit)> RunFreshHostSearchAsync(
        ApiReuseContext context, string assemblyId, Action<bool, string> assert,
        string scenario, long build, long memoryHit, long diskHit,
        bool taskLookup = false, string? cacheDirectory = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.GetFullPath(context.HostAssemblyPath));
        start.Environment["THUNDERROAD_MCP_CONFIG"] = Path.GetFullPath(context.BridgeConfigPath);
        start.Environment["THUNDERROAD_MCP_LOG_DIR"] = Path.GetFullPath(context.LogDirectory);
        start.Environment["THUNDERROAD_MCP_LAUNCH_CONFIG"] = Path.GetFullPath(context.LaunchConfigPath);
        start.Environment["THUNDERROAD_MCP_CACHE_DIR"] = Path.GetFullPath(cacheDirectory ?? context.CacheDirectory);
        start.Environment["THUNDERROAD_MCP_DIAGNOSTICS"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start API cache child host.");
        try
        {
            await WriteRequestAsync(process, new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-03-26", capabilities = new { },
                    clientInfo = new { name = "ApiPersistentCacheCheck", version = "1" }
                }
            });
            using (var initialize = await ReadResponseAsync(process))
                assert(initialize.RootElement.GetProperty("result").GetProperty("serverInfo")
                           .GetProperty("name").GetString() == "ThunderRoadMcp",
                    $"{scenario} initializes an independent MCP host");

            await WriteRequestAsync(process, new
            {
                jsonrpc = "2.0", method = "notifications/initialized", @params = new { }
            });
            await WriteRequestAsync(process, new
            {
                jsonrpc = "2.0", id = 2, method = "tools/call",
                @params = new
                {
                    name = taskLookup ? "api_lookup" : "api_search",
                    arguments = taskLookup
                        ? (object)new
                        {
                            query = "moonstone fixture surface", assemblyId, scope = "installed",
                            maxResults = 3, maxChars = 8000, includeExamples = false,
                        }
                        : new { query = "SharedSurface", kind = "type", assemblyId, offset = 0, limit = 50 }
                }
            });

            using var response = await ReadResponseAsync(process);
            var result = response.RootElement.GetProperty("result");
            if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
                throw new InvalidOperationException($"{scenario} {(taskLookup ? "api_lookup" : "api_search")} failed: {result.GetRawText()}");
            assert(result.TryGetProperty("structuredContent", out var structured) &&
                   structured.ValueKind == JsonValueKind.Object,
                $"{scenario} returns structured content");
            var text = result.GetProperty("content")[0].GetProperty("text").GetString()
                ?? throw new InvalidOperationException($"{scenario} returned no text content.");
            using var search = JsonDocument.Parse(text);
            if (taskLookup)
                AssertCompleteFixtureTaskLookup(search, assert, scenario);
            else
                AssertCompleteFixtureResult(search, assert, scenario);
            using var diagnostics = await ReadDiagnosticAsync(process.StandardError);
            var actualBuild = Counter(diagnostics, "api.index.build");
            var actualMemoryHit = Counter(diagnostics, "api.index.memoryHit");
            var actualDiskHit = Counter(diagnostics, "api.index.diskHit");
            AssertCounters(diagnostics, assert, scenario, build, memoryHit, diskHit);

            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var remainingOutput = await process.StandardOutput.ReadToEndAsync();
            var remainingError = await process.StandardError.ReadToEndAsync();
            assert(process.ExitCode == 0,
                $"{scenario} child host exits successfully: {remainingError}{remainingOutput}");
            return (actualBuild, actualMemoryHit, actualDiskHit);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { }
            }
        }
    }

    private static async Task WriteRequestAsync(Process process, object request)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process)
    {
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))
            ?? throw new EndOfStreamException("API cache child host closed MCP stdout.");
        return JsonDocument.Parse(line);
    }

    private static async Task<JsonDocument> ReadDiagnosticAsync(StreamReader reader)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))
                ?? throw new EndOfStreamException("API cache child host closed stderr before writing ToolTiming diagnostics.");
            try
            {
                var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("counts", out _))
                    return document;
                document.Dispose();
            }
            catch (JsonException) { }
        }
    }
}
