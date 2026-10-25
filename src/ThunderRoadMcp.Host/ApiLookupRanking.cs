internal static class ApiLookupRanker
{
    internal const string Version = "evidence-v10-action-inflection-prefilter";
    internal const int MaxCandidates = 50_000;
    internal const int MinimumConfidenceScore = 500;
    internal const int MinimumCoveragePercent = 75;

    internal readonly record struct RankedResult(
        InstalledApiCatalog.ApiHit Hit,
        int Score,
        int MatchedTokenCount,
        int QueryTokenCount,
        bool IsExact,
        int CoveragePercent,
        bool HasDirectMatch,
        bool IsLowConfidence,
        bool HasDocumentationMatch,
        bool HasCatalogMatch);
    private static readonly IComparer<RankedResult> WorstFirstComparer =
        Comparer<RankedResult>.Create(static (left, right) => CompareRankedResults(right, left));

    internal sealed record ConceptAlias(string Term, string CanonicalConcept, string CatalogTypeName,
        string DataTypeSimpleName, string RuntimeTypeSimpleName);

    private static readonly ConceptAlias[] ConceptAliases =
    [
        new("weapon", "item", "ItemData", "ItemData", "Item"),
        new("sword", "item", "ItemData", "ItemData", "Item"),
        new("prop", "item", "ItemData", "ItemData", "Item"),
        new("npc", "creature", "CreatureData", "CreatureData", "Creature"),
    ];

    internal static IReadOnlyList<ConceptAlias> MatchedConceptAliases(string query)
    {
        var aliases = new List<ConceptAlias>();
        foreach (var token in QueryTokens(query))
        {
            var alias = FindConceptAlias(token);
            if (alias is not null && !aliases.Any(existing => existing.Term == alias.Term))
                aliases.Add(alias);
        }
        return aliases;
    }

    private static ConceptAlias? FindConceptAlias(string token) =>
        ConceptAliases.FirstOrDefault(alias => alias.Term.Equals(token, StringComparison.Ordinal));

    internal static bool IsActionToken(string token) =>
        ActionTokens.Contains(token) ||
        ActionTokens.Contains(NormalizeInflection(token.ToLowerInvariant()));

    private static bool IsApiTypeKind(string kind) =>
        kind is "class" or "struct" or "interface" or "enum" or "delegate" or "type";

    internal sealed class CandidateAccumulator
    {
        private readonly string _exactQuery;
        private readonly List<string> _queryTokens;
        private readonly bool _hasSetterActionSynonymQuery;
        private readonly int _maxCandidates;
        private readonly IReadOnlySet<string> _catalogApiTypes;
        private readonly string[] _potentialMatchTerms;
        private readonly PriorityQueue<RankedResult, RankedResult> _candidates;

        internal CandidateAccumulator(string query, int maxCandidates, IReadOnlySet<string>? catalogApiTypes = null)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (maxCandidates < 1)
                throw new ArgumentOutOfRangeException(nameof(maxCandidates));
            _exactQuery = query.Trim();
            _queryTokens = QueryTokens(query);
            _hasSetterActionSynonymQuery = _queryTokens.Any(SetterActionSynonyms.Contains);
            _maxCandidates = maxCandidates;
            _catalogApiTypes = catalogApiTypes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var potentialTerms = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in _queryTokens)
            {
                if (IsActionToken(token))
                    continue;
                potentialTerms.Add(token);
                if (FindConceptAlias(token) is { } concept)
                    potentialTerms.Add(concept.CanonicalConcept);
                foreach (var group in SynonymGroups)
                    if (group.Contains(token, StringComparer.Ordinal))
                        foreach (var synonym in group)
                            potentialTerms.Add(synonym);
            }
            foreach (var term in potentialTerms.ToArray())
                if (term.Length > 2 && term.EndsWith("y", StringComparison.Ordinal))
                    potentialTerms.Add(term[..^1] + "ies");
            _potentialMatchTerms = potentialTerms.ToArray();
            _candidates = new PriorityQueue<RankedResult, RankedResult>(WorstFirstComparer);
        }

        internal int TotalMatches { get; private set; }
        internal int ExactMatches { get; private set; }
        internal int OmittedCount => TotalMatches - _candidates.Count;

        internal void Add(InstalledApiCatalog.ApiHit hit)
        {
            if (!MightMatch(hit))
                return;
            var result = RankHit(_exactQuery, _queryTokens, hit, _catalogApiTypes);
            if (!result.HasValue)
                return;
            var candidate = result.Value;
            TotalMatches++;
            if (candidate.IsExact)
                ExactMatches++;
            if (_candidates.Count < _maxCandidates)
            {
                _candidates.Enqueue(candidate, candidate);
                return;
            }
            if (_candidates.TryPeek(out var worst, out _) && CompareRankedResults(candidate, worst) < 0)
            {
                _candidates.Dequeue();
                _candidates.Enqueue(candidate, candidate);
            }
        }

        internal RankedResult[] GetSortedResults()
        {
            if (_candidates.Count == 0)
                return Array.Empty<RankedResult>();
            var results = new RankedResult[_candidates.Count];
            var index = 0;
            foreach (var candidate in _candidates.UnorderedItems)
                results[index++] = candidate.Element;
            Array.Sort(results, CompareRankedResults);
            return results;
        }
        private bool MightMatch(InstalledApiCatalog.ApiHit hit)
        {
            if (hit.qualifiedName.Equals(_exactQuery, StringComparison.OrdinalIgnoreCase))
                return true;
            var simpleNameStart = hit.qualifiedName.LastIndexOf('.') + 1;
            if (hit.qualifiedName.AsSpan(simpleNameStart).Equals(_exactQuery, StringComparison.OrdinalIgnoreCase))
                return true;
            if (IsApiTypeKind(hit.kind) && _catalogApiTypes.Contains(hit.qualifiedName))
                return true;
            if (_queryTokens.Count == 1 && FindConceptAlias(_queryTokens[0]) is not null &&
                hit.kind == "method" && hit.qualifiedName.EndsWith(".SpawnAsync", StringComparison.Ordinal) &&
                hit.declaringType is not null && _catalogApiTypes.Contains(hit.declaringType))
                return true;
            foreach (var term in _potentialMatchTerms)
            {
                if (ContainsPotentialTerm(hit.qualifiedName, term) ||
                    ContainsPotentialTerm(hit.declaringType, term) ||
                    ContainsPotentialTerm(hit.signature, term))
                    return true;
                if (hit.xmlDocumentation is not { } documentation)
                    continue;
                if (ContainsPotentialTerm(documentation.Summary, term) ||
                    ContainsPotentialTerm(documentation.Remarks, term) ||
                    ContainsPotentialTerm(documentation.Returns, term) ||
                    ContainsPotentialTerm(documentation.Value, term))
                    return true;
                if (documentation.Parameters is not null)
                    foreach (var parameter in documentation.Parameters)
                        if (ContainsPotentialTerm(parameter.Name, term) ||
                            ContainsPotentialTerm(parameter.Description, term))
                            return true;
                if (documentation.Examples is not null)
                    foreach (var example in documentation.Examples)
                        if (ContainsPotentialTerm(example, term))
                            return true;
            }
            return _hasSetterActionSynonymQuery &&
                   hit.xmlDocumentation is { } setterDocumentation &&
                   DocumentationContainsSetterLanguage(setterDocumentation);
        }

        private static bool ContainsPotentialTerm(string? text, string term) =>
            text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static RankedResult? RankHit(string exactQuery, IReadOnlyList<string> queryTokens,
        InstalledApiCatalog.ApiHit hit, IReadOnlySet<string>? catalogApiTypes = null)
    {
        if (queryTokens.Count == 0)
            return null;

        var lastDot = hit.qualifiedName.LastIndexOf('.');
        var simpleName = lastDot < 0 ? hit.qualifiedName : hit.qualifiedName[(lastDot + 1)..];
        var declaringName = lastDot < 0 ? "" : hit.qualifiedName[..lastDot];
        var simpleTokens = Tokenize(simpleName);
        var declaringTokens = Tokenize(declaringName);
        var signatureTokens = Tokenize(hit.signature);
        var exactQualified = hit.qualifiedName.Equals(exactQuery, StringComparison.OrdinalIgnoreCase);
        var exactSimple = simpleName.Equals(exactQuery, StringComparison.OrdinalIgnoreCase);
        var actionQuery = queryTokens.Any(IsActionToken);
        var setterIntent = queryTokens.Any(IsSetterActionToken);
        var configurationBoolean = setterIntent &&
            queryTokens.Any(token => ValueTargetTokens.Contains(token)) &&
            (hit.kind is "property" or "field") &&
            (Contains(signatureTokens, "bool") || Contains(signatureTokens, "boolean")) &&
            simpleTokens.Any(ConfigurationFlagTokens.Contains);
        var writablePropertyAction = !configurationBoolean && hit.kind == "property" &&
            HasPublicSetter(hit.signature) && setterIntent;
        var namedAction = hit.kind == "method" && simpleTokens.Any(IsSetterActionToken);
        var documentedAction = !configurationBoolean && setterIntent &&
            queryTokens.Any(token => IsSetterActionToken(token) &&
                                     DocumentationMatchesToken(hit.xmlDocumentation, token));
        var actionableMutation = setterIntent &&
            (writablePropertyAction || namedAction || documentedAction);
        var matched = 0;
        var meaningfulMatched = 0;
        var meaningfulQueryTokens = queryTokens.Count(token => !IsActionToken(token));
        var direct = false;
        var documentationMatch = false;
        var catalogTypeName = IsApiTypeKind(hit.kind) ? hit.qualifiedName : null;
        var conceptSpawnMatch = queryTokens.Count == 1 && FindConceptAlias(queryTokens[0]) is not null &&
            hit.kind == "method" && hit.qualifiedName.EndsWith(".SpawnAsync", StringComparison.Ordinal) &&
            hit.declaringType is not null && catalogApiTypes?.Contains(hit.declaringType) == true;
        var catalogTypeMatch = catalogTypeName is not null && catalogApiTypes?.Contains(catalogTypeName) == true;
        var catalogMatch = catalogTypeMatch || conceptSpawnMatch;
        var conceptTypeAgreement = HasConceptTypeAgreement(queryTokens, hit);
        var score = exactQualified ? 12000 : exactSimple ? 11000 : 0;
        if (!exactQualified && !exactSimple)
            score += (conceptSpawnMatch ? 1900 : catalogTypeMatch ? 900 : 0) +
                     (conceptTypeAgreement ? 800 : 0);

        foreach (var token in queryTokens)
        {
            var configurationAction = configurationBoolean && IsSetterActionToken(token);
            var inSimple = !configurationAction && Contains(simpleTokens, token);
            var inDeclaring = !configurationAction && Contains(declaringTokens, token);
            var inSignature = !configurationAction && Contains(signatureTokens, token);
            var synonymInSimple = !configurationAction && !inSimple && SynonymMatches(token, simpleTokens);
            var synonymInDeclaring = !configurationAction && !inDeclaring &&
                SynonymMatches(token, declaringTokens);
            var inDocumentation = !configurationAction && DocumentationMatchesToken(hit.xmlDocumentation, token);
            var concept = FindConceptAlias(token);
            var inConcept = concept is not null &&
                (Contains(simpleTokens, concept.CanonicalConcept) ||
                 Contains(declaringTokens, concept.CanonicalConcept) ||
                 Contains(signatureTokens, concept.CanonicalConcept) ||
                 DocumentationContains(hit.xmlDocumentation, concept.CanonicalConcept));
            var setterAction = actionableMutation && IsSetterActionToken(token);
            if (!inSimple && !inDeclaring && !inSignature && !synonymInSimple && !synonymInDeclaring &&
                !inDocumentation && !inConcept && !setterAction)
                continue;
            matched++;
            if (!IsActionToken(token))
                meaningfulMatched++;
            if (inSimple || inDeclaring || inSignature || setterAction)
                direct = true;
            documentationMatch |= inDocumentation ||
                concept is not null && inConcept && DocumentationContains(hit.xmlDocumentation, concept.CanonicalConcept);
            if (!exactQualified && !exactSimple)
            {
                score += inSimple ? 600 : inSignature ? 180 : inDeclaring ? 120 :
                    synonymInSimple ? 180 : synonymInDeclaring ? 180 :
                    inConcept ? 240 : inDocumentation ? 140 : setterAction ? 400 : 0;
                if (actionQuery && inSimple && IsActionToken(token))
                    score += 400;
                if (actionQuery && IsActionToken(token) && inDocumentation)
                    score += 250;
            }
        }

        if (!exactQualified && !exactSimple)
        {
            if (writablePropertyAction)
                score += 350;
            if (configurationBoolean)
                score -= 600;
        }
        var intentBonus = 0;
        if (!exactQualified && !exactSimple)
        {
            intentBonus = GameplayIntentBonus(queryTokens, hit.qualifiedName);
            score += intentBonus;
        }
        var exact = exactQualified || exactSimple;
        if (!exact && matched == 0 && !catalogMatch)
            return null;
        if (!exact && meaningfulQueryTokens > 0 && meaningfulMatched == 0 && !catalogMatch)
            return null;
        if (!exact && meaningfulQueryTokens == 0 && !catalogMatch &&
            (queryTokens.Count != 1 || !simpleName.Equals(queryTokens[0], StringComparison.OrdinalIgnoreCase)))
            return null;
        var structuredQuery = exactQuery.Contains('.') && !exactQuery.Any(char.IsWhiteSpace) &&
            !exactQuery.Contains('<') && !exactQuery.Contains('>') &&
            !exactQuery.Contains('(') && !exactQuery.Contains(')');
        if (!exact && structuredQuery && (matched != queryTokens.Count || !direct))
            return null;
        var coverage = matched * 100 / queryTokens.Count;
        return new RankedResult(hit, score, matched, queryTokens.Count, exact, coverage, direct,
            !exact && !catalogMatch && intentBonus == 0 &&
            ((!direct && !documentationMatch) || coverage < MinimumCoveragePercent ||
             (score < MinimumConfidenceScore && coverage < 100)),
            documentationMatch, catalogMatch);
    }

    private static bool HasConceptTypeAgreement(IReadOnlyList<string> queryTokens, InstalledApiCatalog.ApiHit hit)
    {
        var ownerType = hit.kind is "class" or "struct" or "interface" or "enum" or "delegate" or "type"
            ? hit.qualifiedName
            : hit.declaringType;
        foreach (var token in queryTokens)
        {
            var concept = FindConceptAlias(token);
            if (concept is not null &&
                (TypeNameMatches(ownerType, concept.RuntimeTypeSimpleName) ||
                 TypeNameMatches(ownerType, concept.DataTypeSimpleName)))
                return true;
        }
        return false;
    }

    private static bool TypeNameMatches(string? qualifiedName, string simpleTypeName)
    {
        if (string.IsNullOrEmpty(qualifiedName))
            return false;
        var separator = Math.Max(qualifiedName.LastIndexOf('.'), qualifiedName.LastIndexOf('+'));
        var simpleName = qualifiedName.AsSpan(separator + 1);
        var genericStart = simpleName.IndexOf('`');
        if (genericStart >= 0)
            simpleName = simpleName[..genericStart];
        return simpleName.Equals(simpleTypeName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSetterActionToken(string token) => SetterActionTokens.Contains(token);

    private static bool HasPublicSetter(string signature)
    {
        var openingBrace = signature.IndexOf('{');
        var closingBrace = signature.LastIndexOf('}');
        if (openingBrace < 0 || closingBrace <= openingBrace)
            return false;
        var accessors = signature.AsSpan(openingBrace + 1, closingBrace - openingBrace - 1);
        var setter = accessors.IndexOf("set;", StringComparison.Ordinal);
        if (setter < 0)
            return false;
        var previousAccessor = accessors[..setter].LastIndexOf(';');
        var modifier = accessors[(previousAccessor + 1)..setter].Trim();
        return modifier.IsEmpty || modifier.Equals("public", StringComparison.Ordinal);
    }
    private static bool DocumentationContains(InstalledApiCatalog.ApiXmlDocumentation? documentation, string token)
    {
        if (documentation is null)
            return false;
        bool ContainsText(string? text) => text?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;
        return ContainsText(documentation.Summary) || ContainsText(documentation.Remarks) ||
            ContainsText(documentation.Returns) || ContainsText(documentation.Value) ||
            (documentation.Parameters?.Any(parameter =>
                ContainsText(parameter.Name) || ContainsText(parameter.Description)) ?? false) ||
            (documentation.Examples?.Any(ContainsText) ?? false);
    }
    private static bool DocumentationMatchesToken(InstalledApiCatalog.ApiXmlDocumentation? documentation, string token)
    {
        if (DocumentationContains(documentation, token))
            return true;
        if (SetterActionSynonyms.Contains(token) && DocumentationContainsSetterLanguage(documentation))
            return true;
        return SynonymGroups.Any(group => group.Contains(token, StringComparer.Ordinal) &&
            group.Any(candidate => !candidate.Equals(token, StringComparison.Ordinal) &&
                                   DocumentationContains(documentation, candidate)));
    }
    private static bool DocumentationContainsSetterLanguage(
        InstalledApiCatalog.ApiXmlDocumentation? documentation)
    {
        if (documentation is null)
            return false;
        if (ContainsSetterLanguage(documentation.Summary) ||
            ContainsSetterLanguage(documentation.Remarks) ||
            ContainsSetterLanguage(documentation.Returns) ||
            ContainsSetterLanguage(documentation.Value))
            return true;
        if (documentation.Parameters is not null)
            foreach (var parameter in documentation.Parameters)
                if (ContainsSetterLanguage(parameter.Name) || ContainsSetterLanguage(parameter.Description))
                    return true;
        if (documentation.Examples is not null)
            foreach (var example in documentation.Examples)
                if (ContainsSetterLanguage(example))
                    return true;
        return false;
    }

    private static bool ContainsSetterLanguage(string? text)
    {
        if (text is null)
            return false;
        for (var index = 0; index < text.Length;)
        {
            while (index < text.Length && !char.IsLetterOrDigit(text[index]))
                index++;
            var start = index;
            while (index < text.Length && char.IsLetterOrDigit(text[index]))
                index++;
            if (index <= start)
                continue;
            var word = text.AsSpan(start, index - start);
            foreach (var setterWord in SetterDocumentationWords)
                if (word.Equals(setterWord, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }


    private static int GameplayIntentBonus(IReadOnlyList<string> queryTokens, string qualifiedName)
    {
        var bonus = 0;
        foreach (var intent in GameplayIntents)
        {
            if (!intent.Terms.All(term => queryTokens.Contains(term, StringComparer.Ordinal)) ||
                !qualifiedName.EndsWith(intent.QualifiedSuffix, StringComparison.Ordinal))
                continue;
            bonus = Math.Max(bonus, intent.Bonus);
        }
        return bonus;
    }

    private static readonly (string[] Terms, string QualifiedSuffix, int Bonus)[] GameplayIntents =
    [
        (["spawn", "item", "player"], "ThunderRoad.ItemData.SpawnAsync", 2000),
        (["spawn", "item"], "ThunderRoad.Catalog.GetData", 900),
        (["player", "head"], "ThunderRoad.Player.head", 700),
        (["player", "local"], "ThunderRoad.Player.local", 700),
    ];

    private static readonly HashSet<string> ActionTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "spawn", "summon", "create", "instantiate", "add", "remove", "destroy", "despawn", "dispose",
        "give", "equip", "set", "get", "find", "load", "save", "start", "stop", "open", "close",
        "apply", "launch", "enable", "disable", "invoke", "call", "play", "kill", "damage", "heal",
        "attach", "detach", "move", "teleport", "follow", "track", "cleanup", "clean",
        "change", "adjust", "modify", "update",
    };
    private static readonly HashSet<string> SetterActionTokens = new(StringComparer.Ordinal)
    {
        "set", "change", "adjust", "modify", "update",
    };
    private static readonly HashSet<string> ValueTargetTokens = new(StringComparer.Ordinal)
    {
        "height", "width", "length", "position", "speed", "direction", "orientation", "rotation",
        "size", "distance", "velocity", "acceleration",
    };
    private static readonly HashSet<string> SetterActionSynonyms = new(StringComparer.Ordinal)
    {
        "set", "change", "adjust", "modify", "update", "assign", "write",
    };
    private static readonly string[] SetterDocumentationWords =
    [
        "set", "setting", "sets", "setter", "setters",
        "assign", "assigns", "assigned", "assigning",
        "write", "writes", "written", "writable",
    ];
    private static readonly HashSet<string> ConfigurationFlagTokens = new(StringComparer.Ordinal)
    {
        "use", "is", "can", "should", "allow", "enable", "enabled", "disable", "disabled",
        "active", "config", "configuration", "setting", "settings",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "to", "in", "on", "at", "for", "with", "and", "or", "by", "from",
        "is", "are", "be", "it", "this", "that", "my", "me", "can", "how", "do", "does",
    };

    private static List<string> QueryTokens(string query) =>
        Tokenize(query.Replace("NPCs", "NPC", StringComparison.OrdinalIgnoreCase))
            .Select(NormalizeInflection).Where(token => !StopWords.Contains(token))
            .Distinct(StringComparer.Ordinal).ToList();


    private static readonly string[][] SynonymGroups =
    [
        ["spawn", "summon", "create", "instantiate", "spawned"],
        ["near", "nearby", "around", "close", "proximity"],
        ["player", "user", "avatar", "localplayer"],
        ["creature", "npc", "character", "enemy", "actor"],
        ["follow", "following", "chase", "track", "escort"],
        ["collision", "collide", "hit", "impact", "contact"],
        ["damage", "hurt", "wound", "health", "injury"],
        ["event", "callback", "handler", "listener", "subscribe"],
        ["cleanup", "clean", "remove", "destroy", "despawn", "dispose"],
        ["runtime", "live", "current", "active", "session"],
        ["counter", "count", "total", "number", "metric", "statistics"],
        ["behavior", "behaviour"],
        ["reference", "ref"],
        ["direction", "dir", "orientation"],
        ["water", "ocean"],
    ];
    private static bool Contains(IReadOnlyList<string> tokens, string token)
    {
        foreach (var candidate in tokens)
        {
            if (string.Equals(candidate, token, StringComparison.Ordinal))
                return true;
            if (token.Length >= 4 && candidate.Length > token.Length &&
                candidate.StartsWith(token, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool SynonymMatches(string token, IReadOnlyList<string> candidates)
    {
        if (SetterActionSynonyms.Contains(token) && candidates.Any(SetterActionSynonyms.Contains))
            return true;
        foreach (var group in SynonymGroups)
            if (group.Contains(token, StringComparer.Ordinal) &&
                candidates.Any(candidate => group.Contains(candidate, StringComparer.Ordinal)))
                return true;
        return false;
    }


    internal static IReadOnlyList<RankedResult> Rank(
        string query, IReadOnlyList<InstalledApiCatalog.ApiHit> hits, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(hits);
        if (maxResults <= 0 || hits.Count == 0)
            return Array.Empty<RankedResult>();

        var queryTokens = QueryTokens(query);
        if (queryTokens.Count == 0)
            return Array.Empty<RankedResult>();

        var results = new List<RankedResult>(Math.Min(hits.Count, maxResults));
        var exactQuery = query.Trim();
        foreach (var hit in hits)
            if (RankHit(exactQuery, queryTokens, hit) is { } candidate)
                results.Add(candidate);

        results.Sort(CompareRankedResults);
        if (results.Count > maxResults)
            results.RemoveRange(maxResults, results.Count - maxResults);
        return results;
    }

    private static int CompareRankedResults(RankedResult left, RankedResult right)
    {
        var order = right.IsExact.CompareTo(left.IsExact);
        if (order != 0) return order;
        order = left.IsLowConfidence.CompareTo(right.IsLowConfidence);
        if (order != 0) return order;
        order = right.CoveragePercent.CompareTo(left.CoveragePercent);
        if (order != 0) return order;
        order = right.Score.CompareTo(left.Score);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(left.Hit.origin, right.Hit.origin);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(left.Hit.relativePath, right.Hit.relativePath);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(left.Hit.qualifiedName, right.Hit.qualifiedName);
        return order != 0 ? order : StringComparer.Ordinal.Compare(left.Hit.symbolId, right.Hit.symbolId);
    }

    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (!char.IsLetterOrDigit(current))
            {
                AddToken(text, start, i, tokens);
                start = -1;
                continue;
            }

            if (start < 0)
                start = i;
            else if (char.IsUpper(current) && (char.IsLower(text[i - 1]) || char.IsDigit(text[i - 1]) ||
                     (i + 1 < text.Length && char.IsLower(text[i + 1]) && char.IsUpper(text[i - 1]))))
            {
                AddToken(text, start, i, tokens);
                start = i;
            }
        }
        AddToken(text, start, text.Length, tokens);
        return tokens;
    }

    private static void AddToken(string text, int start, int end, List<string> tokens)
    {
        if (start >= 0 && end > start)
            tokens.Add(NormalizeInflection(text[start..end].ToLowerInvariant()));
    }

    private static string NormalizeInflection(string token)
    {
        if (token.Length > 6 && token.EndsWith("ing", StringComparison.Ordinal))
            return NormalizeRecognizedActionStem(RemoveRepeatedFinalConsonant(token[..^3]));
        if (token.Length > 5 && token.EndsWith("ed", StringComparison.Ordinal))
            return NormalizeRecognizedActionStem(RemoveRepeatedFinalConsonant(token[..^2]));
        if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal))
            return token[..^3] + "y";
        if (token.Length > 5 && token.EndsWith("es", StringComparison.Ordinal) &&
            (token[..^2].EndsWith("s", StringComparison.Ordinal) || token[..^2].EndsWith("x", StringComparison.Ordinal) ||
             token[..^2].EndsWith("z", StringComparison.Ordinal) || token[..^2].EndsWith("ch", StringComparison.Ordinal) ||
             token[..^2].EndsWith("sh", StringComparison.Ordinal)))
            return token[..^2];
        if (token.Length > 3 && token.EndsWith("s", StringComparison.Ordinal) &&
            !token.EndsWith("ss", StringComparison.Ordinal) && !token.EndsWith("us", StringComparison.Ordinal) &&
            !token.EndsWith("is", StringComparison.Ordinal))
            return token[..^1];
        return token;
    }
    private static string NormalizeRecognizedActionStem(string token) => token switch
    {
        "chang" => "change",
        "updat" => "update",
        _ => token,
    };

    private static string RemoveRepeatedFinalConsonant(string token) =>
        token.Length > 2 && token[^1] == token[^2] && !"aeiou".Contains(token[^1])
            ? token[..^1]
            : token;


}

