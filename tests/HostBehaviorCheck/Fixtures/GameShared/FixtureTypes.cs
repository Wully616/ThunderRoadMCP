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

public sealed class UnfitLookupFixture
{
    public int OverBudgetA(
        int parameterNameForOversizedLookupRecoveryTest001, int parameterNameForOversizedLookupRecoveryTest002, int parameterNameForOversizedLookupRecoveryTest003, int parameterNameForOversizedLookupRecoveryTest004, int parameterNameForOversizedLookupRecoveryTest005, int parameterNameForOversizedLookupRecoveryTest006, int parameterNameForOversizedLookupRecoveryTest007, int parameterNameForOversizedLookupRecoveryTest008,
        int parameterNameForOversizedLookupRecoveryTest009, int parameterNameForOversizedLookupRecoveryTest010, int parameterNameForOversizedLookupRecoveryTest011, int parameterNameForOversizedLookupRecoveryTest012, int parameterNameForOversizedLookupRecoveryTest013, int parameterNameForOversizedLookupRecoveryTest014, int parameterNameForOversizedLookupRecoveryTest015, int parameterNameForOversizedLookupRecoveryTest016,
        int parameterNameForOversizedLookupRecoveryTest017, int parameterNameForOversizedLookupRecoveryTest018, int parameterNameForOversizedLookupRecoveryTest019, int parameterNameForOversizedLookupRecoveryTest020, int parameterNameForOversizedLookupRecoveryTest021, int parameterNameForOversizedLookupRecoveryTest022, int parameterNameForOversizedLookupRecoveryTest023, int parameterNameForOversizedLookupRecoveryTest024,
        int parameterNameForOversizedLookupRecoveryTest025, int parameterNameForOversizedLookupRecoveryTest026, int parameterNameForOversizedLookupRecoveryTest027, int parameterNameForOversizedLookupRecoveryTest028, int parameterNameForOversizedLookupRecoveryTest029, int parameterNameForOversizedLookupRecoveryTest030, int parameterNameForOversizedLookupRecoveryTest031, int parameterNameForOversizedLookupRecoveryTest032,
        int parameterNameForOversizedLookupRecoveryTest033, int parameterNameForOversizedLookupRecoveryTest034, int parameterNameForOversizedLookupRecoveryTest035, int parameterNameForOversizedLookupRecoveryTest036, int parameterNameForOversizedLookupRecoveryTest037, int parameterNameForOversizedLookupRecoveryTest038, int parameterNameForOversizedLookupRecoveryTest039, int parameterNameForOversizedLookupRecoveryTest040,
        int parameterNameForOversizedLookupRecoveryTest041, int parameterNameForOversizedLookupRecoveryTest042, int parameterNameForOversizedLookupRecoveryTest043, int parameterNameForOversizedLookupRecoveryTest044, int parameterNameForOversizedLookupRecoveryTest045, int parameterNameForOversizedLookupRecoveryTest046, int parameterNameForOversizedLookupRecoveryTest047, int parameterNameForOversizedLookupRecoveryTest048,
        int parameterNameForOversizedLookupRecoveryTest049, int parameterNameForOversizedLookupRecoveryTest050, int parameterNameForOversizedLookupRecoveryTest051, int parameterNameForOversizedLookupRecoveryTest052, int parameterNameForOversizedLookupRecoveryTest053, int parameterNameForOversizedLookupRecoveryTest054, int parameterNameForOversizedLookupRecoveryTest055, int parameterNameForOversizedLookupRecoveryTest056,
        int parameterNameForOversizedLookupRecoveryTest057, int parameterNameForOversizedLookupRecoveryTest058, int parameterNameForOversizedLookupRecoveryTest059, int parameterNameForOversizedLookupRecoveryTest060, int parameterNameForOversizedLookupRecoveryTest061, int parameterNameForOversizedLookupRecoveryTest062, int parameterNameForOversizedLookupRecoveryTest063, int parameterNameForOversizedLookupRecoveryTest064,
        int parameterNameForOversizedLookupRecoveryTest065, int parameterNameForOversizedLookupRecoveryTest066, int parameterNameForOversizedLookupRecoveryTest067, int parameterNameForOversizedLookupRecoveryTest068, int parameterNameForOversizedLookupRecoveryTest069, int parameterNameForOversizedLookupRecoveryTest070, int parameterNameForOversizedLookupRecoveryTest071, int parameterNameForOversizedLookupRecoveryTest072,
        int parameterNameForOversizedLookupRecoveryTest073, int parameterNameForOversizedLookupRecoveryTest074, int parameterNameForOversizedLookupRecoveryTest075, int parameterNameForOversizedLookupRecoveryTest076, int parameterNameForOversizedLookupRecoveryTest077, int parameterNameForOversizedLookupRecoveryTest078, int parameterNameForOversizedLookupRecoveryTest079, int parameterNameForOversizedLookupRecoveryTest080) => 1;

    public int OverBudgetB() => 2;
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
