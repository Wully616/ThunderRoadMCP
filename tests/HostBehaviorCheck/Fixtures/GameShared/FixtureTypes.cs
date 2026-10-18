namespace Fixture.Shared;

public class SharedSurface
{
    public string Revision() => "game-v1";
}

public class GameSurface
{
    public int GameOnly() => 1;
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
