internal static class ApiLookupRanker
{
    internal const string Version = "lexical-v3";
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
        bool IsLowConfidence);
    private static readonly IComparer<RankedResult> WorstFirstComparer =
        Comparer<RankedResult>.Create(static (left, right) => CompareRankedResults(right, left));

    internal sealed class CandidateAccumulator
    {
        private readonly string _exactQuery;
        private readonly List<string> _queryTokens;
        private readonly int _maxCandidates;
        private readonly PriorityQueue<RankedResult, RankedResult> _candidates;

        internal CandidateAccumulator(string query, int maxCandidates)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (maxCandidates < 1)
                throw new ArgumentOutOfRangeException(nameof(maxCandidates));
            _exactQuery = query.Trim();
            _queryTokens = QueryTokens(query);
            _maxCandidates = maxCandidates;
            _candidates = new PriorityQueue<RankedResult, RankedResult>(WorstFirstComparer);
        }

        internal int TotalMatches { get; private set; }
        internal int ExactMatches { get; private set; }
        internal int OmittedCount => TotalMatches - _candidates.Count;

        internal void Add(InstalledApiCatalog.ApiHit hit)
        {
            var result = RankHit(_exactQuery, _queryTokens, hit);
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
    }

    private static RankedResult? RankHit(string exactQuery, IReadOnlyList<string> queryTokens, InstalledApiCatalog.ApiHit hit)
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
        var actionQuery = queryTokens.Any(token => ActionTokens.Contains(token));
        var matched = 0;
        var direct = false;
        var score = exactQualified ? 12000 : exactSimple ? 11000 : 0;

        foreach (var token in queryTokens)
        {
            var inSimple = Contains(simpleTokens, token);
            var inDeclaring = Contains(declaringTokens, token);
            var inSignature = Contains(signatureTokens, token);
            var synonymInSimple = !inSimple && SynonymMatches(token, simpleTokens);
            var synonymInDeclaring = !inDeclaring && SynonymMatches(token, declaringTokens);
            if (!inSimple && !inDeclaring && !inSignature && !synonymInSimple && !synonymInDeclaring)
                continue;
            matched++;
            if (inSimple || inDeclaring || inSignature)
                direct = true;
            if (!exactQualified && !exactSimple)
            {
                score += inSimple ? 600 : inSignature ? 180 : inDeclaring ? 120 :
                    synonymInSimple ? 180 : 40;
                if (actionQuery && inSimple && ActionTokens.Contains(token))
                    score += 400;
            }
        }

        var intentBonus = 0;
        if (!exactQualified && !exactSimple)
        {
            intentBonus = GameplayIntentBonus(queryTokens, hit.qualifiedName);
            score += KindPreference(hit.kind, actionQuery) + intentBonus;
        }
        var coverage = matched * 100 / queryTokens.Count;
        var exact = exactQualified || exactSimple;
        if (!exact && matched == 0)
            return null;
        var structuredQuery = exactQuery.Contains('.') && !exactQuery.Any(char.IsWhiteSpace) &&
            !exactQuery.Contains('<') && !exactQuery.Contains('>') &&
            !exactQuery.Contains('(') && !exactQuery.Contains(')');
        if (!exact && structuredQuery && (matched != queryTokens.Count || !direct))
            return null;
        return new RankedResult(hit, score, matched, queryTokens.Count, exact, coverage, direct,
            !exact && intentBonus == 0 && (!direct || coverage < MinimumCoveragePercent ||
                       (score < MinimumConfidenceScore && coverage < 100)));
    }

    private static int KindPreference(string kind, bool actionQuery)
    {
        if (!actionQuery)
            return 0;
        return kind switch
        {
            "method" => 350,
            "property" => -120,
            "event" => -80,
            "constructor" => -300,
            "field" => -250,
            "class" or "struct" or "interface" or "enum" or "delegate" or "type" => -200,
            _ => 0,
        };
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

    private static readonly HashSet<string> ActionTokens = new(StringComparer.Ordinal)
    {
        "spawn", "summon", "create", "instantiate", "add", "remove", "destroy", "despawn", "dispose",
        "give", "equip", "set", "get", "find", "load", "save", "start", "stop", "open", "close",
        "apply", "launch", "enable", "disable", "invoke", "call", "play", "kill", "damage", "heal",
        "attach", "detach", "move", "teleport", "follow", "track", "cleanup", "clean",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "to", "in", "on", "at", "for", "with", "and", "or", "by", "from",
        "is", "are", "be", "it", "this", "that", "my", "me", "can", "how", "do", "does",
    };

    private static List<string> QueryTokens(string query) =>
        Tokenize(query).Select(NormalizeInflection).Where(token => !StopWords.Contains(token))
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
        ["counter", "count", "total", "number", "metric", "statistics"]
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
        foreach (var group in SynonymGroups)
            if (group.Contains(token, StringComparer.Ordinal) &&
                candidates.Any(candidate => group.Contains(candidate, StringComparer.Ordinal)))
                return true;
        return false;
    }

    internal static RankedResult ContextResult(InstalledApiCatalog.ApiHit hit) =>
        new(hit, 0, 0, 0, false, 0, false, true);

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
            return RemoveRepeatedFinalConsonant(token[..^3]);
        if (token.Length > 5 && token.EndsWith("ed", StringComparison.Ordinal))
            return RemoveRepeatedFinalConsonant(token[..^2]);
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

    private static string RemoveRepeatedFinalConsonant(string token) =>
        token.Length > 2 && token[^1] == token[^2] && !"aeiou".Contains(token[^1])
            ? token[..^1]
            : token;


}

