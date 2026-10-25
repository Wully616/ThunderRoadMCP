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

        foreach (var (name, expected) in new[]
                 {
                     ("Extend", "this Fixture.Shared.ApiAttributeSurface receiver"),
                     ("ExtendRef", "this ref int receiver"),
                     ("ExtendIn", "this in int receiver"),
                     ("ExtendRefReadonly", "this ref readonly int receiver"),
                     ("KeywordParameters", "string @class, string @event"),
                 })
        {
            using var signature = await apiToolJsonAsync("api_search", new { query = name, assemblyId });
            var declaration = signature.RootElement.GetProperty("results").EnumerateArray()
                .Single(item => item.GetProperty("qualifiedName").GetString() == "Fixture.Shared.ApiAttributeExtensions." + name)
                .GetProperty("signature").GetString()!;
            assert(declaration.Contains(expected, StringComparison.Ordinal),
                $"formatted {name} signature preserves receiver modifiers and escapes keyword parameter names; got: {declaration}");
        }


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
            var document = await apiToolJsonAsync("api_lookup", new { query, assemblyId, maxResults = 20, maxChars = 16000, includeExamples = false });
            var root = document.RootElement;
            assert(root.TryGetProperty("logicalChars", out var logicalChars) &&
                   logicalChars.GetInt32() == root.GetRawText().Length && logicalChars.GetInt32() <= 16000,
                "task logicalChars equals the serialized response length including itself and stays within the requested maxChars");
            return document;
        }

        static JsonElement[] Results(JsonDocument document) =>
            document.RootElement.GetProperty("results").EnumerateArray().ToArray();
        static InstalledApiCatalog.ApiHit Hit(string symbolId, string qualifiedName, string signature,
            InstalledApiCatalog.ApiXmlDocumentation? documentation = null, string kind = "method",
            string? declaringType = "Fixture.Shared") =>
            new("fixture-assembly", "Fixture.Shared", "game", "Fixture.Shared.dll", symbolId, kind,
                qualifiedName, declaringType, signature, "public", false, Array.Empty<string>(),
                xmlDocumentation: documentation);

        static InstalledApiCatalog.ApiXmlDocumentation Documentation(string summary) =>
            new(summary, null, null, null, null, null, null);


        using (var compact = await apiToolJsonAsync("api_lookup", new { query = "generic nested dictionary", assemblyId }))
        {
            var root = compact.RootElement;
            assert(root.GetProperty("found").GetBoolean() &&
                   root.GetProperty("results").GetArrayLength() is > 0 and <= 3 &&
                   root.GetProperty("examples").GetArrayLength() == 0 &&
                   root.GetProperty("logicalChars").GetInt32() <= 8000,
                "default task lookup returns useful results, limits the page to three declarations, and keeps examples opt-in");
        }

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
        var inflectedActions = ApiLookupRanker.Tokenize("changing changed updating");
        assert(inflectedActions.SequenceEqual(new[] { "change", "change", "update" }) &&
               inflectedActions.All(ApiLookupRanker.IsActionToken),
            "changing, changed, and updating normalize to recognized action tokens");
        var synonymOnly = ApiLookupRanker.Rank("summon", new[] { spawnHit }, 1);
        var partial = ApiLookupRanker.Rank("spawn item", new[] { spawnHit }, 1);
        var vague = ApiLookupRanker.Rank("spawn impossible phrase", new[] { spawnHit }, 1);
        assert(synonymOnly.Count == 0 && partial.Count == 0 && vague.Count == 0,
            "action/synonym-only queries and unsupported partial matches do not retain unrelated declarations");

        var attachmentFalsePositive = Hit("mail-attachment", "System.Net.Mail.Attachment.CreateAttachmentFromString",
            "public static System.Net.Mail.Attachment CreateAttachmentFromString(string value)");
        var itemLifecycle = Hit("item-lifecycle", "ThunderRoad.ItemModule.OnItemLoaded",
            "public virtual void OnItemLoaded(ThunderRoad.Item item)",
            Documentation("Called after an item spawned from its owning data has loaded, to attach a MonoBehaviour and runtime behaviour to it."));
        var intentMatches = ApiLookupRanker.Rank("Create an ItemModule that attaches a MonoBehaviour to an item",
            new[] { attachmentFalsePositive, itemLifecycle }, 2);
        assert(intentMatches.Count == 1 &&
               intentMatches[0].Hit.qualifiedName == "ThunderRoad.ItemModule.OnItemLoaded" &&
               intentMatches[0].HasDocumentationMatch,
            "meaningful identifier and XML evidence ranks the item lifecycle hook while rejecting generic Create false positives");
        assert(ApiLookupRanker.Rank("Create", new[] { attachmentFalsePositive }, 1).Count == 0,
            "a generic action verb alone cannot promote a method with a longer unrelated name");


        var partialSpawnMatch = Hit("partial-spawn", "ThunderRoad.BowString.SpawnAndAttachArrow",
            "public void SpawnAndAttachArrow()", Documentation("Spawns an arrow of the given item ID."));
        var documentedItemLifecycle = Hit("documented-item-lifecycle", "ThunderRoad.ItemModule.OnItemLoaded",
            "public virtual void OnItemLoaded(ThunderRoad.Item item)",
            Documentation("Called once an item spawned from the owning data has loaded, to attach runtime behaviour to it."));
        var lifecycleRanking = ApiLookupRanker.Rank("attach runtime behaviour to a spawned sword",
            new[] { partialSpawnMatch, documentedItemLifecycle }, 2);
        assert(lifecycleRanking.Count == 2 &&
               lifecycleRanking[0].Hit.symbolId == "documented-item-lifecycle" &&
               !lifecycleRanking[0].IsLowConfidence &&
               lifecycleRanking[0].CoveragePercent > lifecycleRanking[1].CoveragePercent,
            "complete XML-documentation evidence outranks a partial direct-name spawn/attach match");
        var catalogBackedType = Hit("catalog-backed-type", "Fixture.Shared.CatalogBackedType",
            "public class CatalogBackedType", kind: "class", declaringType: null);
        var catalogRanker = new ApiLookupRanker.CandidateAccumulator("moonstone", 1,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Fixture.Shared.CatalogBackedType" });
        catalogRanker.Add(catalogBackedType);
        var catalogRank = catalogRanker.GetSortedResults();
        assert(catalogRank.Length == 1 && catalogRank[0].HasCatalogMatch && !catalogRank[0].IsLowConfidence,
            "an indexed API type is supported and ranked when catalog $type evidence matches it");

        var aliases = ApiLookupRanker.MatchedConceptAliases("weapons swords props NPCs");
        assert(aliases.Select(alias => alias.Term).ToHashSet(StringComparer.Ordinal)
                   .SetEquals(new[] { "weapon", "sword", "prop", "npc" }) &&
               aliases.Where(alias => alias.CanonicalConcept == "item").All(alias => alias.CatalogTypeName == "ItemData") &&
               aliases.Where(alias => alias.CanonicalConcept == "creature").All(alias => alias.CatalogTypeName == "CreatureData"),
            "curated singular and plural aliases nominate only the item and creature catalog concepts");
        var flyDirectionReference = Hit("item-fly-reference", "ThunderRoad.Item.flyDirRef",
            "public UnityEngine.Transform flyDirRef { get; set; }",
            Documentation("Reference transform for the item's flight direction and orientation."),
            kind: "property", declaringType: "ThunderRoad.Item");
        var diveMode = Hit("dive-mode", "ThunderRoad.Creature.DiveType", "public enum DiveType",
            Documentation("Orientation mode for creature diving."), kind: "enum", declaringType: "ThunderRoad.Creature");
        var flyReferenceRanking = ApiLookupRanker.Rank("sword fly reference orientation",
            new[] { diveMode, flyDirectionReference }, 2);
        assert(flyReferenceRanking.Count == 2 &&
               flyReferenceRanking[0].Hit.symbolId == "item-fly-reference" &&
               flyReferenceRanking[0].CoveragePercent == 100 &&
               !flyReferenceRanking[0].IsLowConfidence,
            "runtime owner/type agreement and ref-direction normalization rank Item.flyDirRef over an unrelated enum");

        var heightOperation = Hit("documented-height-operation", "ThunderRoad.Water.SetOceanHeight",
            "public void SetOceanHeight(float height)",
            Documentation("Changes the water surface height during runtime updates."),
            declaringType: "ThunderRoad.Water");
        var writableHeight = Hit("writable-height", "ThunderRoad.Water.OceanHeight",
            "public float OceanHeight { get; set; }",
            Documentation("Writable water surface height for the active ocean."),
            kind: "property", declaringType: "ThunderRoad.Water");
        var configurationFlag = Hit("height-configuration", "ThunderRoad.Water.useOceanHeight",
            "public bool useOceanHeight { get; set; }",
            Documentation("Configuration flag controls whether the water surface height is enabled."),
            kind: "property", declaringType: "ThunderRoad.Water");
        var unrelatedHeightMethod = Hit("height-analysis", "ThunderRoad.Water.CalculateHeight",
            "public float CalculateHeight()", kind: "method", declaringType: "ThunderRoad.Water");
        var waterHeightRanking = ApiLookupRanker.Rank("change water surface height",
            new[] { configurationFlag, unrelatedHeightMethod, writableHeight, heightOperation }, 4);
        var operationPosition = Array.FindIndex(waterHeightRanking.ToArray(),
            result => result.Hit.symbolId == "documented-height-operation");
        var propertyPosition = Array.FindIndex(waterHeightRanking.ToArray(),
            result => result.Hit.symbolId == "writable-height");
        var flagPosition = Array.FindIndex(waterHeightRanking.ToArray(),
            result => result.Hit.symbolId == "height-configuration");
        assert(operationPosition >= 0 && propertyPosition >= 0 && flagPosition > operationPosition &&
               flagPosition > propertyPosition && propertyPosition < Array.FindIndex(waterHeightRanking.ToArray(),
                   result => result.Hit.symbolId == "height-analysis"),
            "documented height operations and writable properties outrank configuration flags without methods-first bias");
        var documentedSetterAction = Hit("documented-setter-action", "Fixture.Shared.Projectile.velocity",
            "public float velocity { get; set; }", Documentation("Writable velocity of the projectile."),
            kind: "property", declaringType: "Fixture.Shared.Projectile");
        var directSetterAction = ApiLookupRanker.Rank("assign speed", new[] { documentedSetterAction }, 1);
        var prefilteredSetterAction = new ApiLookupRanker.CandidateAccumulator("assign speed", 1);
        prefilteredSetterAction.Add(documentedSetterAction);
        var prefilteredSetterResults = prefilteredSetterAction.GetSortedResults();
        assert(directSetterAction.Count == 1 && prefilteredSetterResults.Length == 1 &&
               prefilteredSetterResults[0].Hit.symbolId == directSetterAction[0].Hit.symbolId &&
               prefilteredSetterResults[0].HasDocumentationMatch,
            "task prefilter retains XML-only setter-language evidence for assign speed and writable velocity");
        var inflectedWaterActions = new[] { "changing", "changed", "updating" }
            .Select(action => ApiLookupRanker.Rank($"{action} water surface height",
                new[] { heightOperation }, 1).Single());
        assert(inflectedWaterActions.All(result =>
                result.Hit.symbolId == "documented-height-operation" && result.CoveragePercent == 100),
            "inflected mutation actions retain full water-height documentation matches");
        var oceanHeightSetter = Hit("ocean-height-setter", "ThunderRoad.OceanFogController.OceanHeight",
            "public float OceanHeight",
            Documentation("World Y height of ocean surface. Setting it shifts this object to match."),
            kind: "field", declaringType: "ThunderRoad.OceanFogController");
        var automaticWaterHeight = Hit("automatic-water-height", "ThunderRoad.Water.waterHeightCanChangeovertime",
            "public bool waterHeightCanChangeovertime",
            Documentation("When true water height may change over time."),
            kind: "field", declaringType: "ThunderRoad.Water");
        var navMeshHeightBuilder = Hit("navmesh-height-builder", "UnityEngine.AI.NavMeshSurface.buildHeightMesh",
            "public void buildHeightMesh()", kind: "method", declaringType: "UnityEngine.AI.NavMeshSurface");
        var documentedWaterHeightRanking = ApiLookupRanker.Rank("change water surface height",
            new[] { automaticWaterHeight, navMeshHeightBuilder, oceanHeightSetter }, 3);
        var automaticHeightPosition = Array.FindIndex(documentedWaterHeightRanking.ToArray(),
            result => result.Hit.symbolId == "automatic-water-height");
        var navMeshBuilderPosition = Array.FindIndex(documentedWaterHeightRanking.ToArray(),
            result => result.Hit.symbolId == "navmesh-height-builder");
        assert(documentedWaterHeightRanking.Count == 3 &&
               documentedWaterHeightRanking[0].Hit.symbolId == "ocean-height-setter" &&
               automaticHeightPosition > 0 && navMeshBuilderPosition > 0,
            "XML setter and ocean-owner evidence ranks the actionable surface-height property ahead of automatic-change and incidental height candidates");
        var meteorThrow = Hit("throw-meteor", "ThunderRoad.Skill.ThrowMeteor",
            "public void ThrowMeteor()", Documentation("Launches a meteor projectile at the target."),
            declaringType: "ThunderRoad.Skill");
        var genericProjectileLaunch = Hit("generic-projectile-launch", "ThunderRoad.Projectile.Launch",
            "public void Launch()", Documentation("Launches a projectile."),
            declaringType: "ThunderRoad.Projectile");
        var meteorRanking = ApiLookupRanker.Rank("launch meteor projectile",
            new[] { genericProjectileLaunch, meteorThrow }, 2);
        assert(meteorRanking.Count == 2 && meteorRanking[0].Hit.symbolId == "throw-meteor",
            "complete documented meteor evidence keeps ThrowMeteor ahead of a generic projectile launcher");


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
            query = "clean up the spawned creature", assemblyId, maxResults = 5, maxChars = 16000, includeExamples = true
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
            query = "runtime counters", assemblyId, maxResults = 5, maxChars = 16000, includeExamples = true
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

        using (var unsupportedConcept = await Lookup("sword"))
        {
            var hint = unsupportedConcept.RootElement.GetProperty("conceptHints").EnumerateArray().Single();
            assert(hint.GetProperty("term").GetString() == "sword" &&
                   hint.GetProperty("concept").GetString() == "item" &&
                   hint.GetProperty("dataApiType").ValueKind == JsonValueKind.Null &&
                   hint.GetProperty("runtimeApiType").ValueKind == JsonValueKind.Null,
                "an alias may name the item concept but cannot invent API types absent from the selected assembly");
        }

        using (var documentationSearch = await apiToolJsonAsync("api_search", new
        {
            query = "moonstone fixture surface", assemblyId, scope = "installed", limit = 20,
        }))
        {
            var documentedType = documentationSearch.RootElement.GetProperty("results").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("qualifiedName").GetString() == "Fixture.Shared.XmlDocumentationSurface");
            assert(documentedType.ValueKind != JsonValueKind.Undefined &&
                   documentedType.GetProperty("xmlDocumentation").GetProperty("summary").GetString()!
                       .Contains("moonstone fixture surface", StringComparison.OrdinalIgnoreCase),
                "api_search discovers a distinctive XML-only phrase and returns its documentation snippet");
        }

        using (var examples = await apiToolJsonAsync("api_lookup", new
        {
            query = "spawn an item near the player", assemblyId, maxResults = 5, maxChars = 16000, includeExamples = true
        }))
        {
            var exampleRoot = examples.RootElement;
            var exampleIds = exampleRoot.GetProperty("examples").EnumerateArray()
                .Select(example => example.GetProperty("id").GetString()).ToArray();
            assert(exampleRoot.GetProperty("logicalChars").GetInt32() == exampleRoot.GetRawText().Length &&
                   exampleRoot.GetProperty("logicalChars").GetInt32() <= 16000 && exampleIds.FirstOrDefault() == "item.spawn",
                "task logicalChars stays within budget and natural-language item spawning ranks item.spawn first");
        }

        using (var budget = await apiToolJsonAsync("api_lookup", new
        {
            query = "Signature", assemblyId, maxResults = 20, maxChars = 6000, includeExamples = false
        }))
        {
            var root = budget.RootElement;
            assert(root.GetProperty("logicalChars").GetInt32() == root.GetRawText().Length &&
                   root.GetProperty("logicalChars").GetInt32() <= 6000,
                "budget-limited task logicalChars includes itself and the JSON stays within 6000 characters");
            assert(root.GetProperty("truncated").GetBoolean() && !root.GetProperty("complete").GetBoolean(),
                "budget-limited task response reports truncated and incomplete");
            var returnedResults = root.GetProperty("results").EnumerateArray().ToArray();
            var resultIds = returnedResults
                .Select(item => item.TryGetProperty("symbolId", out var symbolId) ? symbolId.GetString() : null)
                .ToArray();
            var returnedResultIds = resultIds.Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!).ToHashSet(StringComparer.Ordinal);
            var relatedTypes = root.GetProperty("relatedTypes").EnumerateArray().ToArray();
            var relatedIds = relatedTypes.Select(item => item.GetProperty("symbolId").GetString()!).ToHashSet(StringComparer.Ordinal);
            var catalogOwners = root.GetProperty("catalog").GetProperty("records").EnumerateArray()
                .Select(item => "catalog:" + item.GetProperty("entryId").GetString()).ToHashSet(StringComparer.Ordinal);
            var resultEdgesResolve = returnedResults.All(item =>
                !item.TryGetProperty("typeContext", out var context) ||
                !context.TryGetProperty("relatedTypeIds", out var ids) ||
                ids.EnumerateArray().All(id => relatedIds.Contains(id.GetString()!)));
            var relatedEdgesResolve = relatedTypes.All(item =>
                item.GetProperty("relatedTo").EnumerateArray().All(owner =>
                    returnedResultIds.Contains(owner.GetString()!) || catalogOwners.Contains(owner.GetString()!)));
            assert(resultIds.Length > 0 && resultIds.All(id => !string.IsNullOrEmpty(id)) &&
                   returnedResultIds.Count == resultIds.Length &&
                   resultIds.Length < root.GetProperty("total").GetInt32() &&
                   resultEdgesResolve && relatedEdgesResolve,
                $"budget trimming preserves IDs for every returned declaration and resolves any returned relationship links; results={resultIds.Length}, related={relatedIds.Count}");
        }


        using (var first = await apiToolJsonAsync("api_lookup", new
        {
            query = "Page", assemblyId, maxResults = 2, maxChars = 12000, includeExamples = false
        }))
        {
            var root = first.RootElement;
            var token = root.GetProperty("continuation").GetString();
            assert(root.GetProperty("logicalChars").GetInt32() == root.GetRawText().Length &&
                   root.GetProperty("logicalChars").GetInt32() <= 12000 && Results(first).Length == 2 &&
                   root.GetProperty("hasMore").GetBoolean() && !string.IsNullOrEmpty(token),
                "paged task response emits multiple results and supplies a continuation");
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
                var oldToken = MalformedContinuation("Page", assemblyId, 2, 12000, false, currentSnapshotId, includeSnapshotId: true);
                var restartError = await ErrorMessage(new
                {
                    query = "Page", assemblyId, maxResults = 2, maxChars = 12000, includeExamples = false,
                    scope = "loaded", continuation = oldToken, snapshotId = currentSnapshotId,
                });
                assert(restartError is not null &&
                       restartError.Contains("lookup.continuation_stale", StringComparison.Ordinal) &&
                       restartError.Contains("\"tool\":\"api_lookup\"", StringComparison.Ordinal),
                    "legacy lk2 continuation returns a structured scope-bound restart error");
            }
        }
        async Task<string[]> CollectPages(string query, int maxResults, int maxChars)
        {
            var symbolIds = new List<string>();
            string? continuation = null;
            string? snapshotId = null;
            var previousOffset = -1;
            for (var pageIndex = 0; pageIndex < 32; pageIndex++)
            {
                using var page = await apiToolJsonAsync("api_lookup", new
                {
                    query, assemblyId, maxResults, maxChars, includeExamples = false, continuation, snapshotId,
                });
                var root = page.RootElement;
                assert(root.GetProperty("logicalChars").GetInt32() == root.GetRawText().Length &&
                       root.GetProperty("logicalChars").GetInt32() <= maxChars,
                    "each task continuation response stays within its requested character budget");
                assert(root.GetProperty("offset").GetInt32() > previousOffset,
                    "task lookup continuation advances beyond the previous page offset");
                previousOffset = root.GetProperty("offset").GetInt32();
                symbolIds.AddRange(Results(page).Select(item => item.GetProperty("symbolId").GetString()!));
                if (!root.GetProperty("hasMore").GetBoolean())
                    return symbolIds.ToArray();
                var previousContinuation = continuation;
                continuation = root.GetProperty("continuation").GetString();
                snapshotId = root.GetProperty("snapshotId").GetString();
                assert(!string.IsNullOrEmpty(continuation) &&
                       !StringComparer.Ordinal.Equals(previousContinuation, continuation),
                    "task lookup supplies an advancing continuation whenever ranked results remain");
                if (string.IsNullOrEmpty(continuation))
                    break;
            }
            assert(false, "task lookup pagination terminates within the ranked candidate bound");
            return symbolIds.ToArray();
        }

        var referencePageIds = await CollectPages("Page", 20, 32000);
        var budgetPageIds = await CollectPages("Page", 3, 3000);
        var fallbackPageIds = await CollectPages("Page", 20, 3000);
        assert(referencePageIds.Length >= 5 && budgetPageIds.SequenceEqual(referencePageIds) &&
               fallbackPageIds.SequenceEqual(referencePageIds),
            "tight and fallback-pruned task lookup pages preserve every ranked candidate in order without skips or duplicates");
        var signatureReferencePageIds = await CollectPages("Signature", 20, 32000);
        var signaturePageIds7k = await CollectPages("Signature", 3, 7000);
        var signaturePageIds10k = await CollectPages("Signature", 3, 10000);
        assert(signatureReferencePageIds.Length >= 5 &&
               signaturePageIds7k.SequenceEqual(signatureReferencePageIds) &&
               signaturePageIds10k.SequenceEqual(signatureReferencePageIds) &&
               signaturePageIds7k.Distinct(StringComparer.Ordinal).Count() == signaturePageIds7k.Length &&
               signaturePageIds10k.Distinct(StringComparer.Ordinal).Count() == signaturePageIds10k.Length,
            "budgeted continuation pages preserve every declaration ID in stable order without skips or duplicates");

        using (var unfit = await apiToolJsonAsync("api_lookup", new
               {
                   query = "OverBudget", assemblyId, maxResults = 3, maxChars = 3000, includeExamples = false
               }))
        {
            var root = unfit.RootElement;
            var recoveryMessage = root.GetProperty("missingInformation").EnumerateArray()
                .Select(item => item.GetString())
                .FirstOrDefault(message => message?.Contains("api_lookup arguments ", StringComparison.Ordinal) == true);
            var recoveryStart = recoveryMessage?.IndexOf('{') ?? -1;
            var recoveryEnd = recoveryMessage?.LastIndexOf('}') ?? -1;
            assert(Results(unfit).Length == 0 && root.GetProperty("hasMore").GetBoolean() &&
                   !string.IsNullOrEmpty(root.GetProperty("continuation").GetString()) &&
                   recoveryMessage is not null && recoveryStart >= 0 && recoveryEnd > recoveryStart,
                "an individually unfit first candidate advances with explicit exact-ID recovery arguments");
            if (recoveryMessage is not null && recoveryStart >= 0 && recoveryEnd > recoveryStart)
            {
                using var recoveryArgs = JsonDocument.Parse(recoveryMessage[recoveryStart..(recoveryEnd + 1)]);
                var args = recoveryArgs.RootElement;
                var recoveredAssemblyId = args.GetProperty("assemblyId").GetString()!;
                var recoveredSymbolId = args.GetProperty("symbolId").GetString()!;
                var recoveredSnapshotId = args.GetProperty("snapshotId").GetString()!;
                assert(recoveredAssemblyId == assemblyId && args.GetProperty("mode").GetString() == "symbol",
                    "unfit recovery includes usable api_lookup assemblyId, symbolId, mode, and snapshotId");
                using var exact = await apiToolJsonAsync("api_lookup", new
                {
                    assemblyId = recoveredAssemblyId, symbolId = recoveredSymbolId, mode = "symbol", snapshotId = recoveredSnapshotId
                });
                assert(exact.RootElement.GetProperty("source").GetProperty("symbolId").GetString() == recoveredSymbolId,
                    "exact recovery arguments retrieve the omitted declaration");
                using var next = await apiToolJsonAsync("api_lookup", new
                {
                    query = "OverBudget", assemblyId, maxResults = 3, maxChars = 3000, includeExamples = false,
                    continuation = root.GetProperty("continuation").GetString(), snapshotId = root.GetProperty("snapshotId").GetString()
                });
                assert(next.RootElement.GetProperty("offset").GetInt32() == 1 &&
                       Results(next).Any(item => item.GetProperty("qualifiedName").GetString()!.EndsWith(".OverBudgetB", StringComparison.Ordinal)),
                    "continuation after exact recovery begins at the next ranked candidate");
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
