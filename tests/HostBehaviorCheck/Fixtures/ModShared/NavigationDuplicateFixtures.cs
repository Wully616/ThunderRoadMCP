namespace Fixture.Shared;

public class NavigationDuplicateTarget
{
    public int Ping() => 2;
}

public class NavigationDuplicateCaller
{
    public int Use(NavigationDuplicateTarget target) => target.Ping();
}
