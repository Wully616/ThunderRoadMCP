namespace Fixture.Shared;

public class NavigationDuplicateTarget
{
    public int Ping() => 1;
}

public class NavigationDuplicateCaller
{
    public int Use(NavigationDuplicateTarget target) => target.Ping();
}
