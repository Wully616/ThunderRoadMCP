internal static class DiscoveryImprovementsCheck
{
    public static void RunRanking()
    {
        // Intentionally shuffled; the ranking must restore tier > type-before-member > game-before-mod order.
        var expected = new[]
        {
            Hit("mod", "AAMod.Lightning", "class"),
            Hit("game", "ThunderRoad.LightningStriker", "class"),
            Hit("mod", "AAMod.LightningSpell", "class"),
            Hit("game", "ThunderRoad.Weather.SetLightning", "method"),
        };
        var sorted = new[] { expected[3], expected[2], expected[1], expected[0] }
            .Order(Comparer<InstalledApiCatalog.ApiHit>.Create((a, b) => InstalledApiCatalog.CompareSearchHits("Lightning", a, b)))
            .Select(hit => hit.qualifiedName)
            .ToArray();
        Assert(sorted.SequenceEqual(expected.Select(hit => hit.qualifiedName)),
            "api_search ranking order was " + string.Join(", ", sorted));

        var game = Hit("game", "ThunderRoad.Lightning", "class");
        var mod = Hit("mod", "AAMod.Lightning", "class");
        Assert(InstalledApiCatalog.CompareSearchHits("Lightning", game, mod) < 0, "game precedes mod within a rank");
    }

    private static InstalledApiCatalog.ApiHit Hit(string origin, string qualifiedName, string kind) =>
        new("asm-" + origin, origin + "-assembly", origin, origin + ".dll", "sym-" + qualifiedName, kind,
            qualifiedName, null, qualifiedName, "public", false, Array.Empty<string>());

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Discovery improvements check failed: " + message);
    }
}
