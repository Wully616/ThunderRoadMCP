using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public static class ApiAttributeCheck
{
    public static async Task RunAsync(Func<string, object, Task<JsonDocument>> apiToolJsonAsync, Action<bool, string> assert,
        ApiReuseContext context)
    {
        using var assemblies = await apiToolJsonAsync("api_assemblies", new { name = "Fixture.Shared", origin = "game", limit = 50 });
        var assemblyId = assemblies.RootElement.GetProperty("assemblies").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Fixture.Shared")
            .GetProperty("assemblyId").GetString()!;

        async Task<JsonElement> Search(string query, string kind, string qualifiedName)
        {
            using var response = await apiToolJsonAsync("api_search", new { query, kind, assemblyId, offset = 0, limit = 50 });
            return response.RootElement.GetProperty("results").EnumerateArray()
                .Single(item => item.GetProperty("qualifiedName").GetString() == qualifiedName).Clone();
        }

        static JsonElement Attribute(JsonElement hit, string name, string? target = null) =>
            hit.GetProperty("attributes").EnumerateArray().Single(attribute =>
                attribute.GetProperty("name").GetString() == name &&
                (target is null ? attribute.GetProperty("target").ValueKind == JsonValueKind.Null :
                    attribute.GetProperty("target").GetString() == target));

        static string FixedString(JsonElement attribute, int index = 0) =>
            attribute.GetProperty("fixedArguments")[index].GetProperty("value").GetString()!;

        static void NotTruncated(JsonElement hit, Action<bool, string> check, string what) =>
            check(!hit.GetProperty("attributesTruncated").GetBoolean(), $"{what} attribute metadata is complete");

        var type = await Search("ApiAttributeSurface", "type", "Fixture.Shared.ApiAttributeSurface");
        NotTruncated(type, assert, "type");
        using (var typeDetails = await apiToolJsonAsync("api_type", new
               {
                   assemblyId,
                   typeSymbolId = type.GetProperty("symbolId").GetString(),
                   offset = 0,
                   limit = 50,
               }))
        {
            var details = typeDetails.RootElement;
            NotTruncated(details, assert, "api_type");
            assert(FixedString(Attribute(details, "Fixture.Shared.FixtureMarkerAttribute")) == "custom",
                "api_type includes indexed attribute metadata");
        }
        var marker = Attribute(type, "Fixture.Shared.FixtureMarkerAttribute");
        assert(FixedString(marker) == "custom" && marker.GetProperty("namedArguments").EnumerateArray()
                .Single(argument => argument.GetProperty("name").GetString() == "Version").GetProperty("value").GetInt32() == 3,
            "custom attribute fixed and named constants are detached into metadata");

        var field = await Search("SerializedValue", "field", "Fixture.Shared.ApiAttributeSurface.SerializedValue");
        assert(FixedString(Attribute(field, "System.ObsoleteAttribute")) == "fixture obsolete message",
            "obsolete constructor constants are read from metadata");
        foreach (var attributeName in new[]
                 {
                     "UnityEngine.SerializeField", "UnityEngine.SerializeReference", "UnityEngine.HideInInspector",
                     "UnityEngine.Serialization.FormerlySerializedAsAttribute",
                 })
            _ = Attribute(field, attributeName);
        assert(FixedString(Attribute(field, "UnityEngine.Serialization.FormerlySerializedAsAttribute")) == "old_value",
            "Unity FormerlySerializedAs metadata retains its old field name");

        var flags = await Search("ApiAttributeFlags", "type", "Fixture.Shared.ApiAttributeFlags");
        _ = Attribute(flags, "System.FlagsAttribute");
        var values = await Search("Values", "method", "Fixture.Shared.ApiAttributeSurface.Values");
        assert(values.GetProperty("signature").GetString()!.Contains("params int[] values", StringComparison.Ordinal),
            "params modifier is preserved in the callable signature");
        var attributedParameter = await Search("AttributedParameter", "method", "Fixture.Shared.ApiAttributeSurface.AttributedParameter");
        assert(FixedString(Attribute(attributedParameter, "Fixture.Shared.FixtureMarkerAttribute", "parameter:0:value")) == "parameter",
            "parameter attribute metadata includes its parameter target");
        var extension = await Search("Extend", "method", "Fixture.Shared.ApiAttributeExtensions.Extend");
        assert(extension.GetProperty("signature").GetString()!.Contains("this Fixture.Shared.ApiAttributeSurface value", StringComparison.Ordinal),
            "extension receiver modifier is preserved in the callable signature");

        _ = await Search("StaticInitializerProbe", "type", "Fixture.Shared.StaticInitializerProbe");
        assert(!System.IO.File.Exists(System.IO.Path.Combine(context.WorkDirectory, "fixture-static-constructor-ran.txt")),
            "attribute browsing does not execute fixture static initializers");
    }
}
