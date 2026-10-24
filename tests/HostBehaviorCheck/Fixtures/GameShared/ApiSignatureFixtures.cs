namespace Fixture.Shared
{
    public enum SignatureMode { Fast, Safe }
    public delegate void SignatureChanged(string value);
    public delegate ref int RefIntDelegate();
    public delegate ref readonly int RefReadonlyIntDelegate();

    public sealed class SelfRecursiveConstraint<T> where T : IComparable<T> { }
    public sealed class MutualRecursiveConstraint<T, U> where T : IComparable<U> where U : IComparable<T> { }

    public class Outer<T>
{
    public class Inner<U> { }
}

    public class SignatureParent<T> where T : class
{
    public T InheritedValue { get; set; } = default!;
}

    public sealed class SignatureSurface<T> : SignatureParent<T> where T : class, new()
{
    public sealed class NestedSurface { }
    public int PrivateSetterValue { get; private set; }
    public void OptionalEscapes(SignatureMode mode = SignatureMode.Safe, string text = "a\\b\"c", char quote = '\'') { }
    public void OptionalSpecialFloats(float finite = 1.25f, float notANumber = float.NaN,
        double positiveInfinity = double.PositiveInfinity, double negativeInfinity = double.NegativeInfinity) { }
    public void NestedGeneric(Outer<int>.Inner<string> value) { }
    public void GenericNested<U>(Dictionary<string, List<U>> values) where U : struct, IComparable<U> { }
#pragma warning disable CS0067 // The event exists to verify metadata formatting.
    public event SignatureChanged? SignatureChangedEvent;
#pragma warning restore CS0067
    public SignatureChanged? SignatureHandler;
    public ref int RefValue() => ref _refValue;
    private int _refValue;
    public void References(ref int first, out string second, in Guid third) => second = third.ToString();
}
}

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeField : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeReference : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class HideInInspector : Attribute { }
}

namespace UnityEngine.Serialization
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public sealed class FormerlySerializedAsAttribute(string oldName) : Attribute
    {
        public string OldName { get; } = oldName;
    }
}

namespace Fixture.Shared
{
    public sealed class FixtureMarkerAttribute : Attribute
    {
        public FixtureMarkerAttribute(string value)
        {
            Value = value;
            var sentinel = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_FIXTURE_SENTINEL");
            if (sentinel is not null)
                File.WriteAllText(sentinel, "attribute constructor executed");
        }

        public string Value { get; }
        public int Version { get; set; }
    }

    [Flags]
    public enum ApiAttributeFlags { None = 0, Enabled = 1 }

    [FixtureMarker("custom", Version = 3)]
    public sealed class ApiAttributeSurface
    {
        [Obsolete("fixture obsolete message")]
        [UnityEngine.SerializeField]
        [UnityEngine.SerializeReference]
        [UnityEngine.HideInInspector]
        [UnityEngine.Serialization.FormerlySerializedAs("old_value")]
        public object? SerializedValue;

        public void Values(params int[] values) { }
        public void AttributedParameter([FixtureMarker("parameter")] string value) { }
    }

    public static class ApiAttributeExtensions
    {
        public static void Extend(this ApiAttributeSurface receiver) { }
        public static void ExtendRef(this ref int receiver) { }
        public static void ExtendIn(this in int receiver) { }
        public static void ExtendRefReadonly(this ref readonly int receiver) { }
        public static void KeywordParameters(string @class, string @event) { }
    }
}
