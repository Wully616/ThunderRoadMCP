using System.IO.Compression;
using System.Text.Json;

internal static class BridgeModInstaller
{
    public static Task<string> InstallAsync(string packagePath, string executablePath, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("gameExecutablePath must be an existing absolute path.");
        return InstallAsync(packagePath, new LaunchOptions(executablePath, Array.Empty<string>()), cancellationToken);
    }

    public static async Task<string> InstallAsync(string packagePath, LaunchOptions options, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(packagePath) || !File.Exists(packagePath))
            throw new InvalidOperationException("packagePath must be an existing absolute path.");
        var modsDirectory = GameInstallConfiguration.ModsDirectory(options);

        var destination = Path.Combine(modsDirectory, "ThunderRoadMcp");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InvalidOperationException($"Refusing to overwrite existing destination '{destination}'.");

        using var archive = ZipFile.OpenRead(packagePath);
        var entries = ValidateModPackage(archive);
        Directory.CreateDirectory(modsDirectory);
        var staging = Path.Combine(modsDirectory, $".ThunderRoadMcp-{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (entry, relativePath) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outputPath = Path.Combine(staging, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var input = entry.Open();
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new InvalidOperationException($"Refusing to overwrite existing destination '{destination}'.");
            Directory.Move(staging, destination);
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }

        return JsonSerializer.Serialize(new { installed = true, destination });
    }

    private static List<(ZipArchiveEntry Entry, string RelativePath)> ValidateModPackage(ZipArchive archive)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(ZipArchiveEntry Entry, string RelativePath)>();
        var hasBridge = false;
        var hasManifest = false;
        var payloadCount = 0;
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            var relativePath = entry.FullName.Replace('\\', '/');
            var isDirectory = relativePath.EndsWith('/');
            if (isDirectory)
                relativePath = relativePath[..^1];
            if (relativePath.Length == 0 || relativePath.StartsWith('/') ||
                relativePath.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':')))
                throw new InvalidOperationException($"Package contains an unsafe path: '{entry.FullName}'.");
            if (!seen.Add(relativePath + (isDirectory ? "/" : "")))
                throw new InvalidOperationException($"Package contains a duplicate path: '{entry.FullName}'.");

            if (isDirectory)
            {
                if (relativePath != "runtime")
                    throw new InvalidOperationException($"Package contains an unexpected directory: '{entry.FullName}'.");
                continue;
            }
            totalLength += entry.Length;
            if (totalLength > 256 * 1024 * 1024)
                throw new InvalidOperationException("Package contents exceed the 256 MiB install limit.");
            if (entry.Length == 0)
                throw new InvalidOperationException($"Package file is empty: '{entry.FullName}'.");
            if (relativePath == "ThunderRoadMcp.Bridge.dll")
                hasBridge = true;
            else if (relativePath == "manifest.json")
            {
                using var manifestStream = entry.Open();
                using var manifest = JsonDocument.Parse(manifestStream);
                if (manifest.RootElement.ValueKind != JsonValueKind.Object ||
                    !manifest.RootElement.TryGetProperty("Name", out var name) ||
                    name.GetString() != "ThunderRoadMcp")
                    throw new InvalidOperationException("manifest.json must identify the package as ThunderRoadMcp.");
                hasManifest = true;
            }
            else if (relativePath.StartsWith("runtime/", StringComparison.Ordinal) &&
                     !relativePath["runtime/".Length..].Contains('/') &&
                     relativePath.EndsWith(".dll.payload", StringComparison.OrdinalIgnoreCase))
                payloadCount++;
            else
                throw new InvalidOperationException($"Package contains an unexpected file: '{entry.FullName}'.");
            files.Add((entry, relativePath));
        }

        if (!hasBridge || !hasManifest || payloadCount == 0)
            throw new InvalidOperationException("Package must contain ThunderRoadMcp.Bridge.dll, manifest.json, and at least one runtime/*.dll.payload file.");
        return files;
    }
}
