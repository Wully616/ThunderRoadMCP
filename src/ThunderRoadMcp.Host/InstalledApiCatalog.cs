using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using System.Xml;

public sealed partial class InstalledApiCatalog
{
    private const int MaxDllFiles = 20000;
    private const int MaxDiagnostics = 200;
    private const int MaxDiagnosticsChars = 600;
    private const int MaxAssemblyBytes = 256 * 1024 * 1024;
    // ScanAsync still reads and hashes every enumerated DLL; this only avoids repeat reads within one request.
    private const long MaxSnapshotRetainedBytes = 64L * 1024 * 1024;
    private const int MaxAssemblyReadAttempts = 3;
    private const int MaxResults = 50000;
    private const int MaxPageSize = 100;
    private const int MaxDependencies = 128;
    private const long MaxDependencyBytes = 512L * 1024 * 1024;
    private const int MaxDecompiledChars = 8 * 1024 * 1024;
    private const int MaxResponseChars = 16 * 1024 * 1024;
    // ponytail: cap retained snapshots at two; raise with a larger shared byte budget.
    private static readonly SemaphoreSlim SnapshotLifetimeSlots = new(2, 2);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<string> AssembliesAsync(string? name, string? origin, int offset, int limit, string? snapshotId, CancellationToken cancellationToken)
    {
        if (name?.Length > 128)
            throw new ArgumentException("name may contain at most 128 characters.", nameof(name));
        ValidatePage(offset, limit, MaxPageSize);
        if (origin is not null && !origin.Equals("game", StringComparison.OrdinalIgnoreCase) && !origin.Equals("mod", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("origin must be 'game' or 'mod'.", nameof(origin));
        using var snapshot = await ScanAsync(cancellationToken);
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
            xmlDocumentation = new { status = a.XmlDocumentationStatus, diagnostic = a.XmlDocumentationDiagnostic },
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

    internal async Task<string> SearchAsync(string query, string? assemblyId, string? kind, bool includeNonPublic,
        int offset, int limit, string? snapshotId, int paginationVersion, string? scopeSnapshotId,
        CancellationToken cancellationToken, ResolvedApiScope scope, string? origin = null)
    {
        ValidatePage(offset, limit, MaxPageSize);
        if (paginationVersion is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(paginationVersion), "paginationVersion must be 1 or 2.");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 128)
            throw new ArgumentException("query must contain 1 to 128 characters.", nameof(query));
        var kindFilter = ParseKind(kind);
        if (origin is not null && !origin.Equals("game", StringComparison.OrdinalIgnoreCase) && !origin.Equals("mod", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("origin must be 'game' or 'mod'.", nameof(origin));
        origin = origin?.ToLowerInvariant();
        using var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        if (offset > 0)
        {
            if (paginationVersion == 2 && string.IsNullOrWhiteSpace(scopeSnapshotId))
                throw new ApiDiscoveryException("scope.snapshot_required",
                    "paginationVersion=2 requires scopeSnapshotId for offset pages; restart api_search at offset 0.",
                    new
                    {
                        tool = "api_search",
                        arguments = new
                        {
                            query, assemblyId, kind = kindFilter, origin, includeNonPublic,
                            offset = 0, limit, snapshotId = snapshot.Id, scope = scope.Requested, paginationVersion = 2,
                        },
                    });
            if (scopeSnapshotId is not null && !scopeSnapshotId.Equals(scope.ScopeSnapshotId, StringComparison.Ordinal))
                throw new ApiDiscoveryException("scope.stale",
                    "scopeSnapshotId does not match the current requested/effective assembly scope; restart api_search at offset 0.",
                    new
                    {
                        tool = "api_search",
                        arguments = new
                        {
                            query, assemblyId, kind = kindFilter, origin, includeNonPublic,
                            offset = 0, limit, snapshotId = snapshot.Id, scope = scope.Requested, paginationVersion = 2,
                        },
                    });
        }
        var (ordered, diagnostics) = await SearchHitsAsync(snapshot, query, assemblyId, kindFilter, includeNonPublic,
            cancellationToken, scope.LoadedAssemblies, scope.Diagnostic, origin);
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
            scope = scope.Requested,
            effectiveScope = scope.Effective,
            scopeSnapshotId = scope.ScopeSnapshotId,
            paginationVersion,
            scopeInventoryTruncated = scope.InventoryTruncated,
            partial = scope.InventoryTruncated,
            diagnostics,
        });
    }
    private async Task<(ApiHit[] Hits, List<string> Diagnostics)> SearchHitsAsync(CatalogSnapshot snapshot, string query,
        string? assemblyId, string? kindFilter, bool includeNonPublic, CancellationToken cancellationToken,
        IReadOnlySet<LoadedAssemblyIdentity>? loadedModAssemblies = null, string? loadedAssemblyDiagnostic = null, string? origin = null)
    {
        using var timing = ToolTiming.Measure("apiSearchMs");
        var hits = new List<ApiHit>();
        var documentationQueryTokens = ApiLookupRanker.Tokenize(query);
        var diagnostics = new List<string>(snapshot.Diagnostics);
        if (loadedAssemblyDiagnostic is not null)
            AddDiagnostic(diagnostics, loadedAssemblyDiagnostic);
        foreach (var entry in SelectAssemblies(snapshot, assemblyId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (origin is not null && entry.Origin != origin)
                continue;
            if (loadedModAssemblies is not null && entry.Origin == "mod" && !Matches(entry, loadedModAssemblies))
                continue;
            try
            {
                using var indexLease = await GetApiIndexAsync(snapshot, entry, cancellationToken);
                if (!indexLease.Index.Complete)
                    AddDiagnostic(diagnostics, IndexOmissionMessage(entry, indexLease.Index));
                foreach (var symbol in indexLease.Index.Symbols)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var hit = symbol.Hit;
                    if (!includeNonPublic && hit.accessibility != "public")
                        continue;
                    var isType = hit.kind is "class" or "struct" or "interface" or "enum" or "delegate" or "type";
                    if (isType && (kindFilter is null || kindFilter is "type" || kindFilter == hit.kind) ||
                        !isType && (kindFilter is null || kindFilter is "member" || kindFilter == hit.kind))
                    {
                        var name = hit.qualifiedName[(hit.qualifiedName.LastIndexOf('.') + 1)..];
                        if (Matches(query, name, hit.qualifiedName) ||
                            MatchesDocumentation(documentationQueryTokens, hit.xmlDocumentation))
                            AddHit(hits, hit);
                    }
                }
                if (includeNonPublic)
                {
                    ToolTiming.Count("api.search.nonPublicFallback");
                    await AddNonPublicSearchHitsAsync(snapshot, entry, query, kindFilter, hits, cancellationToken);
                }
                if (indexLease.Index.LimitReached)
                {
                    ToolTiming.Count("api.search.overflowFallback");
                    await AddOverflowSearchHitsAsync(snapshot, entry, query, kindFilter, includeNonPublic,
                        indexLease.Index, hits, cancellationToken);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (AssemblyChangedException) { throw; }
            catch (ResultLimitException) { throw; }
            catch (Exception exception) when (IsAssemblyReadFailure(exception))
            {
                AddDiagnostic(diagnostics, $"Could not inspect {entry.Origin}/{entry.RelativePath}: {exception.Message}");
            }
        }
        var ordered = hits.GroupBy(hit => hit.symbolId, StringComparer.Ordinal).Select(group => group.First())
            .Order(Comparer<ApiHit>.Create((a, b) => CompareSearchHits(query, a, b)))
            .ToArray();
        return (ordered, diagnostics);
    }

    internal static int CompareSearchHits(string query, ApiHit left, ApiHit right)
    {
        var result = SearchTier(query, left).CompareTo(SearchTier(query, right));
        if (result != 0)
            return result;
        result = IsTypeKind(left.kind) == IsTypeKind(right.kind) ? 0 : IsTypeKind(left.kind) ? -1 : 1;
        if (result != 0)
            return result;
        result = (left.origin == "game" ? 0 : 1).CompareTo(right.origin == "game" ? 0 : 1);
        if (result != 0)
            return result;
        result = StringComparer.OrdinalIgnoreCase.Compare(left.assemblyName, right.assemblyName);
        if (result != 0)
            return result;
        result = StringComparer.Ordinal.Compare(left.origin, right.origin);
        if (result != 0)
            return result;
        result = StringComparer.OrdinalIgnoreCase.Compare(left.relativePath, right.relativePath);
        if (result != 0)
            return result;
        result = StringComparer.OrdinalIgnoreCase.Compare(left.qualifiedName, right.qualifiedName);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left.symbolId, right.symbolId);
    }

    private static int SearchTier(string query, ApiHit hit)
    {
        var qualified = hit.qualifiedName;
        var name = qualified[(qualified.LastIndexOf('.') + 1)..];
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (qualified.Equals(query, StringComparison.OrdinalIgnoreCase) || qualified.EndsWith("." + query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        if (qualified.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 4;
        return 5;
    }

    private async Task AddOverflowSearchHitsAsync(CatalogSnapshot snapshot, AssemblyEntry entry, string query,
        string? kindFilter, bool includeNonPublic, ApiAssemblyIndex index, List<ApiHit> hits,
        CancellationToken cancellationToken)
    {
        using var main = await LoadPeAsync(entry, cancellationToken);
        using var resolver = new RootedAssemblyResolver(snapshot, entry, main);
        var typeSystem = new DecompilerTypeSystem(main.File, resolver, CreateDecompilerSettings());
        var documentationProvider = CreateXmlDocumentationProvider(entry);
        foreach (var type in typeSystem.MainModule.TypeDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (type.MetadataToken.IsNil)
                continue;
            var typeName = FormatTypeName(type.FullTypeName.FullName);
            var isType = type.Kind switch
            {
                TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate => true,
                _ => false,
            };
            if (isType && (kindFilter is null || kindFilter is "type" || kindFilter == TypeKindName(type.Kind)))
            {
                var simpleName = typeName[(typeName.LastIndexOf('.') + 1)..];
                if (Matches(query, simpleName, typeName))
                {
                    var hit = CreateHit(entry, type, TypeKindName(type.Kind), typeName,
                        type.DeclaringTypeDefinition is null ? null : FormatTypeName(type.DeclaringTypeDefinition.FullTypeName.FullName),
                        FormatTypeDeclaration(type), type.EffectiveAccessibility(), type.IsStatic, ObsoleteWarnings(type)) with
                    {
                        xmlDocumentation = ReadXmlDocumentation(type, documentationProvider),
                    };
                    if ((includeNonPublic || hit.accessibility == "public") && !index.SymbolsById.ContainsKey(hit.symbolId))
                        AddHit(hits, hit);
                }
            }
            foreach (var member in DeclaredSearchMembers(type, includeConstructors: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (member.MetadataToken.IsNil)
                    continue;
                if (!includeNonPublic && member.EffectiveAccessibility() != Accessibility.Public)
                    continue;
                var kind = MemberKind(member);
                if (kindFilter is not null && kindFilter is not "member" && kindFilter != kind)
                    continue;
                var declaringType = typeName;
                if (!Matches(query, member.Name, $"{declaringType}.{member.Name}"))
                    continue;
                var hit = CreateMemberHit(snapshot, entry, type, member, new List<string>());
                if (hit is not null && !index.SymbolsById.ContainsKey(hit.symbolId))
                    AddHit(hits, hit with { xmlDocumentation = ReadXmlDocumentation(member.MemberDefinition, documentationProvider) });
            }
        }
    }

    private async Task AddNonPublicSearchHitsAsync(CatalogSnapshot snapshot, AssemblyEntry entry, string query,
        string? kindFilter, List<ApiHit> hits, CancellationToken cancellationToken)
    {
        using var main = await LoadPeAsync(entry, cancellationToken);
        using var resolver = new RootedAssemblyResolver(snapshot, entry, main);
        var typeSystem = new DecompilerTypeSystem(main.File, resolver, CreateDecompilerSettings());
        var documentationProvider = CreateXmlDocumentationProvider(entry);
        foreach (var type in typeSystem.MainModule.TypeDefinitions)
        foreach (var member in DeclaredSearchMembers(type, includeConstructors: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member.MetadataToken.IsNil)
                continue;
            if (member.EffectiveAccessibility() == Accessibility.Public)
                continue;
            var kind = MemberKind(member);
            if (kindFilter is not null && kindFilter is not "member" && kindFilter != kind)
                continue;
            var declaringType = FormatTypeName(type.FullTypeName.FullName);
            var name = member.Name;
            var qualifiedName = $"{declaringType}.{name}";
            if (!Matches(query, name, qualifiedName))
                continue;
            var hit = CreateMemberHit(snapshot, entry, type, member, new List<string>());
            if (hit is not null)
                AddHit(hits, hit with { xmlDocumentation = ReadXmlDocumentation(member.MemberDefinition, documentationProvider) });
        }
    }



    public async Task<string> TypeAsync(string assemblyId, string typeSymbolId, bool includeInherited, bool includeNonPublic,
        string? name, string? kind, int offset, int limit, string? snapshotId, CancellationToken cancellationToken)
    {
        ValidatePage(offset, limit, MaxPageSize);
        if (name?.Length > 128)
            throw new ArgumentException("name may contain at most 128 characters.", nameof(name));
        if (kind is not null)
        {
            kind = kind.Trim().ToLowerInvariant();
            if (kind is not ("constructor" or "method" or "field" or "property" or "event"))
                throw new ArgumentException("kind must be constructor, method, field, property, or event.", nameof(kind));
        }
        using var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        var entry = SelectAssembly(snapshot, assemblyId);
        using var apiIndexLease = await GetApiIndexAsync(snapshot, entry, cancellationToken);
        ApiTypeRecord? indexedType = null;
        ApiIndexedSymbol typeSymbol;
        if (apiIndexLease.Index.TypesById.TryGetValue(typeSymbolId, out var foundType))
        {
            indexedType = foundType;
            typeSymbol = foundType.Symbol;
        }
        else if (apiIndexLease.Index.LimitReached)
        {
            ToolTiming.Count("api.type.overflowFallback");
            var fallbackSymbol = await FindIndexedSymbolByIdAsync(snapshot, entry, typeSymbolId, cancellationToken);
            if (fallbackSymbol is not null && !IsTypeKind(fallbackSymbol.Hit.kind))
                throw WrongKind(entry, fallbackSymbol.Hit, "api_type");
            if (fallbackSymbol is null)
                throw MissingSymbol("typeSymbolId", entry.Id, typeSymbolId, "type");
            typeSymbol = fallbackSymbol;
        }
        else
        {
            if (apiIndexLease.Index.SymbolsById.TryGetValue(typeSymbolId, out var wrongKind))
                throw WrongKind(entry, wrongKind.Hit, "api_type");
            throw MissingSymbol("typeSymbolId", entry.Id, typeSymbolId, "type");
        }
        using var main = await LoadPeAsync(entry, cancellationToken);
        using var resolver = new RootedAssemblyResolver(snapshot, entry, main);
        var typeSystem = new DecompilerTypeSystem(main.File, resolver, CreateDecompilerSettings());
        var type = FindType(typeSystem.MainModule, typeSymbol.MetadataToken)
            ?? throw MissingSymbol("typeSymbolId", entry.Id, typeSymbolId, "type");
        var documentationProvider = indexedType is not null && !includeInherited && !includeNonPublic &&
                                    !apiIndexLease.Index.LimitReached
            ? null
            : CreateXmlDocumentationProvider(entry);
        var typeDocumentation = typeSymbol.Hit.xmlDocumentation ?? ReadXmlDocumentation(type, documentationProvider);
        var typeWarnings = ObsoleteWarnings(type);
        if (!apiIndexLease.Index.Complete)
            AddWarnings(typeWarnings, new[] { IndexOmissionMessage(entry, apiIndexLease.Index) });
        ApiHit[] orderedMembers;
        if (!includeInherited && !includeNonPublic && indexedType is not null && !apiIndexLease.Index.LimitReached)
        {
            orderedMembers = indexedType.Members.Select(symbol => symbol.Hit)
                .OrderBy(member => member.qualifiedName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.symbolId, StringComparer.Ordinal).ToArray();
        }
        else
        {
            ToolTiming.Count("api.type.onDemandMembers");
            var members = new List<ApiHit>();
            var typeMembers = includeInherited
                ? type.GetMembers(null, GetMemberOptions.None).Cast<IMember>().Concat(type.GetConstructors(null, GetMemberOptions.None))
                : DeclaredSearchMembers(type, includeConstructors: true);
            foreach (var member in typeMembers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (member.MetadataToken.IsNil)
                    continue;
                if (!includeNonPublic && member.EffectiveAccessibility() != Accessibility.Public)
                    continue;
                var hit = CreateMemberHit(snapshot, entry, type, member, typeWarnings);
                if (hit is not null)
                {
                    if (hit.assemblyId == entry.Id)
                        hit = hit with { xmlDocumentation = ReadXmlDocumentation(member.MemberDefinition, documentationProvider) };
                    members.Add(hit);
                }
                if (members.Count > MaxResults)
                    throw new InvalidOperationException($"Type has more than {MaxResults} members; request a narrower non-public/inherited surface.");
            }
            orderedMembers = members.OrderBy(member => member.qualifiedName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.symbolId, StringComparer.Ordinal).ToArray();
        }
        if (name is not null || kind is not null)
            orderedMembers = orderedMembers.Where(member =>
                (kind is null || member.kind == kind) &&
                (name is null || member.qualifiedName[(member.qualifiedName.LastIndexOf('.') + 1)..].Contains(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (offset > orderedMembers.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"offset {offset} exceeds the member count {orderedMembers.Length}.");
        var page = orderedMembers.Skip(offset).Take(limit).ToArray();
        var baseTypes = type.DirectBaseTypes.ToArray();
        var baseType = baseTypes.FirstOrDefault(t => t.GetDefinition()?.Kind != TypeKind.Interface);
        var interfaces = baseTypes.Where(t => t.GetDefinition()?.Kind == TypeKind.Interface).Select(FormatType).ToArray();
        var hasMore = offset + page.Length < orderedMembers.Length;
        var typeName = FormatTypeName(type.FullTypeName.FullName);
        AddWarnings(typeWarnings, resolver.Warnings);
        return SerializeResponse(new
        {
            assemblyId = entry.Id,
            assemblyName = entry.Name,
            origin = entry.Origin,
            relativePath = entry.RelativePath,
            symbolId = typeSymbolId,
            qualifiedName = typeName,
            typeKind = TypeKindName(type.Kind),
            typeDeclaration = FormatTypeDeclaration(type),
            attributes = typeSymbol.Hit.attributes,
            attributesTruncated = typeSymbol.Hit.attributesTruncated,
            xmlDocumentation = typeDocumentation,
            baseType = baseType is null ? null : FormatType(baseType),
            interfaces,
            genericConstraints = FormatConstraints(OwnTypeParameters(type)),
            warnings = typeWarnings,
            members = page.Select(member =>
            {
                var node = JsonSerializer.SerializeToNode(member)!.AsObject();
                node["name"] = member.qualifiedName[(member.qualifiedName.LastIndexOf('.') + 1)..];
                if (member.assemblyId == entry.Id)
                {
                    node.Remove("assemblyId");
                    node.Remove("assemblyName");
                    node.Remove("origin");
                    node.Remove("relativePath");
                }
                if (member.declaringType == typeName)
                    node.Remove("declaringType");
                if (member.warnings.Length == 0)
                    node.Remove("warnings");
                if (member.attributes is null or { Length: 0 })
                    node.Remove("attributes");
                if (!member.attributesTruncated)
                    node.Remove("attributesTruncated");
                if (!member.isInherited)
                    node.Remove("isInherited");
                return node;
            }).ToArray(),
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
        using var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        var entry = SelectAssembly(snapshot, assemblyId);
        var (_, source) = await DecompileSnapshotAsync(snapshot, entry, symbolId, startLine, lineCount, includeHit: false, cancellationToken);
        return SerializeResponse(source);
    }

    private async Task<(ApiHit? Hit, ApiSourcePage Source)> DecompileSnapshotAsync(CatalogSnapshot snapshot, AssemblyEntry entry,
        string symbolId, int startLine, int lineCount, bool includeHit, CancellationToken cancellationToken)
    {
        using var timing = ToolTiming.Measure("decompileMs");
        using var indexLease = await GetApiIndexAsync(snapshot, entry, cancellationToken);
        if (!indexLease.Index.SymbolsById.TryGetValue(symbolId, out var symbol))
        {
            ToolTiming.Count("api.index.nonPublicTokenFallback");
            symbol = await FindIndexedSymbolByIdAsync(snapshot, entry, symbolId, cancellationToken)
                ?? throw MissingSymbol("symbolId", entry.Id, symbolId, "type/member");
        }
        using var documentLease = await GetSourceDocumentAsync(snapshot, entry, symbol, cancellationToken);
        return (includeHit ? symbol.Hit : null,
            CreateSourcePage(snapshot, entry, symbol, documentLease.Document, startLine, lineCount));
    }

    private async Task<ApiIndexedSymbol?> FindIndexedSymbolByIdAsync(CatalogSnapshot snapshot, AssemblyEntry entry,
        string symbolId, CancellationToken cancellationToken)
    {
        using var main = await LoadPeAsync(entry, cancellationToken);
        using var resolver = new RootedAssemblyResolver(snapshot, entry, main);
        var typeSystem = new DecompilerTypeSystem(main.File, resolver, CreateDecompilerSettings());
        var documentationProvider = CreateXmlDocumentationProvider(entry);
        foreach (var methodHandle in main.File.Metadata.MethodDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SymbolId(entry.Id, (EntityHandle)methodHandle).Equals(symbolId, StringComparison.Ordinal))
                continue;
            var method = typeSystem.MainModule.ResolveEntity(methodHandle, default) as IMethod;
            var declaringType = method?.DeclaringTypeDefinition;
            if (method is null || declaringType is null)
                return null;
            var hit = CreateMemberHit(snapshot, entry, declaringType, method, new List<string>());
            return hit is null ? null : CreateIndexedSymbol(
                hit with { xmlDocumentation = ReadXmlDocumentation(method.MemberDefinition, documentationProvider) },
                MetadataTokens.GetToken(methodHandle), method);
        }
        foreach (var type in typeSystem.MainModule.TypeDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (type.MetadataToken.IsNil)
                continue;
            if (SymbolId(entry.Id, type.MetadataToken).Equals(symbolId, StringComparison.Ordinal))
            {
                var typeName = FormatTypeName(type.FullTypeName.FullName);
                var hit = CreateHit(entry, type, TypeKindName(type.Kind), typeName,
                    type.DeclaringTypeDefinition is null ? null : FormatTypeName(type.DeclaringTypeDefinition.FullTypeName.FullName),
                    FormatTypeDeclaration(type), type.EffectiveAccessibility(), type.IsStatic, ObsoleteWarnings(type)) with
                {
                    xmlDocumentation = ReadXmlDocumentation(type, documentationProvider),
                };
                return CreateIndexedSymbol(hit, MetadataTokens.GetToken(type.MetadataToken), type);
            }
            foreach (var member in type.Members.Concat(type.Methods))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (member.MetadataToken.IsNil || !SymbolId(entry.Id, member.MetadataToken).Equals(symbolId, StringComparison.Ordinal))
                    continue;
                var hit = CreateMemberHit(snapshot, entry, type, member, new List<string>());
                return hit is null ? null : CreateIndexedSymbol(
                    hit with { xmlDocumentation = ReadXmlDocumentation(member.MemberDefinition, documentationProvider) },
                    MetadataTokens.GetToken(member.MetadataToken), member);
            }
        }
        return null;
    }
    internal async Task<string> LookupAsync(string? query, string? assemblyId, string? symbolId, string? kind,
        string? mode, int startLine, int lineCount, string? snapshotId, int maxResults, int maxChars,
        bool includeExamples, string? continuation, BaseCatalogDataCatalog? baseCatalog, CancellationToken cancellationToken,
        ResolvedApiScope? resolvedScope = null)
    {
        using var timing = ToolTiming.Measure("apiLookupMs");
        if (query is not null && symbolId is not null)
            throw new ArgumentException("Provide either query or the exact assemblyId and symbolId pair, not both.");
        if (query is null && (string.IsNullOrWhiteSpace(assemblyId) || string.IsNullOrWhiteSpace(symbolId)))
            throw new ArgumentException("Provide a query or both exact assemblyId and symbolId values.");
        var lookupMode = (mode ?? (query is null ? "symbol" : "task")).Trim().ToLowerInvariant();
        if (lookupMode is not "task" and not "symbol")
            throw new ArgumentException("mode must be 'task' or 'symbol'.", nameof(mode));
        if (query is null && lookupMode != "symbol")
            throw new ArgumentException("An exact assemblyId and symbolId pair uses symbol mode.", nameof(mode));
        if (query is null && (kind is not null || continuation is not null))
            throw new ArgumentException("kind and continuation are only valid with query mode.");
        if (query is not null)
        {
            var maxQueryLength = lookupMode == "task" ? 2048 : 128;
            if (string.IsNullOrWhiteSpace(query) || query.Length > maxQueryLength)
                throw new ArgumentException($"query must contain 1 to {maxQueryLength} characters.", nameof(query));
        }
        if (continuation is not null && lookupMode != "task")
            throw new ArgumentException("continuation is only valid in task mode.", nameof(continuation));

        var kindFilter = query is null ? null : ParseKind(kind);
        using var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        if (lookupMode == "task")
        {
            if (query is null)
                throw new ArgumentException("Task mode requires query.");
            if (maxResults is < 1 or > 20)
                throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be between 1 and 20.");
            if (maxChars is < 3000 or > 32000)
                throw new ArgumentOutOfRangeException(nameof(maxChars), "maxChars must be between 3000 and 32000.");
            return await TaskLookupAsync(snapshot, query, assemblyId, kindFilter, maxResults, maxChars,
                includeExamples, continuation, baseCatalog, cancellationToken,
                ExcludedModAssemblyIds(snapshot, resolvedScope?.LoadedAssemblies), resolvedScope?.Diagnostic, resolvedScope);
        }
        if (startLine < 1)
            throw new ArgumentOutOfRangeException(nameof(startLine), "startLine must be at least 1.");
        if (lineCount is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(lineCount), "lineCount must be between 1 and 500.");

        if (query is not null)
        {
            var (searchHits, diagnostics) = await SearchHitsAsync(snapshot, query, assemblyId, kindFilter, false, cancellationToken,
                resolvedScope?.LoadedAssemblies, resolvedScope?.Diagnostic);
            var exactHitCount = searchHits.Count(hit => hit.qualifiedName.Equals(query, StringComparison.OrdinalIgnoreCase));
            var hits = exactHitCount > 0
                ? searchHits.Where(hit => hit.qualifiedName.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray()
                : searchHits;
            if (hits.Length == 0)
                await ThrowMissingMemberDiagnosticAsync(snapshot, query, assemblyId, cancellationToken);
            if (hits.Length != 1)
            {
                var candidates = hits.Take(20).ToArray();
                var hasMore = hits.Length > candidates.Length;
                return SerializeResponse(new
                {
                    mode = "symbol",
                    query,
                    found = hits.Length > 0,
                    ambiguous = hits.Length > 1,
                    candidates,
                    total = hits.Length,
                    candidateLimit = 20,
                    hasMore,
                    nextSearch = hasMore
                        ? new
                        {
                            tool = "api_search",
                            arguments = new
                            {
                                query,
                                assemblyId,
                                kind = kindFilter,
                                includeNonPublic = false,
                                offset = 20,
                                limit = 20,
                                snapshotId = snapshot.Id,
                                scope = resolvedScope!.Requested,
                                paginationVersion = 2,
                                scopeSnapshotId = resolvedScope.ScopeSnapshotId,
                            },
                        }
                        : null,
                    snapshotId = snapshot.Id,
                    scope = resolvedScope!.Requested,
                    effectiveScope = resolvedScope.Effective,
                    scopeInventoryTruncated = resolvedScope.InventoryTruncated,
                    partial = resolvedScope.InventoryTruncated,
                    diagnostics,
                });
            }

            var match = hits[0];
            var entry = SelectAssembly(snapshot, match.assemblyId);
            var (_, source) = await DecompileSnapshotAsync(snapshot, entry, match.symbolId, startLine, lineCount, includeHit: false, cancellationToken);
            return SerializeResponse(new
            {
                mode = "symbol", query, found = true, ambiguous = false, match, source, snapshotId = snapshot.Id,
                scope = resolvedScope!.Requested,
                effectiveScope = resolvedScope.Effective,
                scopeInventoryTruncated = resolvedScope.InventoryTruncated,
                partial = resolvedScope.InventoryTruncated,
                diagnostics,
            });
        }

        var exactEntry = SelectAssembly(snapshot, assemblyId!);
        var (exactMatch, exactSource) = await DecompileSnapshotAsync(
            snapshot, exactEntry, symbolId!, startLine, lineCount, includeHit: true, cancellationToken);
        return SerializeResponse(new
        {
            mode = "symbol",
            found = true,
            ambiguous = false,
            match = exactMatch!,
            source = exactSource,
            snapshotId = snapshot.Id,
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
        ShowXmlDocumentation = true,
    };

    private async Task<CatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        await SnapshotLifetimeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScanCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            SnapshotLifetimeSlots.Release();
            throw;
        }
    }

    private async Task<CatalogSnapshot> ScanCoreAsync(CancellationToken cancellationToken)
    {
        using var timing = ToolTiming.Measure("apiScanMs");
        var options = await GameInstallConfiguration.ReadLaunchOptionsAsync(cancellationToken);
        var roots = new[]
        {
            new InstallRoot("game", GameInstallConfiguration.ManagedDirectory(options)),
            new InstallRoot("mod", GameInstallConfiguration.ModsDirectory(options)),
        };
        var entries = new List<AssemblyEntry>();
        var diagnostics = new List<string>();
        long retainedScanBytes = 0;
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
            IEnumerable<string> files;
            using (ToolTiming.Measure("apiInventoryEnumerationMs"))
                files = EnumerateDllFiles(root, configuredRoot.Origin, diagnostics, cancellationToken, ref truncated);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ToolTiming.Count("api.scan.filesConsidered");
                string relativePath = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (configuredRoot.Origin == "mod" && IsUnderscorePrefixedModFolder(relativePath))
                    continue; // disabled mod folder: never part of the installed API surface
                try
                {
                    byte[] bytes;
                    try
                    {
                        bytes = await ReadEntryBytesAsync(root, file, configuredRoot.Origin, cancellationToken);
                    }
                    catch (AssemblyTooLargeException tooLarge)
                    {
                        var oversizedContentHash = await HashOversizedEntryAsync(root, file, configuredRoot.Origin, tooLarge.Length, cancellationToken);
                        AddDiagnostic(diagnostics, $"Ignored {configuredRoot.Origin}/{relativePath}: DLL exceeds the {MaxAssemblyBytes} byte scan limit.");
                        fingerprints.Add($"{configuredRoot.Origin}|{relativePath}|oversize|{oversizedContentHash}");
                        continue;
                    }
                    var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
                    ToolTiming.Count("api.scan.bytesHashed", bytes.LongLength);
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
                    var xmlDocumentation = await InspectXmlDocumentationAsync(configuredRoot.Origin, root, file,
                        assemblyName, cancellationToken);
                    fingerprints.Add($"{configuredRoot.Origin}|{relativePath}|xml|{xmlDocumentation.Status}|{xmlDocumentation.ContentHash ?? ""}");
                    if (xmlDocumentation.Status == "invalid")
                        AddDiagnostic(diagnostics, $"Invalid XML documentation for {configuredRoot.Origin}/{relativePath}: {xmlDocumentation.Diagnostic}");
                    var retainedBytes = bytes.LongLength <= MaxSnapshotRetainedBytes - retainedScanBytes ? bytes : null;
                    if (retainedBytes is not null)
                        retainedScanBytes += bytes.LongLength;
                    entries.Add(new AssemblyEntry(id, assemblyName, version, moduleVersionId, configuredRoot.Origin, root,
                        relativePath, file, contentHash, xmlDocumentation.Path, xmlDocumentation.Status,
                        xmlDocumentation.Diagnostic, xmlDocumentation.ContentHash, retainedBytes));
                }

                catch (OperationCanceledException) { throw; }
                catch (AssemblyChangedException) { throw; }
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
        return new CatalogSnapshot(snapshotId, entries, diagnostics, truncated,
            new SnapshotBufferLifetime(() => SnapshotLifetimeSlots.Release()));
    }
    private static async Task<XmlDocumentationScan> InspectXmlDocumentationAsync(string origin, string root,
        string assemblyPath, string assemblyName, CancellationToken cancellationToken)
    {
        if (origin != "game")
            return new XmlDocumentationScan(null, "missing", null, null);

        try
        {
            var path = Path.ChangeExtension(assemblyPath, ".xml");
            if (!File.Exists(path))
                return new XmlDocumentationScan(null, "missing", null, null);

            var canonicalAssembly = ResolveCanonicalPath(assemblyPath);
            var canonicalDocumentation = ResolveCanonicalPath(path);
            var expectedFileName = Path.GetFileName(Path.ChangeExtension(assemblyPath, ".xml"));
            if (!PathComparer.Equals(Path.GetDirectoryName(canonicalDocumentation), Path.GetDirectoryName(canonicalAssembly)) ||
                !PathComparer.Equals(Path.GetFileName(canonicalDocumentation), expectedFileName))
                return new XmlDocumentationScan(null, "missing", null, null);
            if (!IsUnderRoot(canonicalDocumentation, root))
                return new XmlDocumentationScan(null, "invalid", "The XML documentation sidecar resolves outside the game Managed directory.", null);

            var contentHash = await HashXmlDocumentationAsync(root, canonicalDocumentation, cancellationToken);
            var diagnostic = ValidateXmlDocumentation(root, canonicalDocumentation, assemblyName);
            return diagnostic is null
                ? new XmlDocumentationScan(canonicalDocumentation, "available", null, contentHash)
                : new XmlDocumentationScan(canonicalDocumentation, "invalid", diagnostic, contentHash);
        }
        catch (AssemblyChangedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                                          InvalidOperationException or NotSupportedException or XmlException)
        {
            return new XmlDocumentationScan(null, "invalid",
                BoundXmlDocumentationDiagnostic($"Could not read XML documentation: {exception.Message}"), null);
        }
    }

    private static async Task<string> HashXmlDocumentationAsync(string root, string path, CancellationToken cancellationToken)
    {
        var canonical = ResolveCanonicalPath(path);
        if (!IsUnderRoot(canonical, root))
            throw new IOException("Resolved XML documentation path leaves its configured installation root.");
        await using var stream = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        EnsureAllowedOpenedAssemblyPath(root, "game", stream);
        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long bytesRead = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            hash.AppendData(buffer, 0, count);
            bytesRead += count;
        }
        if (bytesRead != length || stream.Length != length)
            throw new AssemblyChangedException("XML documentation changed while it was being hashed.");
        VerifyEntryPath(root, path, canonical);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string? ValidateXmlDocumentation(string root, string path, string assemblyName)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        EnsureAllowedOpenedAssemblyPath(root, "game", stream);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "doc" || reader.NamespaceURI.Length != 0)
            return "The XML documentation root element must be <doc>.";

        var rootDepth = reader.Depth;
        var assemblyDepth = -1;
        string? documentedAssemblyName = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == rootDepth + 1 &&
                reader.LocalName == "assembly" && reader.NamespaceURI.Length == 0)
            {
                assemblyDepth = reader.Depth;
                continue;
            }
            if (assemblyDepth >= 0 && reader.NodeType == XmlNodeType.Element &&
                reader.Depth == assemblyDepth + 1 && reader.LocalName == "name" && reader.NamespaceURI.Length == 0)
            {
                documentedAssemblyName = reader.ReadElementContentAsString().Trim();
                assemblyDepth = -1;
            }
        }

        if (string.IsNullOrEmpty(documentedAssemblyName))
            return "The XML documentation has no <assembly><name> value.";
        return documentedAssemblyName.Equals(assemblyName, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"The XML documentation assembly name '{BoundXmlDocumentationDiagnostic(documentedAssemblyName)}' does not match '{BoundXmlDocumentationDiagnostic(assemblyName)}'.";
    }

    private const int MaxXmlDocumentationDiagnosticChars = 300;
    private static string BoundXmlDocumentationDiagnostic(string value) =>
        value.Length <= MaxXmlDocumentationDiagnosticChars ? value : value[..MaxXmlDocumentationDiagnosticChars] + "…";
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
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var canonical = ResolveCanonicalPath(path);
                            if (!IsUnderRoot(canonical, root))
                            {
                                AddDiagnostic(diagnostics, $"Skipped path outside the canonical {origin} root: {Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')}.");
                                continue;
                            }
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
                            // ReadEntryBytesAsync rechecks the canonical path before reading a DLL.
                            files.Add(path);
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
        if (entry.ScanBytes is { } scanBytes)
            return OpenPe(entry.FullPath, scanBytes);

        byte[] bytes;
        try
        {
            bytes = await ReadEntryBytesAsync(entry.Root, entry.FullPath, entry.Origin, cancellationToken);
        }
        catch (AssemblyTooLargeException)
        {
            throw AssemblyChanged(entry.FullPath);
        }
        var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
        ToolTiming.Count("api.scan.bytesHashed", bytes.LongLength);
        if (!contentHash.Equals(entry.ContentHash, StringComparison.Ordinal))
            throw new AssemblyChangedException($"Assembly '{entry.Origin}/{entry.RelativePath}' changed during the request; rerun api_assemblies and use its fresh IDs.");
        return OpenPe(entry.FullPath, bytes);
    }

    private static async Task<byte[]> ReadEntryBytesAsync(string root, string fullPath, string origin, CancellationToken cancellationToken)
    {
        using var timing = ToolTiming.Measure("apiFileValidationMs");
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < MaxAssemblyReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var canonical = ResolveCanonicalPath(fullPath);
                EnsureAllowedCanonicalAssemblyPath(root, origin, canonical);
                await using var stream = new FileStream(canonical, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                EnsureAllowedOpenedAssemblyPath(root, origin, stream);
                if (stream.Length > MaxAssemblyBytes)
                {
                    VerifyEntryPath(root, fullPath, canonical);
                    throw new AssemblyTooLargeException(stream.Length);
                }
                var bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
                if (stream.ReadByte() != -1 || stream.Length != bytes.LongLength)
                    throw new AssemblyChangedException("Assembly changed while it was being read.");
                VerifyEntryPath(root, fullPath, canonical);
                return bytes;
            }
            catch (AssemblyTooLargeException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastFailure = exception;
                if (attempt + 1 < MaxAssemblyReadAttempts)
                    await Task.Delay(10 * (attempt + 1), cancellationToken);
            }
        }
        throw AssemblyChanged(fullPath, lastFailure);
    }

    private static async Task<string> HashOversizedEntryAsync(string root, string fullPath, string origin, long expectedLength,
        CancellationToken cancellationToken)
    {
        using var timing = ToolTiming.Measure("apiFileValidationMs");
        var buffer = new byte[65536];
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < MaxAssemblyReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var canonical = ResolveCanonicalPath(fullPath);
                EnsureAllowedCanonicalAssemblyPath(root, origin, canonical);
                await using var stream = new FileStream(canonical, FileMode.Open, FileAccess.Read,
                    FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                EnsureAllowedOpenedAssemblyPath(root, origin, stream);
                if (stream.Length != expectedLength || stream.Length <= MaxAssemblyBytes)
                    throw new AssemblyChangedException("Assembly size changed while it was being scanned.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long bytesRead = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                {
                    hash.AppendData(buffer, 0, count);
                    bytesRead += count;
                    ToolTiming.Count("api.scan.bytesHashed", count);
                }
                if (bytesRead != stream.Length || stream.Length != expectedLength)
                    throw new AssemblyChangedException("Assembly changed while it was being hashed.");
                VerifyEntryPath(root, fullPath, canonical);
                return Convert.ToHexString(hash.GetHashAndReset());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastFailure = exception;
                if (attempt + 1 < MaxAssemblyReadAttempts)
                    await Task.Delay(10 * (attempt + 1), cancellationToken);
            }
        }
        throw AssemblyChanged(fullPath, lastFailure);
    }

    private static void VerifyEntryPath(string root, string fullPath, string canonical)
    {
        var after = ResolveCanonicalPath(fullPath);
        if (!IsUnderRoot(after, root) || !PathComparer.Equals(canonical, after))
            throw new AssemblyChangedException("Assembly path changed while it was being read.");
    }

    private static void EnsureAllowedCanonicalAssemblyPath(string root, string origin, string canonical)
    {
        if (!IsUnderRoot(canonical, root))
            throw new IOException("Resolved assembly path leaves its configured installation root.");
        if (origin == "mod" && IsUnderscorePrefixedModFolder(Path.GetRelativePath(root, canonical).Replace(Path.DirectorySeparatorChar, '/')))
            throw new IOException("Resolved assembly path is inside a disabled mod folder.");
    }
    internal static void EnsureAllowedOpenedAssemblyPath(string root, string origin, FileStream opened)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Opened-file admission requires Windows handle path resolution.");
        var canonicalRoot = NormalizeWindowsPath(root);
        var openedPath = ResolveOpenedFilePath(opened.SafeFileHandle);
        EnsureAllowedCanonicalAssemblyPath(canonicalRoot, origin, openedPath);
    }

    private static string ResolveOpenedFilePath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                throw new IOException("Could not resolve the opened file handle path.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            if (length >= buffer.Capacity)
            {
                buffer.EnsureCapacity(checked((int)length + 1));
                continue;
            }
            return NormalizeWindowsPath(buffer.ToString(0, (int)length));
        }
    }

    private static string NormalizeWindowsPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) &&
                 path.Length >= 7 && char.IsAsciiLetter(path[4]) && path[5] == ':' &&
                 (path[6] == Path.DirectorySeparatorChar || path[6] == Path.AltDirectorySeparatorChar))
            path = path[4..];
        if (!Path.IsPathFullyQualified(path))
            throw new IOException("Resolved Windows path is not fully qualified.");
        return Path.GetFullPath(path);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint pathLength, uint flags);


    private static AssemblyChangedException AssemblyChanged(string fullPath, Exception? innerException = null) =>
        new($"Installed assembly '{Path.GetFileName(fullPath)}' changed or could not be read consistently after {MaxAssemblyReadAttempts} attempts; rerun api_assemblies and use its current IDs.", innerException);

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
            throw new ApiDiscoveryException("snapshot.stale",
                "snapshotId is stale; restart discovery without it and use IDs from the new result.",
                new { tool = "api_assemblies", arguments = new { offset = 0, limit = 50 } });
    }

    private static AssemblyEntry[] SelectAssemblies(CatalogSnapshot snapshot, string? assemblyId)
    {
        if (assemblyId is null) return snapshot.Assemblies.ToArray();
        return new[] { SelectAssembly(snapshot, assemblyId) };
    }
    internal static HashSet<LoadedAssemblyIdentity> CreateLoadedAssemblyIdentitySet() =>
        new(LoadedAssemblyIdentityComparer.Instance);

    internal static LoadedAssemblyIdentity CreateLoadedAssemblyIdentity(string relativePath, Guid moduleVersionId) =>
        new(relativePath.Replace('\\', '/'), moduleVersionId, null, null);

    internal static LoadedAssemblyIdentity CreateLoadedAssemblyIdentity(string name, string version, Guid moduleVersionId) =>
        new(null, moduleVersionId, name, version);

    internal static LoadedAssemblyIdentity CreateLoadedAssemblyIdentity(string relativePath, string name, string version,
        Guid moduleVersionId) =>
        new(relativePath.Replace('\\', '/'), moduleVersionId, name, version);

    private static bool Matches(AssemblyEntry entry, IReadOnlySet<LoadedAssemblyIdentity> loaded)
    {
        var moduleVersionId = Guid.Parse(entry.ModuleVersionId);
        return loaded.Contains(CreateLoadedAssemblyIdentity(entry.RelativePath, moduleVersionId)) ||
               loaded.Contains(CreateLoadedAssemblyIdentity(entry.Name, entry.Version, moduleVersionId));
    }
    /// <summary>Ids of installed mod assemblies the bridge did not report as loaded; empty for installed scope.</summary>
    private static HashSet<string> ExcludedModAssemblyIds(CatalogSnapshot snapshot, IReadOnlySet<LoadedAssemblyIdentity>? loaded) =>
        loaded is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : snapshot.Assemblies.Where(entry => entry.Origin == "mod" && !Matches(entry, loaded))
                .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);

    private static bool IsUnderscorePrefixedModFolder(string relativePath) =>
        relativePath.Length > 0 && relativePath[0] == '_' && relativePath.IndexOf('/') > 0;

    private static AssemblyEntry SelectAssembly(CatalogSnapshot snapshot, string assemblyId) =>
        snapshot.Assemblies.FirstOrDefault(a => a.Id.Equals(assemblyId, StringComparison.Ordinal))
        ?? throw new ApiDiscoveryException("assembly.invalid_or_stale",
            "assemblyId is invalid or stale; obtain a current catalog-issued ID.",
            new { tool = "api_assemblies", arguments = new { offset = 0, limit = 50 } });

    private static ApiDiscoveryException MissingSymbol(string parameter, string assemblyId, string symbolId, string expectedKind) =>
        new("symbol.invalid_or_stale", $"{parameter} is invalid, stale, or does not identify a {expectedKind} in assemblyId.",
            new { tool = "api_assemblies", arguments = new { offset = 0, limit = 50 }, next = "Use a current assemblyId, then search by the intended symbol name." });

    private static object ErrorCandidate(ApiHit hit) => new
    {
        assemblyId = hit.assemblyId,
        symbolId = hit.symbolId,
        kind = hit.kind,
        qualifiedName = ClipErrorText(hit.qualifiedName, 128),
        qualifiedNameTruncated = hit.qualifiedName.Length > 128,
        declaringType = hit.declaringType is null ? null : ClipErrorText(hit.declaringType, 128),
        declaringTypeTruncated = hit.declaringType?.Length > 128,
        signature = ClipErrorText(hit.signature, 256),
        signatureTruncated = hit.signature.Length > 256,
        provenance = new
        {
            origin = hit.origin,
            relativePath = ClipErrorText(hit.relativePath, 128),
            relativePathTruncated = hit.relativePath.Length > 128,
        },
    };

    private static string ClipErrorText(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static ApiDiscoveryException WrongKind(AssemblyEntry entry, ApiHit hit, string expectedTool) =>
        new("symbol.wrong_kind", $"This symbol is a {hit.kind}, not the {expectedTool} target kind.",
            new
            {
                candidates = new[] { ErrorCandidate(hit) },
                nextTool = new { tool = "api_decompile", arguments = new { assemblyId = entry.Id, symbolId = hit.symbolId } },
                provenance = new { assemblyId = entry.Id, origin = entry.Origin, relativePath = entry.RelativePath },
                signature = ClipErrorText(hit.signature, 512),
                signatureTruncated = hit.signature.Length > 512,
            });

    private static ITypeDefinition? FindType(MetadataModule module, int metadataToken) =>
        module.ResolveEntity(MetadataTokens.EntityHandle(metadataToken), default) as ITypeDefinition;

    private static IEntity? FindEntity(MetadataModule module, int metadataToken) =>
        module.ResolveEntity(MetadataTokens.EntityHandle(metadataToken), default);


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

    private static ApiHit CreateExactHit(CatalogSnapshot snapshot, AssemblyEntry entry, IEntity entity)
    {
        if (entity is ITypeDefinition type)
            return CreateHit(entry, type, TypeKindName(type.Kind), FormatTypeName(type.FullTypeName.FullName),
                type.DeclaringTypeDefinition is null ? null : FormatTypeName(type.DeclaringTypeDefinition.FullTypeName.FullName),
                FormatTypeDeclaration(type), type.EffectiveAccessibility(), type.IsStatic, ObsoleteWarnings(type));
        if (entity is IMember member)
        {
            var definition = member.MemberDefinition;
            var actualEntry = FindEntry(snapshot, member.ParentModule?.MetadataFile?.FileName) ?? entry;
            var declaringType = definition.DeclaringTypeDefinition is null
                ? null
                : FormatTypeName(definition.DeclaringTypeDefinition.FullTypeName.FullName);
            return CreateHit(actualEntry, definition, MemberKind(member), $"{declaringType}.{definition.Name}",
                declaringType, FormatMemberSignature(member), member.EffectiveAccessibility(), member.IsStatic,
                ObsoleteWarnings(definition));
        }
        throw new InvalidOperationException("symbolId does not identify a supported type or member.");
    }

    private const int MaxApiAttributes = 32;
    private const int MaxApiAttributeArguments = 16;
    private const int MaxApiAttributeChars = 8192;

    private static ApiHit CreateHit(AssemblyEntry entry, IEntity entity, string kind, string qualifiedName, string? declaringType,
        string signature, Accessibility accessibility, bool isStatic, List<string> warnings, bool isInherited = false)
    {
        var (attributes, attributesTruncated) = ReadApiAttributes(entity);
        return new ApiHit(entry.Id, entry.Name, entry.Origin, entry.RelativePath, SymbolId(entry.Id, entity.MetadataToken), kind,
            qualifiedName, declaringType, signature, AccessibilityKeyword(accessibility).ToLowerInvariant(), isStatic,
            warnings.ToArray(), isInherited, attributes, attributesTruncated);
    }

    private static (ApiAttribute[] Attributes, bool Truncated) ReadApiAttributes(IEntity entity)
    {
        var relevant = new List<(string Target, IAttribute Attribute)>(MaxApiAttributes);
        var truncated = false;
        void Add(IEnumerable<IAttribute> attributes, string? target)
        {
            foreach (var attribute in attributes)
            {
                if (!IsRelevantApiAttribute(attribute.AttributeType.FullName))
                    continue;
                if (relevant.Count >= MaxApiAttributes)
                {
                    truncated = true;
                    continue;
                }
                relevant.Add((target ?? "", attribute));
            }
        }

        Add(entity.GetAttributes(), null);
        IEnumerable<IParameter> parameters = entity switch
        {
            IMethod method => method.Parameters,
            IProperty property => property.Parameters,
            _ => Array.Empty<IParameter>(),
        };
        var parameterIndex = 0;
        foreach (var parameter in parameters)
        {
            Add(parameter.GetAttributes(), $"parameter:{parameterIndex}:{parameter.Name}");
            parameterIndex++;
        }

        relevant.Sort((left, right) =>
        {
            var result = StringComparer.Ordinal.Compare(left.Target, right.Target);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Attribute.AttributeType.FullName, right.Attribute.AttributeType.FullName);
        });

        var result = new List<ApiAttribute>(Math.Min(relevant.Count, MaxApiAttributes));
        var usedChars = 0;
        foreach (var (target, attribute) in relevant)
        {
            if (result.Count >= MaxApiAttributes) break;
            var name = attribute.AttributeType.FullName;
            var fixedArguments = ReadApiAttributeArguments(attribute.FixedArguments.Select(argument => (Name: (string?)null, Value: argument.Value)),
                ref usedChars, ref truncated);
            var namedArguments = ReadApiAttributeArguments(attribute.NamedArguments.Select(argument => (Name: (string?)argument.Name, Value: argument.Value))
                .OrderBy(argument => argument.Name, StringComparer.Ordinal), ref usedChars, ref truncated);
            var cost = name.Length + target.Length;
            if (usedChars + cost > MaxApiAttributeChars)
            {
                truncated = true;
                break;
            }
            usedChars += cost;
            result.Add(new ApiAttribute(name, target.Length == 0 ? null : target, fixedArguments, namedArguments));
            if (truncated && usedChars >= MaxApiAttributeChars) break;
        }
        return (result.ToArray(), truncated);
    }

    private static ApiAttributeArgument[] ReadApiAttributeArguments(
        IEnumerable<(string? Name, object? Value)> arguments, ref int usedChars, ref bool truncated)
    {
        var result = new List<ApiAttributeArgument>();
        foreach (var (name, rawValue) in arguments)
        {
            if (result.Count >= MaxApiAttributeArguments)
            {
                truncated = true;
                break;
            }
            var value = rawValue switch
            {
                null or bool or byte or sbyte or short or ushort or int or uint or long or ulong => rawValue,
                float number => number.ToString("R", CultureInfo.InvariantCulture),
                double number => number.ToString("R", CultureInfo.InvariantCulture),
                char character => character.ToString(),
                string text => text,
                _ => null,
            };
            if (rawValue is not null && value is null)
            {
                truncated = true;
                continue;
            }
            var nameLength = name?.Length ?? 0;
            var valueLength = value switch
            {
                null => 0,
                string text => text.Length,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Length ?? 0,
            };
            var remaining = MaxApiAttributeChars - usedChars;
            if (nameLength + valueLength > remaining)
            {
                truncated = true;
                break;
            }
            usedChars += nameLength + valueLength;
            result.Add(new ApiAttributeArgument(name, value));
        }
        return result.ToArray();
    }

    private static bool IsRelevantApiAttribute(string name) =>
        name is "System.ObsoleteAttribute" or "System.FlagsAttribute" or "System.ParamArrayAttribute" or
            "System.Runtime.CompilerServices.ExtensionAttribute" or "System.NonSerializedAttribute" or
            "UnityEngine.SerializeField" or "UnityEngine.SerializeFieldAttribute" or
            "UnityEngine.SerializeReference" or "UnityEngine.SerializeReferenceAttribute" or
            "UnityEngine.HideInInspector" or "UnityEngine.HideInInspectorAttribute" or
            "UnityEngine.Serialization.FormerlySerializedAs" or "UnityEngine.Serialization.FormerlySerializedAsAttribute" ||
        !name.StartsWith("System.", StringComparison.Ordinal) && !name.StartsWith("UnityEngine.", StringComparison.Ordinal);

    internal sealed record ApiAttribute(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("target")] string? Target,
        [property: System.Text.Json.Serialization.JsonPropertyName("fixedArguments")] ApiAttributeArgument[] FixedArguments,
        [property: System.Text.Json.Serialization.JsonPropertyName("namedArguments")] ApiAttributeArgument[] NamedArguments);
    internal sealed record ApiAttributeArgument(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("value")] object? Value);


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

    private static bool MatchesDocumentation(IReadOnlyList<string> queryTokens, ApiXmlDocumentation? documentation)
    {
        if (documentation is null || queryTokens.Count == 0)
            return false;
        bool Contains(string? text, string token) => text?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;
        foreach (var token in queryTokens)
            if (!Contains(documentation.Summary, token) &&
                !Contains(documentation.Remarks, token) &&
                !Contains(documentation.Returns, token) &&
                !Contains(documentation.Value, token) &&
                !(documentation.Parameters?.Any(parameter =>
                    Contains(parameter.Name, token) || Contains(parameter.Description, token)) ?? false) &&
                !(documentation.Examples?.Any(example => Contains(example, token)) ?? false))
                return false;
        return true;
    }

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

    private static bool IsTypeKind(string kind) =>
        kind is "class" or "struct" or "interface" or "enum" or "delegate" or "type";

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
        var ownParameters = OwnTypeParameters(type);
        if (ownParameters.Length > 0)
            name += "<" + string.Join(", ", ownParameters.Select(p => p.Name)) + ">";
        if (type.Kind == TypeKind.Delegate && type.Methods.FirstOrDefault(m => m.Name == "Invoke") is { } invoke)
        {
            var refReturn = invoke.ReturnType is ByReferenceType
                ? invoke.ReturnTypeIsRefReadOnly ? "ref readonly " : "ref "
                : "";
            var declaration = string.Join(" ", modifiers.Append("delegate").Append(
                $"{refReturn}{FormatType(invoke.ReturnType)} {name}({string.Join(", ", invoke.Parameters.Select(parameter => FormatParameter(parameter)))})"));
            var constraints = FormatConstraints(ownParameters);
            return constraints.Length == 0 ? declaration : declaration + " " + string.Join(" ", constraints);
        }
        return string.Join(" ", modifiers.Append(TypeKindName(type.Kind)).Append(name));
    }

    private static ITypeParameter[] OwnTypeParameters(ITypeDefinition type)
    {
        var total = type.TypeParameters.Count;
        var inherited = type.DeclaringType?.TypeParameterCount ?? 0;
        var ownCount = Math.Max(0, total - inherited);
        if (ownCount == 0)
            return Array.Empty<ITypeParameter>();
        return type.TypeParameters.Skip(total - ownCount).ToArray();
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
            var extensionMethod = method.IsExtensionMethod ||
                (method.IsStatic && method.DeclaringTypeDefinition?.HasExtensions == true) ||
                HasRawExtensionAttribute(method);
            var parameters = string.Join(", ", method.Parameters.Select((parameter, index) =>
                FormatParameter(parameter, index == 0 && extensionMethod)));
            var refReturn = method.ReturnType is ByReferenceType
                ? method.ReturnTypeIsRefReadOnly ? "ref readonly " : "ref "
                : "";
            var prefix = method.IsConstructor ? string.Empty : refReturn + FormatType(method.ReturnType) + " ";
            var signature = string.Join(" ", modifiers.Append(prefix + name + "(" + parameters + ")"));
            var constraints = FormatConstraints(method.TypeParameters);
            return constraints.Length == 0 ? signature : signature + " " + string.Join(" ", constraints);
        }
        if (member is IField field)
        {
            if (field.IsConst) modifiers.Add("const");
            else if (field.IsReadOnly) modifiers.Add("readonly");
            var refField = field.ReturnTypeIsRefReadOnly && field.ReturnType is ByReferenceType ? "ref readonly " : "";
            return string.Join(" ", modifiers.Append(refField + FormatType(field.ReturnType) + " " + field.Name));
        }
        if (member is IProperty property)
        {
            var name = property.IsIndexer ? "this[" + string.Join(", ", property.Parameters.Select(parameter => FormatParameter(parameter))) + "]" : property.Name;
            var accessors = new[]
            {
                FormatAccessor(property.Getter, property.Accessibility, "get;"),
                FormatAccessor(property.Setter, property.Accessibility, property.Setter?.IsInitOnly == true ? "init;" : "set;"),
            }.Where(accessor => accessor is not null);
            var refReturn = property.ReturnType is ByReferenceType
                ? property.ReturnTypeIsRefReadOnly ? "ref readonly " : "ref "
                : "";
            return string.Join(" ", modifiers.Append(refReturn + FormatType(property.ReturnType) + " " + name + " { " + string.Join(" ", accessors) + " }"));
        }
        if (member is IEvent @event)
            return string.Join(" ", modifiers.Append("event " + FormatType(@event.ReturnType) + " " + @event.Name));
        return member.Name;
    }

    private static string? FormatAccessor(IMethod? accessor, Accessibility propertyAccessibility, string declaration)
    {
        if (accessor is null)
            return null;
        var accessibility = accessor.Accessibility == propertyAccessibility
            ? ""
            : AccessibilityKeyword(accessor.Accessibility) + " ";
        return accessibility + declaration;
    }

    private static bool HasRawExtensionAttribute(IMethod method)
    {
        if (method.MetadataToken.IsNil || method.ParentModule?.MetadataFile is not PEFile pe)
            return false;
        try
        {
            var md = pe.Metadata;
            foreach (var handle in md.GetMethodDefinition((MethodDefinitionHandle)method.MetadataToken).GetCustomAttributes())
            {
                var attribute = md.GetCustomAttribute(handle);
                var parent = attribute.Constructor.Kind switch
                {
                    HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                    HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                    _ => default,
                };
                var name = parent.Kind switch
                {
                    HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
                    HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
                    _ => null,
                };
                if (name == "ExtensionAttribute") return true;
            }
        }
        catch (BadImageFormatException) { }
        catch (InvalidOperationException) { }
        return false;
    }

    private static string FormatParameter(IParameter parameter, bool isExtensionReceiver = false)
    {
        var prefix = parameter.ReferenceKind switch
        {
            ReferenceKind.Ref => "ref ",
            ReferenceKind.Out => "out ",
            ReferenceKind.In => "in ",
            ReferenceKind.RefReadOnly => isExtensionReceiver ? "ref readonly " : "in ",
            _ => "",
        };
        if (parameter.IsParams) prefix = "params ";
        else if (isExtensionReceiver) prefix = "this " + prefix;
        var name = FormatParameterIdentifier(parameter.Name);
        var text = prefix + FormatType(parameter.Type) + " " + name;
        if (parameter.IsOptional)
            text += " = " + (parameter.HasConstantValueInSignature
                ? FormatOptionalConstant(parameter.Type, parameter.GetConstantValue())
                : "default");
        return text;
    }

    private static string FormatParameterIdentifier(string name) =>
        ParameterKeywords.Contains(name) ? "@" + name : name;

    private static readonly HashSet<string> ParameterKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
    };

    private static string FormatOptionalConstant(IType type, object? value)
    {
        var definition = type.GetDefinition();
        if (definition?.Kind == TypeKind.Enum)
        {
            var field = definition.Fields.FirstOrDefault(candidate => candidate.IsConst && Equals(candidate.GetConstantValue(), value));
            if (field is not null)
                return FormatType(type) + "." + field.Name;
            return "(" + FormatType(type) + ")" + FormatConstant(value);
        }
        return FormatConstant(value);
    }

    private static string FormatConstant(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        string text => "\"" + EscapeCSharpLiteral(text, isChar: false) + "\"",
        char character => "'" + EscapeCSharpLiteral(character.ToString(), isChar: true) + "'",
        float number when float.IsNaN(number) => "float.NaN",
        float number when float.IsPositiveInfinity(number) => "float.PositiveInfinity",
        float number when float.IsNegativeInfinity(number) => "float.NegativeInfinity",
        float number => number.ToString("R", CultureInfo.InvariantCulture) + "F",
        double number when double.IsNaN(number) => "double.NaN",
        double number when double.IsPositiveInfinity(number) => "double.PositiveInfinity",
        double number when double.IsNegativeInfinity(number) => "double.NegativeInfinity",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture) + "M",
        uint number => number.ToString(CultureInfo.InvariantCulture) + "U",
        long number => number.ToString(CultureInfo.InvariantCulture) + "L",
        ulong number => number.ToString(CultureInfo.InvariantCulture) + "UL",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default",
    };

    private static string EscapeCSharpLiteral(string value, bool isChar)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            result.Append(character switch
            {
                '\\' => "\\\\",
                '\0' => "\\0",
                '\a' => "\\a",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\v' => "\\v",
                '"' when !isChar => "\\\"",
                '\'' when isChar => "\\'",
                _ when char.IsControl(character) => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => character.ToString(),
            });
        }
        return result.ToString();
    }


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
            return FormatParameterizedType(parameterized);
        if (type is ITypeDefinition definition)
            return FormatGenericType(definition.FullTypeName.FullName, definition.TypeParameters);
        return FormatTypeName(type.FullName);
    }

    private static string FormatParameterizedType(ParameterizedType type)
    {
        var definition = type.GenericType.GetDefinition();
        if (definition is null)
            return FormatGenericType(type.GenericType.FullName, type.TypeArguments);
        var chain = new List<ITypeDefinition>();
        for (var current = definition; current is not null; current = current.DeclaringType?.GetDefinition())
            chain.Add(current);
        chain.Reverse();
        var result = new StringBuilder();
        var argumentIndex = 0;
        for (var chainIndex = 0; chainIndex < chain.Count; chainIndex++)
        {
            var segment = chain[chainIndex];
            if (chainIndex == 0)
                result.Append(FormatTypeName(segment.FullTypeName.FullName));
            else
            {
                result.Append('.');
                result.Append(FormatTypeName(segment.Name));
            }
            var inherited = segment.DeclaringType?.TypeParameterCount ?? 0;
            var ownCount = Math.Max(0, segment.TypeParameters.Count - inherited);
            if (ownCount == 0)
                continue;
            if (ownCount > type.TypeArguments.Count - argumentIndex)
                return FormatGenericType(type.GenericType.FullName, type.TypeArguments);
            result.Append('<');
            for (var index = 0; index < ownCount; index++)
            {
                if (index > 0)
                    result.Append(", ");
                result.Append(FormatType(type.TypeArguments[argumentIndex++]));
            }
            result.Append('>');
        }
        return result.ToString();
    }

    private static string FormatGenericType(string fullName, IReadOnlyList<IType> typeArguments)
    {
        var result = new StringBuilder(fullName.Length);
        var argumentIndex = 0;
        foreach (var segment in fullName.Split('+'))
        {
            if (result.Length > 0)
                result.Append('.');
            var arityMarker = segment.LastIndexOf('`');
            var arity = arityMarker >= 0 && int.TryParse(segment.AsSpan(arityMarker + 1),
                NumberStyles.None, CultureInfo.InvariantCulture, out var parsedArity)
                ? parsedArity
                : 0;
            result.Append(FormatTypeName(segment));
            if (arity == 0)
                continue;
            if (arity > typeArguments.Count - argumentIndex)
                return FormatTypeName(fullName) + "<" + string.Join(", ", typeArguments.Select(FormatType)) + ">";
            result.Append('<');
            for (var index = 0; index < arity; index++)
            {
                if (index > 0)
                    result.Append(", ");
                result.Append(FormatType(typeArguments[argumentIndex++]));
            }
            result.Append('>');
        }
        return argumentIndex == typeArguments.Count
            ? result.ToString()
            : FormatTypeName(fullName) + "<" + string.Join(", ", typeArguments.Select(FormatType)) + ">";
    }

    private static string[] FormatConstraints(IReadOnlyList<ITypeParameter> parameters) => parameters
        .Select(parameter =>
        {
            var constraints = new List<string>();
            if (parameter.HasUnmanagedConstraint) constraints.Add("unmanaged");
            else if (parameter.HasValueTypeConstraint) constraints.Add("struct");
            else if (parameter.HasReferenceTypeConstraint) constraints.Add(parameter.NullabilityConstraint == Nullability.Nullable ? "class?" : "class");
            foreach (var constraint in parameter.TypeConstraints)
            {
                var formatted = FormatType(constraint.Type);
                if ((parameter.HasReferenceTypeConstraint && formatted is "object" or "System.Object") ||
                    ((parameter.HasValueTypeConstraint || parameter.HasUnmanagedConstraint) &&
                     formatted is "System.ValueType" or "ValueType"))
                    continue;
                constraints.Add(formatted);
            }
            if (parameter.HasDefaultConstructorConstraint &&
                !parameter.HasValueTypeConstraint && !parameter.HasUnmanagedConstraint)
                constraints.Add("new()");
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

    private static string IndexOmissionMessage(AssemblyEntry entry, ApiAssemblyIndex index) =>
        index.LimitReached
            ? $"{entry.Origin}/{entry.RelativePath}: API index stopped at the {MaxResults} declaration safety limit; additional declarations were not indexed."
            : $"{entry.Origin}/{entry.RelativePath}: API index omitted {index.OmittedPublicDeclarations} public declaration(s) without stable metadata tokens ({index.OmittedDeclarations} total tokenless declarations).";

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

    private static byte[] ReadEntryBytes(string root, string fullPath, string origin)
    {
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < MaxAssemblyReadAttempts; attempt++)
        {
            try
            {
                var canonical = ResolveCanonicalPath(fullPath);
                EnsureAllowedCanonicalAssemblyPath(root, origin, canonical);
                using var stream = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read);
                EnsureAllowedOpenedAssemblyPath(root, origin, stream);
                if (stream.Length > MaxAssemblyBytes)
                    throw new AssemblyChangedException($"Installed assembly '{Path.GetFileName(fullPath)}' grew beyond the scan limit.");
                var bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1 || stream.Length != bytes.LongLength)
                    throw new AssemblyChangedException("Assembly changed while it was being read.");
                VerifyEntryPath(root, fullPath, canonical);
                return bytes;
            }
            catch (IOException exception)
            {
                lastFailure = exception;
                if (attempt + 1 < MaxAssemblyReadAttempts)
                    Thread.Sleep(10 * (attempt + 1));
            }
            catch (UnauthorizedAccessException exception)
            {
                lastFailure = exception;
                if (attempt + 1 < MaxAssemblyReadAttempts)
                    Thread.Sleep(10 * (attempt + 1));
            }
        }
        throw AssemblyChanged(fullPath, lastFailure);
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
            {
                var versionText = reference.Version?.ToString();
                (bool Version, bool Directory, bool Origin) Key(AssemblyEntry a) =>
                    (a.Version.Equals(versionText, StringComparison.OrdinalIgnoreCase),
                     PathComparer.Equals(Path.GetDirectoryName(a.FullPath), preferredDirectory),
                     a.Origin == _mainEntry.Origin);
                var firstKey = Key(candidates[0]);
                if (candidates.Skip(1).Any(a => !a.ContentHash.Equals(candidates[0].ContentHash, StringComparison.Ordinal) && Key(a) == firstKey))
                    AddWarning($"Assembly reference '{reference.Name}' is ambiguous among installed files; chose {candidates[0].Origin}/{candidates[0].RelativePath} deterministically.");
            }
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
                var bytes = selected.ScanBytes ?? ReadEntryBytes(selected.Root, selected.FullPath, selected.Origin);
                if (selected.ScanBytes is null)
                {
                    var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
                    ToolTiming.Count("api.scan.bytesHashed", bytes.LongLength);
                    if (!contentHash.Equals(selected.ContentHash, StringComparison.Ordinal))
                        throw new AssemblyChangedException($"Installed dependency '{selected.Origin}/{selected.RelativePath}' changed during the request; rerun api_assemblies and use current IDs.");
                }
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


    internal readonly record struct LoadedAssemblyIdentity(string? RelativePath, Guid ModuleVersionId, string? Name, string? Version);

    private sealed class LoadedAssemblyIdentityComparer : IEqualityComparer<LoadedAssemblyIdentity>
    {
        public static readonly LoadedAssemblyIdentityComparer Instance = new();

        public bool Equals(LoadedAssemblyIdentity left, LoadedAssemblyIdentity right)
        {
            if (left.ModuleVersionId != right.ModuleVersionId) return false;
            if (left.RelativePath is not null || right.RelativePath is not null)
                return left.RelativePath is not null && right.RelativePath is not null &&
                       PathComparer.Equals(left.RelativePath, right.RelativePath);
            return string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
                   string.Equals(left.Version, right.Version, StringComparison.Ordinal);
        }

        public int GetHashCode(LoadedAssemblyIdentity identity) =>
            identity.RelativePath is not null
                ? HashCode.Combine(0, PathComparer.GetHashCode(identity.RelativePath), identity.ModuleVersionId)
                : HashCode.Combine(1, StringComparer.Ordinal.GetHashCode(identity.Name ?? string.Empty),
                    StringComparer.Ordinal.GetHashCode(identity.Version ?? string.Empty), identity.ModuleVersionId);
    }

    private sealed record InstallRoot(string Origin, string Path);
    private sealed record AssemblyEntry(string Id, string Name, string Version, string ModuleVersionId, string Origin,
        string Root, string RelativePath, string FullPath, string ContentHash, string? XmlDocumentationPath,
        string XmlDocumentationStatus, string? XmlDocumentationDiagnostic, string? XmlDocumentationContentHash,
        byte[]? ScanBytes = null);
    private sealed record XmlDocumentationScan(string? Path, string Status, string? Diagnostic, string? ContentHash);
    private sealed record CatalogSnapshot(string Id, IReadOnlyList<AssemblyEntry> Assemblies, List<string> Diagnostics,
        bool CatalogTruncated, SnapshotBufferLifetime ScanBytesLifetime) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                ScanBytesLifetime.Release();
        }

        public CatalogSnapshot DetachScanBytesExcept(AssemblyEntry retained)
        {
            var assemblies = new AssemblyEntry[Assemblies.Count];
            var found = false;
            for (var index = 0; index < Assemblies.Count; index++)
            {
                var assembly = Assemblies[index];
                if (assembly.Id.Equals(retained.Id, StringComparison.Ordinal))
                {
                    assemblies[index] = retained;
                    found = true;
                }
                else
                {
                    assemblies[index] = assembly with { ScanBytes = null };
                }
            }
            if (!found)
                throw new InvalidOperationException("Assembly entry does not belong to this catalog snapshot.");
            return new CatalogSnapshot(Id, assemblies, new List<string>(Diagnostics), CatalogTruncated,
                ScanBytesLifetime.AddReference());
        }
    }
    private sealed class SnapshotBufferLifetime(Action release)
    {
        private int _references = 1;

        public SnapshotBufferLifetime AddReference()
        {
            while (true)
            {
                var references = Volatile.Read(ref _references);
                if (references <= 0)
                    throw new ObjectDisposedException(nameof(SnapshotBufferLifetime));
                if (Interlocked.CompareExchange(ref _references, references + 1, references) == references)
                    return this;
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
                release();
        }
    }
    internal sealed record ApiHit(string assemblyId, string assemblyName, string origin, string relativePath, string symbolId,
        string kind, string qualifiedName, string? declaringType, string signature, string accessibility, bool isStatic,
        string[] warnings, bool isInherited = false, ApiAttribute[]? attributes = null, bool attributesTruncated = false,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        ApiXmlDocumentation? xmlDocumentation = null);
    internal sealed record ApiXmlDocumentation(
        [property: System.Text.Json.Serialization.JsonPropertyName("summary")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Summary,
        [property: System.Text.Json.Serialization.JsonPropertyName("remarks")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Remarks,
        [property: System.Text.Json.Serialization.JsonPropertyName("parameters")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        ApiXmlDocumentationParameter[]? Parameters,
        [property: System.Text.Json.Serialization.JsonPropertyName("returns")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Returns,
        [property: System.Text.Json.Serialization.JsonPropertyName("value")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Value,
        [property: System.Text.Json.Serialization.JsonPropertyName("examples")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string[]? Examples,
        [property: System.Text.Json.Serialization.JsonPropertyName("truncated")]
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        bool? Truncated);
    internal sealed record ApiXmlDocumentationParameter(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("description")] string Description);
    private sealed record ApiSourcePage(string assemblyId, string assemblyName, string origin, string relativePath,
        string symbolId, string qualifiedName, string sourceKind, object provenance, string source, int startLine,
        int totalLines, int lineCount, bool hasMore, int? nextLine, string snapshotId, string[] warnings, List<string> diagnostics);

    private sealed class LoadedPe : IDisposable
    {
        private readonly MemoryStream _stream;
        public PEFile File { get; }
        public LoadedPe(PEFile file, MemoryStream stream) { File = file; _stream = stream; }
        public void Dispose() { File.Dispose(); _stream.Dispose(); }
    }
    private sealed class AssemblyChangedException : IOException
    {
        public AssemblyChangedException(string message, Exception? innerException = null) : base(message, innerException) { }
    }

    private sealed class AssemblyTooLargeException(long length) : IOException
    {
        public long Length { get; } = length;
    }

    private sealed class ResultLimitException : InvalidOperationException
    {
        public ResultLimitException(string message) : base(message) { }
    }
}
