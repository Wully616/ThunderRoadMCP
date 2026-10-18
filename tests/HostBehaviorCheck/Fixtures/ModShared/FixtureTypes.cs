namespace Fixture.Shared;

public class SharedSurface
{
    public string Revision() => "mod-v1";
}

public class ModSurface
{
    public int Overload(int value) => value + 1;
    public string Overload(string value) => value + "mod";
}

public class DerivedSurface : Fixture.Base.BaseSurface
{
    public string ModOnly() => "mod";
}
