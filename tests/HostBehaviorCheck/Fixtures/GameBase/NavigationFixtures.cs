namespace Fixture.Base;

public class NavigationTarget
{
    public int Value;
    public int Number => Value;
    public event Action? Changed;
    public int Overload(int value) => value + 1;
    public string Overload(string value) => value;
    public T Generic<T>(T value) => value;
    public void Raise() => Changed?.Invoke();
    public void CallbackWork() { }
}

public class NavigationBox<T>
{
    public T Echo(T value) => value;
}

public abstract class AbstractNavigationTarget
{
    public abstract void NoBody();
}
