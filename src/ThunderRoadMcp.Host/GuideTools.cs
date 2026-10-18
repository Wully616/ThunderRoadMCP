using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class GuideTools
{
    private const string ResourceName = "ThunderRoadMcp.Host.game-guide.md";
    private const int MaxContentLength = 16 * 1024;
    private static readonly Lazy<string> GuideText = new(LoadGuide);
    private static readonly Topic[] Topics =
    [
        new("bootstrap", ["bootstrap", "overview", "start", "startup"], "## Bootstrap"),
        new("unity", ["unity", "engine", "unity-2021.3", "unity 2021.3"], "## Unity 2021.3 guidance"),
        new("runtime", ["runtime", "managed-runtime", "managed runtime", "coroutines"], "## Managed runtime"),
        new("spawning", ["spawning", "spawn", "items", "item", "spawning items"], "## Spawning items"),
        new("levels", ["levels", "level", "loading", "loading a level"], "## Loading a level"),
        new("api", ["api", "api-workflow", "console", "console-api", "console/api", "console api", "console workflow", "workflow", "api workflow"], "## API workflow"),
        new("logs", ["logs", "log"], "## Logs"),
        new("eval", ["eval", "evaluate", "evaluate-csharp", "eval-limits", "evaluation", "eval limits"], "## `evaluate_csharp` limits"),
        new("example", ["example", "worked-example", "orbit", "worked example"], "## Worked example: spawn and orbit"),
        new("troubleshooting", ["troubleshooting", "troubleshoot", "debug"], "## Troubleshooting"),
        new("version", ["version", "version-stamp"], "## Version stamp"),
    ];

    [McpServerTool(Name = "game_guide", Title = "Read the Blade & Sorcery game guide", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(System.Text.Json.Nodes.JsonObject))]
    [Description("Read a bounded section of the embedded live-game guide. Topics: bootstrap/overview (default), unity, runtime, spawning, levels, api/console, logs, eval, example, troubleshooting, version. Returns {topic,content}.")]
    public static Task<CallToolResult> GameGuide(
        [Description("Guide topic or alias; defaults to bootstrap.")] string? topic = "bootstrap",
        CancellationToken cancellationToken = default) =>
        ToolInvocation.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = ResolveTopic(topic);
            var content = selected.Name == "bootstrap"
                ? ReadBootstrap(GuideText.Value)
                : ReadSection(GuideText.Value, selected.Heading);
            if (content.Length > MaxContentLength)
                content = content[..(MaxContentLength - 16)] + "\n…[truncated]";
            return Task.FromResult(JsonSerializer.Serialize(new { topic = selected.Name, content }));
        });

    private static Topic ResolveTopic(string? topic)
    {
        var requested = string.IsNullOrWhiteSpace(topic) ? "bootstrap" : topic.Trim();
        foreach (var candidate in Topics)
            if (candidate.Aliases.Contains(requested, StringComparer.OrdinalIgnoreCase))
                return candidate;
        throw new ArgumentException($"Unknown topic '{requested}'. Available topics: {string.Join(", ", Topics.Select(candidate => candidate.Name))}.");
    }

    private static string ReadBootstrap(string markdown)
    {
        var lines = SplitLines(markdown);
        var bootstrapStart = Array.FindIndex(lines, line => line == "## Bootstrap");
        if (bootstrapStart < 0)
            throw new InvalidOperationException("The embedded game guide is missing its Bootstrap section.");
        var preamble = string.Join("\n", lines, 0, bootstrapStart).TrimEnd();
        return preamble + "\n\n" + ReadSection(markdown, "## Bootstrap");
    }

    private static string ReadSection(string markdown, string heading)
    {
        var lines = SplitLines(markdown);
        var start = Array.FindIndex(lines, line => line == heading);
        if (start < 0)
            throw new InvalidOperationException($"The embedded game guide is missing section '{heading}'.");
        var end = start + 1;
        while (end < lines.Length && !lines[end].StartsWith("## ", StringComparison.Ordinal)) end++;
        return string.Join("\n", lines, start, end - start).TrimEnd();
    }

    private static string[] SplitLines(string markdown) =>
        markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string LoadGuide()
    {
        using var stream = typeof(GuideTools).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded game guide resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Topic(string Name, string[] Aliases, string Heading);
}
