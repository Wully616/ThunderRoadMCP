using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

public sealed partial class InstalledApiCatalog
{
    private const int MaxLookupRelatedNames = 24;
    private const int MaxLookupRelatedTypes = 8;
    private const int MaxLookupRelatedDepth = 2;
    private const int MaxLookupInheritedExpansions = 3;

    private const int MaxLookupTotalRelatedNames = MaxLookupRelatedNames + MaxLookupRelatedDepth * MaxLookupRelatedTypes;
    private async Task<string> TaskLookupAsync(CatalogSnapshot snapshot, string query, string? assemblyId, string? kindFilter,
        int maxResults, int maxChars, bool includeExamples, string? continuation, BaseCatalogDataCatalog? baseCatalog,
        CancellationToken cancellationToken)
    {
        var (offset, continuationError) = ReadLookupContinuation(continuation, snapshot.Id, query, assemblyId,
            kindFilter, maxResults, maxChars, includeExamples);
        if (continuationError is not null)
            throw new InvalidOperationException(continuationError);

        var candidates = new ApiLookupRanker.CandidateAccumulator(query, ApiLookupRanker.MaxCandidates);
        var declarationsConsidered = 0;
        var exampleNames = includeExamples
            ? GameplayExamples.All.SelectMany(example => example.Symbols).Select(NormalizeTypeName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exampleCandidates = new Dictionary<string, List<ApiHit>>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<string>(snapshot.Diagnostics);
        var indexComplete = !snapshot.CatalogTruncated;
        var typeMap = await ReadApiTypeMapAsync(snapshot);
        ApiHit[]? directoryHits = null;
        var directoryComplete = false;
        if (assemblyId is null)
            (directoryHits, directoryComplete) = await ReadApiDirectoryAsync(snapshot);
        else
            SelectAssembly(snapshot, assemblyId);
        var collectedTypeMap = typeMap is null && assemblyId is null
            ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            : null;
        var collectedDirectory = directoryHits is null && assemblyId is null ? new List<ApiHit>() : null;
        var typeMapComplete = collectedTypeMap is not null && !snapshot.CatalogTruncated;

        void Consider(ApiHit hit)
        {
            if (hit.accessibility != "public")
                return;
            var normalizedName = NormalizeTypeName(hit.qualifiedName);
            if (exampleNames.Contains(normalizedName))
            {
                if (!exampleCandidates.TryGetValue(normalizedName, out var exampleMatches))
                    exampleCandidates.Add(normalizedName, exampleMatches = new List<ApiHit>(2));
                if (exampleMatches.Count < 2)
                    exampleMatches.Add(hit);
            }
            if (MatchesLookupKind(hit.kind, kindFilter))
            {
                declarationsConsidered++;
                candidates.Add(hit);
            }
        }

        if (directoryHits is not null)
        {
            indexComplete &= directoryComplete;
            if (!directoryComplete)
                AddDiagnostic(diagnostics, "The persisted public symbol directory is incomplete; some declarations may be missing.");
            foreach (var hit in directoryHits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (assemblyId is not null && !hit.assemblyId.Equals(assemblyId, StringComparison.Ordinal))
                    continue;
                Consider(hit);
            }
            if (typeMap is null && collectedTypeMap is not null)
            {
                foreach (var hit in directoryHits)
                {
                    if (!IsTypeKind(hit.kind))
                        continue;
                    var typeName = NormalizeTypeName(hit.qualifiedName);
                    if (typeName.Length == 0)
                        continue;
                    if (!collectedTypeMap.TryGetValue(typeName, out var typeAssemblyIds))
                        collectedTypeMap.Add(typeName, typeAssemblyIds = new HashSet<string>(StringComparer.Ordinal));
                    typeAssemblyIds.Add(hit.assemblyId);
                }
                if (typeMapComplete && directoryComplete)
                {
                    WriteApiTypeMap(snapshot, collectedTypeMap);
                    typeMap = await ReadApiTypeMapAsync(snapshot);
                }
            }
        }
        else
        {
            foreach (var entry in SelectAssemblies(snapshot, assemblyId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var searchTiming = ToolTiming.Measure("apiSearchMs");
                    using var indexLease = await GetApiIndexAsync(snapshot, entry, cancellationToken);
                    if (!indexLease.Index.Complete)
                    {
                        indexComplete = false;
                        AddDiagnostic(diagnostics, IndexOmissionMessage(entry, indexLease.Index));
                    }
                    if (collectedTypeMap is not null)
                    {
                        if (indexLease.Index.LimitReached)
                            typeMapComplete = false;
                        foreach (var type in indexLease.Index.Types)
                        {
                            var typeName = NormalizeTypeName(type.Symbol.Hit.qualifiedName);
                            if (typeName.Length == 0)
                                continue;
                            if (!collectedTypeMap.TryGetValue(typeName, out var typeAssemblyIds))
                                collectedTypeMap.Add(typeName, typeAssemblyIds = new HashSet<string>(StringComparer.Ordinal));
                            typeAssemblyIds.Add(entry.Id);
                        }
                    }
                    foreach (var symbol in indexLease.Index.Symbols)
                    {
                        var hit = symbol.Hit;
                        if (hit.accessibility != "public" && !IsTypeKind(hit.kind))
                            continue;
                        Consider(hit);
                        collectedDirectory?.Add(hit with { attributes = null, warnings = Array.Empty<string>() });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (AssemblyChangedException) { throw; }
                catch (Exception exception) when (IsAssemblyReadFailure(exception))
                {
                    indexComplete = false;
                    typeMapComplete = false;
                    AddDiagnostic(diagnostics, $"Could not index {entry.Origin}/{entry.RelativePath}: {exception.Message}");
                }
            }
            if (collectedTypeMap is not null && typeMapComplete)
            {
                WriteApiTypeMap(snapshot, collectedTypeMap);
                typeMap = await ReadApiTypeMapAsync(snapshot);
            }
            if (collectedDirectory is not null && typeMapComplete)
                WriteApiDirectory(snapshot, collectedDirectory, indexComplete);
        }
        ToolTiming.Count("api.lookup.resultsConsidered", declarationsConsidered);
        ApiLookupRanker.RankedResult[] ranked;
        using (ToolTiming.Measure("apiLookupRankingMs"))
            ranked = candidates.GetSortedResults();
        var omittedCandidates = candidates.OmittedCount;
        if (omittedCandidates > 0)
        {
            indexComplete = false;
            AddDiagnostic(diagnostics, $"Retained the top {ApiLookupRanker.MaxCandidates} ranked declarations; {omittedCandidates} lower-ranked match(es) were omitted.");
        }
        if (offset > ranked.Length)
            throw new InvalidOperationException("continuation offset is no longer available; restart api_lookup without continuation.");

        var page = ranked.Skip(offset).Take(maxResults).ToArray();
        var selectedHits = page.Select(result => result.Hit).ToArray();
        var selectedSymbols = await ReadSelectedSymbolsAsync(snapshot, selectedHits, cancellationToken);
        var catalogSnapshotId = (string?)null;
        IReadOnlyList<CatalogTaskRecord> catalogRecords = Array.Empty<CatalogTaskRecord>();
        IReadOnlyList<string> catalogOmittedTerms = Array.Empty<string>();
        var catalogComplete = false;
        if (baseCatalog is not null)
        {
            try
            {
                var preferredCatalogTypes = selectedHits
                    .Select(hit => NormalizeTypeName(hit.declaringType ?? hit.qualifiedName))
                    .Where(name => name.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var catalogResult = await baseCatalog.SearchTaskCatalogAsync(query, Math.Min(5, maxResults),
                    preferredCatalogTypes, cancellationToken);
                catalogSnapshotId = catalogResult.SnapshotId;
                catalogRecords = catalogResult.Records;
                catalogOmittedTerms = catalogResult.OmittedTerms;
                catalogComplete = catalogResult.IsComplete;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                AddDiagnostic(diagnostics, "Base catalog lookup unavailable: " + exception.Message);
            }
        }

        var relatedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relationOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in selectedSymbols.Values)
        {
            AddRelatedName(symbol.Hit.declaringType, symbol.Hit.symbolId, relatedNames, relationOwners);
            foreach (var name in symbol.RelatedTypeNames)
                AddRelatedName(name, symbol.Hit.symbolId, relatedNames, relationOwners);
        }
        foreach (var record in catalogRecords)
            AddRelatedName(record.DeclaredApiType, "catalog:" + record.EntryId, relatedNames, relationOwners);
        var (relatedTypes, relatedResolutionComplete) = await ResolveRelatedTypesAsync(snapshot, relatedNames, relationOwners,
            typeMap, diagnostics, cancellationToken);
        ToolTiming.Count("api.lookup.relatedConsidered", relatedNames.Count);
        var resolvedRelatedNames = relatedTypes.Select(type => type.Symbol.Hit.qualifiedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingRelatedNames = relatedNames.Where(name => !resolvedRelatedNames.Contains(name) && !IsFrameworkTypeName(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        indexComplete &= relatedResolutionComplete;
        var relevantSymbols = selectedHits.Select(hit => hit.qualifiedName).ToArray();
        var examples = includeExamples ? GameplayExamples.ForQuery(query, relevantSymbols).Take(1).ToArray() : Array.Empty<ApiExample>();

        var response = new JsonObject
        {
            ["mode"] = "task",
            ["query"] = query,
            ["snapshotId"] = snapshot.Id,
            ["rankingVersion"] = ApiLookupRanker.Version,
            ["found"] = candidates.TotalMatches > 0,
            ["ambiguous"] = candidates.ExactMatches > 1,
            ["total"] = candidates.TotalMatches,
            ["offset"] = offset,
            ["maxResults"] = maxResults,
            ["omittedCandidates"] = omittedCandidates,
            ["logicalChars"] = 0,
            ["hasMore"] = false,
            ["continuation"] = null,
            ["results"] = new JsonArray(),
            ["relatedTypes"] = new JsonArray(),
            ["catalog"] = new JsonObject
            {
                ["snapshotId"] = catalogSnapshotId,
                ["records"] = new JsonArray(),
                ["complete"] = catalogComplete,
            },
            ["examples"] = new JsonArray(),
            ["executionContract"] = new JsonObject
            {
                ["body"] = "evaluate_csharp accepts a C# statement body; use return to return a value.",
                ["imports"] = "System, System.Linq, System.Collections.Generic, UnityEngine, ThunderRoad, ThunderRoadMcp.Bridge",
                ["threading"] = "Execution is synchronous on Unity's main thread; use a managed coroutine for work spanning frames.",
                ["async"] = "Do not use top-level await or return Task/Task<T>.",
                ["safety"] = "This lookup is read-only metadata; it does not execute target assemblies or prove live behavior.",
            },
            ["warnings"] = new JsonArray(),
            ["missingInformation"] = new JsonArray(),
            ["complete"] = indexComplete,
            ["truncated"] = omittedCandidates > 0,
            ["confidence"] = ranked.Length == 0 ? "none" : page.Length > 0 && page[0].IsLowConfidence ? "low" : "medium",
        };
        if (LogicalLength(response) > maxChars)
            throw new ArgumentException("query and response envelope exceed maxChars; shorten query or raise maxChars.", nameof(maxChars));
        var responseWarnings = (JsonArray)response["warnings"]!;
        var warningsOmitted = diagnostics.Count > 4;
        foreach (var warning in diagnostics.Take(4))
        {
            AddJsonString(responseWarnings, warning);
            if (LogicalLength(response) > maxChars)
            {
                responseWarnings.RemoveAt(responseWarnings.Count - 1);
                warningsOmitted = true;
                break;
            }
        }
        var results = (JsonArray)response["results"]!;
        var missing = (JsonArray)response["missingInformation"]!;
        if (omittedCandidates > 0)
            AddJsonString(missing, $"Only the top {ApiLookupRanker.MaxCandidates} ranked declarations were retained; {omittedCandidates} lower-ranked matches were omitted. Narrow by assemblyId or kind.");
        var resultNodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var truncated = warningsOmitted || omittedCandidates > 0;
        var complete = indexComplete && !warningsOmitted && omittedCandidates == 0;
        var processedPageCount = 0;
        foreach (var rankedResult in page)
        {
            processedPageCount++;
            if (!selectedSymbols.TryGetValue(rankedResult.Hit.symbolId, out var symbol))
                continue;
            var node = JsonSerializer.SerializeToNode(symbol.Hit)!.AsObject();
            node["relevance"] = new JsonObject
            {
                ["score"] = rankedResult.Score,
                ["matchedTokens"] = rankedResult.MatchedTokenCount,
                ["queryTokens"] = rankedResult.QueryTokenCount,
                ["coveragePercent"] = rankedResult.CoveragePercent,
                ["directMatch"] = rankedResult.HasDirectMatch,
                ["confidence"] = rankedResult.IsLowConfidence ? "low" : "supported",
                ["exactName"] = rankedResult.IsExact,
            };
            node["typeContext"] = new JsonObject
            {
                ["returnType"] = symbol.ReturnType,
                ["attributes"] = JsonSerializer.SerializeToNode(symbol.Hit.attributes),
                ["attributesTruncated"] = symbol.Hit.attributesTruncated,
                ["parameters"] = ToJsonArray(symbol.ParameterSignatures),
                ["genericConstraints"] = ToJsonArray(symbol.GenericConstraints),
                ["relatedTypeNames"] = ToJsonArray(symbol.RelatedTypeNames.Take(MaxLookupRelatedNames)),
                ["relatedTypeIds"] = new JsonArray(),
                ["unresolvedTypeNames"] = new JsonArray(),
                ["relevantMembers"] = new JsonArray(),
                ["contextTruncated"] = symbol.RelatedTypeNames.Length > MaxLookupRelatedNames,
            };
            results.Add(node);
            if (LogicalLength(response) > maxChars)
            {
                results.RemoveAt(results.Count - 1);
                truncated = true;
                complete = false;
                AddJsonString(missing, "A complete result signature did not fit maxChars; request fewer results or use api_search for the exact declaration.");
                continue;
            }
            resultNodes.Add(rankedResult.Hit.symbolId, node);
            if (rankedResult.IsLowConfidence)
            {
                complete = false;
                AddJsonString(missing, $"'{rankedResult.Hit.qualifiedName}' is a low-confidence lexical match; confirm its declaration with api_type/api_search before treating it as an implementation plan.");
            }
            if (symbol.RelatedTypeNames.Length > MaxLookupRelatedNames)
            {
                truncated = true;
                complete = false;
            }
        }

        static void PruneLower(JsonObject node)
        {
            node.Remove("attributes");
            node.Remove("attributesTruncated");
            node.Remove("typeContext");
        }

        static void PruneTopOptional(JsonObject node)
        {
            node.Remove("attributes");
            node.Remove("attributesTruncated");
            if (node["typeContext"] is JsonObject context)
            {
                context.Remove("attributes");
                context.Remove("attributesTruncated");
                context.Remove("relatedTypeNames");
                context.Remove("relatedTypeIds");
                context.Remove("unresolvedTypeNames");
            }
        }

        if (results.Count > 1)
        {
            for (var index = 1; index < results.Count; index++)
                if (results[index] is JsonObject lowerResult)
                    PruneLower(lowerResult);
            truncated = true;
            complete = false;
            AddJsonString(missing, "Lower-ranked results retain assembly/symbol IDs, signatures, and relevance but omit optional type context/attributes so essential related types fit the budget; use api_type/api_search for those details.");
        }
        if (results.Count > 0 && results[0] is JsonObject topResult && LogicalLength(response) > maxChars - 3200)
        {
            PruneTopOptional(topResult);
            truncated = true;
            complete = false;
            AddJsonString(missing, "The top result's optional attributes/related-name lists were omitted to preserve its callable signature, inherited-member context, and essential related types; use api_type for the full type surface.");
        }

        void AppendExamples()
        {
            var examplesArray = (JsonArray)response["examples"]!;
            foreach (var example in examples)
            {
                var matchedSymbols = new List<ApiHit>();
                var resolved = example.Symbols.Count > 0;
                foreach (var symbolName in example.Symbols)
                {
                    var normalizedName = NormalizeTypeName(symbolName);
                    if (!exampleCandidates.TryGetValue(normalizedName, out var exampleMatches) || exampleMatches.Count != 1)
                    {
                        resolved = false;
                        break;
                    }
                    matchedSymbols.Add(exampleMatches[0]);
                }
                var evidence = example.Evidence.Equals("unverified", StringComparison.OrdinalIgnoreCase) && resolved
                    ? "symbols-resolved"
                    : example.Evidence;
                var exampleNode = JsonSerializer.SerializeToNode(new
                {
                    id = example.Id,
                    title = example.Title,
                    language = example.Language,
                    code = example.Code,
                    provenance = example.Provenance,
                    symbols = example.Symbols,
                    prerequisites = example.Prerequisites,
                    cleanup = example.Cleanup,
                    evidence,
                    evidenceIdentity = new
                    {
                        declared = example.EvidenceIdentity,
                        apiSnapshotId = snapshot.Id,
                        assemblyIds = matchedSymbols.Select(match => match.assemblyId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                        symbolIds = matchedSymbols.Select(match => match.symbolId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                    },
                })!;
                examplesArray.Add(exampleNode);
                if (LogicalLength(response) > maxChars)
                {
                    examplesArray.RemoveAt(examplesArray.Count - 1);
                    truncated = true;
                    complete = false;
                }
                if (!resolved)
                {
                    complete = false;
                    AddJsonString(missing, $"Example '{example.Id}' includes symbols not uniquely resolved by this snapshot and remains {evidence}.");
                }
            }
        }

        AppendExamples();
        var inheritedExpansions = 0;
        responseWarnings = (JsonArray)response["warnings"]!;
        foreach (var symbol in selectedSymbols.Values.Where(symbol => symbol.Hit.kind is "class" or "struct" or "interface"))
        {
            if (!resultNodes.TryGetValue(symbol.Hit.symbolId, out var resultNode))
                continue;
            if (inheritedExpansions > 0 && LogicalLength(response) > maxChars - 2500)
            {
                truncated = true;
                complete = false;
                AddJsonString(missing, "Inherited member expansion was omitted to preserve essential related-type context within maxChars; call api_type(includeInherited=true) for it.");
                break;
            }
            if (inheritedExpansions++ >= MaxLookupInheritedExpansions)
            {
                truncated = true;
                complete = false;
                AddJsonString(missing, "Inherited member context is bounded to three selected types; inspect additional types with api_type(includeInherited=true).");
                break;
            }
            if (resultNode["typeContext"] is not JsonObject typeContext)
                continue;
            if (typeContext["relevantMembers"] is not JsonArray memberArray)
                continue;
            var entry = SelectAssembly(snapshot, symbol.Hit.assemblyId);
            using var indexLease = await GetApiIndexAsync(snapshot, entry, cancellationToken);
            if (!indexLease.Index.TypesById.TryGetValue(symbol.Hit.symbolId, out var indexedType))
                continue;
            try
            {
                (ApiHit[] Members, string[] Warnings, bool Truncated) expansion;
                using (ToolTiming.Measure("apiTypeExpansionMs"))
                    expansion = await ExpandTypeForLookupAsync(snapshot, entry, indexedType, cancellationToken);
                List<ApiLookupRanker.RankedResult> rankedMembers;
                using (ToolTiming.Measure("apiLookupRankingMs"))
                    rankedMembers = ApiLookupRanker.Rank(query, expansion.Members, 5).ToList();
                if (!rankedMembers.Any(member => member.Hit.isInherited) &&
                    expansion.Members.FirstOrDefault(member => member.isInherited) is { } inheritedHit)
                    rankedMembers.Insert(0, ApiLookupRanker.ContextResult(inheritedHit));
                foreach (var member in rankedMembers)
                {
                    var memberNode = JsonSerializer.SerializeToNode(member.Hit)!.AsObject();
                    memberNode["name"] = member.Hit.qualifiedName[
                        (member.Hit.qualifiedName.LastIndexOf('.') + 1)..];
                    memberNode["relevance"] = new JsonObject
                    {
                        ["score"] = member.Score,
                        ["coveragePercent"] = member.CoveragePercent,
                        ["confidence"] = member.IsLowConfidence ? "low" : "supported",
                    };
                    memberArray.Add(memberNode);
                    if (LogicalLength(response) > maxChars)
                    {
                        memberArray.RemoveAt(memberArray.Count - 1);
                        truncated = true;
                        complete = false;
                        break;
                    }
                }
                if (expansion.Truncated)
                {
                    complete = false;
                    typeContext["contextTruncated"] = true;
                }
                if (expansion.Warnings.Length > 0)
                {
                    complete = false;
                    foreach (var warning in expansion.Warnings.Take(2))
                        AddJsonString(responseWarnings, warning);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (AssemblyChangedException) { throw; }
            catch (Exception exception) when (IsAssemblyReadFailure(exception))
            {
                complete = false;
                AddJsonString(responseWarnings, $"Could not expand inherited context for {symbol.Hit.qualifiedName}: {exception.Message}");
            }
        }
        var relatedArray = (JsonArray)response["relatedTypes"]!;
        var relatedCount = 0;
        var relatedOmitted = new List<string>();
        var ownerPriority = selectedHits.Select((hit, index) => (hit.symbolId, index))
            .ToDictionary(item => item.symbolId, item => item.index, StringComparer.Ordinal);
        relatedTypes = relatedTypes.OrderBy(related =>
        {
            var name = NormalizeTypeName(related.Symbol.Hit.qualifiedName);
            if (!relationOwners.TryGetValue(name, out var owners) || owners.Count == 0)
                return int.MaxValue;
            var best = int.MaxValue;
            foreach (var owner in owners)
                if (ownerPriority.TryGetValue(owner, out var priority) && priority < best)
                    best = priority;
            return best;
        }).ToArray();
        foreach (var related in relatedTypes)
        {
            if (relatedCount == MaxLookupRelatedTypes)
            {
                relatedOmitted.Add(related.Symbol.Hit.qualifiedName);
                truncated = true;
                complete = false;
                continue;
            }
            var hit = related.Symbol.Hit;
            var ownerName = NormalizeTypeName(hit.qualifiedName);
            var ownerIds = relationOwners.TryGetValue(ownerName, out var foundOwners)
                ? foundOwners
                : new HashSet<string>(StringComparer.Ordinal);
            var relatedNode = new JsonObject
            {
                ["assemblyId"] = hit.assemblyId,
                ["assemblyName"] = hit.assemblyName,
                ["symbolId"] = hit.symbolId,
                ["qualifiedName"] = related.FullName.Replace('+', '.'),
                ["kind"] = hit.kind,
                ["declaration"] = hit.signature,
                ["relatedTo"] = ToJsonArray(ownerIds.OrderBy(id => id, StringComparer.Ordinal)),
            };
            relatedArray.Add(relatedNode);
            var linkedResults = new List<JsonArray>();
            foreach (var ownerId in ownerIds)
            {
                if (ownerId.StartsWith("catalog:", StringComparison.Ordinal) ||
                    !resultNodes.TryGetValue(ownerId, out var resultNode) ||
                    resultNode["typeContext"] is not JsonObject context ||
                    context["relatedTypeIds"] is not JsonArray idArray)
                    continue;
                idArray.Add(hit.symbolId);
                linkedResults.Add(idArray);
            }
            if (LogicalLength(response) > maxChars)
            {
                relatedNode.Remove("declaration");
                if (LogicalLength(response) > maxChars)
                {
                    relatedOmitted.Add(hit.qualifiedName);
                    relatedArray.RemoveAt(relatedArray.Count - 1);
                    foreach (var idArray in linkedResults)
                        idArray.RemoveAt(idArray.Count - 1);
                    truncated = true;
                    complete = false;
                    continue;
                }
                truncated = true;
                complete = false;
            }
            relatedNode["attributes"] = JsonSerializer.SerializeToNode(hit.attributes);
            relatedNode["attributesTruncated"] = hit.attributesTruncated;
            relatedNode["declaredBaseTypes"] = ToJsonArray(related.DirectBaseTypeNames.Take(12));
            relatedNode["declaredBaseTypesTruncated"] = related.DirectBaseTypeNames.Length > 12;
            relatedNode["genericConstraints"] = ToJsonArray(related.GenericConstraints);
            relatedNode["enumValues"] = ToJsonArray(related.EnumValues.Take(12));
            relatedNode["enumValuesTruncated"] = related.EnumValues.Length > 12;
            relatedNode["provenance"] = new JsonObject
            {
                ["origin"] = hit.origin,
                ["relativePath"] = hit.relativePath,
                ["assemblyId"] = hit.assemblyId,
                ["symbolId"] = hit.symbolId,
            };
            if (LogicalLength(response) > maxChars)
            {
                foreach (var optional in new[] { "attributes", "attributesTruncated", "declaredBaseTypes",
                             "declaredBaseTypesTruncated", "genericConstraints", "enumValues", "enumValuesTruncated", "provenance" })
                    relatedNode.Remove(optional);
                relatedNode["contextTruncated"] = true;
                truncated = true;
                complete = false;
            }
            relatedCount++;
        }
        if (relatedOmitted.Count > 0)
            AddJsonString(missing, "Related type records omitted for the character budget or related-type limit: " +
                string.Join(", ", relatedOmitted.Take(8)) + (relatedOmitted.Count > 8 ? ", …" : "") +
                ". Use api_type/api_decompile for the omitted types.");
        foreach (var symbol in selectedSymbols.Values)
        {
            if (!resultNodes.TryGetValue(symbol.Hit.symbolId, out var resultNode))
                continue;
            if (resultNode["typeContext"] is not JsonObject context ||
                context["unresolvedTypeNames"] is not JsonArray unresolved)
                continue;
            foreach (var name in symbol.RelatedTypeNames.Take(MaxLookupRelatedNames))
            {
                if (resolvedRelatedNames.Contains(name) || IsFrameworkTypeName(name))
                    continue;
                unresolved.Add(name);
                if (LogicalLength(response) > maxChars)
                {
                    unresolved.RemoveAt(unresolved.Count - 1);
                    truncated = true;
                    complete = false;
                    break;
                }
            }
        }
        if (missingRelatedNames.Length > 0)
        {
            complete = false;
            AddJsonString(missing, "Some referenced types are unresolved in the installed snapshot: " + string.Join(", ", missingRelatedNames.Take(8)) + (missingRelatedNames.Length > 8 ? ", …" : ""));
        }

        var catalogNode = (JsonObject)response["catalog"]!;
        var catalogResultArray = (JsonArray)catalogNode["records"]!;
        bool TrimLowerResults(long limit)
        {
            var trimmedAny = false;
            while (LogicalLength(response) > limit && results.Count > 1)
            {
                var removed = results[^1]!.AsObject();
                results.RemoveAt(results.Count - 1);
                if (removed["symbolId"]?.GetValue<string>() is { } removedId)
                    resultNodes.Remove(removedId);
                trimmedAny = true;
            }
            if (trimmedAny)
            {
                truncated = true;
                complete = false;
                AddJsonString(missing,
                    "Lower-ranked result headers were omitted to preserve catalog IDs and related context; use the continuation for the remaining candidates.");
            }
            return trimmedAny;
        }
        foreach (var record in catalogRecords)
        {
            var typeMatches = relatedTypes.Where(type => record.DeclaredApiType is not null &&
                    NormalizeTypeName(type.Symbol.Hit.qualifiedName).Equals(NormalizeTypeName(record.DeclaredApiType), StringComparison.OrdinalIgnoreCase))
                .Select(type => new
                {
                    assemblyId = type.Symbol.Hit.assemblyId,
                    symbolId = type.Symbol.Hit.symbolId,
                    qualifiedName = type.Symbol.Hit.qualifiedName,
                    origin = type.Symbol.Hit.origin,
                    relativePath = type.Symbol.Hit.relativePath,
                    evidence = "direct-$type",
                }).ToArray();
            var recordNode = JsonSerializer.SerializeToNode(new
            {
                entryId = record.EntryId,
                id = record.Id,
                recordType = record.RecordType,
                archivePath = record.ArchivePath,
                name = record.Name,
                matchedTerms = record.MatchedTerms,
                declaredApiType = record.DeclaredApiType,
                apiTypeMatches = typeMatches,
                evidence = record.DeclaredApiType is null ? "no-direct-type-evidence" : "direct-$type",
            })!;
            catalogResultArray.Add(recordNode);
            if (LogicalLength(response) > maxChars)
                TrimLowerResults(maxChars);
            if (LogicalLength(response) > maxChars)
            {
                catalogResultArray.RemoveAt(catalogResultArray.Count - 1);
                catalogNode["complete"] = false;
                truncated = true;
                complete = false;
            }
        }
        if (!catalogComplete)
        {
            if (catalogOmittedTerms.Count > 0)
                missing.Add("Base catalog omitted search terms: " + string.Join(", ", catalogOmittedTerms));
            complete = false;
            AddJsonString(missing, baseCatalog is null
                ? "Base catalog lookup was not available; catalog links are not verified."
                : "Base catalog search was incomplete; rerun catalog_search with a narrower query for complete record coverage.");
        }
        foreach (var record in catalogRecords.Where(record => record.DeclaredApiType is not null &&
                     !relatedTypes.Any(type => NormalizeTypeName(type.Symbol.Hit.qualifiedName).Equals(NormalizeTypeName(record.DeclaredApiType), StringComparison.OrdinalIgnoreCase))))
        {
            complete = false;
            AddJsonString(missing, $"Catalog `$type` '{record.DeclaredApiType}' has no exact installed API type match; inspect catalog and installed APIs separately.");
        }
        var hasMore = offset + processedPageCount < ranked.Length;
        response["hasMore"] = hasMore;
        if (hasMore)
            response["continuation"] = MakeLookupContinuation(snapshot.Id, query, assemblyId, kindFilter,
                maxResults, maxChars, includeExamples, offset + processedPageCount);
        response["truncated"] = truncated;
        response["complete"] = complete && !truncated;
        response["confidence"] = results.Count == 0 ? "none" :
            results[0]!["relevance"]!["confidence"]!.GetValue<string>() == "low" ? "low" : "supported";
        if (ranked.Length == 0)
            AddJsonString(missing, "No installed public API declaration matched this query; narrow it or try a distinctive type/member name with api_search.");
        if (truncated)
        {
            ToolTiming.Count("api.lookup.truncated");
            AddJsonString(missing, "Some results or context were omitted; follow the continuation. omittedCandidates reports lower-ranked matches excluded from the ranking set.");
        }
        ToolTiming.Count("api.lookup.resultsReturned", results.Count);
        if (LogicalLength(response) > maxChars)
        {
            response["warnings"] = new JsonArray();
            var fallbackMissing = new JsonArray();
            if (catalogOmittedTerms.Count > 0)
                fallbackMissing.Add("Base catalog omitted search terms: " + string.Join(", ", catalogOmittedTerms));
            if (omittedCandidates > 0)
                fallbackMissing.Add($"Retained the top {ApiLookupRanker.MaxCandidates} ranked declarations; {omittedCandidates} lower-ranked matches were omitted.");
            if (fallbackMissing.Count == 0)
                fallbackMissing.Add("Optional context was omitted to stay within maxChars; use the stable continuation or raise the budget.");
            response["missingInformation"] = fallbackMissing;
            var fallbackRelatedArray = (JsonArray)response["relatedTypes"]!;
            while (LogicalLength(response) > maxChars && fallbackRelatedArray.Count > 1)
                fallbackRelatedArray.RemoveAt(fallbackRelatedArray.Count - 1);
            var remainingRelatedIds = fallbackRelatedArray.OfType<JsonObject>()
                .Select(node => node["symbolId"]?.GetValue<string>()).OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resultNode in resultNodes.Values)
            {
                if (resultNode["typeContext"] is not JsonObject context ||
                    context["relatedTypeIds"] is not JsonArray idArray)
                    continue;
                for (var idIndex = idArray.Count - 1; idIndex >= 0; idIndex--)
                    if (idArray[idIndex]?.GetValue<string>() is { } id && !remainingRelatedIds.Contains(id))
                        idArray.RemoveAt(idIndex);
            }
            var exampleArray = (JsonArray)response["examples"]!;
            while (LogicalLength(response) > maxChars && exampleArray.Count > 1)
                exampleArray.RemoveAt(exampleArray.Count - 1);
            var removedResults = false;
            while (LogicalLength(response) > maxChars && results.Count > 1)
            {
                results.RemoveAt(results.Count - 1);
                removedResults = true;
            }
            if (LogicalLength(response) > maxChars && exampleArray.Count > 0)
            {
                exampleArray.Clear();
                removedResults = true;
            }
            while (LogicalLength(response) > maxChars && catalogResultArray.Count > 1)
                catalogResultArray.RemoveAt(catalogResultArray.Count - 1);
            if (LogicalLength(response) > maxChars)
            {
                fallbackRelatedArray.Clear();
                results.Clear();
                removedResults = true;
            }
            response["truncated"] = true;
            response["complete"] = false;
            var nextOffset = removedResults ? offset + results.Count : offset + processedPageCount;
            response["hasMore"] = removedResults || nextOffset < ranked.Length;
            response["continuation"] = response["hasMore"]!.GetValue<bool>()
                ? MakeLookupContinuation(snapshot.Id, query, assemblyId, kindFilter, maxResults, maxChars, includeExamples, nextOffset)
                : null;
        }
        if (LogicalLength(response) > maxChars)
            throw new ArgumentException("response envelope exceeds maxChars after optional context was omitted.", nameof(maxChars));
        return JsonSerializer.Serialize(response);
    }

    private async Task<Dictionary<string, ApiIndexedSymbol>> ReadSelectedSymbolsAsync(CatalogSnapshot snapshot,
        IReadOnlyList<ApiHit> hits, CancellationToken cancellationToken)
    {
        var selected = new Dictionary<string, ApiIndexedSymbol>(StringComparer.Ordinal);
        foreach (var group in hits.GroupBy(hit => hit.assemblyId, StringComparer.Ordinal))
        {
            var entry = SelectAssembly(snapshot, group.Key);
            using var indexLease = await GetApiIndexAsync(snapshot, entry, cancellationToken);
            foreach (var hit in group)
                if (indexLease.Index.SymbolsById.TryGetValue(hit.symbolId, out var symbol))
                    selected.Add(hit.symbolId, symbol);
        }
        return selected;
    }

    private async Task<(ApiTypeRecord[] Types, bool Complete)> ResolveRelatedTypesAsync(CatalogSnapshot snapshot,
        HashSet<string> names, Dictionary<string, HashSet<string>> relationOwners,
        IReadOnlyDictionary<string, string[]>? typeMap, List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0)
            return (Array.Empty<ApiTypeRecord>(), true);
        var wanted = names.Select(NormalizeTypeName).Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, ApiTypeRecord>(StringComparer.Ordinal);
        var foundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        if (typeMap is not null)
        {
            for (var depth = 0; depth < MaxLookupRelatedDepth; depth++)
            {
                var passNames = wanted.Where(name => !foundNames.Contains(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (passNames.Count == 0)
                    break;
                foreach (var typeName in passNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!typeMap.TryGetValue(typeName, out var assemblyIds))
                        continue;
                    foreach (var assemblyId in assemblyIds)
                    {
                        var entry = snapshot.Assemblies.FirstOrDefault(candidate =>
                            candidate.Id.Equals(assemblyId, StringComparison.Ordinal));
                        if (entry is null)
                        {
                            complete = false;
                            continue;
                        }
                        try
                        {
                            using var indexLease = await GetApiIndexAsync(snapshot, entry, cancellationToken);
                            var type = indexLease.Index.Types.FirstOrDefault(candidate =>
                                NormalizeTypeName(candidate.Symbol.Hit.qualifiedName).Equals(typeName, StringComparison.OrdinalIgnoreCase));
                            if (type is null || !found.TryAdd(type.Symbol.Hit.symbolId,
                                    type with { Members = Array.Empty<ApiIndexedSymbol>() }))
                                continue;
                            foundNames.Add(typeName);
                            if (!relationOwners.ContainsKey(typeName))
                                relationOwners.Add(typeName, new HashSet<string>(StringComparer.Ordinal));
                            if (depth + 1 >= MaxLookupRelatedDepth)
                                continue;
                            foreach (var baseTypeName in type.DirectBaseTypeNames)
                                AddRelatedName(baseTypeName, type.Symbol.Hit.symbolId, wanted, relationOwners);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (AssemblyChangedException) { throw; }
                        catch (Exception exception) when (IsAssemblyReadFailure(exception))
                        {
                            complete = false;
                            AddDiagnostic(diagnostics, $"Could not resolve related types from {entry.Origin}/{entry.RelativePath}: {exception.Message}");
                        }
                    }
                }
            }
            if (foundNames.Count > 0)
                return (OrderRelatedTypes(found), complete);
        }
        for (var depth = 0; depth < MaxLookupRelatedDepth; depth++)
        {
            var passNames = wanted.Where(name => !foundNames.Contains(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (passNames.Count == 0)
                break;
            foreach (var entry in snapshot.Assemblies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var indexLease = await GetApiIndexAsync(snapshot, entry, cancellationToken);
                    foreach (var type in indexLease.Index.Types)
                    {
                        var typeName = NormalizeTypeName(type.Symbol.Hit.qualifiedName);
                        if (!passNames.Contains(typeName) ||
                            !found.TryAdd(type.Symbol.Hit.symbolId, type with { Members = Array.Empty<ApiIndexedSymbol>() }))
                            continue;
                        foundNames.Add(typeName);
                        if (!relationOwners.ContainsKey(typeName))
                            relationOwners.Add(typeName, new HashSet<string>(StringComparer.Ordinal));
                        if (depth + 1 >= MaxLookupRelatedDepth)
                            continue;
                        foreach (var baseTypeName in type.DirectBaseTypeNames)
                            AddRelatedName(baseTypeName, type.Symbol.Hit.symbolId, wanted, relationOwners);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (AssemblyChangedException) { throw; }
                catch (Exception exception) when (IsAssemblyReadFailure(exception))
                {
                    complete = false;
                    AddDiagnostic(diagnostics, $"Could not resolve related types from {entry.Origin}/{entry.RelativePath}: {exception.Message}");
                }
            }
        }
        return (OrderRelatedTypes(found), complete);
    }

    private static ApiTypeRecord[] OrderRelatedTypes(Dictionary<string, ApiTypeRecord> found) =>
        found.Values.OrderBy(type => type.Symbol.Hit.assemblyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(type => type.Symbol.Hit.origin, StringComparer.Ordinal)
            .ThenBy(type => type.Symbol.Hit.relativePath, StringComparer.Ordinal)
            .ThenBy(type => type.Symbol.Hit.qualifiedName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(type => type.Symbol.Hit.symbolId, StringComparer.Ordinal).ToArray();

    private async Task<(ApiHit[] Members, string[] Warnings, bool Truncated)> ExpandTypeForLookupAsync(CatalogSnapshot snapshot,
        AssemblyEntry entry, ApiTypeRecord indexedType, CancellationToken cancellationToken)
    {
        using var main = await LoadPeAsync(entry, cancellationToken);
        using var resolver = new RootedAssemblyResolver(snapshot, entry, main);
        var typeSystem = new DecompilerTypeSystem(main.File, resolver, CreateDecompilerSettings());
        var type = FindType(typeSystem.MainModule, indexedType.Symbol.MetadataToken)
            ?? throw new InvalidOperationException("typeSymbolId is invalid, stale, or does not identify a type in assemblyId.");
        var warnings = new List<string>();
        var members = new List<ApiHit>();
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        var memberEnumerationTruncated = false;
        var hierarchy = new List<IType> { type };
        var pending = new Stack<IType>();
        AddBaseTypes(type);
        while (pending.Count > 0 && hierarchy.Count < 8)
        {
            var current = pending.Pop();
            if (hierarchy.Any(existing => ReferenceEquals(existing, current)))
                continue;
            hierarchy.Add(current);
            AddBaseTypes(current);
        }

        void AddBaseTypes(IType current)
        {
            foreach (var baseType in current.DirectBaseTypes)
            {
                if (baseType.GetDefinition()?.Kind == TypeKind.Interface ||
                    IsFrameworkTypeName(FormatTypeName(baseType.FullName)))
                    continue;
                pending.Push(baseType);
            }
        }

        foreach (var current in hierarchy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<IMember> currentMembers = current.GetMembers(null, GetMemberOptions.None).Cast<IMember>();
            if (ReferenceEquals(current, type))
                currentMembers = currentMembers.Concat(type.GetConstructors(null, GetMemberOptions.None));
            var currentDefinition = current.GetDefinition();
            foreach (var member in currentMembers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (member.MetadataToken.IsNil)
                    continue;
                if (currentDefinition is not null && member.MemberDefinition.DeclaringTypeDefinition is { } declaredBy &&
                    !ReferenceEquals(declaredBy, currentDefinition))
                    continue;
                if (!ReferenceEquals(current, type) &&
                    (member is IMethod { IsConstructor: true } ||
                     member.MemberDefinition.Name is ".ctor" or ".cctor" ||
                     (member.Name is ".ctor" or ".cctor")))
                    continue;
                if (member.EffectiveAccessibility() != Accessibility.Public)
                    continue;
                if (member.MemberDefinition.DeclaringTypeDefinition is { } declaring &&
                    IsFrameworkTypeName(FormatTypeName(declaring.FullTypeName.FullName)))
                    continue;
                var hit = CreateMemberHit(snapshot, entry, type, member, warnings);
                if (hit is not null && seenSymbols.Add(hit.symbolId))
                    members.Add(hit);
                if (members.Count >= 1000)
                {
                    memberEnumerationTruncated = true;
                    break;
                }
            }
            if (memberEnumerationTruncated)
                break;
        }
        if (!members.Any(member => member.isInherited))
        {
            using var indexLease = await GetApiIndexAsync(snapshot, entry, cancellationToken);
            if (FindIndexedInheritedHit(indexLease.Index, indexedType) is { } inheritedHit)
                members.Add(inheritedHit);
        }
        AddWarnings(warnings, resolver.Warnings);
        return (members.OrderBy(member => member.qualifiedName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.symbolId, StringComparer.Ordinal).ToArray(), warnings.ToArray(), memberEnumerationTruncated);
    }

    private static ApiHit? FindIndexedInheritedHit(ApiAssemblyIndex index, ApiTypeRecord type)
    {
        var baseNames = type.DirectBaseTypeNames.Select(NormalizeTypeName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in index.Types)
        {
            if (!baseNames.Contains(NormalizeTypeName(candidate.Symbol.Hit.qualifiedName)))
                continue;
            foreach (var member in candidate.Members)
            {
                if (member.Hit.accessibility == "public" && member.Hit.kind != "constructor" &&
                    !member.Hit.qualifiedName.EndsWith(".ctor", StringComparison.Ordinal) &&
                    !member.Hit.qualifiedName.EndsWith(".cctor", StringComparison.Ordinal))
                    return member.Hit with { isInherited = true };
            }
        }
        return null;
    }

    private static bool MatchesLookupKind(string hitKind, string? kindFilter) => kindFilter is null || kindFilter switch
    {
        "type" => hitKind is "class" or "struct" or "interface" or "enum" or "delegate" or "type",
        "member" => hitKind is not "class" and not "struct" and not "interface" and not "enum" and not "delegate" and not "type",
        _ => hitKind == kindFilter,
    };

    private static void AddRelatedName(string? name, string ownerId, HashSet<string> names,
        Dictionary<string, HashSet<string>> owners)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        var normalized = NormalizeTypeName(name);
        if (normalized.Length == 0 || normalized.Length > 256 || IsFrameworkTypeName(normalized))
            return;
        if (!names.Contains(normalized) && names.Count >= MaxLookupTotalRelatedNames)
            return;
        names.Add(normalized);
        if (!owners.TryGetValue(normalized, out var ids))
            owners.Add(normalized, ids = new HashSet<string>(StringComparer.Ordinal));
        ids.Add(ownerId);
    }

    private static bool IsFrameworkTypeName(string name) => name.StartsWith("System.", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.", StringComparison.Ordinal) || name is "void" or "bool" or "byte" or "sbyte" or "char" or "decimal" or "double" or "float" or "int" or "uint" or "long" or "ulong" or "short" or "ushort" or "string" or "object";

    private static string NormalizeTypeName(string name)
    {
        var trimmed = name.Trim();
        var genericStart = trimmed.IndexOf('<');
        if (genericStart >= 0)
            trimmed = trimmed[..genericStart];
        var arityStart = trimmed.IndexOf('`');
        if (arityStart > 0)
            trimmed = trimmed[..arityStart];
        if (trimmed.EndsWith("[]", StringComparison.Ordinal))
            trimmed = trimmed[..^2];
        return trimmed.Replace('+', '.');
    }

    private static int LogicalLength(JsonObject response)
    {
        response["logicalChars"] = 0;
        var baseLength = JsonSerializer.Serialize(response).Length;
        var length = baseLength;
        while (true)
        {
            var digits = 1;
            for (var value = length; value >= 10; value /= 10)
                digits++;
            var measuredLength = baseLength + digits - 1;
            if (measuredLength == length)
                break;
            length = measuredLength;
        }
        response["logicalChars"] = length;
        return length;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    private static void AddJsonString(JsonArray array, string value)
    {
        if (array.Count >= 4)
            return;
        array.Add(value.Length <= 300 ? value : value[..300] + "…");
    }

    private static (int Offset, string? Error) ReadLookupContinuation(string? token, string snapshotId, string query,
        string? assemblyId, string? kind, int maxResults, int maxChars, bool includeExamples)
    {
        if (token is null)
            return (0, null);
        try
        {
            var parts = token.Split('.', 3);
            if (parts.Length != 3 || parts[0] != "lk2")
                return (0, "continuation is invalid; restart api_lookup without continuation.");
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(encoded);
            var check = Convert.ToHexString(SHA256.HashData(bytes))[..16];
            if (!check.Equals(parts[2], StringComparison.OrdinalIgnoreCase))
                return (0, "continuation is invalid; restart api_lookup without continuation.");
            var payload = JsonSerializer.Deserialize<LookupContinuationPayload>(bytes);
            if (payload is null)
                return (0, "continuation is invalid; restart api_lookup without continuation.");
            if (payload.Version != 2 || payload.RankingVersion != ApiLookupRanker.Version)
                return (0, "continuation version is no longer supported; restart api_lookup without continuation.");
            if (string.IsNullOrWhiteSpace(payload.SnapshotId) || string.IsNullOrWhiteSpace(payload.QueryFingerprint) || payload.Offset < 0)
                return (0, "continuation is invalid; restart api_lookup without continuation.");
            if (!string.Equals(payload.SnapshotId, snapshotId, StringComparison.Ordinal))
                return (0, "continuation snapshot is stale; restart api_lookup without continuation and use current IDs.");
            if (!string.Equals(payload.QueryFingerprint, QueryFingerprint(query), StringComparison.Ordinal) ||
                payload.AssemblyId != assemblyId || payload.Kind != kind || payload.MaxResults != maxResults ||
                payload.MaxChars != maxChars || payload.IncludeExamples != includeExamples)
                return (0, "continuation is bound to the original query and options; repeat them exactly or restart api_lookup without continuation.");
            return (payload.Offset, null);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return (0, "continuation is invalid; restart api_lookup without continuation.");
        }
    }

    private static string MakeLookupContinuation(string snapshotId, string query, string? assemblyId, string? kind,
        int maxResults, int maxChars, bool includeExamples, int offset)
    {
        var payload = new LookupContinuationPayload(2, snapshotId, QueryFingerprint(query), assemblyId, kind, maxResults, maxChars,
            includeExamples, ApiLookupRanker.Version, offset);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var encoded = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "lk2." + encoded + "." + Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }

    private static string QueryFingerprint(string query) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query)));

    private sealed record LookupContinuationPayload(int Version, string SnapshotId, string QueryFingerprint, string? AssemblyId,
        string? Kind, int MaxResults, int MaxChars, bool IncludeExamples, string RankingVersion, int Offset);
    private async Task ThrowMissingMemberDiagnosticAsync(CatalogSnapshot snapshot, string query, string? assemblyId,
        CancellationToken cancellationToken)
    {
        var lastDot = query.LastIndexOf('.');
        if (lastDot < 1 || lastDot == query.Length - 1)
            return;

        for (var dot = lastDot; dot > 0; dot = query.LastIndexOf('.', dot - 1))
        {
            var typeName = query[..dot];
            var memberName = query[(dot + 1)..];
            var types = new List<(AssemblyEntry Entry, ApiTypeRecord Type)>();
            foreach (var entry in SelectAssemblies(snapshot, assemblyId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var lease = await GetApiIndexAsync(snapshot, entry, cancellationToken);
                if (lease.Index.TypesByName.TryGetValue(typeName, out var matches))
                    types.AddRange(matches.Select(type => (entry, type)));
            }
            if (types.Count == 0)
                continue;

            if (types.Count > 1)
                throw new ApiDiscoveryException("type.ambiguous",
                    $"The type prefix '{typeName}' resolves to multiple installed types; select one assembly before retrying.",
                    new
                    {
                        candidates = types.Take(8).Select(candidate => ErrorCandidate(candidate.Type.Symbol.Hit)).ToArray(),
                        candidateLimit = 8,
                        hasMore = types.Count > 8,
                        nextSearch = new { tool = "api_search", arguments = new { query = typeName, kind = "type", offset = 0, limit = 20, snapshotId = snapshot.Id } },
                    });

            var (resolvedEntry, resolvedType) = types[0];
            var candidates = resolvedType.Members
                .Select(member => member.Hit)
                .OrderBy(hit => hit.qualifiedName.EndsWith("." + memberName, StringComparison.OrdinalIgnoreCase) ? 0 :
                    hit.qualifiedName[(hit.qualifiedName.LastIndexOf('.') + 1)..].Contains(memberName, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(hit => hit.qualifiedName, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(ErrorCandidate)
                .ToArray();
            throw new ApiDiscoveryException("member.not_found",
                $"Type '{ClipErrorText(resolvedType.FullName, 256)}' resolves in the installed snapshot, but member '{memberName}' was not found.",
                new
                {
                    diagnostic = "Type.MissingMember",
                    notFound = true,
                    requestedMember = memberName,
                    resolvedType = new
                    {
                        assemblyId = resolvedEntry.Id,
                        symbolId = resolvedType.Symbol.Hit.symbolId,
                        qualifiedName = ClipErrorText(resolvedType.FullName, 128),
                        qualifiedNameTruncated = resolvedType.FullName.Length > 128,
                        origin = resolvedEntry.Origin,
                        relativePath = ClipErrorText(resolvedEntry.RelativePath, 128),
                        relativePathTruncated = resolvedEntry.RelativePath.Length > 128,
                        signature = ClipErrorText(resolvedType.Symbol.Hit.signature, 256),
                        signatureTruncated = resolvedType.Symbol.Hit.signature.Length > 256,
                    },
                    candidates,
                    candidateLimit = 8,
                    nextTool = new { tool = "api_type", arguments = new { assemblyId = resolvedEntry.Id, typeSymbolId = resolvedType.Symbol.Hit.symbolId, snapshotId = snapshot.Id, limit = 100 } },
                });
        }
    }
}
