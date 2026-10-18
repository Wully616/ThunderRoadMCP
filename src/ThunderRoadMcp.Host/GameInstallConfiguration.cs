using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal sealed record LaunchOptions(string ExecutablePath, string[] Arguments);

internal sealed record GameInstallation(string Store, string ExecutablePath);

internal static class GameInstallConfiguration
{
    private const string LaunchConfigEnvironmentVariable = "THUNDERROAD_MCP_LAUNCH_CONFIG";
    private const string SteamGameName = "Blade & Sorcery";
    private const string GameExecutableName = "BladeAndSorcery.exe";
    private const string MetaLibrariesRegistryPath = @"Software\Oculus VR, LLC\Oculus\Libraries";
    private static readonly string[] SteamRegistryPathValueNames = { "SteamPath", "InstallPath" };
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThunderRoadMcp");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex VdfPairPattern = new(
        "\"(?<key>(?:\\\\.|[^\"\\\\])*)\"\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string LaunchConfigPath => Environment.GetEnvironmentVariable(LaunchConfigEnvironmentVariable)
        ?? Path.Combine(ConfigDirectory, "launch.json");

    public static async Task<LaunchOptions> ReadLaunchOptionsAsync(CancellationToken cancellationToken)
    {
        var configuredPath = Environment.GetEnvironmentVariable(LaunchConfigEnvironmentVariable);
        var configPath = configuredPath ?? Path.Combine(ConfigDirectory, "launch.json");
        if (configuredPath is not null || File.Exists(configPath))
        {
            await using var input = File.OpenRead(configPath);
            LaunchOptions options;
            try
            {
                options = await JsonSerializer.DeserializeAsync<LaunchOptions>(input, JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException($"Launch configuration '{configPath}' is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"Launch configuration '{configPath}' is invalid: {exception.Message}", exception);
            }

            if (string.IsNullOrWhiteSpace(options.ExecutablePath)
                || !Path.IsPathFullyQualified(options.ExecutablePath)
                || !File.Exists(options.ExecutablePath))
                throw new InvalidOperationException(
                    $"Launch configuration '{configPath}' must specify an existing absolute executablePath.");
            if (options.Arguments is null || options.Arguments.Any(argument => argument is null))
                throw new InvalidOperationException(
                    $"Launch configuration '{configPath}' must contain a string arguments array.");
            return options;
        }

        return ResolveUniqueInstallation(DiscoverInstallations(), configPath);
    }

    public static IReadOnlyList<GameInstallation> DiscoverInstallations()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<GameInstallation>();

        return DiscoverInstallationsFromRoots(ReadSteamRoots(), ReadMetaLibraryRoots());
    }

    public static string ListInstallationsJson()
    {
        var installations = DiscoverInstallations()
            .OrderBy(installation => installation.Store, StringComparer.OrdinalIgnoreCase)
            .ThenBy(installation => installation.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(installation =>
            {
                var options = new LaunchOptions(installation.ExecutablePath, Array.Empty<string>());
                var catalogArchivePath = CatalogArchivePath(options);
                return new
                {
                    store = installation.Store,
                    executablePath = installation.ExecutablePath,
                    managedDirectory = ManagedDirectory(options),
                    modsDirectory = ModsDirectory(options),
                    catalogArchivePath,
                    catalogArchiveAvailable = File.Exists(catalogArchivePath),
                };
            })
            .ToArray();
        return JsonSerializer.Serialize(new { launchConfigPath = LaunchConfigPath, installations });
    }

    internal static IReadOnlyList<GameInstallation> DiscoverInstallationsFromRoots(
        IEnumerable<string> steamRoots,
        IEnumerable<string> metaRoots)
    {
        var installations = new Dictionary<string, GameInstallation>(StringComparer.OrdinalIgnoreCase);
        DiscoverSteamInstallations(steamRoots, installations);
        DiscoverMetaInstallations(metaRoots, installations);
        return installations.Values.ToArray();
    }

    internal static LaunchOptions ResolveUniqueInstallation(
        IReadOnlyList<GameInstallation> installations,
        string configPath)
    {
        if (installations.Count == 1)
            return new LaunchOptions(installations[0].ExecutablePath, Array.Empty<string>());

        if (installations.Count == 0)
            throw new InvalidOperationException(
                $"No Blade & Sorcery installation was found. Create '{configPath}' with an existing absolute " +
                $"executablePath and a string arguments array, or set {LaunchConfigEnvironmentVariable} " +
                "to an existing launch.json.");

        var candidates = string.Join(
            Environment.NewLine,
            installations
                .OrderBy(installation => installation.Store, StringComparer.OrdinalIgnoreCase)
                .ThenBy(installation => installation.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .Select(installation => $"  {installation.Store}: {installation.ExecutablePath}"));
        throw new InvalidOperationException(
            $"Found multiple Blade & Sorcery installations. Create '{configPath}' with the chosen absolute " +
            $"executablePath and a string arguments array, or set {LaunchConfigEnvironmentVariable} " +
            $"to an existing launch.json. Candidates:{Environment.NewLine}{candidates}");
    }

    public static string ManagedDirectory(LaunchOptions options) => Path.Combine(GameDataDirectory(options), "Managed");

    public static string ModsDirectory(LaunchOptions options) => Path.Combine(GameDataDirectory(options), "StreamingAssets", "Mods");

    public static string CatalogArchivePath(LaunchOptions options) => Path.Combine(
        GameDataDirectory(options), "StreamingAssets", "Default", "bas.jsondb");

    private static string GameDataDirectory(LaunchOptions options) => Path.Combine(
        Path.GetDirectoryName(options.ExecutablePath)!, Path.GetFileNameWithoutExtension(options.ExecutablePath) + "_Data");

    [SupportedOSPlatform("windows")]
    private static List<string> ReadSteamRoots()
    {
        var roots = new List<string>();
        AddRegistryPathValues(Registry.CurrentUser, @"Software\Valve\Steam", roots);
        AddRegistryPathValues(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", roots);
        AddRegistryPathValues(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", roots);
        return roots;
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ReadMetaLibraryRoots()
    {
        var roots = new List<string>();
        try
        {
            using var libraries = Registry.CurrentUser.OpenSubKey(MetaLibrariesRegistryPath);
            if (libraries is null)
                return roots;

            foreach (var name in libraries.GetSubKeyNames())
            {
                if (!Guid.TryParse(name, out _))
                    continue;

                try
                {
                    using var library = libraries.OpenSubKey(name);
                    var originalPath = library?.GetValue("OriginalPath") as string;
                    var path = library?.GetValue("Path") as string;
                    if (TryNormalizeExistingDirectory(originalPath, out var existingOriginalPath))
                        roots.Add(existingOriginalPath);
                    else if (!string.IsNullOrWhiteSpace(path))
                        roots.Add(path);
                }
                catch (Exception exception) when (IsExpectedAccessFailure(exception))
                {
                }
            }
        }
        catch (Exception exception) when (IsExpectedAccessFailure(exception))
        {
        }

        return roots;
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryPathValues(RegistryKey hive, string subKeyPath, ICollection<string> roots)
    {
        try
        {
            using var key = hive.OpenSubKey(subKeyPath);
            foreach (var valueName in SteamRegistryPathValueNames)
                if (key?.GetValue(valueName) is string path && !string.IsNullOrWhiteSpace(path))
                    roots.Add(path);
        }
        catch (Exception exception) when (IsExpectedAccessFailure(exception))
        {
        }
    }

    private static void DiscoverSteamInstallations(
        IEnumerable<string> steamRoots,
        IDictionary<string, GameInstallation> installations)
    {
        foreach (var root in steamRoots)
        {
            if (!TryNormalizeExistingDirectory(root, out var steamRoot))
                continue;

            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
            var libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            var libraryFolders = TryReadTextFile(libraryFoldersPath);
            if (libraryFolders is not null)
            {
                foreach (var pair in ReadVdfPairs(libraryFolders))
                {
                    var isLibraryPath = pair.Key.Equals("path", StringComparison.OrdinalIgnoreCase)
                        || int.TryParse(pair.Key, out _);
                    if (isLibraryPath && TryNormalizeExistingDirectory(pair.Value, out var libraryRoot))
                        libraries.Add(libraryRoot);
                }
            }

            foreach (var library in libraries)
                AddSteamLibraryInstallations(library, installations);
        }
    }

    private static void AddSteamLibraryInstallations(
        string libraryRoot,
        IDictionary<string, GameInstallation> installations)
    {
        var steamApps = Path.Combine(libraryRoot, "steamapps");
        foreach (var manifestPath in GetFiles(steamApps, "appmanifest_*.acf"))
        {
            var manifest = TryReadTextFile(manifestPath);
            if (manifest is null)
                continue;

            string? name = null;
            string? installDirectory = null;
            foreach (var pair in ReadVdfPairs(manifest))
            {
                if (pair.Key.Equals("name", StringComparison.OrdinalIgnoreCase))
                    name ??= pair.Value;
                else if (pair.Key.Equals("installdir", StringComparison.OrdinalIgnoreCase))
                    installDirectory ??= pair.Value;
            }

            if (!string.Equals(name, SteamGameName, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(installDirectory)
                || !string.Equals(installDirectory, Path.GetFileName(installDirectory), StringComparison.Ordinal))
                continue;

            AddInstallation(
                installations,
                "Steam",
                Path.Combine(steamApps, "common", installDirectory, GameExecutableName));
        }
    }

    private static void DiscoverMetaInstallations(
        IEnumerable<string> metaRoots,
        IDictionary<string, GameInstallation> installations)
    {
        foreach (var root in metaRoots)
        {
            if (!TryNormalizeExistingDirectory(root, out var libraryRoot))
                continue;

            AddInstallation(installations, "Meta Horizon Link", Path.Combine(libraryRoot, GameExecutableName));
            foreach (var directChild in GetDirectories(libraryRoot))
                AddInstallation(installations, "Meta Horizon Link", Path.Combine(directChild, GameExecutableName));
            foreach (var softwareChild in GetDirectories(Path.Combine(libraryRoot, "Software")))
                AddInstallation(installations, "Meta Horizon Link", Path.Combine(softwareChild, GameExecutableName));
        }
    }

    private static void AddInstallation(
        IDictionary<string, GameInstallation> installations,
        string store,
        string executablePath)
    {
        if (!File.Exists(executablePath))
            return;

        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            installations.TryAdd(fullPath, new GameInstallation(store, fullPath));
        }
        catch (Exception exception) when (IsExpectedAccessFailure(exception))
        {
        }
    }

    private static bool TryNormalizeExistingDirectory(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var normalizedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (!Directory.Exists(normalizedPath))
                return false;
            fullPath = normalizedPath;
            return true;
        }
        catch (Exception exception) when (IsExpectedAccessFailure(exception))
        {
            return false;
        }
    }

    private static string[] GetDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
        }
        catch (Exception exception) when (IsExpectedAccessFailure(exception))
        {
            return Array.Empty<string>();
        }
    }

    private static string[] GetFiles(string path, string searchPattern)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
        }
        catch (Exception exception) when (IsExpectedAccessFailure(exception))
        {
            return Array.Empty<string>();
        }
    }

    private static string? TryReadTextFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (IsExpectedAccessFailure(exception))
        {
            return null;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadVdfPairs(string contents)
    {
        foreach (Match match in VdfPairPattern.Matches(contents))
            yield return new KeyValuePair<string, string>(
                UnescapeVdfString(match.Groups["key"].Value),
                UnescapeVdfString(match.Groups["value"].Value));
    }

    private static string UnescapeVdfString(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\'
                && index + 1 < value.Length
                && value[index + 1] is '\\' or '"')
                result.Append(value[++index]);
            else
                result.Append(value[index]);
        }
        return result.ToString();
    }

    private static bool IsExpectedAccessFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException;
}
