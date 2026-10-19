using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public static class ApiIdentityFreshnessCheck
{
    public static async Task RunAsync(Func<string, object, Task<JsonDocument>> apiToolJsonAsync,
        Action<bool, string> assert, ApiReuseContext context)
    {
        async Task<string> AssemblyIdAsync()
        {
            using var result = await apiToolJsonAsync("api_assemblies", new { name = "Fixture.Shared", origin = "game", limit = 50 });
            return result.RootElement.GetProperty("assemblies").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "Fixture.Shared")
                .GetProperty("assemblyId").GetString()!;
        }

        var installedBytes = await File.ReadAllBytesAsync(context.GameSharedAssemblyPath);
        var installedTimestamp = File.GetLastWriteTimeUtc(context.GameSharedAssemblyPath);
        var baselineId = await AssemblyIdAsync();
        try
        {
            var peOffset = BitConverter.ToInt32(installedBytes, 0x3c);
            var timestampOffset = checked(peOffset + 8);
            var changedBytes = (byte[])installedBytes.Clone();
            changedBytes[timestampOffset] ^= 0x01;
            await File.WriteAllBytesAsync(context.GameSharedAssemblyPath, changedBytes);
            File.SetLastWriteTimeUtc(context.GameSharedAssemblyPath, installedTimestamp);

            var changedInfo = new FileInfo(context.GameSharedAssemblyPath);
            assert(changedInfo.Length == installedBytes.Length, "COFF timestamp mutation preserves installed assembly size");
            assert(changedInfo.LastWriteTimeUtc == installedTimestamp, "COFF timestamp mutation preserves installed assembly filesystem timestamp");
            var changedId = await AssemblyIdAsync();
            assert(changedId != baselineId, "assemblyId changes when installed assembly content changes at unchanged size and timestamp");
        }
        finally
        {
            await File.WriteAllBytesAsync(context.GameSharedAssemblyPath, installedBytes);
            File.SetLastWriteTimeUtc(context.GameSharedAssemblyPath, installedTimestamp);
        }

        var launchConfigBytes = await File.ReadAllBytesAsync(context.LaunchConfigPath);
        var secondInstall = Path.Combine(context.WorkDirectory, "api-identity-second-install-" + Guid.NewGuid().ToString("N"));
        var secondManaged = Path.Combine(secondInstall, "BladeAndSorcery_Data", "Managed");
        var secondExecutable = Path.Combine(secondInstall, "BladeAndSorcery.exe");
        Directory.CreateDirectory(secondManaged);
        await File.WriteAllBytesAsync(secondExecutable, Array.Empty<byte>());
        File.Copy(context.GameSharedAssemblyPath, Path.Combine(secondManaged, Path.GetFileName(context.GameSharedAssemblyPath)));
        try
        {
            await File.WriteAllTextAsync(context.LaunchConfigPath, JsonSerializer.Serialize(new
            {
                executablePath = secondExecutable,
                arguments = Array.Empty<string>()
            }));
            var secondInstallId = await AssemblyIdAsync();
            assert(secondInstallId != baselineId, "assemblyId changes when identical assembly content is discovered under a different canonical install root");
        }
        finally
        {
            await File.WriteAllBytesAsync(context.LaunchConfigPath, launchConfigBytes);
            Directory.Delete(secondInstall, recursive: true);
        }
    }
}
