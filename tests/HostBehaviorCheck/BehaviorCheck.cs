using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Threading.Channels;
using System.Reflection.PortableExecutable;

if (args.Length == 3 && args[0] == "--catalog-cache-child")
{
    Environment.SetEnvironmentVariable("THUNDERROAD_MCP_LAUNCH_CONFIG", Path.GetFullPath(args[1]));
    Environment.SetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR", Path.GetFullPath(args[2]));
    Environment.SetEnvironmentVariable("THUNDERROAD_MCP_DIAGNOSTICS", "1");
    Console.WriteLine(await new BaseCatalogDataCatalog().SearchAsync(null, null, null, 0, 1, null, CancellationToken.None));
    return;
}
if (args.Length != 1)
    throw new ArgumentException("Pass the built ThunderRoadMcp.Host.dll path.");
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
using var cancellation = new CancellationTokenSource();
var directory = Path.Combine(Path.GetTempPath(), "ThunderRoadMcp-check-" + Guid.NewGuid().ToString("N"));
var apiCacheDirectory = Path.Combine(directory, "api-cache");
Directory.CreateDirectory(apiCacheDirectory);
var logDirectory = Path.Combine(directory, "logs");
Directory.CreateDirectory(logDirectory);
await File.WriteAllTextAsync(Path.Combine(logDirectory, "Player.log"), "first\ncafé\nlast\n");
await File.WriteAllTextAsync(Path.Combine(logDirectory, "Player-prev.log"), "previous session\n", new UTF8Encoding(false));
var configPath = Path.Combine(directory, "bridge.json");
await WriteConfigAsync(new string('a', 64));
var gameDirectory = Path.Combine(directory, "game");
Directory.CreateDirectory(gameDirectory);
var executablePath = Path.Combine(gameDirectory, "BladeAndSorcery.exe");
await File.WriteAllTextAsync(executablePath, "");
var launchConfigPath = Path.Combine(directory, "launch.json");
await WriteLaunchConfigAsync(executablePath);
var managedDirectory = Path.Combine(gameDirectory, "BladeAndSorcery_Data", "Managed");
Directory.CreateDirectory(managedDirectory);
var catalogArchivePath = Path.Combine(gameDirectory, "BladeAndSorcery_Data", "StreamingAssets", "Default", "bas.jsondb");
Directory.CreateDirectory(Path.GetDirectoryName(catalogArchivePath)!);
var levelJson = """
{
  "$type": "ThunderRoad.LevelData, ThunderRoad",
  "id": "Arena",
  "name": "Arena",
  "modes": [
    {
      "name": "Survival",
      "modules": [
        { "$type": "ThunderRoad.LevelModuleSurvival, ThunderRoad" }
      ]
    }
  ]
}
""";
var itemJson = """
{
  "$type": "ThunderRoad.ItemData, ThunderRoad",
  "id": "CrystalBlank",
  "name": "Prismatic Crystal Core"
}
""";
var apiTypeJson = """
{
  "$type": "Fixture.Base.BaseSurface, Fixture.Base",
  "id": "BaseSurfaceRecord",
  "name": "Fixture Base Surface"
}
""";
var nestedGenericJson = JsonSerializer.Serialize(new Dictionary<string, string>
{
    ["$type"] = typeof(Dictionary<string, int>.KeyCollection).AssemblyQualifiedName!,
    ["id"] = "NestedGenericRecord",
    ["name"] = "Nested generic type"
});
WritePackage(catalogArchivePath,
    ("Levels/Level_Arena.json", levelJson),
    ("Items/Item_CrystalBlank.json", itemJson),
    ("Types/BaseSurface.json", apiTypeJson),
    ("Types/NestedGeneric.json", nestedGenericJson),
    ("Broken/Invalid.json", "{"));
var catalogArchiveBytes = await File.ReadAllBytesAsync(catalogArchivePath);

var discoverySteamRoot = Path.Combine(directory, "discovery-steam");
var discoverySteamLibrary = Path.Combine(directory, "discovery-library");
var discoverySteamGame = Path.Combine(discoverySteamLibrary, "steamapps", "common", "Blade & Sorcery");
Directory.CreateDirectory(Path.Combine(discoverySteamRoot, "steamapps"));
Directory.CreateDirectory(discoverySteamGame);
var discoverySteamExecutable = Path.Combine(discoverySteamGame, "BladeAndSorcery.exe");
await File.WriteAllTextAsync(discoverySteamExecutable, "");
var escapedSteamRoot = discoverySteamRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
var escapedSteamLibrary = discoverySteamLibrary.Replace("\\", "\\\\", StringComparison.Ordinal);
await File.WriteAllTextAsync(
    Path.Combine(discoverySteamRoot, "steamapps", "libraryfolders.vdf"),
    $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{escapedSteamRoot}\" }} \"1\" {{ \"path\" \"{escapedSteamLibrary}\" }} }}");
await File.WriteAllTextAsync(
    Path.Combine(discoverySteamLibrary, "steamapps", "appmanifest_123.acf"),
    "\"AppState\" { \"name\" \"Blade & Sorcery\" \"installdir\" \"Blade & Sorcery\" }");
var discoveryMetaLibrary = Path.Combine(directory, "discovery-meta");
var discoveryMetaGame = Path.Combine(discoveryMetaLibrary, "Software", "warpfrog-blade-sorcery");
Directory.CreateDirectory(discoveryMetaGame);
var discoveryMetaExecutable = Path.Combine(discoveryMetaGame, "BladeAndSorcery.exe");
await File.WriteAllTextAsync(discoveryMetaExecutable, "");
var discoveredInstallations = GameInstallConfiguration.DiscoverInstallationsFromRoots(
    new[] { discoverySteamRoot }, new[] { discoveryMetaLibrary });
Assert(discoveredInstallations.Count == 2, "Steam and Meta library roots discover both installations");
var discoveredSteam = discoveredInstallations.Single(installation => installation.Store == "Steam");
var discoveredMeta = discoveredInstallations.Single(installation => installation.Store == "Meta Horizon Link");
Assert(discoveredSteam.ExecutablePath == Path.GetFullPath(discoverySteamExecutable) &&
       discoveredMeta.ExecutablePath == Path.GetFullPath(discoveryMetaExecutable),
    "store discovery returns the exact executable paths");
var selectedInstall = GameInstallConfiguration.ResolveUniqueInstallation(new[] { discoveredSteam }, launchConfigPath);
Assert(selectedInstall.ExecutablePath == discoveredSteam.ExecutablePath && selectedInstall.Arguments.Length == 0,
    "a unique discovered installation is selected with no launch arguments");
var noInstallRejected = false;
try { GameInstallConfiguration.ResolveUniqueInstallation(Array.Empty<GameInstallation>(), launchConfigPath); }
catch (InvalidOperationException exception) { noInstallRejected = exception.Message.Contains("No Blade & Sorcery installation", StringComparison.Ordinal); }
Assert(noInstallRejected, "no installation produces actionable launch configuration guidance");
var multipleInstallsRejected = false;
try { GameInstallConfiguration.ResolveUniqueInstallation(discoveredInstallations, launchConfigPath); }
catch (InvalidOperationException exception)
{
    multipleInstallsRejected = exception.Message.Contains(discoveredSteam.ExecutablePath, StringComparison.Ordinal) &&
        exception.Message.Contains(discoveredMeta.ExecutablePath, StringComparison.Ordinal);
}
Assert(multipleInstallsRejected, "multiple installations produce an ambiguity error listing both executable paths");
var apiFixtureModDirectory = Path.Combine(gameDirectory, "BladeAndSorcery_Data", "StreamingAssets", "Mods", "FixtureMod");
Directory.CreateDirectory(apiFixtureModDirectory);
var fixtureOutputDirectory = Path.Combine(AppContext.BaseDirectory, "api-fixtures");
var apiGameSharedPath = Path.Combine(managedDirectory, "Fixture.Shared.dll");
var apiModSharedPath = Path.Combine(apiFixtureModDirectory, "Fixture.Shared.dll");
var apiGameBasePath = Path.Combine(managedDirectory, "Fixture.Base.dll");
File.Copy(Path.Combine(fixtureOutputDirectory, "game", "Fixture.Shared.dll"), apiGameSharedPath);
File.Copy(Path.Combine(fixtureOutputDirectory, "mod", "Fixture.Shared.dll"), apiModSharedPath);
await File.WriteAllTextAsync(Path.ChangeExtension(apiGameSharedPath, ".xml"),
    """<doc><members><member name="T:Fixture.Shared.SharedSurface"><summary>STALE_XML_SIDECAR_TEXT</summary></member></members></doc>""");
File.Copy(Path.Combine(fixtureOutputDirectory, "game", "Fixture.Base.dll"), apiGameBasePath);
await File.WriteAllTextAsync(Path.Combine(managedDirectory, "Bad.dll"), "not a managed PE image");
await File.WriteAllBytesAsync(Path.Combine(managedDirectory, "Native.dll"), [0x4d, 0x5a, 0, 0]);
var fixtureSentinelPath = Path.Combine(directory, "fixture-static-constructor-ran.txt");
var changedFixturePath = Path.Combine(fixtureOutputDirectory, "changed", "Fixture.Shared.dll");
var changedAssemblyPath = Path.Combine(directory, "changed-fixture.dll");
File.Copy(changedFixturePath, changedAssemblyPath);
var missingDependencyPath = Path.Combine(directory, "temporarily-missing-Fixture.Base.dll");
File.Move(apiGameBasePath, missingDependencyPath);
var packagePath = Path.Combine(directory, "valid-package.zip");
WritePackage(packagePath,
    ("ThunderRoadMcp.Bridge.dll", "bridge"),
    ("manifest.json", "{\"Name\":\"ThunderRoadMcp\"}"),
    ("runtime/ThunderRoadMcp.Compiler.dll.payload", "compiler"));
var maliciousGameDirectory = Path.Combine(directory, "malicious-game");
Directory.CreateDirectory(maliciousGameDirectory);
var maliciousExecutablePath = Path.Combine(maliciousGameDirectory, "BladeAndSorcery.exe");
await File.WriteAllTextAsync(maliciousExecutablePath, "");
var cliGameDirectory = Path.Combine(directory, "cli-game");
Directory.CreateDirectory(cliGameDirectory);
var cliExecutablePath = Path.Combine(cliGameDirectory, "BladeAndSorcery.exe");
await File.WriteAllTextAsync(cliExecutablePath, "");
var maliciousPackagePath = Path.Combine(directory, "malicious-package.zip");
WritePackage(maliciousPackagePath,
    ("ThunderRoadMcp.Bridge.dll", "bridge"),
    ("manifest.json", "{\"Name\":\"ThunderRoadMcp\"}"),
    ("runtime/ThunderRoadMcp.Compiler.dll.payload", "compiler"),
    ("../outside.txt", "escape"),
    ("unexpected.txt", "unexpected"));
var duplicatePackagePath = Path.Combine(directory, "duplicate-package.zip");
WritePackage(duplicatePackagePath,
    ("ThunderRoadMcp.Bridge.dll", "bridge"),
    ("ThunderRoadMcp.Bridge.dll", "duplicate"),
    ("manifest.json", "{\"Name\":\"ThunderRoadMcp\"}"),
    ("runtime/ThunderRoadMcp.Compiler.dll.payload", "compiler"));
var bridgeRequests = 0;
var delayNextLevelRequest = 0;
var reloadAlreadyCurrentNext = 0;
var sameLevelReloadPhase = 0;
var oldBridgeStatus = false;
var oldBridgeRuntime = false;
var loadedAssemblyFixture = new[]
{
    new
    {
        name = "Fixture.Mod",
        version = "1.0.0.0",
        moduleVersionId = "00000000-0000-0000-0000-000000000001",
        origin = "mod",
        relativePath = "Fixture.Mod.dll",
    },
};
var server = Task.Run(async () =>
{
    while (!cancellation.IsCancellationRequested)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(cancellation.Token); }
        catch (OperationCanceledException) { break; }
        catch (SocketException) { break; }
        _ = Task.Run(() => HandleRequestAsync(client, cancellation.Token));
    }
});

var start = new ProcessStartInfo("dotnet")
{
    UseShellExecute = false,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
};
start.ArgumentList.Add(Path.GetFullPath(args[0]));
start.Environment["THUNDERROAD_MCP_CONFIG"] = configPath;
start.Environment["THUNDERROAD_MCP_LOG_DIR"] = logDirectory;
start.Environment["THUNDERROAD_MCP_LAUNCH_CONFIG"] = launchConfigPath;
start.Environment["THUNDERROAD_MCP_FIXTURE_SENTINEL"] = fixtureSentinelPath;
start.Environment["THUNDERROAD_MCP_CACHE_DIR"] = apiCacheDirectory;
start.Environment["THUNDERROAD_MCP_DIAGNOSTICS"] = "1";
using var host = Process.Start(start) ?? throw new InvalidOperationException("Host process did not start.");
var apiDiagnosticLines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
{
    SingleReader = true,
    SingleWriter = false,
});
host.ErrorDataReceived += (_, eventArgs) =>
{
    if (eventArgs.Data is { } line)
        apiDiagnosticLines.Writer.TryWrite(line);
};
host.BeginErrorReadLine();
async Task<JsonDocument> NextApiDiagnosticsAsync()
{
    while (true)
    {
        var line = await apiDiagnosticLines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("counts", out _))
                return document;
            document.Dispose();
        }
        catch (JsonException) { }
    }
}
var writer = host.StandardInput;
var reader = host.StandardOutput;
var requestId = 0;

try
{
    var helpCommand = await RunHostCliAsync("--help");
    Assert(helpCommand.ExitCode == 0 && helpCommand.StandardError.Length == 0,
        "host CLI help exits without starting MCP");
    var listCommand = await RunHostCliAsync("--list-installations");
    Assert(listCommand.ExitCode == 0 && listCommand.StandardError.Length == 0,
        "host discovery CLI exits without MCP logs");
    using (var listed = JsonDocument.Parse(listCommand.StandardOutput))
        Assert(listed.RootElement.TryGetProperty("launchConfigPath", out _) &&
               listed.RootElement.GetProperty("installations").ValueKind == JsonValueKind.Array,
            "host discovery CLI returns the MCP installation envelope");
    var launchConfigBeforeCli = await File.ReadAllTextAsync(launchConfigPath);
    var installCommand = await RunHostCliAsync("--install-bridge", packagePath, "--game-executable", cliExecutablePath);
    var cliInstallDirectory = Path.Combine(cliGameDirectory, "BladeAndSorcery_Data", "StreamingAssets", "Mods", "ThunderRoadMcp");
    Assert(installCommand.ExitCode == 0 && installCommand.StandardError.Length == 0 &&
           File.Exists(Path.Combine(cliInstallDirectory, "ThunderRoadMcp.Bridge.dll")) &&
           File.Exists(Path.Combine(cliInstallDirectory, "runtime", "ThunderRoadMcp.Compiler.dll.payload")),
        "host CLI safely installs the validated bridge package at the explicit game path");
    Assert(await File.ReadAllTextAsync(launchConfigPath) == launchConfigBeforeCli,
        "host CLI installer does not write or change launch.json");
    using var init = await CallAsync("initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "HostBehaviorCheck", version = "1" } });
    var initResult = init.RootElement.GetProperty("result");
    var serverInfo = initResult.GetProperty("serverInfo");
    Assert(serverInfo.GetProperty("name").GetString() == "ThunderRoadMcp",
        "initialize identifies the host implementation");
    await ApiReuseCheck.RunAsync(ApiToolJsonAsync, Assert, new ApiReuseContext(
        Path.GetFullPath(args[0]), configPath, launchConfigPath, logDirectory, apiCacheDirectory,
        executablePath, managedDirectory, apiGameSharedPath, apiModSharedPath, apiGameBasePath,
        missingDependencyPath, changedFixturePath, fixtureOutputDirectory, directory, NextApiDiagnosticsAsync));
    await CheckApiToolsAsync();
    await CheckCatalogToolsAsync();
    await ApiSignatureCheck.RunAsync(ApiToolJsonAsync, Assert);
    await CatalogReuseCheck.RunAsync();
    using (var apiResultDocument = await ToolAsync("api_assemblies", new { offset = 0, limit = 1 }))
    {
        var result = apiResultDocument.RootElement.GetProperty("result");
        var hasTimings = result.TryGetProperty("_meta", out var metadata) &&
            metadata.TryGetProperty("timings", out _);
        Assert(!hasTimings && result.GetProperty("content").GetArrayLength() == 1 &&
               result.GetProperty("content")[0].GetProperty("text").GetString() == result.GetProperty("structuredContent").GetRawText(),
            "offline API responses keep structured JSON and one domain text without detailed timing blocks");
    }
    using (var bridgeStatus = await ToolAsync("game_status", new { }))
    {
        var result = bridgeStatus.RootElement.GetProperty("result");
        var hasTimings = result.TryGetProperty("_meta", out var metadata) &&
            metadata.TryGetProperty("timings", out _);
        using var domainText = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert(!hasTimings && result.GetProperty("content").GetArrayLength() == 1 &&
               result.GetProperty("structuredContent").GetProperty("currentLevel").GetString() ==
               domainText.RootElement.GetProperty("currentLevel").GetString(),
            "bridge responses keep the domain result without detailed timing blocks");
    }
    using (var loadedStatus = await ApiToolJsonAsync("game_status", new { }))
    {
        var status = loadedStatus.RootElement;
        Assert(status.GetProperty("bridgeReady").GetBoolean() &&
               status.GetProperty("currentLevel").GetString() == "Arena" &&
               status.GetProperty("levelState").GetString() == "Loaded" &&
               status.GetProperty("sceneReady").GetBoolean(),
            "game_status reports bridge and level readiness");
        Assert(status.GetProperty("status").GetProperty("currentLevel").GetString() == "Arena" &&
               !status.GetProperty("status").TryGetProperty("loadedAssemblies", out _),
            "game_status retains raw bridge fields while keeping inventory compact by default");
        using (var inventoryStatus = await ApiToolJsonAsync("game_status", new { includeAssemblies = true }))
        {
            var inventory = inventoryStatus.RootElement.GetProperty("status");
            Assert(inventory.GetProperty("loadedAssemblies").GetArrayLength() == 1 &&
                   inventory.GetProperty("loadedAssemblies")[0].GetProperty("moduleVersionId").GetString() == "00000000-0000-0000-0000-000000000001" &&
                   inventory.GetProperty("loadedAssemblyCount").GetInt32() == 1 &&
                   !inventory.GetProperty("loadedAssembliesTruncated").GetBoolean(),
                "game_status explicitly requests loaded assembly inventory and preserves MVID metadata");
        }
    }
    oldBridgeStatus = true;
    using (var oldStatus = await ApiToolJsonAsync("game_status", new { }))
    {
        var status = oldStatus.RootElement;
        var rawStatus = status.GetProperty("status");
        Assert(status.GetProperty("currentLevel").ValueKind == JsonValueKind.Null &&
               status.GetProperty("levelState").ValueKind == JsonValueKind.Null &&
               status.GetProperty("sceneReady").ValueKind == JsonValueKind.Null &&
               !rawStatus.TryGetProperty("loadedAssemblies", out _) &&
               rawStatus.GetProperty("loadedAssemblyCount").GetInt32() == 1,
            "older bridge inventory is stripped while its other raw fields remain");
    }
    oldBridgeStatus = false;

    using (var waitReady = await ApiToolJsonAsync("game_wait_ready", new { level = "arena" }))
    {
        var waitRoot = waitReady.RootElement;
        Assert(waitRoot.GetProperty("ready").GetBoolean() &&
               !waitRoot.GetProperty("timedOut").GetBoolean() &&
               waitRoot.GetProperty("status").GetProperty("currentLevel").GetString() == "Arena",
            "game_wait_ready reports readiness and matches the level case-insensitively");
        var rawStatus = waitRoot.GetProperty("status").GetProperty("status");
        Assert(!rawStatus.TryGetProperty("loadedAssemblies", out _),
            "game_wait_ready keeps its nested bridge status compact");
    }
    using (var waitTimedOut = await ApiToolJsonAsync("game_wait_ready", new { level = "Missing", timeoutMs = 600 }))
    {
        var waitRoot = waitTimedOut.RootElement;
        Assert(!waitRoot.GetProperty("ready").GetBoolean() &&
               waitRoot.GetProperty("timedOut").GetBoolean() &&
               waitRoot.GetProperty("elapsedMs").GetInt64() >= 0 &&
               waitRoot.GetProperty("status").GetProperty("bridgeConnected").GetBoolean(),
            "game_wait_ready returns a timed-out result with the last status");
    }
    Assert(IsError(await ToolAsync("game_wait_ready", new { timeoutMs = 0 })), "game_wait_ready enforces timeout bounds");

    using (var loadedLevel = await ApiToolJsonAsync("game_load_level", new { levelId = "Arena", mode = "Sandbox" }))
    {
        var levelRoot = loadedLevel.RootElement;
        Assert(levelRoot.GetProperty("levelId").GetString() == "Arena" &&
               levelRoot.GetProperty("mode").GetString() == "Sandbox" &&
               levelRoot.GetProperty("currentLevelBefore").GetString() == "Home",
            "game_load_level routes the requested level and mode through the bridge");
    }
    Interlocked.Exchange(ref delayNextLevelRequest, 1);
    using (var waitedLevel = await ApiToolJsonAsync("game_load_level", new { levelId = "Arena", mode = "Sandbox", waitUntilReady = true, timeoutMs = 2000 }))
    {
        var waitedRoot = waitedLevel.RootElement;
        Assert(waitedRoot.GetProperty("levelId").GetString() == "Arena" &&
               waitedRoot.GetProperty("mode").GetString() == "Sandbox" &&
               waitedRoot.GetProperty("currentLevelBefore").GetString() == "Home" &&
               waitedRoot.GetProperty("levelStateBefore").GetString() == "Loaded" &&
               waitedRoot.GetProperty("sceneReadyBefore").GetBoolean() &&
               waitedRoot.GetProperty("requestAcknowledged").GetBoolean() &&
               waitedRoot.GetProperty("completed").GetBoolean() &&
               !waitedRoot.GetProperty("timedOut").GetBoolean() &&
               waitedRoot.GetProperty("elapsedMs").GetInt64() >= 90,
            "game_load_level waits for readiness and measures elapsed time from before the delayed load request");
    }
    using (var timedOutLevel = await ApiToolJsonAsync("game_load_level", new { levelId = "Missing", mode = "Sandbox", waitUntilReady = true, timeoutMs = 100 }))
        Assert(timedOutLevel.RootElement.GetProperty("requestAcknowledged").GetBoolean() &&
               timedOutLevel.RootElement.GetProperty("currentLevelBefore").GetString() == "Home" &&
               !timedOutLevel.RootElement.GetProperty("completed").GetBoolean() &&
               timedOutLevel.RootElement.GetProperty("timedOut").GetBoolean() &&
               timedOutLevel.RootElement.GetProperty("elapsedMs").GetInt64() >= 100,
            "game_load_level reports an acknowledged readiness timeout with its pre-request snapshot");
    Interlocked.Exchange(ref delayNextLevelRequest, 1);
    using (var unacknowledgedLevel = await ApiToolJsonAsync("game_load_level", new { levelId = "Arena", mode = "Sandbox", waitUntilReady = true, timeoutMs = 40 }))
    {
        var unacknowledgedRoot = unacknowledgedLevel.RootElement;
        Assert(!unacknowledgedRoot.GetProperty("requestAcknowledged").GetBoolean() &&
               !unacknowledgedRoot.GetProperty("completed").GetBoolean() &&
               unacknowledgedRoot.GetProperty("timedOut").GetBoolean() &&
               unacknowledgedRoot.GetProperty("elapsedMs").GetInt64() >= 30 &&
               !unacknowledgedRoot.TryGetProperty("currentLevelBefore", out _) &&
               !unacknowledgedRoot.TryGetProperty("levelStateBefore", out _) &&
               !unacknowledgedRoot.TryGetProperty("sceneReadyBefore", out _),
            "game_load_level reports a total-deadline timeout before acknowledgement without inventing a snapshot");
    }
    await Task.Delay(120);
    Interlocked.Exchange(ref reloadAlreadyCurrentNext, 1);
    using (var sameLevelReload = await ApiToolJsonAsync("game_load_level", new { levelId = "Arena", mode = "Sandbox", waitUntilReady = true, timeoutMs = 3000 }))
        Assert(sameLevelReload.RootElement.GetProperty("currentLevelBefore").GetString() == "Arena" &&
               sameLevelReload.RootElement.GetProperty("requestAcknowledged").GetBoolean() &&
               sameLevelReload.RootElement.GetProperty("completed").GetBoolean() &&
               sameLevelReload.RootElement.GetProperty("elapsedMs").GetInt64() >= 850,
            "same-level reload waits for an observed transition before treating the pre-request ready state as completion");
    Assert(IsError(await ToolAsync("game_load_level", new { levelId = "Arena", mode = "Sandbox", waitUntilReady = true, timeoutMs = 0 })),
        "game_load_level validates the optional readiness timeout before requesting a load");
    Assert(IsError(await ToolAsync("game_load_level", new { levelId = "", mode = "Sandbox" })), "game_load_level validates the level id");

    using (var spawnedItem = await ApiToolJsonAsync("game_spawn_item", new { itemId = "CrystalBlank", key = "test-item", disallowDespawn = false }))
    {
        var spawnRoot = spawnedItem.RootElement;
        Assert(spawnRoot.GetProperty("key").GetString() == "test-item" &&
               spawnRoot.GetProperty("itemId").GetString() == "CrystalBlank" &&
               spawnRoot.GetProperty("instanceId").GetInt32() == 42 &&
               spawnRoot.GetProperty("name").GetString() == "Test Item" &&
               !spawnRoot.GetProperty("disallowDespawn").GetBoolean(),
            "game_spawn_item returns the runtime key and spawned item details");
    }
    using (var generatedItem = await ApiToolJsonAsync("game_spawn_item", new { itemId = "CrystalBlank", x = 1, y = 2, z = 3 }))
        Assert(generatedItem.RootElement.GetProperty("key").GetString() == "item-generated" &&
               generatedItem.RootElement.GetProperty("disallowDespawn").GetBoolean(),
            "game_spawn_item accepts explicit coordinates, a generated key, and the default despawn policy");
    Assert(IsError(await ToolAsync("game_spawn_item", new { itemId = "CrystalBlank", x = 1 })), "game_spawn_item requires all coordinates");
    Assert(IsError(await ToolAsync("game_spawn_item", new { itemId = "" })), "game_spawn_item validates the item id");

    using (var listedRuntime = await ApiToolJsonAsync("game_runtime", new { action = "list" }))
        Assert(listedRuntime.RootElement.GetProperty("coroutineCount").GetInt32() == 1 &&
               listedRuntime.RootElement.GetProperty("objects").GetArrayLength() == 1,
            "game_runtime lists bridge-owned coroutines and objects");
    using (var stoppedRuntime = await ApiToolJsonAsync("game_runtime", new { action = "stop", key = "demo" }))
        Assert(stoppedRuntime.RootElement.GetProperty("stopped").GetBoolean(),
            "game_runtime stops a managed coroutine by key");
    using (var clearedRuntime = await ApiToolJsonAsync("game_runtime", new { action = "clear" }))
        Assert(clearedRuntime.RootElement.GetProperty("cleared").GetBoolean(),
            "game_runtime clears bridge-owned state");
    using (var inspectedRuntime = await ApiToolJsonAsync("game_runtime", new { action = "inspect", key = "stored" }))
        Assert(inspectedRuntime.RootElement.GetProperty("found").GetBoolean() &&
               inspectedRuntime.RootElement.GetProperty("value").GetProperty("kind").GetString() == "integer",
            "game_runtime inspect forwards a bounded bridge DTO");
    oldBridgeRuntime = true;
    var legacyInspect = await ToolAsync("game_runtime", new { action = "inspect", key = "stored" });
    Assert(IsError(legacyInspect) &&
           ErrorText(legacyInspect).Contains("does not support runtime inspection", StringComparison.Ordinal) &&
           ErrorText(legacyInspect).Contains("restart", StringComparison.OrdinalIgnoreCase),
        "legacy bridge inspect errors become actionable bridge upgrade guidance");
    oldBridgeRuntime = false;
    Assert(IsError(await ToolAsync("game_runtime", new { action = "stop" })), "game_runtime stop requires a key");
    Assert(IsError(await ToolAsync("game_runtime", new { action = "bogus" })), "game_runtime validates the action");

    var installResult = await ToolAsync("game_install_mod", new { packagePath });
    Assert(!IsError(installResult), "valid package installs successfully");
    var modsDirectory = Path.Combine(gameDirectory, "BladeAndSorcery_Data", "StreamingAssets", "Mods");
    var installedDirectory = Path.Combine(modsDirectory, "ThunderRoadMcp");
    Assert(File.Exists(Path.Combine(installedDirectory, "ThunderRoadMcp.Bridge.dll")), "installed package contains the bridge");
    Assert(File.Exists(Path.Combine(installedDirectory, "runtime", "ThunderRoadMcp.Compiler.dll.payload")), "installed package contains runtime payload");
    await File.WriteAllTextAsync(Path.Combine(installedDirectory, "user-data.txt"), "preserve");
    var occupiedInstall = await ToolAsync("game_install_mod", new { packagePath });
    Assert(IsError(occupiedInstall), "occupied mod destination is refused");
    Assert(await File.ReadAllTextAsync(Path.Combine(installedDirectory, "user-data.txt")) == "preserve", "occupied destination contents are preserved");

    await WriteLaunchConfigAsync(maliciousExecutablePath);
    var maliciousInstall = await ToolAsync("game_install_mod", new { packagePath = maliciousPackagePath });
    Assert(IsError(maliciousInstall), "unexpected and traversal archive paths are rejected");
    var maliciousModsDirectory = Path.Combine(maliciousGameDirectory, "BladeAndSorcery_Data", "StreamingAssets", "Mods");
    Assert(!Directory.Exists(maliciousModsDirectory), "rejected package creates no install destination");
    Assert(!File.Exists(Path.Combine(maliciousGameDirectory, "BladeAndSorcery_Data", "StreamingAssets", "outside.txt")), "traversal entry is not written outside staging");
    var duplicateInstall = await ToolAsync("game_install_mod", new { packagePath = duplicatePackagePath });
    Assert(IsError(duplicateInstall), "duplicate archive paths are rejected");
    Assert(!Directory.Exists(maliciousModsDirectory), "duplicate package creates no install destination");

    var oversized = await ToolAsync("game_logs", new { source = "bridge", since = 99L });
    Assert(IsError(oversized), "oversized bridge response becomes an MCP tool error");

    var diagnostic = await ToolAsync("game_logs", new { source = "bridge", since = 97L });
    Assert(IsError(diagnostic), "bridge errors become MCP tool errors");
    Assert(ErrorText(diagnostic).Contains("REMOTE_DIAGNOSTIC_SENTINEL", StringComparison.Ordinal), "bridge diagnostic remains visible to the MCP caller");

    var mismatched = await ToolAsync("game_logs", new { source = "bridge", since = 98L });
    Assert(IsError(mismatched), "response ID mismatch is rejected");
    var currentLog = await ToolAsync("game_logs", new { source = "current", tailLines = 2 });
    Assert(!IsError(currentLog), "current Player.log is accessible without bridge config");
    using var currentText = JsonDocument.Parse(ErrorText(currentLog));
    Assert(currentText.RootElement.GetProperty("text").GetString() == "café\nlast", "UTF-8 decoding and line tail are correct");
    var previousLog = await ToolAsync("game_logs", new { source = "previous", tailLines = 2 });
    Assert(!IsError(previousLog), "previous Player log is accessible independently");
    using var previousText = JsonDocument.Parse(ErrorText(previousLog));
    Assert(previousText.RootElement.GetProperty("text").GetString() == "previous session", "previous source is explicit");
    Assert(IsError(await ToolAsync("game_logs", new { source = "previous", tailLines = 0 })), "tail line bounds are enforced");
    Assert(IsError(await ToolAsync("game_logs", new { source = "unknown" })), "unsupported source is rejected");
    var logPath = Path.Combine(logDirectory, "Player.log");
    await File.WriteAllTextAsync(logPath, "one\ntwo\nthree\n", new UTF8Encoding(false));
    using var shortFirst = JsonDocument.Parse(ErrorText(await ToolAsync("game_logs", new { source = "current", tailLines = 1 })));
    var shortCursor = shortFirst.RootElement.GetProperty("nextBeforeByteOffset").GetInt64();
    var shortSnapshot = shortFirst.RootElement.GetProperty("snapshotId").GetString();
    await File.AppendAllTextAsync(logPath, "four\n");
    using var shortOlder = JsonDocument.Parse(ErrorText(await ToolAsync("game_logs", new { source = "current", tailLines = 1, beforeByteOffset = shortCursor, snapshotId = shortSnapshot })));
    Assert(shortOlder.RootElement.GetProperty("text").GetString() == "two", "append preserves short-log pagination snapshot");
    await File.WriteAllTextAsync(logPath, "single\n");
    var singleLine = await ToolAsync("game_logs", new { source = "current", tailLines = 1 });
    using var singleText = JsonDocument.Parse(ErrorText(singleLine));
    Assert(singleText.RootElement.GetProperty("text").GetString() == "single", "newline-terminated one-line tail is preserved");
    var pageLines = string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"line-{i:D4}|" + new string('q', 55))) + "\n";
    await File.WriteAllTextAsync(logPath, pageLines, new UTF8Encoding(false));
    var firstPage = await ToolAsync("game_logs", new { source = "current", tailLines = 2000 });
    using var first = JsonDocument.Parse(ErrorText(firstPage));
    var firstRoot = first.RootElement;
    var firstText = firstRoot.GetProperty("text").GetString()!;
    var firstLines = firstText.Split('\n');
    Assert(firstLines[^1] == "line-2999|" + new string('q', 55), "first page includes newest line");
    Assert(firstRoot.GetProperty("nextBeforeByteOffset").GetInt64() < firstRoot.GetProperty("endByteOffset").GetInt64(), "every pagination cursor moves backwards");
    var cursor = firstRoot.GetProperty("nextBeforeByteOffset").GetInt64();
    var snapshot = firstRoot.GetProperty("snapshotId").GetString();
    await File.AppendAllTextAsync(logPath, "appended while game is running\n");
    var secondPage = await ToolAsync("game_logs", new { source = "current", tailLines = 2000, beforeByteOffset = cursor, snapshotId = snapshot });
    using var second = JsonDocument.Parse(ErrorText(secondPage));
    var secondRoot = second.RootElement;
    var secondLines = secondRoot.GetProperty("text").GetString()!.Split('\n');
    Assert(secondLines[0] == "line-0000|" + new string('q', 55), "older page reaches the beginning of the file after an append");
    Assert(int.Parse(secondLines[^1].AsSpan(5, 4)) + 1 == int.Parse(firstLines[0].AsSpan(5, 4)), "adjacent pages meet without skipped or duplicated lines");
    Assert(secondRoot.GetProperty("endByteOffset").GetInt64() == firstRoot.GetProperty("startByteOffset").GetInt64(), "page byte offsets are contiguous");
    Assert(secondRoot.GetProperty("nextBeforeByteOffset").ValueKind == JsonValueKind.Null, "page at file start has no further cursor");
    Assert(!secondRoot.GetProperty("text").GetString()!.Contains("appended while game is running", StringComparison.Ordinal), "appended content does not enter the older page");
    await File.WriteAllTextAsync(logPath, "rotated session\n" + pageLines);
    Assert(IsError(await ToolAsync("game_logs", new { source = "current", beforeByteOffset = cursor, snapshotId = snapshot })), "rotated file invalidates its snapshot cursor");

    await File.WriteAllTextAsync(logPath, "a\n\n");
    using var blankPage = JsonDocument.Parse(ErrorText(await ToolAsync("game_logs", new { source = "current", tailLines = 1 })));
    Assert(blankPage.RootElement.GetProperty("text").GetString() == "", "trailing blank line is retained");

    var cutOnUtf8 = "界" + new string('x', 131066) + "\nend\n";
    await File.WriteAllTextAsync(logPath, cutOnUtf8, new UTF8Encoding(false));
    using var utf8Page = JsonDocument.Parse(ErrorText(await ToolAsync("game_logs", new { source = "current", tailLines = 1 })));
    Assert(utf8Page.RootElement.GetProperty("text").GetString() == "end", "cap beginning inside a multibyte character does not emit replacement characters");
    await File.WriteAllTextAsync(logPath, "start\n" + new string('a', 131070) + "界\nend\n", new UTF8Encoding(false));
    var partialCharacterOffset = 6 + 131070 + 2;
    using var utf8Snapshot = JsonDocument.Parse(ErrorText(await ToolAsync("game_logs", new { source = "current", tailLines = 1 })));
    var utf8SnapshotId = utf8Snapshot.RootElement.GetProperty("snapshotId").GetString();
    using var partialEnd = JsonDocument.Parse(ErrorText(await ToolAsync("game_logs", new { source = "current", tailLines = 1, beforeByteOffset = partialCharacterOffset, snapshotId = utf8SnapshotId })));
    Assert(partialEnd.RootElement.GetProperty("text").GetString() == new string('a', 131070), "page ending inside a UTF-8 character omits the incomplete character");

    await File.WriteAllTextAsync(logPath, new string('界', 50000), new UTF8Encoding(false));
    using var longLine = JsonDocument.Parse(ErrorText(await ToolAsync("game_logs", new { source = "current", tailLines = 1 })));
    Assert(longLine.RootElement.GetProperty("partialFirstLine").GetBoolean(), "oversized single line is identified as partial");
    Assert(longLine.RootElement.GetProperty("truncated").GetBoolean() && longLine.RootElement.GetProperty("text").GetString()!.Length > 0, "oversized single line returns marked partial content");
    Assert(!longLine.RootElement.GetProperty("text").GetString()!.Contains('\ufffd'), "partial single-line UTF-8 remains valid");
    await File.WriteAllTextAsync(logPath, new string('界', 50000) + "\n", new UTF8Encoding(false));
    using var terminatedLongLine = JsonDocument.Parse(ErrorText(await ToolAsync("game_logs", new { source = "current", tailLines = 1 })));
    Assert(terminatedLongLine.RootElement.GetProperty("partialFirstLine").GetBoolean(), "oversized newline-terminated line is marked partial");
    Assert(terminatedLongLine.RootElement.GetProperty("text").GetString()!.Length > 0, "oversized newline-terminated line returns bounded content");
    Assert(terminatedLongLine.RootElement.GetProperty("nextBeforeByteOffset").GetInt64() < terminatedLongLine.RootElement.GetProperty("endByteOffset").GetInt64(), "newline-terminated long-line cursor makes progress");

    using (var activeWriter = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
    {
        var activeRead = await ToolAsync("game_logs", new { source = "current", tailLines = 1 });
        Assert(!IsError(activeRead), "active writer sharing does not block log retrieval");
    }
    File.Delete(Path.Combine(logDirectory, "Player-prev.log"));
    Assert(IsError(await ToolAsync("game_logs", new { source = "previous" })), "missing previous file is an explicit error");

    Assert(IsError(await ToolAsync("game_stop", new { })), "stop without an owned process returns an MCP error");
    Assert(IsError(await ToolAsync("game_restart", new { })), "restart without an owned process returns an MCP error");

    var fakeGameExecutable = Path.Combine(gameDirectory, $"FakeGame-{Guid.NewGuid():N}.exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fakeGameExecutable);
    await WriteLaunchConfigAsync(fakeGameExecutable, new[] { "-t", "127.0.0.1" });
    Assert(IsError(await ToolAsync("game_launch", new { arguments = new[] { new string('x', 4097) } })),
        "oversized launch arguments are rejected");
    using (var configLaunch = await ApiToolJsonAsync("game_launch", new { }))
    {
        var launchArguments = configLaunch.RootElement.GetProperty("arguments").EnumerateArray()
            .Select(argument => argument.GetString()).ToArray();
        Assert(launchArguments.SequenceEqual(new[] { "-t", "127.0.0.1" }),
            "game_launch uses launch.json arguments when the override is omitted");
    }
    using (var stopped = await ToolAsync("game_stop", new { force = true }))
    {
        var result = stopped.RootElement.GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert(!IsError(stopped) && text is { Length: > 0 } &&
               result.GetProperty("structuredContent").GetProperty("value").GetString() == text,
            "successful game_stop preserves its text and structured scalar result");
    }
    var overrideArguments = new[] { "-n", "10", "127.0.0.1" };
    using (var overrideLaunch = await ApiToolJsonAsync("game_launch", new { arguments = overrideArguments }))
    {
        var launchArguments = overrideLaunch.RootElement.GetProperty("arguments").EnumerateArray()
            .Select(argument => argument.GetString()).ToArray();
        Assert(overrideLaunch.RootElement.GetProperty("gameProcessLaunched").GetBoolean() &&
               launchArguments.SequenceEqual(overrideArguments),
            "game_launch applies raw argument overrides");
    }
    using (var restarted = await ToolAsync("game_restart", new { force = true }))
    {
        var structured = restarted.RootElement.GetProperty("result").GetProperty("structuredContent");
        var restartArguments = structured.GetProperty("arguments").EnumerateArray()
            .Select(argument => argument.GetString()).ToArray();
        Assert(!IsError(restarted) &&
               structured.GetProperty("gameProcessLaunched").GetBoolean() &&
               restartArguments.SequenceEqual(overrideArguments),
            "game_restart preserves the arguments used by the current launch");
    }
    Assert(!IsError(await ToolAsync("game_stop", new { force = true })), "restarted fixture process stops cleanly");

    File.Delete(configPath);
    await WriteLaunchConfigAsync(fakeGameExecutable, new[] { "-t", "127.0.0.1" });
    Assert(!IsError(await ToolAsync("game_launch", new { })), "game launch does not require bridge configuration");
    using (var offlineStatus = await ToolAsync("game_status", new { }))
    {
        var status = offlineStatus.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert(!IsError(offlineStatus) && !status.GetProperty("bridgeConnected").GetBoolean() &&
               status.GetProperty("gameProcessRunning").GetBoolean(),
            "status reports a running game with the bridge offline");
    }
    Assert(!IsError(await ToolAsync("game_restart", new { force = true })), "game restart does not require bridge configuration");
    Assert(!IsError(await ToolAsync("game_stop", new { force = true })), "offline fixture process stops cleanly");
    await WriteConfigAsync(new string('a', 64));
    await WriteLaunchConfigAsync(executablePath);

    var requestsBeforeInvalidConfig = Volatile.Read(ref bridgeRequests);
    await WriteConfigAsync("bad");
    var invalidConfig = await ToolAsync("game_status", new { });
    Assert(!IsError(invalidConfig), "status reports bridge disconnection as status data");
    Assert(Volatile.Read(ref bridgeRequests) == requestsBeforeInvalidConfig, "invalid bridge config prevents network access");
    Console.WriteLine("Host boundary behavior check passed.");
}
finally
{
    if (!host.HasExited)
    {
        try { using var cleanup = await ToolAsync("game_stop", new { force = true }); }
        catch { }
        host.Kill(entireProcessTree: true);
        await host.WaitForExitAsync();
    }
    cancellation.Cancel();
    listener.Stop();
    await server;
    if (Environment.GetEnvironmentVariable("THUNDERROAD_MCP_KEEP_CHECK_DIR") == "1")
        Console.WriteLine("Kept check directory: " + directory);
    else
        Directory.Delete(directory, recursive: true);
}

async Task WriteConfigAsync(string token) =>
    await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { enabled = true, host = "127.0.0.1", port, token }));

async Task WriteLaunchConfigAsync(string path, string[]? arguments = null) =>
    await File.WriteAllTextAsync(launchConfigPath, JsonSerializer.Serialize(new { executablePath = path, arguments = arguments ?? new[] { "-level", "arena" } }));

static void WritePackage(string path, params (string EntryPath, string Content)[] entries)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (entryPath, content) in entries)
    {
        using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open());
        writer.Write(content);
    }
}
 

async Task<(int ExitCode, string StandardOutput, string StandardError)> RunHostCliAsync(params string[] command)
{
    var cli = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    cli.ArgumentList.Add(Path.GetFullPath(args[0]));
    foreach (var argument in command)
        cli.ArgumentList.Add(argument);
    using var process = Process.Start(cli) ?? throw new InvalidOperationException("Host CLI process did not start.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    return (process.ExitCode, await output, await error);
}

async Task<JsonDocument> CallAsync(string method, object parameters)
{
    var id = Interlocked.Increment(ref requestId);
    await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
    await writer.FlushAsync();
    var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    return JsonDocument.Parse(line ?? throw new EndOfStreamException("Host closed MCP stdout."));
}

Task<JsonDocument> ToolAsync(string name, object arguments) =>
    CallAsync("tools/call", new { name, arguments });

async Task<JsonDocument> ApiToolJsonAsync(string name, object arguments)
{
    using var response = await ToolAsync(name, arguments);
    Assert(!IsError(response), $"{name} succeeds: {ErrorText(response)}");
    var result = response.RootElement.GetProperty("result");
    var text = result.GetProperty("content")[0].GetProperty("text").GetString();
    Assert(text is not null && result.GetProperty("structuredContent").ValueKind == JsonValueKind.Object,
        $"{name} preserves text output and supplies structured object content");
    return JsonDocument.Parse(text!);
}

async Task CheckApiToolsAsync()
{
    using var list = await CallAsync("tools/list", new { });
    var toolNames = list.RootElement.GetProperty("result").GetProperty("tools")
        .EnumerateArray().Select(tool => tool.GetProperty("name").GetString() ?? "").ToHashSet(StringComparer.Ordinal);
    foreach (var toolName in new[] { "game_installations", "game_status", "game_guide", "game_launch", "game_wait_ready", "game_load_level", "game_spawn_item", "game_runtime", "game_install_mod", "game_stop", "game_restart", "game_logs", "evaluate_csharp", "api_assemblies", "api_search", "api_type", "api_decompile", "api_decompile_batch", "api_find_callers", "api_find_callees", "api_find_usages", "api_lookup", "catalog_search", "catalog_read" })
        Assert(toolNames.Contains(toolName), $"{toolName} is exposed through tools/list");
    var toolDefinitions = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
        .ToDictionary(tool => tool.GetProperty("name").GetString()!, tool => tool, StringComparer.Ordinal);
    var evaluateTool = toolDefinitions["evaluate_csharp"];
    var statusAnnotations = toolDefinitions["game_status"].GetProperty("annotations");
    var evaluateAnnotations = evaluateTool.GetProperty("annotations");
    Assert(statusAnnotations.GetProperty("readOnlyHint").GetBoolean() &&
           evaluateAnnotations.GetProperty("destructiveHint").GetBoolean() &&
           evaluateAnnotations.GetProperty("openWorldHint").GetBoolean(),
        "tools/list exposes accurate read-only and eval risk hints");
    Assert(IsReadOnlyTool(toolDefinitions["game_wait_ready"]) && IsReadOnlyTool(toolDefinitions["game_guide"]),
        "game_wait_ready and game_guide are read-only");
    Assert(IsReadOnlyTool(toolDefinitions["api_lookup"]), "api_lookup is read-only");
    Assert(!IsReadOnlyTool(toolDefinitions["game_load_level"]) &&
           !IsReadOnlyTool(toolDefinitions["game_spawn_item"]) &&
           !IsReadOnlyTool(toolDefinitions["game_runtime"]),
        "level, spawn, and runtime tools are not read-only");

    string? bootstrapGuideContent;
    using (var bootstrapGuide = await ApiToolJsonAsync("game_guide", new { }))
    {
        bootstrapGuideContent = bootstrapGuide.RootElement.GetProperty("content").GetString();
        Assert(bootstrapGuide.RootElement.GetProperty("topic").GetString() == "bootstrap" &&
               bootstrapGuideContent is { Length: > 200 },
            "game_guide defaults to the bootstrap section");
    }
    using (var overviewGuide = await ApiToolJsonAsync("game_guide", new { topic = "overview" }))
        Assert(overviewGuide.RootElement.GetProperty("topic").GetString() == "bootstrap",
            "game_guide accepts the overview alias");
    using (var unityGuide = await ApiToolJsonAsync("game_guide", new { topic = "unity" }))
    {
        var unityContent = unityGuide.RootElement.GetProperty("content").GetString();
        Assert(unityGuide.RootElement.GetProperty("topic").GetString() == "unity" &&
               unityContent is { Length: > 200 } &&
               unityContent != bootstrapGuideContent,
            "game_guide returns a distinct Unity section");
    }
    var unknownGuide = await ToolAsync("game_guide", new { topic = "not-a-topic" });
    Assert(IsError(unknownGuide) &&
           ErrorText(unknownGuide).Contains("bootstrap", StringComparison.Ordinal) &&
           ErrorText(unknownGuide).Contains("troubleshooting", StringComparison.Ordinal),
        "unknown game_guide topics list the available topics");
    Assert(evaluateTool.GetProperty("outputSchema").GetProperty("type").GetString() == "object",
        "tools/list exposes the structured result schema");
    using var installationResponse = await ToolAsync("game_installations", new { });
    using var installationText = JsonDocument.Parse(ErrorText(installationResponse));
    var installationStructure = installationResponse.RootElement.GetProperty("result").GetProperty("structuredContent");
    Assert(installationStructure.GetProperty("launchConfigPath").GetString() ==
           installationText.RootElement.GetProperty("launchConfigPath").GetString() &&
           installationStructure.GetProperty("installations").GetArrayLength() ==
           installationText.RootElement.GetProperty("installations").GetArrayLength(),
        "game_installations structured content matches its unchanged text JSON");
    using var discoveredGames = await ApiToolJsonAsync("game_installations", new { });
    var gameCandidates = discoveredGames.RootElement.GetProperty("installations").EnumerateArray().ToArray();
    Assert(gameCandidates.All(candidate =>
            candidate.GetProperty("store").GetString() is { Length: > 0 } &&
            candidate.GetProperty("executablePath").GetString() is { Length: > 0 } &&
            candidate.GetProperty("catalogArchivePath").GetString() is { Length: > 0 }),
        "game_installations reports store, executable, and catalog paths for every candidate");

    using var incompleteAssemblies = await ApiToolJsonAsync("api_assemblies", new { offset = 0, limit = 50 });
    Assert(incompleteAssemblies.RootElement.GetProperty("assemblies").GetArrayLength() >= 2,
        "invalid and native files do not block unrelated managed assembly discovery");
    var initialDiagnostics = incompleteAssemblies.RootElement.GetProperty("diagnostics").EnumerateArray()
        .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.ToString()).ToArray();
    Assert(initialDiagnostics.Any(item => item.Contains("Bad.dll", StringComparison.OrdinalIgnoreCase)), "invalid managed image is diagnosed");
    Assert(initialDiagnostics.Any(item => item.Contains("Native.dll", StringComparison.OrdinalIgnoreCase)), "native/non-managed image is diagnosed");

    using var gameSharedAssemblies = await ApiToolJsonAsync("api_assemblies", new { name = "Fixture.Shared", origin = "game", limit = 50 });
    using var modSharedAssemblies = await ApiToolJsonAsync("api_assemblies", new { name = "Fixture.Shared", origin = "mod", limit = 50 });
    var gameAssembly = gameSharedAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single();
    var modAssembly = modSharedAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single();
    var gameAssemblyId = gameAssembly.GetProperty("assemblyId").GetString()!;
    var modAssemblyId = modAssembly.GetProperty("assemblyId").GetString()!;
    Assert(gameAssemblyId != modAssemblyId, "same-named game and mod assemblies have distinct IDs");
    Assert(gameAssembly.GetProperty("origin").GetString() == "game" && modAssembly.GetProperty("origin").GetString() == "mod", "assembly origin filters distinguish game and mod");
    using (var fixtureStream = File.OpenRead(apiGameSharedPath))
    using (var fixturePe = new PEReader(fixtureStream))
    {
        var fixtureMetadata = fixturePe.GetMetadataReader();
        var fixtureMvid = fixtureMetadata.GetGuid(fixtureMetadata.GetModuleDefinition().Mvid).ToString("D");
        Assert(gameAssembly.GetProperty("moduleVersionId").GetString() == fixtureMvid,
            "installed moduleVersionId matches the PE fixture metadata");
    }
    Assert(!Path.IsPathRooted(gameAssembly.GetProperty("relativePath").GetString()!) &&
           !Path.IsPathRooted(modAssembly.GetProperty("relativePath").GetString()!), "assembly provenance paths are relative");

    using var missingDependencyType = await ApiToolJsonAsync("api_search", new { query = "DerivedSurface", kind = "type", assemblyId = modAssemblyId });
    var derivedHit = missingDependencyType.RootElement.GetProperty("results").EnumerateArray().Single();
    var derivedTypeId = derivedHit.GetProperty("symbolId").GetString()!;
    using var missingDependencySource = await ApiToolJsonAsync("api_decompile", new { assemblyId = modAssemblyId, symbolId = derivedTypeId, startLine = 1, lineCount = 40 });
    var missingWarnings = missingDependencySource.RootElement.GetProperty("warnings").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
    Assert(missingWarnings.Any(warning => warning.Contains("Fixture.Base", StringComparison.OrdinalIgnoreCase) ||
                                          warning.Contains("missing", StringComparison.OrdinalIgnoreCase)), "missing dependency is reported while type decompilation remains available");
    Assert(missingDependencySource.RootElement.GetProperty("source").GetString()!.Contains("DerivedSurface", StringComparison.Ordinal), "missing dependency does not block unrelated source discovery");
    File.Move(missingDependencyPath, apiGameBasePath);

    using var baseAssemblies = await ApiToolJsonAsync("api_assemblies", new { name = "Fixture.Base", origin = "game", limit = 50 });
    var baseAssemblyId = baseAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single().GetProperty("assemblyId").GetString()!;

    using var assemblyPage = await ApiToolJsonAsync("api_assemblies", new { offset = 0, limit = 1 });
    var assemblyPageRoot = assemblyPage.RootElement;
    Assert(assemblyPageRoot.GetProperty("total").GetInt32() >= 3 && assemblyPageRoot.GetProperty("hasMore").GetBoolean(), "assembly listing is paginated");
    var assemblySnapshotId = assemblyPageRoot.GetProperty("snapshotId").GetString()!;
    var nextAssemblyOffset = assemblyPageRoot.GetProperty("nextOffset").GetInt32();
    using var nextAssemblyPage = await ApiToolJsonAsync("api_assemblies", new { offset = nextAssemblyOffset, limit = 1, snapshotId = assemblySnapshotId });
    Assert(assemblyPageRoot.GetProperty("assemblies")[0].GetProperty("assemblyId").GetString() !=
           nextAssemblyPage.RootElement.GetProperty("assemblies")[0].GetProperty("assemblyId").GetString(), "assembly pages advance without duplicates");

    using var allAssemblies = await ApiToolJsonAsync("api_assemblies", new { offset = 0, limit = 50 });
    var fullAssemblyIds = allAssemblies.RootElement.GetProperty("assemblies").EnumerateArray()
        .Select(assembly => assembly.GetProperty("assemblyId").GetString()).ToArray();
    using var repeatedAssemblies = await ApiToolJsonAsync("api_assemblies", new { offset = 0, limit = 50 });
    Assert(fullAssemblyIds.SequenceEqual(repeatedAssemblies.RootElement.GetProperty("assemblies").EnumerateArray()
        .Select(assembly => assembly.GetProperty("assemblyId").GetString())), "assembly ordering is deterministic");

    using var sharedSearch = await ApiToolJsonAsync("api_search", new { query = "sHaReDsUrFaCe", kind = "type", includeNonPublic = false, offset = 0, limit = 10 });
    var sharedHits = sharedSearch.RootElement.GetProperty("results").EnumerateArray().ToArray();
    Assert(sharedHits.Length == 2 && sharedHits.Select(hit => hit.GetProperty("assemblyId").GetString()).Distinct().Count() == 2,
        "case-insensitive search disambiguates duplicate type names by assembly ID");
    var gameSharedHit = sharedHits.Single(hit => hit.GetProperty("assemblyId").GetString() == gameAssemblyId);
    var sharedTypeId = gameSharedHit.GetProperty("symbolId").GetString()!;
    Assert(sharedHits.All(hit => !hit.TryGetProperty("xmlDocumentation", out _)) &&
           !sharedSearch.RootElement.GetRawText().Contains("STALE_XML_SIDECAR_TEXT", StringComparison.Ordinal),
        "api_search omits XML sidecar fields and text even when a stale sidecar is present");
    using var sharedType = await ApiToolJsonAsync("api_type", new { assemblyId = gameAssemblyId, typeSymbolId = sharedTypeId });
    Assert(!sharedType.RootElement.TryGetProperty("xmlDocumentation", out _) &&
           sharedType.RootElement.GetProperty("members").EnumerateArray().All(member => !member.TryGetProperty("xmlDocumentation", out _)) &&
           !sharedType.RootElement.GetRawText().Contains("STALE_XML_SIDECAR_TEXT", StringComparison.Ordinal),
        "api_type omits XML sidecar fields and text");
    using var sharedSource = await ApiToolJsonAsync("api_decompile", new { assemblyId = gameAssemblyId, symbolId = sharedTypeId, startLine = 1, lineCount = 20 });
    Assert(!sharedSource.RootElement.TryGetProperty("xmlDocumentation", out _) &&
           !sharedSource.RootElement.GetRawText().Contains("STALE_XML_SIDECAR_TEXT", StringComparison.Ordinal),
        "api_decompile omits XML sidecar fields and text");
    using var modSharedSearch = await ApiToolJsonAsync("api_search", new { query = "SharedSurface", kind = "type", assemblyId = modAssemblyId });
    Assert(modSharedSearch.RootElement.GetProperty("results").GetArrayLength() == 1 &&
           modSharedSearch.RootElement.GetProperty("results")[0].GetProperty("assemblyId").GetString() == modAssemblyId,
        "assembly-filtered search selects only the requested mod");

    using var sharedSearchPage = await ApiToolJsonAsync("api_search", new { query = "SharedSurface", kind = "type", offset = 0, limit = 1 });
    Assert(sharedSearchPage.RootElement.GetProperty("total").GetInt32() == 2 && sharedSearchPage.RootElement.GetProperty("hasMore").GetBoolean(),
        "search result pagination reports remaining hits");
    var nextSearchOffset = sharedSearchPage.RootElement.GetProperty("nextOffset").GetInt32();
    using var nextSharedSearchPage = await ApiToolJsonAsync("api_search", new { query = "SharedSurface", kind = "type", offset = nextSearchOffset, limit = 1 });
    Assert(sharedSearchPage.RootElement.GetProperty("results")[0].GetProperty("assemblyId").GetString() !=
           nextSharedSearchPage.RootElement.GetProperty("results")[0].GetProperty("assemblyId").GetString(), "search pages expose both duplicate assemblies");

    using var overloadSearch = await ApiToolJsonAsync("api_search", new { query = "overload", assemblyId = modAssemblyId });
    var overloads = overloadSearch.RootElement.GetProperty("results").EnumerateArray().ToArray();
    Assert(overloads.Length == 2 && overloads.Select(hit => hit.GetProperty("symbolId").GetString()).Distinct().Count() == 2,
        "overloads have distinct symbol IDs");
    Assert(overloads.Select(hit => hit.GetProperty("signature").GetString()).Distinct().Count() == 2,
        "overload signatures are distinct");
    using var ambiguousLookup = await ApiToolJsonAsync("api_lookup", new { query = "overload", mode = "symbol", assemblyId = modAssemblyId, kind = "method" });
    var ambiguousRoot = ambiguousLookup.RootElement;
    var candidateIds = ambiguousRoot.GetProperty("candidates").EnumerateArray()
        .Select(candidate => candidate.GetProperty("symbolId").GetString()!).ToHashSet(StringComparer.Ordinal);
    Assert(ambiguousRoot.GetProperty("ambiguous").GetBoolean() &&
           ambiguousRoot.GetProperty("total").GetInt32() == 2 &&
           candidateIds.SetEquals(overloads.Select(hit => hit.GetProperty("symbolId").GetString()!)) &&
           ambiguousRoot.GetProperty("candidates").EnumerateArray().All(candidate =>
               candidate.GetProperty("signature").GetString() is { Length: > 0 }) &&
           !ambiguousRoot.TryGetProperty("source", out _),
        "ambiguous lookup returns exact overload candidates without choosing one");
    using var taskLookup = await ApiToolJsonAsync("api_lookup", new
    {
        query = "overload",
        assemblyId = modAssemblyId,
        kind = "method",
        maxResults = 1,
        includeExamples = false,
    });
    var taskLookupRoot = taskLookup.RootElement;
    var firstTaskResult = taskLookupRoot.GetProperty("results")[0];
    var firstTaskId = firstTaskResult.GetProperty("symbolId").GetString()!;
    var taskContinuation = taskLookupRoot.GetProperty("continuation").GetString()!;
    Assert(taskLookupRoot.GetProperty("mode").GetString() == "task" &&
           taskLookupRoot.GetProperty("hasMore").GetBoolean() &&
           taskLookupRoot.GetProperty("results").GetArrayLength() == 1 &&
           taskLookupRoot.GetProperty("logicalChars").GetInt32() <= 16000 &&
           !taskLookupRoot.TryGetProperty("source", out _),
        "default api_lookup ranks metadata without eager source and respects its logical response budget");
    using var nextTaskLookup = await ApiToolJsonAsync("api_lookup", new
    {
        query = "overload",
        assemblyId = modAssemblyId,
        kind = "method",
        maxResults = 1,
        includeExamples = false,
        continuation = taskContinuation,
    });
    Assert(nextTaskLookup.RootElement.GetProperty("offset").GetInt32() == 1 &&
           nextTaskLookup.RootElement.GetProperty("results")[0].GetProperty("symbolId").GetString() != firstTaskId &&
           nextTaskLookup.RootElement.GetProperty("snapshotId").GetString() == taskLookupRoot.GetProperty("snapshotId").GetString(),
        "task lookup continuation is stable and bound to the installed API snapshot");
    const string exactQualifiedQuery = "Fixture.Shared.ExactLookupSurface.Resolve";
    using var exactQualifiedLookup = await ApiToolJsonAsync("api_lookup", new
    {
        mode = "symbol",
        query = exactQualifiedQuery,
        assemblyId = gameAssemblyId,
        kind = "method",
    });
    var exactQualifiedRoot = exactQualifiedLookup.RootElement;
    var exactQualifiedMatch = exactQualifiedRoot.GetProperty("match");
    var exactQualifiedSource = exactQualifiedRoot.GetProperty("source").GetProperty("source").GetString()!;
    Assert(!exactQualifiedRoot.GetProperty("ambiguous").GetBoolean() &&
           exactQualifiedMatch.GetProperty("qualifiedName").GetString() == exactQualifiedQuery &&
           exactQualifiedMatch.GetProperty("signature").GetString()!.Contains("Resolve(", StringComparison.Ordinal) &&
           exactQualifiedSource.Contains("Resolve()", StringComparison.Ordinal) &&
           !exactQualifiedSource.Contains("ResolveHelper", StringComparison.Ordinal),
        "fully qualified api_lookup prefers exact names over longer substring matches");
    using var boundedLookup = await ApiToolJsonAsync("api_lookup", new { query = "Page", mode = "symbol", assemblyId = gameAssemblyId, kind = "method" });
    var boundedRoot = boundedLookup.RootElement;
    var nextSearch = boundedRoot.GetProperty("nextSearch");
    var nextSearchArguments = nextSearch.GetProperty("arguments");
    var firstCandidateIds = boundedRoot.GetProperty("candidates").EnumerateArray()
        .Select(candidate => candidate.GetProperty("symbolId").GetString()!).ToHashSet(StringComparer.Ordinal);
    Assert(boundedRoot.GetProperty("total").GetInt32() == 21 &&
           boundedRoot.GetProperty("candidates").GetArrayLength() == 20 &&
           boundedRoot.GetProperty("hasMore").GetBoolean() &&
           nextSearch.GetProperty("tool").GetString() == "api_search" &&
           nextSearchArguments.GetProperty("offset").GetInt32() == 20 &&
           nextSearchArguments.GetProperty("assemblyId").GetString() == gameAssemblyId &&
           nextSearchArguments.GetProperty("kind").GetString() == "method" &&
           nextSearchArguments.GetProperty("snapshotId").GetString() == boundedRoot.GetProperty("snapshotId").GetString(),
        "bounded ambiguous lookup provides an actionable api_search continuation");
    using var remainingSearch = await ApiToolJsonAsync("api_search", nextSearchArguments);
    Assert(remainingSearch.RootElement.GetProperty("offset").GetInt32() == 20 &&
           remainingSearch.RootElement.GetProperty("results").GetArrayLength() == 1 &&
           !firstCandidateIds.Contains(remainingSearch.RootElement.GetProperty("results")[0].GetProperty("symbolId").GetString()!),
        "nextSearch retrieves candidates omitted from bounded lookup results");
    foreach (var overload in overloads)
    {
        var symbolId = overload.GetProperty("symbolId").GetString()!;
        using var memberLookup = await ApiToolJsonAsync("api_lookup", new { assemblyId = modAssemblyId, symbolId, startLine = 1, lineCount = 20 });
        var match = memberLookup.RootElement.GetProperty("match");
        var source = memberLookup.RootElement.GetProperty("source").GetProperty("source").GetString()!;
        var signature = overload.GetProperty("signature").GetString()!;
        var expectedParameter = signature.Contains("String", StringComparison.OrdinalIgnoreCase) ? "Overload(string" : "Overload(int";
        var otherParameter = expectedParameter == "Overload(int" ? "Overload(string" : "Overload(int";
        Assert(match.GetProperty("signature").GetString() == signature &&
               source.Contains(expectedParameter, StringComparison.Ordinal) && !source.Contains(otherParameter, StringComparison.Ordinal),
            "exact api_lookup returns the selected overload's signature and source");
    }

    using var derivedSearch = await ApiToolJsonAsync("api_search", new { query = "DerivedSurface", kind = "type", assemblyId = modAssemblyId });
    derivedHit = derivedSearch.RootElement.GetProperty("results").EnumerateArray().Single();
    derivedTypeId = derivedHit.GetProperty("symbolId").GetString()!;
    using var directType = await ApiToolJsonAsync("api_type", new { assemblyId = modAssemblyId, typeSymbolId = derivedTypeId, includeInherited = false, limit = 100 });
    Assert(!directType.RootElement.GetProperty("members").EnumerateArray().Any(member => SymbolName(member) == "Inherited"),
        "inherited members are excluded unless requested");
    using var inheritedType = await ApiToolJsonAsync("api_type", new { assemblyId = modAssemblyId, typeSymbolId = derivedTypeId, includeInherited = true, limit = 100 });
    var inheritedMember = inheritedType.RootElement.GetProperty("members").EnumerateArray()
        .Single(member => SymbolName(member) == "Inherited");
    Assert(inheritedMember.GetProperty("assemblyId").GetString() == baseAssemblyId &&
           inheritedMember.GetProperty("declaringType").GetString() == "Fixture.Base.BaseSurface",
        "inherited member reports its declaring game assembly and type");
    using var inheritedSource = await ApiToolJsonAsync("api_decompile", new
    {
        assemblyId = inheritedMember.GetProperty("assemblyId").GetString(),
        symbolId = inheritedMember.GetProperty("symbolId").GetString(),
        startLine = 1,
        lineCount = 20
    });
    Assert(inheritedSource.RootElement.GetProperty("source").GetString()!.Contains("Inherited", StringComparison.Ordinal),
        "inherited member IDs work directly with api_decompile");

    using var pagedTypeSearch = await ApiToolJsonAsync("api_search", new { query = "PagedSurface", kind = "type", assemblyId = gameAssemblyId });
    var pagedTypeId = pagedTypeSearch.RootElement.GetProperty("results")[0].GetProperty("symbolId").GetString()!;
    using var typeMemberPage = await ApiToolJsonAsync("api_type", new { assemblyId = gameAssemblyId, typeSymbolId = pagedTypeId, offset = 0, limit = 3 });
    Assert(typeMemberPage.RootElement.GetProperty("members").GetArrayLength() == 3 &&
           typeMemberPage.RootElement.GetProperty("hasMore").GetBoolean(), "type members are paginated");
    var typeNextOffset = typeMemberPage.RootElement.GetProperty("nextOffset").GetInt32();
    using var nextTypeMemberPage = await ApiToolJsonAsync("api_type", new { assemblyId = gameAssemblyId, typeSymbolId = pagedTypeId, offset = typeNextOffset, limit = 3 });
    var firstMemberIds = typeMemberPage.RootElement.GetProperty("members").EnumerateArray()
        .Select(member => member.GetProperty("symbolId").GetString()).ToHashSet(StringComparer.Ordinal);
    Assert(!nextTypeMemberPage.RootElement.GetProperty("members").EnumerateArray()
        .Any(member => firstMemberIds.Contains(member.GetProperty("symbolId").GetString())), "type member pages do not repeat symbols");

    using var firstSourcePage = await ApiToolJsonAsync("api_decompile", new { assemblyId = gameAssemblyId, symbolId = pagedTypeId, startLine = 1, lineCount = 4 });
    var firstSourceRoot = firstSourcePage.RootElement;
    Assert(firstSourceRoot.GetProperty("sourceKind").GetString() == "reconstructed-csharp" &&
           firstSourceRoot.GetProperty("lineCount").GetInt32() <= 4 &&
           firstSourceRoot.GetProperty("hasMore").GetBoolean(), "type source reports bounded, truncated first page");
    var nextSourceLine = firstSourceRoot.GetProperty("nextLine").GetInt32();
    using var secondSourcePage = await ApiToolJsonAsync("api_decompile", new { assemblyId = gameAssemblyId, symbolId = pagedTypeId, startLine = nextSourceLine, lineCount = 4 });
    Assert(secondSourcePage.RootElement.GetProperty("startLine").GetInt32() == nextSourceLine &&
           secondSourcePage.RootElement.GetProperty("source").GetString() != firstSourceRoot.GetProperty("source").GetString(),
        "source continuation retrieves the next line range");

    using var staticProbeSearch = await ApiToolJsonAsync("api_search", new { query = "StaticInitializerProbe", kind = "type", assemblyId = gameAssemblyId });
    var staticProbeId = staticProbeSearch.RootElement.GetProperty("results")[0].GetProperty("symbolId").GetString()!;
    using var staticProbeSource = await ApiToolJsonAsync("api_decompile", new { assemblyId = gameAssemblyId, symbolId = staticProbeId, startLine = 1, lineCount = 40 });
    Assert(staticProbeSource.RootElement.GetProperty("source").GetString()!.Contains("StaticInitializerProbe", StringComparison.Ordinal) &&
           !File.Exists(fixtureSentinelPath), "metadata browsing and decompilation never execute fixture static initializers");

    using var invalidAssemblyId = await ToolAsync("api_search", new { query = "SharedSurface", assemblyId = "invalid-assembly-id" });
    using var invalidTypeId = await ToolAsync("api_type", new { assemblyId = gameAssemblyId, typeSymbolId = "invalid-type-id" });
    using var invalidSymbolId = await ToolAsync("api_decompile", new { assemblyId = gameAssemblyId, symbolId = "invalid-symbol-id" });
    Assert(IsError(invalidAssemblyId) && IsError(invalidTypeId) && IsError(invalidSymbolId), "invalid opaque assembly and symbol IDs return MCP tool errors");
    using var signatureTypeSearch = await ApiToolJsonAsync("api_search", new { query = "SignatureSurface", kind = "type", assemblyId = gameAssemblyId });
    var signatureTypeId = signatureTypeSearch.RootElement.GetProperty("results").EnumerateArray()
        .Single(hit => hit.GetProperty("qualifiedName").GetString() == "Fixture.Shared.SignatureSurface")
        .GetProperty("symbolId").GetString()!;
    using var missingMember = await ToolAsync("api_lookup", new
    {
        query = "Fixture.Shared.SignatureSurface.MissingMemberProbe",
        mode = "symbol",
        assemblyId = gameAssemblyId,
    });
    var missingMemberResult = missingMember.RootElement.GetProperty("result");
    var missingMemberError = missingMemberResult.GetProperty("structuredContent").GetProperty("error");
    var missingMemberRecovery = missingMemberError.GetProperty("recovery");
    Assert(IsError(missingMember) && missingMemberError.GetProperty("code").GetString() == "member.not_found" &&
           missingMemberRecovery.GetProperty("diagnostic").GetString() == "Type.MissingMember" &&
           missingMemberRecovery.GetProperty("notFound").GetBoolean() &&
           missingMemberRecovery.GetProperty("resolvedType").GetProperty("symbolId").GetString() == signatureTypeId &&
           missingMemberRecovery.GetProperty("candidates").GetArrayLength() is > 0 and <= 20 &&
           missingMemberRecovery.GetProperty("nextTool").GetProperty("tool").GetString() == "api_type",
        "qualified missing member reports the resolved type, bounded real member candidates, and an actionable api_type recovery");
    using var invalidBounds = await ToolAsync("api_assemblies", new { offset = 0, limit = 0 });
    Assert(IsError(invalidBounds), "invalid pagination bounds return an MCP tool error");

    using var preChangeAssemblyPage = await ApiToolJsonAsync("api_assemblies", new { offset = 0, limit = 1 });
    var staleSnapshotId = preChangeAssemblyPage.RootElement.GetProperty("snapshotId").GetString()!;
    var staleSnapshotNextOffset = preChangeAssemblyPage.RootElement.GetProperty("nextOffset").GetInt32();
    using var preChangeLookupPage = await ApiToolJsonAsync("api_lookup", new
    {
        assemblyId = gameAssemblyId,
        symbolId = sharedTypeId,
        startLine = 1,
        lineCount = 1,
    });
    var lookupSourcePage = preChangeLookupPage.RootElement.GetProperty("source");
    var staleLookupSnapshotId = lookupSourcePage.GetProperty("snapshotId").GetString()!;
    var staleLookupNextLine = lookupSourcePage.GetProperty("nextLine").GetInt32();
    Assert(lookupSourcePage.GetProperty("hasMore").GetBoolean(), "exact api_lookup source exposes a continuation line");
    using var nextLookupPage = await ApiToolJsonAsync("api_lookup", new
    {
        assemblyId = gameAssemblyId,
        symbolId = sharedTypeId,
        startLine = staleLookupNextLine,
        lineCount = 1,
        snapshotId = staleLookupSnapshotId,
    });
    var nextLookupSource = nextLookupPage.RootElement.GetProperty("source");
    Assert(nextLookupSource.GetProperty("startLine").GetInt32() == staleLookupNextLine &&
           nextLookupSource.GetProperty("source").GetString() != lookupSourcePage.GetProperty("source").GetString(),
        "exact api_lookup continues reconstructed source with the returned snapshot and next line");
    using var navigationWrongKind = await ToolAsync("api_find_callers", new { assemblyId = gameAssemblyId, symbolId = signatureTypeId, scope = "assembly" });
    using var navigationInvalidId = await ToolAsync("api_find_callees", new { assemblyId = gameAssemblyId, symbolId = "invalid-symbol-id" });
    using var navigationInvalidBounds = await ToolAsync("api_find_usages", new { assemblyId = gameAssemblyId, symbolId = sharedTypeId, scope = "assembly", offset = -1 });
    Assert(IsError(navigationWrongKind) && navigationWrongKind.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "symbol.wrong_kind" &&
           IsError(navigationInvalidId) && navigationInvalidId.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "symbol.invalid_or_stale" &&
           IsError(navigationInvalidBounds) && navigationInvalidBounds.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "request.invalid",
        "navigation wrong-kind, invalid-symbol, and invalid-page requests expose structured MCP error codes");
    using var oversizedBatchId = await ToolAsync("api_decompile_batch", new
    {
        items = new[] { new { assemblyId = new string('x', 100_000), symbolId = "sym_invalid", startLine = 1, lineCount = 1 } },
    });
    Assert(IsError(oversizedBatchId) &&
           oversizedBatchId.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "request.invalid" &&
           oversizedBatchId.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!.Length < 2048,
        "batch rejects oversized untrusted IDs without echoing them into an unbounded structured error");
    File.Copy(changedAssemblyPath, apiGameSharedPath, overwrite: true);
    using var refreshedAssemblies = await ApiToolJsonAsync("api_assemblies", new { name = "Fixture.Shared", origin = "game", limit = 50 });
    var refreshedAssemblyId = refreshedAssemblies.RootElement.GetProperty("assemblies")[0].GetProperty("assemblyId").GetString()!;
    Assert(refreshedAssemblyId != gameAssemblyId, "replacing assembly content yields a fresh assembly ID");
    using var staleBatchSnapshot = await ToolAsync("api_decompile_batch", new
    {
        snapshotId = staleSnapshotId,
        items = new[] { new { assemblyId = gameAssemblyId, symbolId = sharedTypeId, startLine = 1, lineCount = 1 } },
    });
    Assert(IsError(staleBatchSnapshot) &&
           staleBatchSnapshot.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "snapshot.stale",
        "batch rejects stale snapshots as request-level structured MCP errors");
    using var navigationStaleSnapshot = await ToolAsync("api_find_callees", new { assemblyId = gameAssemblyId, symbolId = sharedTypeId, snapshotId = staleSnapshotId });
    Assert(IsError(navigationStaleSnapshot) &&
           navigationStaleSnapshot.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "snapshot.stale",
        "navigation rejects stale snapshot IDs through structured MCP errors");
    using var staleLookupContinuation = await ToolAsync("api_lookup", new
    {
        assemblyId = gameAssemblyId,
        symbolId = sharedTypeId,
        startLine = staleLookupNextLine,
        lineCount = 1,
        snapshotId = staleLookupSnapshotId,
    });
    Assert(IsError(staleLookupContinuation) &&
           staleLookupContinuation.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "snapshot.stale" &&
           staleLookupContinuation.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("recovery").GetProperty("tool").GetString() == "api_assemblies",
        "stale lookup continuation returns structured snapshot recovery");
    var oldGameSharedSymbolId = sharedHits.Single(hit => hit.GetProperty("assemblyId").GetString() == gameAssemblyId)
        .GetProperty("symbolId").GetString()!;
    using var staleAssemblySearch = await ToolAsync("api_search", new { query = "SharedSurface", assemblyId = gameAssemblyId });
    using var staleSymbolSource = await ToolAsync("api_decompile", new { assemblyId = gameAssemblyId, symbolId = oldGameSharedSymbolId });
    using var staleSymbolInFreshAssembly = await ToolAsync("api_decompile", new { assemblyId = refreshedAssemblyId, symbolId = oldGameSharedSymbolId });
    Assert(IsError(staleAssemblySearch) && IsError(staleSymbolSource) && IsError(staleSymbolInFreshAssembly),
        "assembly replacement invalidates previously issued assembly and symbol IDs");
    using var changedSearch = await ApiToolJsonAsync("api_search", new { query = "ChangedOnly", assemblyId = refreshedAssemblyId });
    Assert(changedSearch.RootElement.GetProperty("results").GetArrayLength() == 1, "fresh assembly metadata exposes replacement content");

    var renamedModPath = apiModSharedPath + ".renamed";
    File.Move(apiModSharedPath, renamedModPath);
    using var removedAssemblySearch = await ToolAsync("api_search", new { query = "SharedSurface", assemblyId = modAssemblyId });
    Assert(IsError(removedAssemblySearch), "removed assembly IDs are rejected after browsing");
    File.Delete(renamedModPath);
    Assert(!File.Exists(renamedModPath), "browsed assembly can be renamed and deleted without a file lock");
    Assert(!File.Exists(fixtureSentinelPath), "fixture static initializer remains unexecuted after all API calls");
    File.Copy(Path.Combine(fixtureOutputDirectory, "game", "Fixture.Shared.dll"), apiGameSharedPath, overwrite: true);
}
async Task CheckCatalogToolsAsync()
{
    using var search = await ApiToolJsonAsync("catalog_search", new { catalogType = "LevelData", query = "Survival" });
    var root = search.RootElement;
    var record = root.GetProperty("results").EnumerateArray().Single();
    Assert(root.GetProperty("total").GetInt32() == 1 &&
           record.GetProperty("id").GetString() == "Arena" &&
           record.GetProperty("catalogType").GetString() == "LevelData" &&
           record.GetProperty("memberPath").GetString() == "Levels/Level_Arena.json",
        "catalog search finds a LevelData record by text nested inside its JSON");
    var apiLookup = record.GetProperty("apiLookup");
    Assert(apiLookup.GetProperty("query").GetString() == "ThunderRoad.LevelData" &&
           apiLookup.GetProperty("kind").GetString() == "type",
        "catalog search links from the actual $type to an api_lookup type query");
    using var linkedCatalogType = await ApiToolJsonAsync("catalog_search", new { id = "BaseSurfaceRecord" });
    var linkedRecord = linkedCatalogType.RootElement.GetProperty("results")[0];
    var typeHandoff = linkedRecord.GetProperty("apiLookup");
    using var linkedApiType = await ApiToolJsonAsync("api_lookup", new
    {
        query = typeHandoff.GetProperty("query").GetString(),
        kind = typeHandoff.GetProperty("kind").GetString(),
    });
    var linkedApiMatch = linkedApiType.RootElement.GetProperty("results")[0];
    var linkedAssemblyId = linkedApiMatch.GetProperty("assemblyId").GetString()!;
    var linkedSymbolId = linkedApiMatch.GetProperty("symbolId").GetString()!;
    Assert(linkedApiMatch.GetProperty("qualifiedName").GetString() == "Fixture.Base.BaseSurface" &&
           linkedAssemblyId.Length > 0 && linkedSymbolId.Length > 0 &&
           linkedApiMatch.GetProperty("relevance").GetProperty("confidence").GetString() is "supported" or "low",
        "catalog $type handoff resolves to a ranked, actionable API type identity");
    using var linkedApiSource = await ApiToolJsonAsync("api_lookup", new { assemblyId = linkedAssemblyId, symbolId = linkedSymbolId });
    Assert(linkedApiSource.RootElement.GetProperty("source").GetProperty("source").GetString()!.Contains("BaseSurface", StringComparison.Ordinal),
        "exact catalog API identity retrieves paged source in symbol mode");
    using var nestedGenericRecordSearch = await ApiToolJsonAsync("catalog_search", new { id = "NestedGenericRecord" });
    var nestedGenericRecord = nestedGenericRecordSearch.RootElement.GetProperty("results")[0];
    var nestedGenericFullType = nestedGenericRecord.GetProperty("fullType").GetString()!;
    Assert(nestedGenericFullType.Contains('+') &&
           nestedGenericFullType.Contains('[') &&
           nestedGenericRecord.GetProperty("apiLookup").ValueKind == JsonValueKind.Null,
        "unsupported nested generic CLR $type does not emit a malformed api_lookup query");
    var entryId = record.GetProperty("entryId").GetString()!;
    var snapshotId = root.GetProperty("snapshotId").GetString()!;
    using var repeatedSearch = await ApiToolJsonAsync("catalog_search", new { catalogType = "LevelData", query = "Survival" });
    var repeatedRoot = repeatedSearch.RootElement;
    Assert(repeatedRoot.GetProperty("snapshotId").GetString() == snapshotId &&
           repeatedRoot.GetProperty("results")[0].GetProperty("entryId").GetString() == entryId,
        "unchanged archive content retains its catalog snapshot and entry ID");
    var diagnostics = root.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
    Assert(diagnostics.Any(message => message.Contains("Broken/Invalid.json", StringComparison.Ordinal)),
        "malformed catalog JSON is diagnosed without hiding valid records");

    using var firstPage = await ApiToolJsonAsync("catalog_read", new { entryId, snapshotId, startLine = 1, lineCount = 3 });
    var firstPageRoot = firstPage.RootElement;
    Assert(firstPageRoot.GetProperty("apiLookup").GetProperty("query").GetString() == "ThunderRoad.LevelData",
        "catalog read retains the actual $type api_lookup handoff");
    Assert(firstPageRoot.GetProperty("hasMore").GetBoolean(), "catalog JSON can be paged");
    var nextLine = firstPageRoot.GetProperty("nextLine").GetInt32();
    using var lastPage = await ApiToolJsonAsync("catalog_read", new { entryId, snapshotId, startLine = nextLine, lineCount = 100 });
    Assert(!lastPage.RootElement.GetProperty("hasMore").GetBoolean(), "catalog JSON pagination completes");
    var readText = firstPageRoot.GetProperty("text").GetString() + lastPage.RootElement.GetProperty("text").GetString();
    Assert(readText == levelJson, "catalog pages reconstruct the exact JSON record");
    var catalogArchiveBytesAfterRead = await File.ReadAllBytesAsync(catalogArchivePath);
    Assert(catalogArchiveBytes.SequenceEqual(catalogArchiveBytesAfterRead),
        "catalog search and read leave the base archive byte-for-byte unchanged");

    File.Delete(catalogArchivePath);
    WritePackage(catalogArchivePath, ("Levels/Level_Changed.json", levelJson));
    using var stalePage = await ToolAsync("catalog_read", new { entryId, snapshotId, startLine = 1, lineCount = 3 });
    Assert(IsError(stalePage) &&
           ErrorText(stalePage).Contains(
               "snapshotId is not for the current bas.jsondb archive; rerun catalog_search, then use its current entryId and snapshotId together.",
               StringComparison.Ordinal),
        "stale catalog snapshot error gives the current entryId and snapshotId recovery sequence");
}

static bool IsReadOnlyTool(JsonElement tool) =>
    tool.TryGetProperty("annotations", out var annotations) &&
    annotations.TryGetProperty("readOnlyHint", out var readOnly) &&
    readOnly.ValueKind == JsonValueKind.True;

static string SymbolName(JsonElement symbol)
{
    if (symbol.TryGetProperty("name", out var name))
        return name.GetString() ?? "";
    if (!symbol.TryGetProperty("qualifiedName", out var qualified))
        return "";
    var fullName = qualified.GetString() ?? "";
    var leaf = fullName[(fullName.LastIndexOf('.') + 1)..];
    return leaf.Split('(')[0];
}

static bool IsError(JsonDocument response) =>
    response.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();

static string ErrorText(JsonDocument response) =>
    response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "";

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("Boundary check failed: " + message);
}

async Task HandleRequestAsync(TcpClient client, CancellationToken token)
{
    using (client)
    using (var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true))
    {
        var line = await reader.ReadLineAsync(token);
        if (line is null)
            return;
        Interlocked.Increment(ref bridgeRequests);
        using var request = JsonDocument.Parse(line);
        var root = request.RootElement;
        var id = root.GetProperty("id").GetString();
        var parameters = root.GetProperty("parameters");
        var method = root.GetProperty("method").GetString();
        if (method == "status")
        {
            var includeAssemblies = parameters.TryGetProperty("includeAssemblies", out var include) &&
                                    include.ValueKind == JsonValueKind.True;
            object statusResult;
            if (oldBridgeStatus)
                statusResult = new
                {
                    processId = 123,
                    bridgeReady = true,
                    loadedAssemblies = loadedAssemblyFixture,
                    loadedAssemblyCount = 1,
                    loadedAssembliesTruncated = false,
                };
            else if (!includeAssemblies && Volatile.Read(ref sameLevelReloadPhase) > 0)
            {
                var phase = Interlocked.CompareExchange(ref sameLevelReloadPhase, 0, 0);
                if (phase == 1)
                {
                    statusResult = new { processId = 123, bridgeReady = true, currentLevel = "Arena", levelState = "Loaded", sceneReady = true };
                    Interlocked.CompareExchange(ref sameLevelReloadPhase, 2, 1);
                }
                else if (phase == 2)
                {
                    statusResult = new { processId = 123, bridgeReady = true, currentLevel = "Home", levelState = "Loading", sceneReady = false };
                    Interlocked.CompareExchange(ref sameLevelReloadPhase, 3, 2);
                }
                else
                {
                    statusResult = new { processId = 123, bridgeReady = true, currentLevel = "Arena", levelState = "Loaded", sceneReady = true };
                    Interlocked.CompareExchange(ref sameLevelReloadPhase, 0, phase);
                }
            }
            else if (includeAssemblies)
                statusResult = new
                {
                    processId = 123,
                    bridgeReady = true,
                    currentLevel = "Arena",
                    levelState = "Loaded",
                    sceneReady = true,
                    loadedAssemblies = loadedAssemblyFixture,
                    loadedAssemblyCount = 1,
                    loadedAssembliesTruncated = false,
                };
            else
                statusResult = new { processId = 123, bridgeReady = true, currentLevel = "Arena", levelState = "Loaded", sceneReady = true };
            var statusResponse = "{\"id\":" + JsonSerializer.Serialize(id) +
                ",\"success\":true,\"result\":" + JsonSerializer.Serialize(statusResult) +
                ",\"timings\":{\"bridgeRequestTotalMs\":1.25,\"compilerQueueWaitMs\":-1,\"serializationMs\":60001,\"invocationMs\":1e1000,\"unknownStageMs\":3}}\n";
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(statusResponse), token);
            return;
        }
        var delayedLoadResponse = method == "load_level" && Interlocked.Exchange(ref delayNextLevelRequest, 0) != 0;
        if (delayedLoadResponse)
            await Task.Delay(120, token);
        var sameLevelReload = method == "load_level" && Interlocked.Exchange(ref reloadAlreadyCurrentNext, 0) != 0;
        if (sameLevelReload)
            Interlocked.Exchange(ref sameLevelReloadPhase, 1);
        if (method == "runtime" && oldBridgeRuntime &&
            parameters.GetProperty("action").GetString() == "inspect")
        {
            var legacyError = JsonSerializer.Serialize(new
            {
                id,
                success = false,
                error = "runtime.action must be list, stop, or clear.\n   at Stack.Trace",
            }) + "\n";
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(legacyError), token);
            return;
        }
        if (method is "load_level" or "spawn_item" or "runtime")
        {
            object routedResult = method switch
            {
                "load_level" => new
                {
                    levelId = parameters.GetProperty("levelId").GetString(),
                    mode = parameters.GetProperty("mode").GetString(),
                    currentLevelBefore = sameLevelReload ? "Arena" : "Home",
                    levelStateBefore = "Loaded",
                    sceneReadyBefore = true,
                },
                "spawn_item" => new
                {
                    key = parameters.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                        ? key.GetString()
                        : "item-generated",
                    itemId = parameters.GetProperty("itemId").GetString(),
                    position = new { x = 1, y = 2, z = 3 },
                    instanceId = 42,
                    name = "Test Item",
                    disallowDespawn = !parameters.TryGetProperty("disallowDespawn", out var allow) ||
                                      allow.ValueKind != JsonValueKind.False,
                },
                _ => parameters.GetProperty("action").GetString() switch
                {
                    "list" => new
                    {
                        coroutines = new[] { new { key = "demo", type = "UnityEngine.Coroutine" } },
                        objects = new[] { new { key = "stored", type = "System.String" } },
                        coroutineCount = 1,
                        objectCount = 1,
                    },
                    "stop" => new
                    {
                        action = "stop",
                        key = parameters.TryGetProperty("key", out var stopKey) ? stopKey.GetString() : null,
                        stopped = parameters.TryGetProperty("key", out var stoppedKey) && stoppedKey.GetString() == "demo",
                    },
                    "inspect" => new
                    {
                        action = "inspect",
                        key = parameters.GetProperty("key").GetString(),
                        found = true,
                        type = "System.Int32",
                        value = new { kind = "integer", value = 42 },
                    },
                    _ => new { action = "clear", cleared = true },
                },
            };
            var routedResponse = JsonSerializer.Serialize(new { id, success = true, result = routedResult }) + "\n";
            try
            {
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(routedResponse), token);
            }
            catch (Exception exception) when (
                delayedLoadResponse &&
                (exception is IOException or ObjectDisposedException or SocketException))
            {
            }
            return;
        }
        var since = parameters.GetProperty("since").GetInt64();
        var result = new { pad = new string('x', 65536) };
        var success = true;
        string? error = null;
        if (since == 97)
        {
            result = new { pad = "" };
            success = false;
            error = "REMOTE_DIAGNOSTIC_SENTINEL";
        }
        else if (since == 98)
        {
            result = new { pad = "" };
            id = "mismatched-id";
        }
        var response = JsonSerializer.Serialize(new { id, success, result, error }) + "\n";
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(response), token);
    }
}
