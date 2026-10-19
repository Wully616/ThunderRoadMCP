namespace Fixture.Shared;

public class NavigationCaller
{
    public int Call(Fixture.Base.NavigationTarget target, int value)
    {
        target.Value = value;
        var read = target.Value;
        _ = target.Number;
        target.Changed += OnChanged;
        target.Changed -= OnChanged;
        return target.Overload(value) + target.Generic(value) + read;
    }

    public int SecondCall(Fixture.Base.NavigationTarget target) => target.Overload(2);
    public string StringCall(Fixture.Base.NavigationTarget target, string value) => target.Overload(value);

    public Fixture.Base.NavigationTarget GenericTypeCall(Fixture.Base.NavigationTarget target)
    {
        var box = new Fixture.Base.NavigationBox<Fixture.Base.NavigationTarget>();
        return box.Echo(target);
    }
    public void SubscribeLambda(Fixture.Base.NavigationTarget target) => target.Changed += () => target.CallbackWork();
    private static void OnChanged() { }
}
