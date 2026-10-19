using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public static class ApiDecompileBatchCheck
{
    public static async Task RunAsync(Func<string, object, Task<JsonDocument>> apiToolJsonAsync, Action<bool, string> assert)
    {
        using (var canceled = new System.Threading.CancellationTokenSource())
        {
            canceled.Cancel();
            var cancellationObserved = false;
            try
            {
                await new InstalledApiCatalog().DecompileBatchAsync(
                    [new ApiDecompileBatchItem("asm_cancel", "sym_cancel")], null, 2000, canceled.Token);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }
            assert(cancellationObserved, "batch honors cancellation before scanning or decompiling items");
        }
        using var assemblies = await apiToolJsonAsync("api_assemblies", new { name = "Fixture.Shared", origin = "game", limit = 50 });
        var assemblyId = assemblies.RootElement.GetProperty("assemblies").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Fixture.Shared").GetProperty("assemblyId").GetString()!;
        using var search = await apiToolJsonAsync("api_search", new { query = "GenericNested", assemblyId, kind = "method" });
        var hit = search.RootElement.GetProperty("results").EnumerateArray().First();
        var symbolId = hit.GetProperty("symbolId").GetString()!;
        using var typeSearch = await apiToolJsonAsync("api_search", new { query = "SignatureSurface", assemblyId, kind = "type" });
        var typeSymbolId = typeSearch.RootElement.GetProperty("results").EnumerateArray().First()
            .GetProperty("symbolId").GetString()!;
        var snapshotId = search.RootElement.GetProperty("snapshotId").GetString()!;
        using var batch = await apiToolJsonAsync("api_decompile_batch", new
        {
            snapshotId,
            items = new[]
            {
                new { assemblyId, symbolId, startLine = 1, lineCount = 500 },
                new { assemblyId = "invalid-assembly", symbolId, startLine = 1, lineCount = 10 },
                new { assemblyId, symbolId = typeSymbolId, startLine = 1, lineCount = 500 },
                new { assemblyId, symbolId, startLine = int.MaxValue, lineCount = 10 },
            },
        });
        var root = batch.RootElement;
        var results = root.GetProperty("results").EnumerateArray().ToArray();
        using var exactMethodSource = await apiToolJsonAsync("api_decompile", new { assemblyId, symbolId, startLine = 1, lineCount = 500, snapshotId });
        using var exactTypeSource = await apiToolJsonAsync("api_decompile", new { assemblyId, symbolId = typeSymbolId, startLine = 1, lineCount = 500, snapshotId });
        var firstBody = results[0].GetProperty("source").GetProperty("source").GetString();
        var secondBody = results[2].GetProperty("source").GetProperty("source").GetString();
        assert(results.Length == 4 && results[0].GetProperty("success").GetBoolean() &&
               results[0].GetProperty("source").GetProperty("symbolId").GetString() == symbolId &&
               results[0].GetProperty("source").GetProperty("assemblyId").GetString() == assemblyId &&
               firstBody == exactMethodSource.RootElement.GetProperty("source").GetString() &&
               results[2].GetProperty("success").GetBoolean() &&
               results[2].GetProperty("source").GetProperty("symbolId").GetString() == typeSymbolId &&
               secondBody == exactTypeSource.RootElement.GetProperty("source").GetString() &&
               firstBody != secondBody,
            "batch source exactly matches two individual source reads in input order around a mixed failure");
        assert(!results[1].GetProperty("success").GetBoolean() &&
               results[1].GetProperty("error").GetProperty("code").GetString() == "assembly.invalid_or_stale" &&
               !results[3].GetProperty("success").GetBoolean() &&
               results[3].GetProperty("error").GetProperty("code").GetString() == "source.invalid_page",
            "batch preserves valid entries around invalid assembly and per-entry source-page failures");

        using var largeSingle = await apiToolJsonAsync("api_decompile_batch", new
        {
            snapshotId,
            items = new[] { new { assemblyId, symbolId = typeSymbolId, startLine = 1, lineCount = 500 } },
        });
        var continuationBudget = Math.Max(2000, largeSingle.RootElement.GetRawText().Length + 500);
        using var partial = await apiToolJsonAsync("api_decompile_batch", new
        {
            snapshotId,
            maxChars = continuationBudget,
            items = new[]
            {
                new { assemblyId, symbolId = typeSymbolId, startLine = 1, lineCount = 500 },
                new { assemblyId, symbolId, startLine = 1, lineCount = 500 },
            },
        });
        var partialRoot = partial.RootElement;
        assert(partialRoot.GetProperty("results").GetArrayLength() == 1 &&
               partialRoot.GetProperty("results")[0].GetProperty("success").GetBoolean() &&
               partialRoot.GetProperty("nextItems").GetArrayLength() == 1 &&
               partialRoot.GetProperty("nextItems")[0].GetProperty("symbolId").GetString() == symbolId,
            "cumulative budget exhaustion returns the first source and a usable continuation for the next item");
        using var continued = await apiToolJsonAsync("api_decompile_batch", new
        {
            snapshotId,
            maxChars = continuationBudget,
            items = partialRoot.GetProperty("nextItems"),
        });
        assert(continued.RootElement.GetProperty("results").GetArrayLength() == 1 &&
               continued.RootElement.GetProperty("results")[0].GetProperty("source").GetProperty("symbolId").GetString() == symbolId,
            "batch continuation makes progress with the same budget and exact IDs");
        using var bounded = await apiToolJsonAsync("api_decompile_batch", new
        {
            snapshotId,
            maxChars = 2000,
            items = new[] { new { assemblyId, symbolId, startLine = 1, lineCount = 500 } },
        });
        assert(bounded.RootElement.GetRawText().Length <= 2000 &&
               bounded.RootElement.GetProperty("results").GetArrayLength() > 0 &&
               (bounded.RootElement.GetProperty("truncated").GetBoolean() ||
                !bounded.RootElement.GetProperty("results")[0].GetProperty("source").GetProperty("hasMore").GetBoolean()),
            "batch enforces total serialized budget while retaining source or an explicit continuation");
    }
}
