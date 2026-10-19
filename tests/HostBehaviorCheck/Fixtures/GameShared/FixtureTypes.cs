namespace Fixture.Shared;

public class SharedSurface
{
    public string Revision() => "game-v1";
}

public class GameSurface
{
    public int GameOnly() => 1;
}

public class ExactLookupSurface
{
    public int Resolve() => 1;
    public int ResolveHelper() => 2;
    private int PrivateIndexFallbackMarker() => 7;
}

public class PagedSurface
{
    public int Page01() => 1;
    public int Page02() => 2;
    public int Page03() => 3;
    public int Page04() => 4;
    public int Page05() => 5;
    public int Page06() => 6;
    public int Page07() => 7;
    public int Page08() => 8;
    public int Page09() => 9;
    public int Page10() => 10;
    public int Page11() => 11;
    public int Page12() => 12;
    public int Page13() => 13;
    public int Page14() => 14;
    public int Page15() => 15;
    public int Page16() => 16;
    public int Page17() => 17;
    public int Page18() => 18;
    public int Page19() => 19;
    public int Page20() => 20;
    public int Page21() => 21;
}

public sealed class StaticInitializerProbe
{
    static StaticInitializerProbe()
    {
        var path = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_FIXTURE_SENTINEL");
        if (path is not null)
            File.WriteAllText(path, "executed");
    }

    public static string Value => "not invoked";
}
