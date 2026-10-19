using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public static class ApiLookupRegressionCheck
{
    public static async Task RunAsync(Func<string, object, Task<JsonDocument>> apiToolJsonAsync, Action<bool, string> assert)
    {
        using var assemblies = await apiToolJsonAsync("api_assemblies", new { name = "Fixture.Shared", origin = "game", limit = 50 });
        var assemblyId = assemblies.RootElement.GetProperty("assemblies").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Fixture.Shared")
            .GetProperty("assemblyId").GetString()!;


        using (var publicPrivateSearch = await apiToolJsonAsync("api_search", new
        {
            query = "PrivateIndexFallbackMarker", assemblyId, includeNonPublic = false,
        }))
            assert(publicPrivateSearch.RootElement.GetProperty("results").GetArrayLength() == 0,
                "private declarations are not exposed by the default public API index");
        using (var nonPublicSearch = await apiToolJsonAsync("api_search", new
        {
            query = "PrivateIndexFallbackMarker", assemblyId, includeNonPublic = true,
        }))
        {
            var privateHit = nonPublicSearch.RootElement.GetProperty("results").EnumerateArray().Single();
            assert(privateHit.GetProperty("accessibility").GetString() == "private",
                "non-public API search resolves private declarations through the bounded fallback");
            using var privateSource = await apiToolJsonAsync("api_decompile", new
            {
                assemblyId, symbolId = privateHit.GetProperty("symbolId").GetString(), startLine = 1, lineCount = 20,
            });
            assert(privateSource.RootElement.GetProperty("source").GetString()!.Contains("PrivateIndexFallbackMarker", StringComparison.Ordinal),
                "private fallback symbol IDs remain usable by api_decompile");
        }
        async Task<JsonDocument> Lookup(string query)
        {
            var document = await apiToolJsonAsync("api_lookup", new { query, assemblyId, maxResults = 20, includeExamples = false });
            var root = document.RootElement;
            assert(root.TryGetProperty("logicalChars", out var logicalChars) &&
                   logicalChars.GetInt32() == root.GetRawText().Length && logicalChars.GetInt32() <= 16000,
                "task logicalChars equals the serialized response length including itself and stays within the default maxChars");
            return document;
        }

        static JsonElement[] Results(JsonDocument document) =>
            document.RootElement.GetProperty("results").EnumerateArray().ToArray();
        static InstalledApiCatalog.ApiHit Hit(string symbolId, string qualifiedName, string signature) =>
            new("fixture-assembly", "Fixture.Shared", "game", "Fixture.Shared.dll", symbolId, "method",
                qualifiedName, "Fixture.Shared", signature, "public", false, Array.Empty<string>());

        static string MalformedContinuation(string query, string? assemblyId, int maxResults, int maxChars,
            bool includeExamples, string? snapshotId, bool includeSnapshotId)
        {
            var payload = new Dictionary<string, object?>
            {
                ["Version"] = 2,
                ["QueryFingerprint"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query))),
                ["AssemblyId"] = assemblyId,
                ["Kind"] = null,
                ["MaxResults"] = maxResults,
                ["MaxChars"] = maxChars,
                ["IncludeExamples"] = includeExamples,
                ["RankingVersion"] = ApiLookupRanker.Version,
                ["Offset"] = 2,
            };
            if (includeSnapshotId)
                payload["SnapshotId"] = snapshotId;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            var encoded = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "lk2." + encoded + "." + Convert.ToHexString(SHA256.HashData(bytes))[..16];
        }

        var spawnHit = Hit("spawn-symbol", "Fixture.Shared.Spawn", "public void Spawn()");
        var normalizedSpawn = ApiLookupRanker.Tokenize("spawning spawned spawns");
        var spawningResults = ApiLookupRanker.Rank("spawning", new[] { spawnHit }, 1);
        assert(normalizedSpawn.SequenceEqual(new[] { "spawn", "spawn", "spawn" }) && spawningResults.Count == 1 &&
               spawningResults[0].MatchedTokenCount == 1 && spawningResults[0].CoveragePercent == 100 && !spawningResults[0].IsLowConfidence,
            "gerund, past-tense, and plural forms normalize to a direct, full-coverage spawn match");
        var synonymOnly = ApiLookupRanker.Rank("summon", new[] { spawnHit }, 1);
        var partial = ApiLookupRanker.Rank("spawn item", new[] { spawnHit }, 1);
        var vague = ApiLookupRanker.Rank("spawn impossible phrase", new[] { spawnHit }, 1);
        assert(synonymOnly.Count == 1 && synonymOnly[0].IsLowConfidence &&
               partial.Count == 1 && partial[0].IsLowConfidence && vague.Count == 1 && vague[0].IsLowConfidence,
            "synonym-only, partial-coverage, and low-coverage matches remain low-confidence");

        var boundedCandidates = new ApiLookupRanker.CandidateAccumulator("target", ApiLookupRanker.MaxCandidates);
        for (var index = 0; index < ApiLookupRanker.MaxCandidates; index++)
            boundedCandidates.Add(Hit($"target-{index:D5}", "Fixture.Shared.TargetCandidate", "public void TargetCandidate()"));
        boundedCandidates.Add(Hit("best-target", "Fixture.Shared.Target", "public void Target()"));
        var retainedCandidates = boundedCandidates.GetSortedResults();
        assert(retainedCandidates.Length == ApiLookupRanker.MaxCandidates &&
               boundedCandidates.TotalMatches == ApiLookupRanker.MaxCandidates + 1 && boundedCandidates.OmittedCount == 1 &&
               retainedCandidates[0].Hit.symbolId == "best-target",
            "task lookup retains the best ranked candidates, bounds memory, and counts omitted matches");
        using (var task = await Lookup("generic nested dictionary"))
        {
            var root = task.RootElement;
            assert(root.GetProperty("mode").GetString() == "task" && root.TryGetProperty("logicalChars", out _) &&
                   root.GetProperty("omittedCandidates").GetInt32() == 0 && root.TryGetProperty("complete", out _) &&
                   root.TryGetProperty("truncated", out _) && root.TryGetProperty("continuation", out _),
                "query defaults to task mode with current response metadata");
            var result = Results(task).FirstOrDefault(item => item.GetProperty("qualifiedName").GetString() == "Fixture.Shared.SignatureSurface.GenericNested");
            assert(result.ValueKind != JsonValueKind.Undefined && result.GetProperty("relevance").GetProperty("score").GetInt32() > 0,
                "generic nested dictionary query ranks the fixture method with relevance metadata");
            assert(result.ValueKind != JsonValueKind.Undefined &&
                   result.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array &&
                   result.TryGetProperty("attributesTruncated", out _) &&
                   result.GetProperty("typeContext").TryGetProperty("attributes", out _) &&
                   result.GetProperty("typeContext").TryGetProperty("attributesTruncated", out _),
                "task result and type context include attribute metadata");
            if (result.ValueKind != JsonValueKind.Undefined)
                assert(result.GetProperty("typeContext").GetProperty("parameters").EnumerateArray()
                    .Any(parameter => parameter.GetString()!.Contains("Dictionary<string", StringComparison.Ordinal)),
                    "generic method response includes its signature type context");
        }

        using (var synonym = await Lookup("callback"))
            assert(Results(synonym).Any(item => item.GetProperty("qualifiedName").GetString() == "Fixture.Shared.SignatureSurface.SignatureChangedEvent"),
                "callback synonym discovers the fixture event");
        using (var camel = await Lookup("private setter"))
            assert(Results(camel).Any(item => item.GetProperty("qualifiedName").GetString() == "Fixture.Shared.SignatureSurface.PrivateSetterValue"),
                "separated query tokens discover the camel-case fixture property");
        using (var heldOut = await Lookup("nested constrained generic method"))
            assert(Results(heldOut).Any(item => item.GetProperty("qualifiedName").GetString() == "Fixture.Shared.SignatureSurface.GenericNested"),
                "held-out useful query finds the generic fixture method");
        using (var following = await Lookup("make a creature follow the local player"))
            assert(!following.RootElement.GetProperty("found").GetBoolean() && Results(following).Length == 0,
                "creature-following query does not invent declarations absent from the installed fixture");
        using (var heldOutFollowing = await Lookup("escort an actor alongside the avatar"))
            assert(!heldOutFollowing.RootElement.GetProperty("found").GetBoolean() && Results(heldOutFollowing).Length == 0,
                "held-out creature-following synonyms remain not-found when the fixture has no such API");
        using (var collisionEvents = await Lookup("collision damage event callbacks"))
        {
            var matches = Results(collisionEvents);
            assert(matches.Any(item => item.GetProperty("qualifiedName").GetString() == "Fixture.Shared.SignatureSurface.SignatureChangedEvent") &&
                   matches.All(item => item.GetProperty("relevance").GetProperty("confidence").GetString() == "low") &&
                   matches.All(item =>
                   {
                       var name = item.GetProperty("qualifiedName").GetString()!;
                       return !name.Contains("collision", StringComparison.OrdinalIgnoreCase) &&
                              !name.Contains("damage", StringComparison.OrdinalIgnoreCase);
                   }),
                "collision/damage event phrasing exposes only low-confidence generic fixture events, not invented collision APIs");
        }
        using (var cleanup = await apiToolJsonAsync("api_lookup", new
        {
            query = "clean up the spawned creature", assemblyId, maxResults = 5, includeExamples = true
        }))
        {
            var cleanupRoot = cleanup.RootElement;
            var cleanupExample = cleanupRoot.GetProperty("examples").EnumerateArray().FirstOrDefault();
            assert(!cleanupRoot.GetProperty("found").GetBoolean() && cleanupExample.ValueKind != JsonValueKind.Undefined &&
                   cleanupExample.GetProperty("id").GetString() == "creature.spawn" &&
                   cleanupExample.GetProperty("cleanup").EnumerateArray().Any(item =>
                       item.GetString()!.Contains("Despawn", StringComparison.Ordinal) &&
                       item.GetString()!.Contains("Remove", StringComparison.Ordinal)),
                "cleanup query returns the evidence-backed creature example with explicit despawn and runtime removal");
        }
        using (var runtimeCounters = await apiToolJsonAsync("api_lookup", new
        {
            query = "runtime counters", assemblyId, maxResults = 5, includeExamples = true
        }))
        {
            var counterRoot = runtimeCounters.RootElement;
            var counterExample = counterRoot.GetProperty("examples").EnumerateArray().FirstOrDefault();
            assert(!counterRoot.GetProperty("found").GetBoolean() && counterExample.ValueKind != JsonValueKind.Undefined &&
                   counterExample.GetProperty("id").GetString() == "runtime.counter",
                "runtime-counter query selects the matching evidence-backed counter example without claiming a fixture declaration");
        }
        using (var missingCreatureSpawn = await Lookup("Creature.SpawnAsync"))
            assert(!missingCreatureSpawn.RootElement.GetProperty("found").GetBoolean() &&
                   Results(missingCreatureSpawn).Length == 0,
                "Creature.SpawnAsync is explicitly not found in the installed fixture");
        assert(GameplayExamples.ForQuery("collision damage", Array.Empty<string>()).Count == 0,
            "no gameplay example claims collision/damage event support without matching evidence");

        using (var absent = await Lookup("quantum teleportation warp drive"))
            assert(!absent.RootElement.GetProperty("found").GetBoolean() && Results(absent).Length == 0,
                "nonexistent task query returns no declarations");
        using (var examples = await apiToolJsonAsync("api_lookup", new
        {
            query = "spawn an item near the player", assemblyId, maxResults = 5, includeExamples = true
        }))
        {
            var exampleRoot = examples.RootElement;
            var exampleIds = exampleRoot.GetProperty("examples").EnumerateArray()
                .Select(example => example.GetProperty("id").GetString()).ToArray();
            assert(exampleRoot.GetProperty("logicalChars").GetInt32() == exampleRoot.GetRawText().Length &&
                   exampleRoot.GetProperty("logicalChars").GetInt32() <= 16000 && exampleIds.FirstOrDefault() == "item.spawn",
                "task logicalChars stays within budget and natural-language item spawning ranks item.spawn first");
        }

        using (var context = await apiToolJsonAsync("api_lookup", new
               {
                   query = "SignatureSurface", assemblyId, maxResults = 20, maxChars = 20000, includeExamples = false
               }))
        {
            var result = Results(context).FirstOrDefault(item => item.GetProperty("qualifiedName").GetString() == "Fixture.Shared.SignatureSurface");
            assert(result.ValueKind != JsonValueKind.Undefined && result.GetProperty("typeContext").GetProperty("relevantMembers").EnumerateArray()
                       .Any(member => member.GetProperty("name").GetString() == "InheritedValue"),
                "generic fixture type context includes its inherited member");
            assert(context.RootElement.GetProperty("relatedTypes").EnumerateArray()
                       .Any(type => type.GetProperty("qualifiedName").GetString() == "Fixture.Shared.SignatureParent`1"),
                "generic fixture context resolves its related base type");
        }

        using (var budget = await apiToolJsonAsync("api_lookup", new
        {
            query = "Page", assemblyId, maxResults = 20, maxChars = 3000, includeExamples = false
        }))
        {
            var root = budget.RootElement;
            assert(root.GetProperty("logicalChars").GetInt32() == root.GetRawText().Length &&
                   root.GetProperty("logicalChars").GetInt32() <= 3000,
                "budget-limited task logicalChars includes itself and the JSON stays within 3000 characters");
            assert(root.GetProperty("truncated").GetBoolean() && !root.GetProperty("complete").GetBoolean(),
                "budget-limited task response reports truncated and incomplete");
        }

        using (var first = await apiToolJsonAsync("api_lookup", new
        {
            query = "Page", assemblyId, maxResults = 2, maxChars = 12000, includeExamples = false
        }))
        {
            var root = first.RootElement;
            var token = root.GetProperty("continuation").GetString();
            assert(root.GetProperty("logicalChars").GetInt32() == root.GetRawText().Length &&
                   root.GetProperty("logicalChars").GetInt32() <= 12000 &&
                   root.GetProperty("hasMore").GetBoolean() && !string.IsNullOrEmpty(token),
                "paged task response includes its own logicalChars and supplies a continuation");
            if (!string.IsNullOrEmpty(token))
            {
                using var next = await apiToolJsonAsync("api_lookup", new
                {
                    query = "Page", assemblyId, maxResults = 2, maxChars = 12000, includeExamples = false,
                    continuation = token, snapshotId = root.GetProperty("snapshotId").GetString()
                });
                var nextRoot = next.RootElement;
                assert(nextRoot.GetProperty("logicalChars").GetInt32() == nextRoot.GetRawText().Length &&
                       nextRoot.GetProperty("logicalChars").GetInt32() <= 12000 && nextRoot.GetProperty("offset").GetInt32() == 2 &&
                       Results(next).Select(item => item.GetProperty("symbolId").GetString()).Intersect(
                           Results(first).Select(item => item.GetProperty("symbolId").GetString())).Any() == false,
                    "continuation response includes its own logicalChars and advances without repeating declarations");

                async Task<bool> Rejected(object args)
                {
                    try { using var ignored = await apiToolJsonAsync("api_lookup", args); return false; }
                    catch (Exception) { return true; }
                }
                assert(await Rejected(new { query = "Page02", assemblyId, maxResults = 2, maxChars = 12000, includeExamples = false, continuation = token }),
                    "continuation is rejected for a changed query");
                assert(await Rejected(new { query = "Page", assemblyId, maxResults = 3, maxChars = 12000, includeExamples = false, continuation = token }),
                    "continuation is rejected for changed options");
                assert(await Rejected(new { query = "Page", assemblyId, maxResults = 2, maxChars = 12000, includeExamples = false, continuation = token, snapshotId = "changed-snapshot" }),
                    "continuation is rejected for a changed snapshot");
                async Task<string?> ErrorMessage(object args)
                {
                    try { using var ignored = await apiToolJsonAsync("api_lookup", args); return null; }
                    catch (Exception exception) { return exception.Message; }
                }
                var currentSnapshotId = root.GetProperty("snapshotId").GetString();
                foreach (var malformed in new[]
                         {
                             MalformedContinuation("Page", assemblyId, 2, 12000, false, null, includeSnapshotId: false),
                             MalformedContinuation("Page", assemblyId, 2, 12000, false, null, includeSnapshotId: true),
                         })
                {
                    var error = await ErrorMessage(new
                    {
                        query = "Page", assemblyId, maxResults = 2, maxChars = 12000, includeExamples = false,
                        continuation = malformed, snapshotId = currentSnapshotId
                    });
                    assert(error is not null && error.Contains("continuation is invalid", StringComparison.OrdinalIgnoreCase) &&
                           !error.Contains("NullReferenceException", StringComparison.OrdinalIgnoreCase),
                        "continuations with missing or null SnapshotId return compact actionable errors");
                }
            }
        }

        async Task<bool> OversizedQueryRejected()
        {
            try { using var ignored = await Lookup(new string('x', 2049)); return false; }
            catch (Exception) { return true; }
        }
        assert(await OversizedQueryRejected(), "oversized natural-language query is rejected rather than truncated");
    }
}
