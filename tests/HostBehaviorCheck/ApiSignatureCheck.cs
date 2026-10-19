using System;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;

public static class ApiSignatureCheck
{
    public static async Task RunAsync(Func<string, object, Task<JsonDocument>> apiToolJsonAsync, Action<bool, string> assert)
    {
        using var assemblies = await apiToolJsonAsync("api_assemblies", new { name = "Fixture.Shared", origin = "game", limit = 50 });
        var assembly = assemblies.RootElement.GetProperty("assemblies").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Fixture.Shared");
        var assemblyId = assembly.GetProperty("assemblyId").GetString()!;

        async Task<JsonElement> Search(string query, string kind, string? exactQualifiedName = null)
        {
            using var result = await apiToolJsonAsync("api_search", new
            {
                query, kind, assemblyId, offset = 0, limit = 50
            });
            var matches = result.RootElement.GetProperty("results").EnumerateArray()
                .Where(item => exactQualifiedName is null ||
                    item.GetProperty("qualifiedName").GetString() == exactQualifiedName).ToArray();
            assert(matches.Length == 1, $"{query} returns one {kind} signature, got {matches.Length}: " +
                string.Join(", ", matches.Select(item => item.GetProperty("qualifiedName").GetString())));
            return matches[0].Clone();
        }

        async Task Signature(string query, string kind, string expected)
        {
            var result = await Search(query, kind);
            assert(result.GetProperty("signature").GetString() == expected, $"{query} signature is `{expected}`");
        }
        await Signature("InheritedValue", "property", "public T InheritedValue { get; set; }");
        var surface = await Search("SignatureSurface", "type", "Fixture.Shared.SignatureSurface");
        using var type = await apiToolJsonAsync("api_type", new
        {
            assemblyId, typeSymbolId = surface.GetProperty("symbolId").GetString(),
            includeInherited = true, limit = 100
        });
        var inherited = type.RootElement.GetProperty("members").EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "InheritedValue");
        assert(inherited.GetProperty("isInherited").GetBoolean() &&
               inherited.GetProperty("signature").GetString() == "public T InheritedValue { get; set; }",
            "generic base member is returned with its signature and inherited marker");
        assert(surface.GetProperty("signature").GetString() == "public sealed class SignatureSurface<T>" &&
               type.RootElement.GetProperty("genericConstraints").EnumerateArray().Single().GetString() == "where T : class, new()",
            "generic constrained type declaration is formatted");
        await Signature("NestedSurface", "type", "public sealed class NestedSurface");
        var delegateType = await Search("SignatureChanged", "type");
        assert(delegateType.GetProperty("signature").GetString() == "public delegate void SignatureChanged(string value)",
            "delegate declaration includes its parameter signature");
        await Signature("RefIntDelegate", "type", "public delegate ref int RefIntDelegate()");
        await Signature("RefReadonlyIntDelegate", "type", "public delegate ref readonly int RefReadonlyIntDelegate()");

        await Signature("PrivateSetterValue", "property", "public int PrivateSetterValue { get; private set; }");
        await Signature("OptionalEscapes", "method", "public void OptionalEscapes(Fixture.Shared.SignatureMode mode = Fixture.Shared.SignatureMode.Safe, string text = \"a\\\\b\\\"c\", char quote = '\\\'')");
        await Signature("OptionalSpecialFloats", "method", "public void OptionalSpecialFloats(float finite = 1.25F, float notANumber = float.NaN, double positiveInfinity = double.PositiveInfinity, double negativeInfinity = double.NegativeInfinity)");
        await Signature("NestedGeneric", "method", "public void NestedGeneric(Fixture.Shared.Outer<int>.Inner<string> value)");
        await Signature("GenericNested", "method", "public void GenericNested<U>(System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<U>> values) where U : struct, System.IComparable<U>");
        await Signature("SignatureChangedEvent", "event", "public event Fixture.Shared.SignatureChanged SignatureChangedEvent");
        await Signature("SignatureHandler", "field", "public Fixture.Shared.SignatureChanged SignatureHandler");
        await Signature("References", "method", "public void References(ref int first, out string second, in System.Guid third)");
        await Signature("RefValue", "method", "public ref int RefValue()");
        var constructor = await Search("SignatureSurface", "constructor", "Fixture.Shared.SignatureSurface..ctor");
        assert(constructor.GetProperty("signature").GetString() == "public SignatureSurface()",
            "SignatureSurface constructor signature is `public SignatureSurface()`");
        var selfRecursive = await Search("SelfRecursiveConstraint", "type", "Fixture.Shared.SelfRecursiveConstraint");
        using var selfType = await apiToolJsonAsync("api_type", new
        {
            assemblyId, typeSymbolId = selfRecursive.GetProperty("symbolId").GetString(), limit = 100
        });
        assert(selfType.RootElement.GetProperty("genericConstraints").EnumerateArray().Single().GetString() ==
               "where T : System.IComparable<T>",
            "self-referential generic constraint is indexed and formatted");
        var mutualRecursive = await Search("MutualRecursiveConstraint", "type", "Fixture.Shared.MutualRecursiveConstraint");
        using var mutualType = await apiToolJsonAsync("api_type", new
        {
            assemblyId, typeSymbolId = mutualRecursive.GetProperty("symbolId").GetString(), limit = 100
        });
        assert(mutualType.RootElement.GetProperty("genericConstraints").EnumerateArray().Select(item => item.GetString())
                   .SequenceEqual(new[] { "where T : System.IComparable<U>", "where U : System.IComparable<T>" }),
            "mutually recursive generic constraints are indexed and formatted");
    }
}
