using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class ApiExample
{
    public ApiExample(string id, string title, string language, string code, string provenance,
        IReadOnlyList<string> symbols, IReadOnlyList<string> prerequisites, IReadOnlyList<string> cleanup,
        string evidence, string evidenceIdentity)
    {
        Id = id;
        Title = title;
        Language = language;
        Code = code;
        Provenance = provenance;
        Symbols = symbols;
        Prerequisites = prerequisites;
        Cleanup = cleanup;
        Evidence = evidence;
        EvidenceIdentity = evidenceIdentity;
    }

    public string Id { get; }
    public string Title { get; }
    public string Language { get; }
    public string Code { get; }
    public string Provenance { get; }
    public IReadOnlyList<string> Symbols { get; }
    public IReadOnlyList<string> Prerequisites { get; }
    public IReadOnlyList<string> Cleanup { get; }
    public string Evidence { get; }
    public string EvidenceIdentity { get; }
}

internal static class GameplayExamples
{
    private const string UnverifiedIdentity = "No matching compile or live validation report for the selected installed assemblies and evaluator identity.";
    private const int QueryTermScore = 20;
    private const int RelevantSymbolBonus = 5;
    private const int CSharpExampleBonus = 15;

    internal static IReadOnlyList<ApiExample> All { get; } = Array.AsReadOnly(new[]
    {
        Example(
            "player.head-position",
            "Read the player's head position",
            "csharp",
            @"var player = Player.local;
if (player == null || player.head == null) return new { available = false };
return new { available = true, position = player.head.transform.position };",
            "Installed ThunderRoad bridge spawn path uses Player.local, Player.head, and the head Transform for its default spawn location; exact symbol IDs must be resolved by api_lookup for the selected installation.",
            new[] { "ThunderRoad.Player.local", "ThunderRoad.Player.head", "UnityEngine.Transform.position" },
            new[] { "A ready game bridge and an active local player with a head." },
            new string[0]),
        Example(
            "item.spawn",
            "Spawn a catalog item near the player",
            "mcp",
            "game_spawn_item(itemId: \"<ItemData id>\", key: \"demo-item\")",
            "ThunderRoadMcp.Bridge.BridgeRequestHandler.SpawnItemAsync resolves Catalog.GetData<ItemData> and invokes ItemData.SpawnAsync on a bridge-managed coroutine; no custom C# spawn code is needed.",
            new[] { "ThunderRoad.Catalog.GetData<ThunderRoad.ItemData>", "ThunderRoad.ItemData.SpawnAsync", "ThunderRoadMcp.Bridge.McpRuntime.StartManagedCoroutine" },
            new[] { "A ready game bridge and an ItemData id from catalog_search(catalogType=\"ItemData\")." },
            new[] { "Use evaluate_csharp to call Item.Despawn() for a spawned object, then McpRuntime.Current.Remove(key). game_runtime(action:\"clear\") only drops references; it does not despawn objects." }),
        Example(
            "creature.spawn",
            "Spawn a configured creature near the player",
            "csharp",
            @"var player = Player.local;
if (player == null || player.head == null) return new { requested = false, reason = ""no local player"" };
var data = Catalog.GetData<CreatureData>(""<CreatureData id>"");
if (data == null) return new { requested = false, reason = ""CreatureData id not found"" };
var runtime = McpRuntime.Current;
const string creatureKey = ""demo.creature"";
const string requestKey = ""demo.creature.request"";
const string timeoutKey = ""demo.creature.timeout"";
if (runtime.Get<object>(requestKey) != null)
    return new { requested = false, reason = ""spawn already pending"" };
Creature previous = runtime.Get<Creature>(creatureKey);
if (previous != null) previous.Despawn();
runtime.Remove(creatureKey);
object request = new object();
runtime.Set(requestKey, request);
try
{
    runtime.StartManagedCoroutine(timeoutKey, ExpireRequest());
    data.SpawnAsync(
        player.head.transform.position + player.head.transform.forward * 2f,
        player.head.transform.eulerAngles.y,
        null,
        true,
        null,
        creature =>
        {
            if (!runtime.TryGet<object>(requestKey, out object activeRequest) || !ReferenceEquals(activeRequest, request))
            {
                if (creature != null) creature.Despawn();
                return;
            }
            runtime.Remove(requestKey);
            runtime.StopManagedCoroutine(timeoutKey);
            if (creature != null) runtime.Set(creatureKey, creature);
        });
}
catch
{
    runtime.Remove(requestKey);
    runtime.StopManagedCoroutine(timeoutKey);
    throw;
}
return new { requested = true, key = creatureKey };

System.Collections.IEnumerator ExpireRequest()
{
    for (int frame = 0; frame < 300; frame++)
    {
        if (!runtime.TryGet<object>(requestKey, out object activeRequest) || !ReferenceEquals(activeRequest, request))
            yield break;
        yield return null;
    }
    if (runtime.TryGet<object>(requestKey, out object expiredRequest) && ReferenceEquals(expiredRequest, request))
        runtime.Remove(requestKey);
}",
            "Installed Blade & Sorcery 1.3.2 ThunderRoad.dll metadata: public CreatureData.SpawnAsync(Vector3, Single, Transform, Boolean, WayPoint[], Action<Creature>) calls ConfigCreature before its callback. asm_FB4AFDCA51764E3CAC303D71A457ED86, sym_51831887BF24B494F2EA8C54F2E9B7C7, MVID 7c3f68b0-6dd9-431c-b97d-3ea4fdbc55ec; the selected installation identity must be re-resolved by api_lookup.",
            new[] { "ThunderRoad.Catalog.GetData<T>", "ThunderRoad.Player.local", "ThunderRoad.Player.head", "ThunderRoad.CreatureData.SpawnAsync", "ThunderRoad.Creature.Despawn", "ThunderRoadMcp.Bridge.McpRuntime.Set", "ThunderRoadMcp.Bridge.McpRuntime.Get<T>", "ThunderRoadMcp.Bridge.McpRuntime.TryGet<T>", "ThunderRoadMcp.Bridge.McpRuntime.Remove", "ThunderRoadMcp.Bridge.McpRuntime.StartManagedCoroutine", "ThunderRoadMcp.Bridge.McpRuntime.StopManagedCoroutine" },
            new[] { "A ready game bridge, a local player with a head, and a CreatureData id resolved through catalog_search(catalogType=\"CreatureData\"). Spawn completion is asynchronous; the callback is not guaranteed if the game content is inactive. The 300-frame expiry releases pending state; cleanup despawns completed creatures and a late callback despawns its result." },
            new[] { @"var runtime = McpRuntime.Current;
const string creatureKey = ""demo.creature"";
const string requestKey = ""demo.creature.request"";
const string timeoutKey = ""demo.creature.timeout"";
runtime.StopManagedCoroutine(timeoutKey);
Creature creature = runtime.Get<Creature>(creatureKey);
runtime.Remove(requestKey);
if (creature != null) creature.Despawn();
runtime.Remove(creatureKey);
return new { cleaned = true, hadCreature = creature != null };" }),
        Example(
            "runtime.counter",
            "Store and inspect a runtime counter",
            "csharp",
            @"var runtime = McpRuntime.Current;
runtime.Set(""demo.counter"", 1);
if (!runtime.TryGetValue<int>(""demo.counter"", out int count)) return new { found = false };
return new { found = true, count, next = count + 1 };",
            "McpRuntime public API in ThunderRoadMcp.Bridge; value retrieval uses the distinct unconstrained TryGetValue<T> API.",
            new[] { "ThunderRoadMcp.Bridge.McpRuntime.Set", "ThunderRoadMcp.Bridge.McpRuntime.TryGetValue<T>" },
            new[] { "A ready bridge; the key is local to the game process." },
            new[] { "McpRuntime.Current.Remove(\"demo.counter\");\nreturn new { removed = true };" }),
        Example(
            "runtime.coroutine",
            "Run bounded work across frames",
            "csharp",
            @"var runtime = McpRuntime.Current;
const string key = ""demo.frames"";
runtime.StartManagedCoroutine(key, CountFrames());
return new { started = true, key };

System.Collections.IEnumerator CountFrames()
{
    for (int frame = 0; frame < 3; frame++) yield return null;
}",
            "McpRuntime public StartManagedCoroutine implementation; Unity coroutine execution is frame-scheduled on the main thread.",
            new[] { "ThunderRoadMcp.Bridge.McpRuntime.StartManagedCoroutine", "UnityEngine.MonoBehaviour.StartCoroutine" },
            new[] { "A ready bridge and an unused runtime coroutine key." },
            new[] { "return new { stopped = McpRuntime.Current.StopManagedCoroutine(\"demo.frames\") };" }),
        Example(
            "unity.scene-event",
            "Subscribe once and explicitly unsubscribe a Unity scene event",
            "csharp",
            @"var runtime = McpRuntime.Current;
const string key = ""demo.scene-handler"";
var previous = runtime.Get<UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene>>(key);
if (previous != null) UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= previous;
UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene> handler =
    (oldScene, newScene) => UnityEngine.Debug.Log(""Active scene changed to "" + newScene.name);
runtime.Set(key, handler);
UnityEngine.SceneManagement.SceneManager.activeSceneChanged += handler;
return new { subscribed = true, key };",
            "UnityEngine.SceneManagement.SceneManager and UnityAction event signatures from the Unity 2021.3 API; the actual installed reference set must compile-check this pattern before use.",
            new[] { "UnityEngine.SceneManagement.SceneManager.activeSceneChanged", "UnityEngine.Events.UnityAction<T1,T2>", "ThunderRoadMcp.Bridge.McpRuntime.Set" },
            new[] { "The bridge is ready; event subscription occurs on Unity's main thread." },
            new[] { @"var runtime = McpRuntime.Current;
const string key = ""demo.scene-handler"";
var handler = runtime.Get<UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene>>(key);
if (handler != null) UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= handler;
runtime.Remove(key);
return new { unsubscribed = handler != null };" })
    });

    internal static IReadOnlyList<ApiExample> ForQuery(string query, IReadOnlyCollection<string> relevantSymbols)
    {
        var symbols = relevantSymbols == null ? null : new HashSet<string>(relevantSymbols, StringComparer.Ordinal);
        var queryTerms = new HashSet<string>(TokenizeExampleText(query).Select(NormalizeExampleTerm),
            StringComparer.Ordinal);
        var matches = new List<(ApiExample Example, int Score)>();
        foreach (ApiExample example in All)
        {
            var exampleTerms = new HashSet<string>(
                TokenizeExampleText(example.Id + " " + example.Title).Select(NormalizeExampleTerm),
                StringComparer.Ordinal);
            var score = 0;
            foreach (var term in queryTerms)
                if (exampleTerms.Contains(term))
                    score += QueryTermScore;
            var symbolMatch = false;
            if (symbols != null)
                foreach (string symbol in example.Symbols)
                    if (symbols.Contains(symbol)) { symbolMatch = true; break; }
            if (symbolMatch)
                score += RelevantSymbolBonus;
            if (score == 0)
                continue;
            if (example.Language.Equals("csharp", StringComparison.OrdinalIgnoreCase))
                score += CSharpExampleBonus;
            matches.Add((example, score));
        }
        matches.Sort(static (left, right) =>
        {
            var order = right.Score.CompareTo(left.Score);
            return order != 0 ? order : StringComparer.Ordinal.Compare(left.Example.Id, right.Example.Id);
        });
        var ranked = new ApiExample[matches.Count];
        for (var i = 0; i < matches.Count; i++)
            ranked[i] = matches[i].Example;
        return Array.AsReadOnly(ranked);
    }

    private static IEnumerable<string> TokenizeExampleText(string text)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (!char.IsLetterOrDigit(current))
            {
                AddExampleToken(text, start, i, tokens);
                start = -1;
                continue;
            }
            if (start < 0)
                start = i;
            else if (char.IsUpper(current) && (char.IsLower(text[i - 1]) || char.IsDigit(text[i - 1])))
            {
                AddExampleToken(text, start, i, tokens);
                start = i;
            }
        }
        AddExampleToken(text, start, text.Length, tokens);
        return tokens;
    }

    private static void AddExampleToken(string text, int start, int end, List<string> tokens)
    {
        if (start >= 0 && end > start)
            tokens.Add(text.Substring(start, end - start).ToLowerInvariant());
    }

    private static string NormalizeExampleTerm(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal))
            return term.Substring(0, term.Length - 3) + "y";
        if (term.Length > 4 && term.EndsWith("es", StringComparison.Ordinal) &&
            (term.Substring(0, term.Length - 2).EndsWith("s", StringComparison.Ordinal) ||
             term.Substring(0, term.Length - 2).EndsWith("x", StringComparison.Ordinal) ||
             term.Substring(0, term.Length - 2).EndsWith("ch", StringComparison.Ordinal) ||
             term.Substring(0, term.Length - 2).EndsWith("sh", StringComparison.Ordinal)))
            return term.Substring(0, term.Length - 2);
        if (term.Length > 3 && term.EndsWith("s", StringComparison.Ordinal) &&
            !term.EndsWith("ss", StringComparison.Ordinal) && !term.EndsWith("us", StringComparison.Ordinal) &&
            !term.EndsWith("is", StringComparison.Ordinal))
            return term.Substring(0, term.Length - 1);
        return term;
    }

    private static ApiExample Example(string id, string title, string language, string code, string provenance,
        string[] symbols, string[] prerequisites, string[] cleanup)
    {
        return new ApiExample(id, title, language, code, provenance,
            Array.AsReadOnly(symbols), Array.AsReadOnly(prerequisites), Array.AsReadOnly(cleanup),
            "unverified", UnverifiedIdentity);
    }

}
