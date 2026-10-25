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
        await CheckXmlDocumentationAsync(apiToolJsonAsync, assert, context);
        CheckOpenedHandleAdmission(assert, context);
    }

    private static void CheckOpenedHandleAdmission(Action<bool, string> assert, ApiReuseContext context)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var fixtureRoot = Path.Combine(context.WorkDirectory, "api-open-handle-" + Guid.NewGuid().ToString("N"));
        var enabledRoot = Path.Combine(fixtureRoot, new string('a', 80), new string('b', 80), new string('c', 80));
        var enabledAssembly = Path.Combine(enabledRoot, "Fixture.Shared.dll");
        var modRoot = Path.Combine(fixtureRoot, "mods");
        var disabledAssembly = Path.Combine(modRoot, "_disabled", "Fixture.Shared.dll");
        var outsideAssembly = Path.Combine(fixtureRoot, "outside", "Fixture.Shared.dll");
        try
        {
            Directory.CreateDirectory(ExtendedWindowsPath(enabledRoot));
            Directory.CreateDirectory(ExtendedWindowsPath(Path.GetDirectoryName(disabledAssembly)!));
            Directory.CreateDirectory(ExtendedWindowsPath(Path.GetDirectoryName(outsideAssembly)!));
            File.WriteAllBytes(ExtendedWindowsPath(enabledAssembly), new byte[] { 1 });
            File.WriteAllBytes(ExtendedWindowsPath(disabledAssembly), new byte[] { 1 });
            File.WriteAllBytes(ExtendedWindowsPath(outsideAssembly), new byte[] { 1 });

            using (var opened = new FileStream(ExtendedWindowsPath(enabledAssembly), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                InstalledApiCatalog.EnsureAllowedOpenedAssemblyPath(ExtendedWindowsPath(enabledRoot).ToUpperInvariant(), "game", opened);
                assert(true, "opened-handle admission accepts a long enabled root with casing and extended-prefix differences");
            }

            using (var opened = new FileStream(ExtendedWindowsPath(outsideAssembly), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var denied = IsDenied(enabledRoot, "game", opened);
                assert(denied, "opened-handle admission rejects an opened target outside the selected root");
            }

            using (var opened = new FileStream(ExtendedWindowsPath(disabledAssembly), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var denied = IsDenied(modRoot, "mod", opened);
                assert(denied, "opened-handle admission rejects an opened target in a disabled mod folder");
            }
        }
        finally
        {
            Directory.Delete(ExtendedWindowsPath(fixtureRoot), recursive: true);
        }

        static bool IsDenied(string root, string origin, FileStream opened)
        {
            try
            {
                InstalledApiCatalog.EnsureAllowedOpenedAssemblyPath(root, origin, opened);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        static string ExtendedWindowsPath(string path) =>
            path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..]
                : @"\\?\" + path;
    }

    private static async Task CheckXmlDocumentationAsync(
        Func<string, object, Task<JsonDocument>> apiToolJsonAsync, Action<bool, string> assert, ApiReuseContext context)
    {
        var xmlPath = Path.ChangeExtension(context.GameSharedAssemblyPath, ".xml");
        var originalXml = await File.ReadAllBytesAsync(xmlPath);
        var originalTimestamp = File.GetLastWriteTimeUtc(xmlPath);
        try
        {
            using var initialAssemblies = await apiToolJsonAsync("api_assemblies",
                new { name = "Fixture.Shared", origin = "game", limit = 50 });
            var initialAssembly = initialAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single();
            var assemblyId = initialAssembly.GetProperty("assemblyId").GetString()!;
            var initialSnapshot = initialAssemblies.RootElement.GetProperty("snapshotId").GetString()!;
            if (OperatingSystem.IsWindows())
            {
                var provider = InstalledApiCatalog.CreateXmlDocumentationProvider(
                    context.GameManagedDirectory, xmlPath, Path.GetFullPath(xmlPath),
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalXml)));
                try
                {
                    var changedProviderXml = System.Text.Encoding.UTF8.GetString(originalXml)
                        .Replace("XML_DOC_MEMBER_SUMMARY", "XML_DOC_CHANGED_MEMBER_SUMMARY", StringComparison.Ordinal);
                    await File.WriteAllTextAsync(xmlPath, changedProviderXml);
                    var providerText = provider.GetDocumentation(
                        "M:Fixture.Shared.XmlDocumentationSurface.DocumentedOperation(System.String)");
                    assert(providerText is not null &&
                           providerText.Contains("XML_DOC_MEMBER_SUMMARY", StringComparison.Ordinal) &&
                           !providerText.Contains("XML_DOC_CHANGED_MEMBER_SUMMARY", StringComparison.Ordinal),
                        "XML provider serves its admitted, hashed bytes after the sidecar changes");
                }
                finally
                {
                    await File.WriteAllBytesAsync(xmlPath, originalXml);
                    File.SetLastWriteTimeUtc(xmlPath, originalTimestamp);
                }
            }

            assert(initialAssembly.GetProperty("xmlDocumentation").GetProperty("status").GetString() == "available",
                "matching XML sidecar is reported available");

            using var typeSearch = await apiToolJsonAsync("api_search",
                new { query = "XmlDocumentationSurface", assemblyId, kind = "type" });
            var typeHit = typeSearch.RootElement.GetProperty("results").EnumerateArray().Single();
            var typeSymbolId = typeHit.GetProperty("symbolId").GetString()!;
            assert(typeHit.GetProperty("xmlDocumentation").GetProperty("summary").GetString() ==
                   "XML_DOC_TYPE_SUMMARY describes the moonstone fixture surface.",
                "metadata name search returns a symbol with its XML-only documentation");

            using var typeDetails = await apiToolJsonAsync("api_type", new { assemblyId, typeSymbolId });
            var typeDocumentation = typeDetails.RootElement.GetProperty("xmlDocumentation");
            var documentedMember = typeDetails.RootElement.GetProperty("members").EnumerateArray()
                .Single(member => member.GetProperty("name").GetString() == "DocumentedOperation");
            var memberDocumentation = documentedMember.GetProperty("xmlDocumentation");
            assert(typeDocumentation.GetProperty("remarks").GetString() ==
                       "XML_DOC_TYPE_REMARKS remain separate from the summary." &&
                   typeDocumentation.GetProperty("examples")[0].GetString() ==
                       "XML_DOC_TYPE_EXAMPLE demonstrates normalized example text." &&
                   memberDocumentation.GetProperty("parameters")[0].GetProperty("name").GetString() == "target" &&
                   memberDocumentation.GetProperty("parameters")[0].GetProperty("description").GetString() ==
                       "XML_DOC_PARAMETER_DESCRIPTION identifies the operation target." &&
                   memberDocumentation.GetProperty("returns").GetString() ==
                       "XML_DOC_RETURN_DESCRIPTION returns the documented target.",
                "api_type exposes normalized type, parameter, return, and example documentation");

            var memberSummary = memberDocumentation.GetProperty("summary").GetString()!;
            assert(memberSummary ==
                   "XML_DOC_MEMBER_SUMMARY describes the documented operation at world point; generic T references Fixture.Shared.SharedSurface, plus null and XML_DOC_SEE_DISPLAY_LABEL.",
                "XML inline references render parameter/type-parameter names, cref/langword labels, and nested display text once");
            using var referenceSearch = await apiToolJsonAsync("api_search",
                new { query = "Fixture.Shared.SharedSurface", assemblyId, kind = "method" });
            var referenceHit = referenceSearch.RootElement.GetProperty("results").EnumerateArray()
                .FirstOrDefault(hit => hit.GetProperty("qualifiedName").GetString() ==
                                       "Fixture.Shared.XmlDocumentationSurface.DocumentedOperation");
            assert(referenceHit.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                   referenceHit.GetProperty("xmlDocumentation").GetProperty("summary").GetString() == memberSummary,
                "api_search finds a cref identifier rendered from XML documentation");
            string? lookupContinuation = null;
            string? lookupSnapshotId = null;
            var lookupFound = false;
            for (var pageIndex = 0; pageIndex < 32; pageIndex++)
            {
                using var inlineLookup = await apiToolJsonAsync("api_lookup", new
                {
                    query = "Fixture.Shared.SharedSurface", assemblyId, kind = "method",
                    maxResults = 5, maxChars = 16000, includeExamples = false,
                    continuation = lookupContinuation, snapshotId = lookupSnapshotId,
                });
                var lookupRoot = inlineLookup.RootElement;
                var lookupHit = lookupRoot.GetProperty("results").EnumerateArray()
                    .FirstOrDefault(hit => hit.GetProperty("qualifiedName").GetString() ==
                                           "Fixture.Shared.XmlDocumentationSurface.DocumentedOperation");
                if (lookupHit.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                {
                    assert(lookupHit.GetProperty("relevance").GetProperty("documentationMatch").GetBoolean(),
                        "api_lookup ranks a cref identifier as XML documentation evidence");
                    lookupFound = true;
                    break;
                }
                if (!lookupRoot.GetProperty("hasMore").GetBoolean())
                    break;
                lookupContinuation = lookupRoot.GetProperty("continuation").GetString();
                lookupSnapshotId = lookupRoot.GetProperty("snapshotId").GetString();
                if (string.IsNullOrEmpty(lookupContinuation))
                    break;
            }
            assert(lookupFound, "api_lookup continuation pages find a method documented by a cref identifier");

            using var source = await apiToolJsonAsync("api_decompile", new
            {
                assemblyId,
                symbolId = documentedMember.GetProperty("symbolId").GetString(),
                startLine = 1,
                lineCount = 40,
            });
            assert(source.RootElement.GetProperty("source").GetString()!
                       .Contains("XML_DOC_MEMBER_SUMMARY", StringComparison.Ordinal),
                "api_decompile emits comments from the matching sidecar");

            var oversizedXml = System.Text.Encoding.UTF8.GetString(originalXml)
                .Replace("XML_DOC_TYPE_SUMMARY describes the moonstone fixture surface.", new string('x', 5000),
                    StringComparison.Ordinal);
            await File.WriteAllTextAsync(xmlPath, oversizedXml);
            using var oversizedSearch = await apiToolJsonAsync("api_search",
                new { query = "XmlDocumentationSurface", assemblyId, kind = "type" });
            var oversizedDocumentation = oversizedSearch.RootElement.GetProperty("results")[0]
                .GetProperty("xmlDocumentation");
            assert(oversizedDocumentation.GetProperty("summary").GetString()!.Length <= 4 * 1024 &&
                   oversizedDocumentation.GetProperty("truncated").GetBoolean(),
                "per-symbol documentation is capped at 4 KiB and marked truncated");

            var mismatchedXml = System.Text.Encoding.UTF8.GetString(originalXml)
                .Replace("<name>Fixture.Shared</name>", "<name>Fixture.Other</name>", StringComparison.Ordinal);
            await File.WriteAllTextAsync(xmlPath, mismatchedXml);
            using var mismatchedAssemblies = await apiToolJsonAsync("api_assemblies",
                new { name = "Fixture.Shared", origin = "game", limit = 50 });
            var mismatchedAssembly = mismatchedAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single();
            var mismatchedDiagnostic = mismatchedAssembly.GetProperty("xmlDocumentation").GetProperty("diagnostic").GetString();
            assert(mismatchedAssembly.GetProperty("xmlDocumentation").GetProperty("status").GetString() == "invalid" &&
                   mismatchedDiagnostic is { Length: > 0 and <= 300 } &&
                   mismatchedDiagnostic.Contains("does not match", StringComparison.Ordinal),
                "a sidecar with a mismatched XML assembly name is invalid with a bounded diagnostic");
            using var mismatchedSearch = await apiToolJsonAsync("api_search",
                new { query = "XmlDocumentationSurface", assemblyId, kind = "type" });
            assert(mismatchedSearch.RootElement.GetProperty("results").GetArrayLength() == 1 &&
                   !mismatchedSearch.RootElement.GetProperty("results")[0].TryGetProperty("xmlDocumentation", out _),
                "mismatched XML identity does not supply docs or disable metadata discovery");


            var changedXml = System.Text.Encoding.UTF8.GetString(originalXml)
                .Replace("XML_DOC_MEMBER_SUMMARY", "XML_DOC_CHANGED_MEMBER_SUMMARY", StringComparison.Ordinal);
            await File.WriteAllTextAsync(xmlPath, changedXml);
            using var changedAssemblies = await apiToolJsonAsync("api_assemblies",
                new { name = "Fixture.Shared", origin = "game", limit = 50 });
            var changedAssembly = changedAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single();
            var changedSnapshot = changedAssemblies.RootElement.GetProperty("snapshotId").GetString()!;
            assert(changedSnapshot != initialSnapshot &&
                   changedAssembly.GetProperty("assemblyId").GetString() == assemblyId,
                "XML-only changes invalidate catalog snapshots without changing DLL-derived assembly IDs");
            using var changedSearch = await apiToolJsonAsync("api_search",
                new { query = "XmlDocumentationSurface", assemblyId, kind = "type" });
            var changedHit = changedSearch.RootElement.GetProperty("results").EnumerateArray().Single();
            assert(changedHit.GetProperty("symbolId").GetString() == typeSymbolId &&
                   changedHit.GetProperty("xmlDocumentation").GetProperty("summary").GetString() ==
                       "XML_DOC_TYPE_SUMMARY describes the moonstone fixture surface.",
                "XML-only changes rebuild the API index while preserving metadata-derived symbol IDs");
            using var changedMemberSearch = await apiToolJsonAsync("api_search",
                new { query = "DocumentedOperation", assemblyId, kind = "method" });
            assert(changedMemberSearch.RootElement.GetProperty("results")[0].GetProperty("xmlDocumentation")
                       .GetProperty("summary").GetString() ==
                   "XML_DOC_CHANGED_MEMBER_SUMMARY describes the documented operation at world point; generic T references Fixture.Shared.SharedSurface, plus null and XML_DOC_SEE_DISPLAY_LABEL.",
                "the rebuilt API index exposes updated sidecar text");

            File.Delete(xmlPath);
            using var missingAssemblies = await apiToolJsonAsync("api_assemblies",
                new { name = "Fixture.Shared", origin = "game", limit = 50 });
            var missingAssembly = missingAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single();
            assert(missingAssembly.GetProperty("xmlDocumentation").GetProperty("status").GetString() == "missing",
                "missing XML sidecar is reported without removing the assembly");
            using var missingSearch = await apiToolJsonAsync("api_search",
                new { query = "XmlDocumentationSurface", assemblyId, kind = "type" });
            assert(missingSearch.RootElement.GetProperty("results").GetArrayLength() == 1 &&
                   !missingSearch.RootElement.GetProperty("results")[0].TryGetProperty("xmlDocumentation", out _),
                "metadata lookup continues when the XML sidecar is missing");

            await File.WriteAllTextAsync(xmlPath, "<doc><assembly><name>Fixture.Shared</name>");
            using var invalidAssemblies = await apiToolJsonAsync("api_assemblies",
                new { name = "Fixture.Shared", origin = "game", limit = 50 });
            var invalidAssembly = invalidAssemblies.RootElement.GetProperty("assemblies").EnumerateArray().Single();
            var invalidDiagnostic = invalidAssembly.GetProperty("xmlDocumentation").GetProperty("diagnostic").GetString();
            assert(invalidAssembly.GetProperty("xmlDocumentation").GetProperty("status").GetString() == "invalid" &&
                   invalidDiagnostic is { Length: > 0 and <= 300 },
                "malformed XML is reported as invalid with a bounded diagnostic");
            using var invalidSearch = await apiToolJsonAsync("api_search",
                new { query = "XmlDocumentationSurface", assemblyId, kind = "type" });
            assert(invalidSearch.RootElement.GetProperty("results").GetArrayLength() == 1 &&
                   invalidSearch.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(item => item.GetString()!.Contains("Invalid XML documentation", StringComparison.Ordinal)),
                "metadata lookup continues and reports malformed XML diagnostics");
        }
        finally
        {
            await File.WriteAllBytesAsync(xmlPath, originalXml);
            File.SetLastWriteTimeUtc(xmlPath, originalTimestamp);
        }
    }
}
