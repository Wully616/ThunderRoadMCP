using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

public sealed class InstalledApiCatalog
{
    private const int MaxDllFiles = 20000;
    private const int MaxDiagnostics = 200;
    private const int MaxDiagnosticsChars = 600;
    private const int MaxAssemblyBytes = 256 * 1024 * 1024;
    private const int MaxResults = 50000;
    private const int MaxPageSize = 100;
    private const int MaxDependencies = 128;
    private const long MaxDependencyBytes = 512L * 1024 * 1024;
    private const int MaxDecompiledChars = 8 * 1024 * 1024;
    private const int MaxResponseChars = 16 * 1024 * 1024;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<string> AssembliesAsync(string? name, string? origin, int offset, int limit, string? snapshotId, CancellationToken cancellationToken)
    {
        if (name?.Length > 128)
            throw new ArgumentException("name may contain at most 128 characters.", nameof(name));
        ValidatePage(offset, limit, MaxPageSize);
        if (origin is not null && !origin.Equals("game", StringComparison.OrdinalIgnoreCase) && !origin.Equals("mod", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("origin must be 'game' or 'mod'.", nameof(origin));
        var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        var assemblies = snapshot.Assemblies.Where(a =>
            (name is null || a.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
            (origin is null || a.Origin.Equals(origin, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(a => a.Origin, StringComparer.Ordinal)
            .ThenBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.RelativePath, StringComparer.Ordinal)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToArray();
        if (offset > assemblies.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"offset {offset} exceeds the filtered assembly count {assemblies.Length}.");
        var page = assemblies.Skip(offset).Take(limit).Select(a => new
        {
            assemblyId = a.Id,
            name = a.Name,
            version = a.Version,
            moduleVersionId = a.ModuleVersionId,
            origin = a.Origin,
            relativePath = a.RelativePath,
        }).ToArray();
        var hasMore = offset + page.Length < assemblies.Length;
        return SerializeResponse(new
        {
            assemblies = page,
            total = assemblies.Length,
            offset,
            limit,
            hasMore,
            nextOffset = hasMore ? offset + page.Length : (int?)null,
            snapshotId = snapshot.Id,
            catalogTruncated = snapshot.CatalogTruncated,
            diagnostics = snapshot.Diagnostics,
        });
    }

    public async Task<string> SearchAsync(string query, string? assemblyId, string? kind, bool includeNonPublic,
        int offset, int limit, string? snapshotId, CancellationToken cancellationToken)
    {
        ValidatePage(offset, limit, MaxPageSize);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 128)
            throw new ArgumentException("query must contain 1 to 128 characters.", nameof(query));
        var kindFilter = ParseKind(kind);
        var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        var entries = SelectAssemblies(snapshot, assemblyId);
        var hits = new List<ApiHit>();
        var diagnostics = new List<string>(snapshot.Diagnostics);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var main = await LoadPeAsync(entry, cancellationToken);
                using var resolver = new RootedAssemblyResolver(snapshot, entry, main);
                var settings = CreateDecompilerSettings();
                var typeSystem = new DecompilerTypeSystem(main.File, resolver, settings);

                foreach (var type in typeSystem.MainModule.TypeDefinitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!includeNonPublic && type.EffectiveAccessibility() != Accessibility.Public)
                        continue;
                    var typeName = FormatTypeName(type.FullTypeName.FullName);
                    var typeKind = TypeKindName(type.Kind);
                    if ((kindFilter is null || kindFilter == "type" || kindFilter == typeKind) &&
                        Matches(query, type.Name, typeName))
                    {
                        AddHit(hits, CreateHit(entry, type, typeKind, typeName,
                            type.DeclaringTypeDefinition is null ? null : FormatTypeName(type.DeclaringTypeDefinition.FullTypeName.FullName), FormatTypeDeclaration(type),
                            type.EffectiveAccessibility(), type.IsStatic, ObsoleteWarnings(type)));
                    }

                    if (kindFilter is not null && kindFilter is not "member" && kindFilter is not "constructor" &&
                        kindFilter is not "method" && kindFilter is not "field" && kindFilter is not "property" && kindFilter is not "event")
                        continue;
                    foreach (var member in DeclaredSearchMembers(type, includeConstructors: true))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!includeNonPublic && member.EffectiveAccessibility() != Accessibility.Public)
                            continue;
                        var memberKind = MemberKind(member);
                        if (kindFilter is not null && kindFilter != "member" && kindFilter != memberKind)
                            continue;
                        var qualifiedName = $"{typeName}.{member.Name}";
                        if (!Matches(query, member.Name, qualifiedName))
                            continue;
                        var actualEntry = FindEntry(snapshot, member.ParentModule?.MetadataFile?.FileName) ?? entry;
                        AddHit(hits, CreateHit(actualEntry, member.MemberDefinition, memberKind, qualifiedName,
                            member.DeclaringTypeDefinition is null ? null : FormatTypeName(member.DeclaringTypeDefinition.FullTypeName.FullName), FormatMemberSignature(member),
                            member.EffectiveAccessibility(), member.IsStatic, ObsoleteWarnings(member.MemberDefinition)));
                    }
                }
                AddWarnings(diagnostics, resolver.Warnings.Select(w => $"{entry.Origin}/{entry.RelativePath}: {w}"));
            }
            catch (OperationCanceledException) { throw; }
            catch (AssemblyChangedException) { throw; }
            catch (ResultLimitException) { throw; }
            catch (Exception exception) when (IsAssemblyReadFailure(exception))
            {
                AddDiagnostic(diagnostics, $"Could not inspect {entry.Origin}/{entry.RelativePath}: {exception.Message}");
            }
        }
        if (hits.Count > MaxResults)
            throw new InvalidOperationException($"Search produced more than {MaxResults} hits; narrow the query with assemblyId or kind.");
        var ordered = hits.OrderBy(h => h.assemblyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.origin, StringComparer.Ordinal)
            .ThenBy(h => h.relativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.qualifiedName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.symbolId, StringComparer.Ordinal)
            .ToArray();
        if (offset > ordered.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"offset {offset} exceeds the result count {ordered.Length}.");
        var page = ordered.Skip(offset).Take(limit).ToArray();
        var hasMore = offset + page.Length < ordered.Length;
        return SerializeResponse(new
        {
            results = page,
            total = ordered.Length,
            offset,
            limit,
            hasMore,
            nextOffset = hasMore ? offset + page.Length : (int?)null,
            snapshotId = snapshot.Id,
            diagnostics,
        });
    }

    public async Task<string> TypeAsync(string assemblyId, string typeSymbolId, bool includeInherited, bool includeNonPublic,
        int offset, int limit, string? snapshotId, CancellationToken cancellationToken)
    {
        ValidatePage(offset, limit, MaxPageSize);
        var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        var entry = SelectAssembly(snapshot, assemblyId);
        using var main = await LoadPeAsync(entry, cancellationToken);
        using var resolver = new RootedAssemblyResolver(snapshot, entry, main);
        var typeSystem = new DecompilerTypeSystem(main.File, resolver, CreateDecompilerSettings());
        var type = FindType(typeSystem.MainModule, entry, typeSymbolId)
            ?? throw new InvalidOperationException("typeSymbolId is invalid, stale, or does not identify a type in assemblyId.");
        var typeWarnings = ObsoleteWarnings(type);
        var options = includeInherited ? GetMemberOptions.None : GetMemberOptions.IgnoreInheritedMembers;
        var members = new List<ApiHit>();
        foreach (var member in type.GetMembers(null, options).Cast<IMember>().Concat(type.GetConstructors(null, options)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!includeNonPublic && member.EffectiveAccessibility() != Accessibility.Public)
                continue;
            var hit = CreateMemberHit(snapshot, entry, type, member, typeWarnings);
            if (hit is not null)
                members.Add(hit);
            if (members.Count > MaxResults)
                throw new InvalidOperationException($"Type has more than {MaxResults} members; request a narrower non-public/inherited surface.");
        }
        var orderedMembers = members.OrderBy(m => m.qualifiedName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.symbolId, StringComparer.Ordinal).ToArray();
        if (offset > orderedMembers.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"offset {offset} exceeds the member count {orderedMembers.Length}.");
        var page = orderedMembers.Skip(offset).Take(limit).ToArray();
        var baseTypes = type.DirectBaseTypes.ToArray();
        var baseType = baseTypes.FirstOrDefault(t => t.GetDefinition()?.Kind != TypeKind.Interface);
        var interfaces = baseTypes.Where(t => t.GetDefinition()?.Kind == TypeKind.Interface).Select(FormatType).ToArray();
        var hasMore = offset + page.Length < orderedMembers.Length;
        AddWarnings(snapshot.Diagnostics, resolver.Warnings.Select(w => $"{entry.Origin}/{entry.RelativePath}: {w}"));
        AddWarnings(typeWarnings, resolver.Warnings);
        return SerializeResponse(new
        {
            assemblyId = entry.Id,
            assemblyName = entry.Name,
            origin = entry.Origin,
            relativePath = entry.RelativePath,
            symbolId = typeSymbolId,
            qualifiedName = FormatTypeName(type.FullTypeName.FullName),
            typeKind = TypeKindName(type.Kind),
            typeDeclaration = FormatTypeDeclaration(type),
            baseType = baseType is null ? null : FormatType(baseType),
            interfaces,
            genericConstraints = FormatConstraints(type.TypeParameters),
            warnings = typeWarnings,
            members = page,
            total = orderedMembers.Length,
            offset,
            limit,
            hasMore,
            nextOffset = hasMore ? offset + page.Length : (int?)null,
            snapshotId = snapshot.Id,
            diagnostics = snapshot.Diagnostics,
        });
    }

    public async Task<string> DecompileAsync(string assemblyId, string symbolId, int startLine, int lineCount,
        string? snapshotId, CancellationToken cancellationToken)
    {
        if (startLine < 1)
            throw new ArgumentOutOfRangeException(nameof(startLine), "startLine must be at least 1.");
        if (lineCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(lineCount), "lineCount must be between 1 and 500.");
        var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        var entry = SelectAssembly(snapshot, assemblyId);
        using var main = await LoadPeAsync(entry, cancellationToken);
        using var resolver = new RootedAssemblyResolver(snapshot, entry, main);
        var settings = CreateDecompilerSettings();
        var typeSystem = new DecompilerTypeSystem(main.File, resolver, settings);
        var entity = FindEntity(typeSystem.MainModule, entry, symbolId)
            ?? throw new InvalidOperationException("symbolId is invalid, stale, or does not identify a type/member in assemblyId.");
        var warnings = ObsoleteWarnings(entity).ToList();
        string source;
        if (entity is IMethod method && !method.HasBody)
        {
            source = FormatMemberSignature(method) + ";";
            warnings.Add("The selected method has no managed IL body (for example, it is abstract, extern, or has no implementation); no body was reconstructed.");
        }
        else
        {
            var decompiler = new CSharpDecompiler(typeSystem, settings) { CancellationToken = cancellationToken };
            source = entity is ITypeDefinition definition
                ? decompiler.DecompileTypeAsString(definition.FullTypeName)
                : decompiler.DecompileAsString(entity.MetadataToken);
            AddWarnings(warnings, decompiler.Errors.Select(CSharpDecompiler.GetErrorHeadline));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > MaxDecompiledChars)
            throw new InvalidOperationException($"Reconstructed source exceeds the {MaxDecompiledChars} character safety limit; select a smaller member.");
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var totalLines = 1;
        for (var i = 0; i < normalizedSource.Length; i++)
        {
            if ((i & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (normalizedSource[i] == '\n') totalLines++;
        }
        var first = startLine - 1;
        if (first >= totalLines)
            throw new ArgumentOutOfRangeException(nameof(startLine), $"startLine {startLine} exceeds the source line count {totalLines}.");
        var page = new string[Math.Min(lineCount, totalLines - first)];
        var sourceLine = 0;
        var pageIndex = 0;
        var lineStart = 0;
        for (var i = 0; i <= normalizedSource.Length; i++)
        {
            if ((i & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (i < normalizedSource.Length && normalizedSource[i] != '\n') continue;
            if (sourceLine >= first && pageIndex < page.Length)
                page[pageIndex++] = normalizedSource[lineStart..i];
            sourceLine++;
            lineStart = i + 1;
        }
        var hasMore = first + page.Length < totalLines;
        AddWarnings(warnings, resolver.Warnings);
        AddWarnings(snapshot.Diagnostics, resolver.Warnings.Select(w => $"{entry.Origin}/{entry.RelativePath}: {w}"));
        return SerializeResponse(new
        {
            assemblyId = entry.Id,
            assemblyName = entry.Name,
            origin = entry.Origin,
            relativePath = entry.RelativePath,
            symbolId,
            qualifiedName = entity is ITypeDefinition type ? FormatTypeName(type.FullTypeName.FullName) :
                $"{FormatTypeName(entity.DeclaringTypeDefinition?.FullTypeName.FullName ?? string.Empty)}.{entity.Name}",
            sourceKind = "reconstructed-csharp",
            provenance = new { origin = entry.Origin, relativePath = entry.RelativePath, assemblyName = entry.Name, assemblyId = entry.Id, symbolId },
            source = string.Join('\n', page),
            startLine,
            totalLines,
            lineCount = page.Length,
            hasMore,
            nextLine = hasMore ? startLine + page.Length : (int?)null,
            snapshotId = snapshot.Id,
            warnings,
            diagnostics = snapshot.Diagnostics,
        });
    }

    private static string SerializeResponse(object value)
    {
        var json = JsonSerializer.Serialize(value);
        if (json.Length > MaxResponseChars)
            throw new InvalidOperationException($"Response exceeds the {MaxResponseChars} character safety limit; narrow the query or request a smaller page.");
        return json;
    }

    private static DecompilerSettings CreateDecompilerSettings() => new()
    {
        LoadInMemory = true,
        ThrowOnAssemblyResolveErrors = false,
    };

    private async Task<CatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        var options = await GameInstallConfiguration.ReadLaunchOptionsAsync(cancellationToken);
        var roots = new[]
        {
            new InstallRoot("game", GameInstallConfiguration.ManagedDirectory(options)),
            new InstallRoot("mod", GameInstallConfiguration.ModsDirectory(options)),
        };
        var diagnostics = new List<string>();
        var entries = new List<AssemblyEntry>();
        var fingerprints = new List<string>();
        var canonicalRoots = new List<string>();
        var truncated = false;
        foreach (var configuredRoot in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root;
            try { root = ResolveCanonicalPath(configuredRoot.Path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AddDiagnostic(diagnostics, $"{configuredRoot.Origin} root could not be canonicalized: {exception.Message}");
                canonicalRoots.Add(configuredRoot.Origin + "|<unavailable>");
                continue;
            }
            canonicalRoots.Add(configuredRoot.Origin + "|" + root);
            if (!Directory.Exists(root))
            {
                AddDiagnostic(diagnostics, $"{configuredRoot.Origin} root is missing: {root}");
                continue;
            }
            var files = EnumerateDllFiles(root, configuredRoot.Origin, diagnostics, cancellationToken, ref truncated);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxAssemblyBytes)
                    {
                        AddDiagnostic(diagnostics, $"Ignored {configuredRoot.Origin}/{relativePath}: DLL exceeds the {MaxAssemblyBytes} byte scan limit.");
                        fingerprints.Add($"{configuredRoot.Origin}|{relativePath}|oversize|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
                        continue;
                    }
                    var bytes = await ReadEntryBytesAsync(root, file, cancellationToken);
                    if (bytes.LongLength > MaxAssemblyBytes)
                    {
                        AddDiagnostic(diagnostics, $"Ignored {configuredRoot.Origin}/{relativePath}: DLL exceeds the {MaxAssemblyBytes} byte scan limit.");
                        fingerprints.Add($"{configuredRoot.Origin}|{relativePath}|oversize|{bytes.LongLength}");
                        continue;
                    }
                    var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
                    fingerprints.Add($"{configuredRoot.Origin}|{relativePath}|{contentHash}");
                    using var loaded = OpenPe(file, bytes);
                    var metadata = loaded.File.Metadata;
                    if (!metadata.IsAssembly)
                    {
                        AddDiagnostic(diagnostics, $"Ignored {configuredRoot.Origin}/{relativePath}: managed PE is a module, not an assembly.");
                        continue;
                    }
                    var definition = metadata.GetAssemblyDefinition();
                    var assemblyName = metadata.GetString(definition.Name);
                    var version = definition.Version.ToString();
                    var moduleVersionId = metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D");
                    var id = MakeAssemblyId(configuredRoot.Origin, root, relativePath, contentHash);
                    entries.Add(new AssemblyEntry(id, assemblyName, version, moduleVersionId, configuredRoot.Origin, root, relativePath, file, contentHash));
                }
                catch (OperationCanceledException) { throw; }
                catch (BadImageFormatException)
                {
                    AddDiagnostic(diagnostics, $"Ignored {configuredRoot.Origin}/{relativePath}: file is native or not a valid managed assembly.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
                {
                    AddDiagnostic(diagnostics, $"Could not read {configuredRoot.Origin}/{relativePath}: {exception.Message}");
                    fingerprints.Add($"{configuredRoot.Origin}|{relativePath}|unreadable");
                }
            }
        }
        entries = entries.OrderBy(e => e.Origin, StringComparer.Ordinal)
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.RelativePath, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        var snapshotParts = canonicalRoots.Concat(fingerprints.OrderBy(f => f, StringComparer.Ordinal));
        var snapshotText = JsonSerializer.Serialize(snapshotParts);
        var snapshotId = "snap_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotText))).Substring(0, 32);
        return new CatalogSnapshot(snapshotId, entries, diagnostics, truncated);
    }

    private static IEnumerable<string> EnumerateDllFiles(string root, string origin, List<string> diagnostics,
        CancellationToken cancellationToken, ref bool truncated)
    {
        var files = new List<string>();
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = stack.Pop();
            if (depth > 40)
            {
                AddDiagnostic(diagnostics, $"Stopped scanning {origin}/{Path.GetRelativePath(root, directory)}: directory depth limit reached.");
                truncated = true;
                continue;
            }
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(p => p, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            AddDiagnostic(diagnostics, $"Skipped reparse point under {origin}/{Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')}; links are not traversed.");
                            continue;
                        }
                        var canonical = ResolveCanonicalPath(path);
                        if (!IsUnderRoot(canonical, root))
                        {
                            AddDiagnostic(diagnostics, $"Skipped path outside the canonical {origin} root: {Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')}.");
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            stack.Push((canonical, depth + 1));
                        }
                        else if (Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            if (files.Count == MaxDllFiles)
                            {
                                AddDiagnostic(diagnostics, $"Stopped scanning {origin}: DLL file limit {MaxDllFiles} reached.");
                                truncated = true;
                                return files;
                            }
                            files.Add(canonical);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        AddDiagnostic(diagnostics, $"Could not inspect {origin}/{Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')}: {exception.Message}");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AddDiagnostic(diagnostics, $"Could not enumerate {origin}/{Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/')}: {exception.Message}");
            }
        }
        return files.OrderBy(p => Path.GetRelativePath(root, p), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => Path.GetRelativePath(root, p), StringComparer.Ordinal).ToArray();
    }

    private static LoadedPe OpenPe(string path, byte[] bytes)
    {
        var stream = new MemoryStream(bytes, writable: false);
        try
        {
            var file = new PEFile(path, stream, PEStreamOptions.PrefetchEntireImage);
            return new LoadedPe(file, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private async Task<LoadedPe> LoadPeAsync(AssemblyEntry entry, CancellationToken cancellationToken)
    {
        var bytes = await ReadEntryBytesAsync(entry.Root, entry.FullPath, cancellationToken);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.ContentHash, StringComparison.Ordinal))
            throw new AssemblyChangedException($"Assembly '{entry.Origin}/{entry.RelativePath}' changed during the request; rerun api_assemblies and use its fresh IDs.");
        return OpenPe(entry.FullPath, bytes);
    }

    private static async Task<byte[]> ReadEntryBytesAsync(string root, string fullPath, CancellationToken cancellationToken)
    {
        var canonical = ResolveCanonicalPath(fullPath);
        if (!IsUnderRoot(canonical, root))
            throw new IOException("Resolved assembly path leaves its configured installation root.");
        await using var stream = new FileStream(canonical, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxAssemblyBytes)
            throw new IOException($"Assembly exceeds the {MaxAssemblyBytes} byte scan limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1 || stream.Length != bytes.LongLength)
            throw new AssemblyChangedException("Assembly changed while it was being read.");
        var after = ResolveCanonicalPath(fullPath);
        if (!IsUnderRoot(after, root) || !PathComparer.Equals(canonical, after))
            throw new AssemblyChangedException("Assembly path changed while it was being read.");
        return bytes;
    }

    private static void ValidatePage(int offset, int limit, int maxLimit)
    {
        if (offset < 0 || offset > MaxResults)
            throw new ArgumentOutOfRangeException(nameof(offset), $"offset must be between 0 and {MaxResults}.");
        if (limit < 1 || limit > maxLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be between 1 and {maxLimit}.");
    }

    private static string? ParseKind(string? kind)
    {
        if (kind is null) return null;
        var normalized = kind.Trim().ToLowerInvariant();
        if (normalized is "type" or "member" or "class" or "struct" or "interface" or "enum" or "delegate" or "constructor" or "method" or "field" or "property" or "event")
            return normalized;
        throw new ArgumentException("kind must be type/member, class/struct/interface/enum/delegate, or constructor/method/field/property/event.", nameof(kind));
    }

    private static void CheckSnapshot(CatalogSnapshot snapshot, string? expected)
    {
        if (expected is not null && !expected.Equals(snapshot.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("snapshotId is stale; installed files changed since the previous page; restart the query.");
    }

    private static AssemblyEntry[] SelectAssemblies(CatalogSnapshot snapshot, string? assemblyId)
    {
        if (assemblyId is null) return snapshot.Assemblies.ToArray();
        return new[] { SelectAssembly(snapshot, assemblyId) };
    }

    private static AssemblyEntry SelectAssembly(CatalogSnapshot snapshot, string assemblyId) =>
        snapshot.Assemblies.FirstOrDefault(a => a.Id.Equals(assemblyId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException("assemblyId is invalid or stale; rerun api_assemblies and use a current catalog-issued ID.");

    private static ITypeDefinition? FindType(MetadataModule module, AssemblyEntry entry, string symbolId) =>
        module.TypeDefinitions.FirstOrDefault(type => !type.MetadataToken.IsNil && SymbolId(entry.Id, type.MetadataToken).Equals(symbolId, StringComparison.Ordinal));

    private static IEntity? FindEntity(MetadataModule module, AssemblyEntry entry, string symbolId)
    {
        foreach (var type in module.TypeDefinitions)
        {
            if (!type.MetadataToken.IsNil && SymbolId(entry.Id, type.MetadataToken).Equals(symbolId, StringComparison.Ordinal))
                return type;
            foreach (var member in DeclaredSearchMembers(type, includeConstructors: true))
            {
                if (!member.MetadataToken.IsNil && SymbolId(entry.Id, member.MetadataToken).Equals(symbolId, StringComparison.Ordinal))
                    return member;
            }
        }
        return null;
    }

    private static IEnumerable<IMember> DeclaredSearchMembers(ITypeDefinition type) => DeclaredSearchMembers(type, includeConstructors: false);

    private static IEnumerable<IMember> DeclaredSearchMembers(ITypeDefinition type, bool includeConstructors)
    {
        foreach (var member in type.Members)
        {
            if (member is IMethod method && (method.IsAccessor || method.IsLocalFunction || method.IsConstructor))
                continue;
            yield return member;
        }
        if (includeConstructors)
            foreach (var constructor in type.Methods.Where(m => m.IsConstructor))
                yield return constructor;
    }

    private static ApiHit? CreateMemberHit(CatalogSnapshot snapshot, AssemblyEntry fallbackEntry, ITypeDefinition requestedType,
        IMember member, List<string> warnings)
    {
        var definition = member.MemberDefinition;
        var declaringType = definition.DeclaringTypeDefinition is null ? null : FormatTypeName(definition.DeclaringTypeDefinition.FullTypeName.FullName);
        var actualEntry = FindEntry(snapshot, member.ParentModule?.MetadataFile?.FileName);
        var inherited = (actualEntry is not null && !actualEntry.Id.Equals(fallbackEntry.Id, StringComparison.Ordinal)) ||
            !string.Equals(declaringType, FormatTypeName(requestedType.FullTypeName.FullName), StringComparison.Ordinal);
        if (actualEntry is null && inherited)
        {
            AddDiagnostic(warnings, $"Omitted inherited member '{declaringType}.{definition.Name}': its declaring assembly is outside the installed catalog.");
            return null;
        }
        actualEntry ??= fallbackEntry;
        var qualifiedName = $"{declaringType}.{definition.Name}";
        return CreateHit(actualEntry, definition, MemberKind(definition), qualifiedName, declaringType,
            FormatMemberSignature(member), member.EffectiveAccessibility(), member.IsStatic,
            ObsoleteWarnings(definition), inherited);
    }

    private static ApiHit CreateHit(AssemblyEntry entry, IEntity entity, string kind, string qualifiedName, string? declaringType,
        string signature, Accessibility accessibility, bool isStatic, List<string> warnings, bool isInherited = false) =>
        new(entry.Id, entry.Name, entry.Origin, entry.RelativePath, SymbolId(entry.Id, entity.MetadataToken), kind,
            qualifiedName, declaringType, signature, AccessibilityKeyword(accessibility).ToLowerInvariant(), isStatic,
            warnings, isInherited);

    private static AssemblyEntry? FindEntry(CatalogSnapshot snapshot, string? metadataPath)
    {
        if (metadataPath is null) return null;
        var path = Path.GetFullPath(metadataPath);
        return snapshot.Assemblies.FirstOrDefault(a => PathComparer.Equals(a.FullPath, path));
    }

    private static void AddHit(List<ApiHit> hits, ApiHit hit)
    {
        hits.Add(hit);
        if (hits.Count > MaxResults)
            throw new ResultLimitException($"Search produced more than {MaxResults} hits; narrow the query with assemblyId or kind.");
    }

    private static bool Matches(string query, string name, string qualifiedName) =>
        name.Contains(query, StringComparison.OrdinalIgnoreCase) || qualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string MakeAssemblyId(string origin, string root, string relativePath, string contentHash)
    {
        var value = JsonSerializer.Serialize(new[] { origin, root, relativePath, contentHash });
        return "asm_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).Substring(0, 32);
    }

    private static string SymbolId(string assemblyId, EntityHandle token)
    {
        if (token.IsNil) throw new InvalidOperationException("Metadata did not provide a stable symbol token.");
        var value = $"{assemblyId}:{MetadataTokens.GetToken(token):X8}";
        return "sym_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).Substring(0, 32);
    }

    private static string MemberKind(IMember member) => member switch
    {
        IMethod method when method.IsConstructor => "constructor",
        IMethod => "method",
        IField => "field",
        IProperty => "property",
        IEvent => "event",
        _ => "member",
    };

    private static string TypeKindName(TypeKind kind) => kind switch
    {
        TypeKind.Class => "class",
        TypeKind.Struct => "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => "type",
    };

    private static string FormatTypeDeclaration(ITypeDefinition type)
    {
        var modifiers = new List<string>();
        var accessibility = AccessibilityKeyword(type.Accessibility);
        if (accessibility.Length > 0) modifiers.Add(accessibility);
        if (type.IsStatic) modifiers.Add("static");
        else if (type.Kind == TypeKind.Class && type.IsAbstract) modifiers.Add("abstract");
        else if (type.Kind == TypeKind.Class && type.IsSealed) modifiers.Add("sealed");
        var name = FormatTypeName(type.Name);
        if (type.TypeParameters.Count > 0)
            name += "<" + string.Join(", ", type.TypeParameters.Select(p => p.Name)) + ">";
        if (type.Kind == TypeKind.Delegate && type.Methods.FirstOrDefault(m => m.Name == "Invoke") is { } invoke)
        {
            var declaration = string.Join(" ", modifiers.Append("delegate").Append(
                $"{FormatType(invoke.ReturnType)} {name}({string.Join(", ", invoke.Parameters.Select(FormatParameter))})"));
            var constraints = FormatConstraints(type.TypeParameters);
            return constraints.Length == 0 ? declaration : declaration + " " + string.Join(" ", constraints);
        }
        return string.Join(" ", modifiers.Append(TypeKindName(type.Kind)).Append(name));
    }

    private static string FormatMemberSignature(IMember member)
    {
        var modifiers = new List<string>();
        var accessibility = AccessibilityKeyword(member.Accessibility);
        if (accessibility.Length > 0) modifiers.Add(accessibility);
        if (member.IsStatic && (member is not IField constantField || !constantField.IsConst)) modifiers.Add("static");
        if (member is IMethod method)
        {
            if (method.IsAbstract) modifiers.Add("abstract");
            else if (method.IsOverride) modifiers.Add("override");
            else if (method.IsVirtual) modifiers.Add("virtual");
            var name = method.IsConstructor ? FormatTypeName(method.DeclaringTypeDefinition?.Name ?? method.Name) : method.Name;
            if (method.TypeParameters.Count > 0)
                name += "<" + string.Join(", ", method.TypeParameters.Select(p => p.Name)) + ">";
            var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));
            var prefix = method.IsConstructor ? string.Empty : FormatType(method.ReturnType) + " ";
            var signature = string.Join(" ", modifiers.Append(prefix + name + "(" + parameters + ")"));
            var constraints = FormatConstraints(method.TypeParameters);
            return constraints.Length == 0 ? signature : signature + " " + string.Join(" ", constraints);
        }
        if (member is IField field)
        {
            if (field.IsConst) modifiers.Add("const");
            else if (field.IsReadOnly) modifiers.Add("readonly");
            return string.Join(" ", modifiers.Append(FormatType(field.ReturnType) + " " + field.Name));
        }
        if (member is IProperty property)
        {
            var name = property.IsIndexer ? "this[" + string.Join(", ", property.Parameters.Select(FormatParameter)) + "]" : property.Name;
            var accessors = string.Join(" ", new[] { property.CanGet ? "get;" : null, property.CanSet ? "set;" : null }.Where(x => x is not null));
            return string.Join(" ", modifiers.Append(FormatType(property.ReturnType) + " " + name + " { " + accessors + " }"));
        }
        if (member is IEvent @event)
            return string.Join(" ", modifiers.Append("event " + FormatType(@event.ReturnType) + " " + @event.Name));
        return member.Name;
    }

    private static string FormatParameter(IParameter parameter)
    {
        var prefix = parameter.ReferenceKind switch
        {
            ReferenceKind.Ref => "ref ",
            ReferenceKind.Out => "out ",
            ReferenceKind.In or ReferenceKind.RefReadOnly => "in ",
            _ => "",
        };
        if (parameter.IsParams) prefix = "params ";
        var text = prefix + FormatType(parameter.Type) + " " + parameter.Name;
        if (parameter.IsOptional)
            text += " = " + (parameter.HasConstantValueInSignature ? FormatConstant(parameter.GetConstantValue()) : "default");
        return text;
    }

    private static string FormatConstant(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        string text => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
        char character => "'" + (character == '\'' ? "\\'" : character.ToString()) + "'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default",
    };

    private static string FormatTypeName(string name)
    {
        var result = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '`')
            {
                while (i + 1 < name.Length && char.IsAsciiDigit(name[i + 1])) i++;
                continue;
            }
            result.Append(name[i] == '+' ? '.' : name[i]);
        }
        return result.ToString();
    }

    private static string FormatType(IType type)
    {
        if (type is ITypeParameter parameter) return parameter.Name;
        string? keyword = type.FullName switch
        {
            "System.Void" => "void",
            "System.Boolean" => "bool",
            "System.Byte" => "byte",
            "System.SByte" => "sbyte",
            "System.Char" => "char",
            "System.Decimal" => "decimal",
            "System.Double" => "double",
            "System.Single" => "float",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.String" => "string",
            "System.Object" => "object",
            _ => null,
        };
        if (keyword is not null) return keyword;
        if (type is ArrayType array)
            return FormatType(array.ElementType) + "[" + new string(',', array.Dimensions - 1) + "]";
        if (type is ByReferenceType byReference) return FormatType(byReference.ElementType);
        if (type is PointerType pointer) return FormatType(pointer.ElementType) + "*";
        if (type is ParameterizedType parameterized)
        {
            var definition = parameterized.GetDefinition();
            var name = FormatTypeName(definition?.FullTypeName.FullName ?? parameterized.GenericType.FullName);
            return name + "<" + string.Join(", ", parameterized.TypeArguments.Select(FormatType)) + ">";
        }
        var typeDefinition = type.GetDefinition();
        if (typeDefinition is not null)
        {
            var name = FormatTypeName(typeDefinition.FullTypeName.FullName);
            if (typeDefinition.TypeParameters.Count > 0)
                name += "<" + string.Join(", ", typeDefinition.TypeParameters.Select(p => p.Name)) + ">";
            return name;
        }
        return type.FullName;
    }

    private static string[] FormatConstraints(IReadOnlyList<ITypeParameter> parameters) => parameters
        .Select(parameter =>
        {
            var constraints = new List<string>();
            if (parameter.HasUnmanagedConstraint) constraints.Add("unmanaged");
            else if (parameter.HasValueTypeConstraint) constraints.Add("struct");
            else if (parameter.HasReferenceTypeConstraint) constraints.Add(parameter.NullabilityConstraint == Nullability.Nullable ? "class?" : "class");
            constraints.AddRange(parameter.TypeConstraints.Select(c => FormatType(c.Type)));
            if (parameter.HasDefaultConstructorConstraint) constraints.Add("new()");
            if (parameter.AllowsRefLikeType) constraints.Add("allows ref struct");
            return constraints.Count == 0 ? null : $"where {parameter.Name} : {string.Join(", ", constraints)}";
        })
        .Where(c => c is not null).Cast<string>().ToArray();

    private static string AccessibilityKeyword(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        _ => "",
    };

    private static List<string> ObsoleteWarnings(IEntity entity)
    {
        var attribute = entity.GetAttributes().FirstOrDefault(a => a.AttributeType.FullName == "System.ObsoleteAttribute");
        if (attribute is null) return new List<string>();
        var message = attribute.FixedArguments.Length > 0 ? Convert.ToString(attribute.FixedArguments[0].Value, CultureInfo.InvariantCulture) : null;
        var isError = attribute.NamedArguments.Any(a => a.Name == "IsError" && a.Value is true);
        var warnings = new List<string>();
        AddDiagnostic(warnings, "Obsolete" + (isError ? " (error)" : "") + (string.IsNullOrWhiteSpace(message) ? "." : $": {message}"));
        return warnings;
    }

    private static bool IsAssemblyReadFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or ArgumentException;

    private static void AddDiagnostic(List<string> diagnostics, string message)
    {
        if (diagnostics.Count >= MaxDiagnostics) return;
        diagnostics.Add(message.Length <= MaxDiagnosticsChars ? message : message[..MaxDiagnosticsChars] + "…");
    }

    private static void AddWarnings(List<string> target, IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            if (target.Count >= MaxDiagnostics) break;
            AddDiagnostic(target, warning);
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        if (PathComparer.Equals(path, root)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string ResolveCanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(full) ?? throw new IOException("Path has no volume root.");
        var current = volumeRoot;
        var components = full[volumeRoot.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in components)
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            current = Path.GetFullPath(info.ResolveLinkTarget(true)?.FullName ?? throw new IOException($"Could not resolve link '{current}'."));
        }
        return Path.GetFullPath(current);
    }

    private static byte[] ReadEntryBytes(string root, string fullPath)
    {
        var canonical = ResolveCanonicalPath(fullPath);
        if (!IsUnderRoot(canonical, root))
            throw new IOException("Resolved assembly path leaves its configured installation root.");
        using var stream = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxAssemblyBytes)
            throw new IOException($"Assembly exceeds the {MaxAssemblyBytes} byte scan limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || stream.Length != bytes.LongLength)
            throw new AssemblyChangedException("Assembly changed while it was being read.");
        var after = ResolveCanonicalPath(fullPath);
        if (!IsUnderRoot(after, root) || !PathComparer.Equals(canonical, after))
            throw new AssemblyChangedException("Assembly path changed while it was being read.");
        return bytes;
    }

    private sealed class RootedAssemblyResolver : IAssemblyResolver, IDisposable
    {
        private readonly CatalogSnapshot _snapshot;
        private readonly AssemblyEntry _mainEntry;
        private readonly LoadedPe _main;
        private readonly Dictionary<string, LoadedPe> _opened = new(StringComparer.Ordinal);
        private long _openedBytes;
        private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _warnings = new();

        public IReadOnlyList<string> Warnings
        {
            get
            {
                var result = new List<string>(_warnings);
                if (_missing.Count > 0)
                    result.Add("Unresolved installed dependencies: " + string.Join(", ", _missing.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(20)) + (_missing.Count > 20 ? ", …" : ""));
                return result;
            }
        }

        public RootedAssemblyResolver(CatalogSnapshot snapshot, AssemblyEntry mainEntry, LoadedPe main)
        {
            _snapshot = snapshot;
            _mainEntry = mainEntry;
            _main = main;
        }

        public MetadataFile? Resolve(IAssemblyReference reference)
        {
            var candidates = _snapshot.Assemblies.Where(a => a.Name.Equals(reference.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0)
            {
                MarkMissing(reference.Name);
                return null;
            }
            var referencingPath = reference is ICSharpCode.Decompiler.Metadata.AssemblyReference { ReferencingModule: { } referencingModule }
                ? referencingModule.FileName
                : _mainEntry.FullPath;
            var preferredDirectory = Path.GetDirectoryName(referencingPath);
            candidates = candidates.OrderByDescending(a => a.Version.Equals(reference.Version?.ToString(), StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(a => PathComparer.Equals(Path.GetDirectoryName(a.FullPath), preferredDirectory))
                .ThenByDescending(a => a.Origin == _mainEntry.Origin)
                .ThenBy(a => a.Origin, StringComparer.Ordinal)
                .ThenBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.RelativePath, StringComparer.Ordinal).ToArray();
            if (candidates.Length > 1)
                AddWarning($"Assembly reference '{reference.Name}' is ambiguous among installed files; chose {candidates[0].Origin}/{candidates[0].RelativePath} deterministically.");
            var selected = candidates[0];
            if (selected.Id.Equals(_mainEntry.Id, StringComparison.Ordinal)) return _main.File;
            if (_opened.TryGetValue(selected.Id, out var alreadyOpen)) return alreadyOpen.File;
            if (_opened.Count >= MaxDependencies)
            {
                AddWarning($"Dependency resolution limit ({MaxDependencies}) reached; '{reference.Name}' was not opened.");
                MarkMissing(reference.Name);
                return null;
            }
            try
            {
                var bytes = ReadEntryBytes(selected.Root, selected.FullPath);
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(selected.ContentHash, StringComparison.Ordinal))
                    throw new AssemblyChangedException($"Installed dependency '{selected.Origin}/{selected.RelativePath}' changed during the request.");
                if (_openedBytes + bytes.LongLength > MaxDependencyBytes)
                {
                    AddWarning($"Dependency byte limit ({MaxDependencyBytes}) reached; '{reference.Name}' was not opened.");
                    MarkMissing(reference.Name);
                    return null;
                }
                var loaded = OpenPe(selected.FullPath, bytes);
                _opened.Add(selected.Id, loaded);
                _openedBytes += bytes.LongLength;
                return loaded.File;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or InvalidOperationException or OverflowException) && exception is not AssemblyChangedException)
            {
                AddWarning($"Could not read installed dependency '{selected.Origin}/{selected.RelativePath}': {exception.Message}");
                MarkMissing(reference.Name);
                return null;
            }
        }

        public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
        {
            AddWarning($"Linked module '{moduleName}' was not resolved; only catalogued managed assemblies are supported.");
            return null;
        }

        public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));
        public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));
        public IDisposable? BeginSnapshot() => null;

        private void MarkMissing(string name) => _missing.Add(name.Length <= 128 ? name : name[..128] + "…");

        private void AddWarning(string warning)
        {
            if (_warnings.Count >= 50 || _warnings.Contains(warning, StringComparer.Ordinal)) return;
            _warnings.Add(warning.Length <= MaxDiagnosticsChars ? warning : warning[..MaxDiagnosticsChars] + "…");
        }

        public void Dispose()
        {
            foreach (var pe in _opened.Values) pe.Dispose();
            _opened.Clear();
        }
    }


    private sealed record InstallRoot(string Origin, string Path);
    private sealed record AssemblyEntry(string Id, string Name, string Version, string ModuleVersionId, string Origin, string Root, string RelativePath, string FullPath, string ContentHash);
    private sealed record CatalogSnapshot(string Id, IReadOnlyList<AssemblyEntry> Assemblies, List<string> Diagnostics, bool CatalogTruncated);
    private sealed record ApiHit(string assemblyId, string assemblyName, string origin, string relativePath, string symbolId,
        string kind, string qualifiedName, string? declaringType, string signature, string accessibility, bool isStatic,
        List<string> warnings, bool isInherited = false);

    private sealed class LoadedPe : IDisposable
    {
        private readonly MemoryStream _stream;
        public PEFile File { get; }
        public LoadedPe(PEFile file, MemoryStream stream) { File = file; _stream = stream; }
        public void Dispose() { File.Dispose(); _stream.Dispose(); }
    }
    private sealed class AssemblyChangedException : IOException
    {
        public AssemblyChangedException(string message) : base(message) { }
    }

    private sealed class ResultLimitException : InvalidOperationException
    {
        public ResultLimitException(string message) : base(message) { }
    }
}
