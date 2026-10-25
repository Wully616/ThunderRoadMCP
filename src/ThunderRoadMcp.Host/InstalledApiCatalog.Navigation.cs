using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using IlOperandType = System.Reflection.Emit.OperandType;

public sealed partial class InstalledApiCatalog
{
    private const int NavigationMaxResults = 50000;
    private const int NavigationMaxPage = 100;
    private const int NavigationMaxMethods = 500000;
    private const int NavigationMaxInstructionsPerBody = 2000000;
    private const int NavigationMaxTotalInstructions = 10000000;
    private const int NavigationMaxIndexedReferences = 100000;
    private const int NavigationMaxReferences = 1000000;
    private const int NavigationMaxOverflowMethods = 2000000;
    private const int NavigationMaxOverflowInstructions = 50000000;
    private const int NavigationMaxOverflowReferences = 5000000;
    private static readonly IReadOnlyDictionary<ushort, OpCode> NavigationOpCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(op => unchecked((ushort)op.Value));
    private static readonly SemaphoreSlim NavigationIndexBuildGate = new(1, 1);
    private static NavigationOverflowCache? _navigationOverflowCache;
    private static NavigationReferenceIndex? _navigationReferenceIndex;

    public Task<string> FindCallersAsync(string assemblyId, string symbolId, string scope, int offset, int limit,
        string? snapshotId, bool includeDiagnostics, CancellationToken cancellationToken) => FindReferencesAsync("callers", assemblyId, symbolId, scope, offset, limit, snapshotId, includeDiagnostics, cancellationToken);

    public Task<string> FindUsagesAsync(string assemblyId, string symbolId, string scope, int offset, int limit,
        string? snapshotId, bool includeDiagnostics, CancellationToken cancellationToken) => FindReferencesAsync("usages", assemblyId, symbolId, scope, offset, limit, snapshotId, includeDiagnostics, cancellationToken);

    public async Task<string> FindCalleesAsync(string assemblyId, string symbolId, int offset, int limit,
        string? snapshotId, CancellationToken cancellationToken)
    {
        ValidateNavigationPage(offset, limit);
        using var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        var entry = SelectAssembly(snapshot, assemblyId);
        using var pe = await LoadPeAsync(entry, cancellationToken);
        using var resolver = new RootedAssemblyResolver(snapshot, entry, pe);
        var typeSystem = new DecompilerTypeSystem(pe.File, resolver, CreateDecompilerSettings());
        using var metadata = await LoadNavigationMetadataAsync(entry, cancellationToken);
        var reader = metadata.Reader;
        var targetSymbol = FindTypeSystemSymbol(typeSystem.MainModule, reader, entry.Id, symbolId);
        if (targetSymbol is null)
            throw MissingSymbol("symbolId", entry.Id, symbolId, "method");
        if (targetSymbol is not IMethod)
            await ThrowNavigationWrongKindAsync(snapshot, entry, symbolId, "api_find_callees", cancellationToken).ConfigureAwait(false);
        var methodDefinition = (IMethod)targetSymbol;
        var method = (MethodDefinitionHandle)MetadataTokens.EntityHandle(MetadataTokens.GetToken(methodDefinition.MetadataToken));
        var body = ReadMethodBody(metadata.PeReader, reader, method);
        if (body is null)
        {
            var noBodyDiagnostics = new[] { "Method has no IL body (abstract, external, or runtime-provided); no static callees are present." };
            return SerializeResponse(new { results = Array.Empty<object>(), total = 0, offset, limit, hasMore = false,
                nextOffset = (int?)null, snapshotId = snapshot.Id, hasBody = false, noBodyReason = "no_il_body",
                complete = true, diagnostics = noBodyDiagnostics });
        }
        var refs = new List<CalleeResult>();
        var diagnostics = new List<string>();
        var unresolved = 0;
        var limited = false;
        var referenceEnumerator = DecodeReferences(body, cancellationToken, diagnostics);
        while (referenceEnumerator.MoveNext())
        {
            var reference = referenceEnumerator.Current;
            if (reference.Kind != "method" || reference.opcode is not ("call" or "callvirt" or "newobj")) continue;
            if (refs.Count == NavigationMaxResults) { limited = true; break; }
            var resolved = ResolveReference(snapshot, typeSystem.MainModule, reference.Token);
            var targetEntry = resolved is null ? null : snapshot.Assemblies.FirstOrDefault(candidate => candidate.Id == resolved.AssemblyId);
            if (resolved is null) unresolved++;
            refs.Add(new CalleeResult(DescribeReference(reader, reference.Token), MetadataTokens.GetToken(reference.Token),
                reference.Token.Kind.ToString(), resolved?.AssemblyId,
                resolved is null ? null : SymbolId(resolved.AssemblyId, MetadataTokens.EntityHandle(resolved.Token)),
                targetEntry?.Name ?? ReferencedAssembly(reader, reference.Token),
                targetEntry?.Version ?? ReferencedAssemblyVersion(reader, reference.Token), targetEntry?.Origin,
                reference.opcode, reference.ilOffset, reference.operandKind.ToString(), resolved is not null));
        }
        if (limited) AddDiagnostic(diagnostics, $"Callee results stopped at the {NavigationMaxResults} result safety limit.");
        var page = refs.Skip(offset).Take(limit).ToArray();
        var hasMore = offset + page.Length < refs.Count;
        return SerializeResponse(new { results = page, total = refs.Count, offset, limit, hasMore,
            nextOffset = hasMore ? offset + page.Length : (int?)null, snapshotId = snapshot.Id, hasBody = true,
            unresolvedReferences = unresolved, complete = !limited && unresolved == 0 && diagnostics.Count == 0, diagnostics });
    }

    private async Task<string> FindReferencesAsync(string mode, string assemblyId, string symbolId, string scope, int offset, int limit,
        string? snapshotId, bool includeDiagnostics, CancellationToken cancellationToken)
    {
        ValidateNavigationPage(offset, limit);
        if (scope is not ("assembly" or "snapshot"))
            throw new ArgumentException("scope must be 'assembly' or 'snapshot'.", nameof(scope));
        using var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        var selected = SelectAssembly(snapshot, assemblyId);
        var target = await ResolveApiEntityAsync(snapshot, selected, symbolId, mode == "callers", mode == "usages", cancellationToken);
        var indexScope = scope == "assembly" ? selected.Id : "*";
        var index = await GetNavigationReferenceIndexAsync(snapshot, indexScope, selected, cancellationToken);
        var matched = new List<NavigationEdge>();
        var searchedAssemblies = index.SearchedAssemblies;
        var unresolvedReferences = index.UnresolvedReferences;
        var diagnostics = index.Diagnostics;
        var complete = index.Complete;
        if (index.LimitReached)
        {
            var overflow = await GetNavigationOverflowScanAsync(snapshot, index, target, indexScope, mode, cancellationToken).ConfigureAwait(false);
            matched.AddRange(overflow.Edges);
            searchedAssemblies = overflow.SearchedAssemblies;
            unresolvedReferences = overflow.UnresolvedReferences;
            diagnostics = overflow.Diagnostics;
            complete = overflow.Complete;
        }
        else
        {
            foreach (var token in target.Tokens)
                if (index.ByTarget.TryGetValue(new NavigationTargetKey(target.AssemblyId, token), out var references))
                    matched.AddRange(references.Where(reference =>
                        (scope != "assembly" || index.Assemblies[reference.AssemblyIndex].Id == selected.Id) &&
                        (mode != "callers" || reference.opcode is "call" or "callvirt" or "newobj")));
            foreach (var targetKey in target.PotentialTargets)
                if (index.ByTarget.TryGetValue(targetKey, out var potentialReferences))
                    matched.AddRange(potentialReferences.Where(reference => reference.opcode == "callvirt" &&
                        (scope != "assembly" || index.Assemblies[reference.AssemblyIndex].Id == selected.Id))
                        .Select(reference => reference with { potentialVirtualDispatch = true }));
        }
        matched = DeduplicateCallSites(matched);
        matched.Sort(static (left, right) =>
        {
            var result = left.AssemblyIndex.CompareTo(right.AssemblyIndex);
            if (result != 0) return result;
            result = left.MethodToken.CompareTo(right.MethodToken);
            return result != 0 ? result : left.ilOffset.CompareTo(right.ilOffset);
        });
        var queryLimited = matched.Count > NavigationMaxResults;
        var total = Math.Min(matched.Count, NavigationMaxResults);
        var pageEdges = matched.Take(total).Skip(offset).Take(limit).ToArray();
        var page = await MaterializeNavigationResultsAsync(snapshot, index.Assemblies, pageEdges, cancellationToken).ConfigureAwait(false);
        var hasMore = offset + page.Length < total;
        if (queryLimited) diagnostics = diagnostics.Append($"Target results stopped at the {NavigationMaxResults} result safety limit.").ToArray();
        var response = new Dictionary<string, object?>
        {
            ["total"] = total, ["offset"] = offset, ["limit"] = limit, ["hasMore"] = hasMore,
            ["nextOffset"] = hasMore ? offset + page.Length : (int?)null, ["snapshotId"] = snapshot.Id, ["scope"] = scope,
            ["searchedAssemblyCount"] = searchedAssemblies.Length, ["unresolvedReferences"] = unresolvedReferences,
            ["complete"] = complete && !queryLimited, ["diagnosticCount"] = diagnostics.Length,
            ["diagnostics"] = includeDiagnostics ? diagnostics : diagnostics.Take(5).ToArray(),
            ["results"] = page,
        };
        if (includeDiagnostics)
            response["searchedAssemblies"] = searchedAssemblies;
        return SerializeResponse(response);
    }
    internal static List<NavigationEdge> DeduplicateCallSites(IEnumerable<NavigationEdge> edges) =>
        edges.DistinctBy(edge => (edge.AssemblyIndex, edge.MethodToken, edge.ilOffset)).ToList();
    private async Task<NavigationQueryScan> ScanNavigationOverflowAsync(CatalogSnapshot snapshot,
        NavigationAssembly[] assemblies, ResolvedNavigationTarget target, string mode, CancellationToken cancellationToken)
    {
        var directTargetKeys = target.Tokens.Select(token => new NavigationTargetKey(target.AssemblyId, token)).ToHashSet();
        var potentialTargetKeys = target.PotentialTargets.ToHashSet();
        var edges = new List<NavigationEdge>();
        var searched = new List<string>();
        var diagnostics = snapshot.Diagnostics.ToList();
        if (snapshot.CatalogTruncated)
            AddDiagnostic(diagnostics, "Installed assembly inventory was truncated; reference analysis may omit assemblies.");
        var unresolved = 0;
        var methodCount = 0;
        var referenceCount = 0;
        var budget = new NavigationBudget(NavigationMaxOverflowInstructions);
        var limited = false;
        for (var assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = assemblies[assemblyIndex];
            var entry = snapshot.Assemblies.First(candidate => candidate.Id == identity.Id);
            try
            {
                using var pe = await LoadPeAsync(entry, cancellationToken).ConfigureAwait(false);
                using var resolver = new RootedAssemblyResolver(snapshot, entry, pe);
                var typeSystem = new DecompilerTypeSystem(pe.File, resolver, CreateDecompilerSettings());
                using var metadata = await LoadNavigationMetadataAsync(entry, cancellationToken).ConfigureAwait(false);
                var reader = metadata.Reader;
                searched.Add(entry.Id);
                foreach (var methodHandle in reader.MethodDefinitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++methodCount > NavigationMaxOverflowMethods) { limited = true; break; }
                    var body = ReadMethodBody(metadata.PeReader, reader, methodHandle);
                    if (body is null) continue;
                    var referenceEnumerator = DecodeReferences(body, cancellationToken, diagnostics, budget);
                    while (referenceEnumerator.MoveNext())
                    {
                        var reference = referenceEnumerator.Current;
                        if (++referenceCount > NavigationMaxOverflowReferences) { limited = true; break; }
                        if (mode == "callers" &&
                            (reference.Kind != "method" || reference.opcode is not ("call" or "callvirt" or "newobj")))
                            continue;
                        var resolved = ResolveReference(snapshot, typeSystem.MainModule, reference.Token);
                        if (resolved is null) { unresolved++; continue; }
                        var targetKey = new NavigationTargetKey(resolved.AssemblyId, resolved.Token);
                        var isDirect = directTargetKeys.Contains(targetKey);
                        var isPotentialVirtualDispatch = !isDirect && mode == "usages" && reference.Kind == "method" &&
                            reference.opcode == "callvirt" && potentialTargetKeys.Contains(targetKey);
                        if (!isDirect && !isPotentialVirtualDispatch) continue;
                        edges.Add(new NavigationEdge(assemblyIndex, MetadataTokens.GetToken(methodHandle),
                            reference.opcode, reference.ilOffset, reference.operandKind, isPotentialVirtualDispatch));
                    }
                    if (budget.Exhausted) limited = true;
                    if (limited) break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (IsAssemblyReadFailure(exception))
            {
                AddDiagnostic(diagnostics, $"Omitted {entry.Origin}/{entry.RelativePath}: IL analysis failed: {exception.Message}");
            }
            if (limited) break;
        }
        if (limited)
            AddDiagnostic(diagnostics, $"Targeted IL overflow scan stopped at {NavigationMaxOverflowMethods} methods, {NavigationMaxOverflowReferences} references, or {NavigationMaxOverflowInstructions} instructions/results.");
        if (unresolved > 0)
            AddDiagnostic(diagnostics, $"{unresolved} IL metadata reference(s) could not be resolved to an installed definition.");
        var complete = !limited && unresolved == 0 && diagnostics.Count == 0 && searched.Count == assemblies.Length;
        return new NavigationQueryScan(edges.ToArray(), searched.ToArray(), diagnostics.ToArray(), unresolved, complete);
    }
    private async Task<NavigationQueryScan> GetNavigationOverflowScanAsync(CatalogSnapshot snapshot,
        NavigationReferenceIndex index, ResolvedNavigationTarget target, string scope, string mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetTokens = string.Join(",", target.Tokens.Order().Select(token => token.ToString("X8")));
        var key = $"{snapshot.Id}|{scope}|{mode}|{target.AssemblyId}|{targetTokens}";
        var cached = Volatile.Read(ref _navigationOverflowCache);
        if (cached is not null && cached.Key.Equals(key, StringComparison.Ordinal))
            return cached.Scan;
        await NavigationIndexBuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            cached = Volatile.Read(ref _navigationOverflowCache);
            if (cached is not null && cached.Key.Equals(key, StringComparison.Ordinal))
                return cached.Scan;
            var scan = await ScanNavigationOverflowAsync(snapshot, index.Assemblies, target, mode, cancellationToken).ConfigureAwait(false);
            var frozen = scan with { Edges = scan.Edges.ToArray() };
            Volatile.Write(ref _navigationOverflowCache, new NavigationOverflowCache(key, frozen));
            return frozen;
        }
        finally { NavigationIndexBuildGate.Release(); }
    }
    private async Task<NavigationResult[]> MaterializeNavigationResultsAsync(CatalogSnapshot snapshot,
        NavigationAssembly[] assemblies, NavigationEdge[] edges, CancellationToken cancellationToken)
    {
        var opened = new Dictionary<int, NavigationMetadata>();
        var results = new NavigationResult[edges.Length];
        var methodInfoByToken = new Dictionary<(int AssemblyIndex, int MethodToken), (string SymbolId, string Display)>();
        try
        {
            for (var index = 0; index < edges.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var edge = edges[index];
                if (!opened.TryGetValue(edge.AssemblyIndex, out var metadata))
                {
                    var assembly = assemblies[edge.AssemblyIndex];
                    var entry = snapshot.Assemblies.First(candidate => candidate.Id == assembly.Id);
                    metadata = await LoadNavigationMetadataAsync(entry, cancellationToken).ConfigureAwait(false);
                    opened.Add(edge.AssemblyIndex, metadata);
                }
                var sourceAssembly = assemblies[edge.AssemblyIndex];
                var methodHandle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(edge.MethodToken);
                var methodKey = (edge.AssemblyIndex, edge.MethodToken);
                if (!methodInfoByToken.TryGetValue(methodKey, out var methodInfo))
                {
                    methodInfo = (SymbolId(sourceAssembly.Id, methodHandle), GetMethodDisplay(metadata.Reader, methodHandle));
                    methodInfoByToken.Add(methodKey, methodInfo);
                }
                results[index] = new NavigationResult(sourceAssembly.Id, sourceAssembly.Name, sourceAssembly.Origin,
                    sourceAssembly.RelativePath, methodInfo.SymbolId, methodInfo.Display,
                    edge.opcode, edge.ilOffset, edge.operandKind.ToString(),
                    edge.potentialVirtualDispatch ? "potentialVirtualDispatch" : "direct");
            }
        }
        finally
        {
            foreach (var metadata in opened.Values) metadata.Dispose();
        }
        return results;
    }

    private async Task<NavigationReferenceIndex> GetNavigationReferenceIndexAsync(CatalogSnapshot snapshot, string scope,
        AssemblyEntry selected, CancellationToken cancellationToken)
    {
        var key = snapshot.Id + "|" + scope;
        var existing = Volatile.Read(ref _navigationReferenceIndex);
        if (existing is not null && existing.Key.Equals(key, StringComparison.Ordinal))
            return existing;
        await NavigationIndexBuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = Volatile.Read(ref _navigationReferenceIndex);
            if (existing is not null && existing.Key.Equals(key, StringComparison.Ordinal))
                return existing;
            var built = await BuildNavigationReferenceIndexAsync(snapshot, key,
                scope == "*" ? snapshot.Assemblies : new[] { selected }, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _navigationReferenceIndex, built);
            return built;
        }
        finally { NavigationIndexBuildGate.Release(); }
    }

    private async Task<NavigationReferenceIndex> BuildNavigationReferenceIndexAsync(CatalogSnapshot snapshot, string key,
        IReadOnlyList<AssemblyEntry> entries, CancellationToken cancellationToken)
    {
        var byTarget = new Dictionary<NavigationTargetKey, List<NavigationEdge>>();
        var searched = new List<string>();
        var diagnostics = snapshot.Diagnostics.ToList();
        if (snapshot.CatalogTruncated)
            AddDiagnostic(diagnostics, "Installed assembly inventory was truncated; reference analysis may omit assemblies.");
        var unresolved = 0;
        var indexedReferences = 0;
        var decodedReferences = 0;
        var scannedMethods = 0;
        var budget = new NavigationBudget(NavigationMaxTotalInstructions);
        var limited = false;
        for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[entryIndex];
            try
            {
                using var pe = await LoadPeAsync(entry, cancellationToken).ConfigureAwait(false);
                using var resolver = new RootedAssemblyResolver(snapshot, entry, pe);
                var typeSystem = new DecompilerTypeSystem(pe.File, resolver, CreateDecompilerSettings());
                using var metadata = await LoadNavigationMetadataAsync(entry, cancellationToken).ConfigureAwait(false);
                var reader = metadata.Reader;
                searched.Add(entry.Id);
                foreach (var methodHandle in reader.MethodDefinitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++scannedMethods > NavigationMaxMethods) { limited = true; break; }
                    var body = ReadMethodBody(metadata.PeReader, reader, methodHandle);
                    if (body is null) continue;
                    var referenceEnumerator = DecodeReferences(body, cancellationToken, diagnostics, budget);
                    while (referenceEnumerator.MoveNext())
                    {
                        var reference = referenceEnumerator.Current;
                        if (++decodedReferences > NavigationMaxReferences || indexedReferences >= NavigationMaxIndexedReferences)
                        {
                            limited = true;
                            break;
                        }
                        var target = ResolveReference(snapshot, typeSystem.MainModule, reference.Token);
                        if (target is null) { unresolved++; continue; }
                        var edge = new NavigationEdge(entryIndex, MetadataTokens.GetToken(methodHandle),
                            reference.opcode, reference.ilOffset, reference.operandKind);
                        var targetKey = new NavigationTargetKey(target.AssemblyId, target.Token);
                        if (!byTarget.TryGetValue(targetKey, out var matches))
                            byTarget.Add(targetKey, matches = new List<NavigationEdge>());
                        matches.Add(edge);
                        indexedReferences++;
                    }
                    if (budget.Exhausted) limited = true;
                    if (limited) break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (IsAssemblyReadFailure(exception))
            {
                AddDiagnostic(diagnostics, $"Omitted {entry.Origin}/{entry.RelativePath}: IL analysis failed: {exception.Message}");
            }
            if (limited) break;
        }
        if (limited)
            AddDiagnostic(diagnostics, $"IL index stopped at {NavigationMaxMethods} methods, {NavigationMaxReferences} decoded references, or {NavigationMaxIndexedReferences} indexed definitions.");
        if (unresolved > 0)
            AddDiagnostic(diagnostics, $"{unresolved} IL metadata reference(s) could not be resolved to an installed definition.");
        var frozen = byTarget.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var assemblies = entries.Select(entry => new NavigationAssembly(entry.Id, entry.Name, entry.Origin, entry.RelativePath)).ToArray();
        var complete = !limited && unresolved == 0 && diagnostics.Count == 0 && searched.Count == entries.Count;
        return new NavigationReferenceIndex(key, frozen, assemblies, searched.ToArray(), diagnostics.ToArray(), unresolved, limited, complete);
    }


    private static void ValidateNavigationPage(int offset, int limit)
    {
        if (offset < 0 || offset > NavigationMaxResults) throw new ArgumentOutOfRangeException(nameof(offset), $"offset must be between 0 and {NavigationMaxResults}.");
        if (limit < 1 || limit > NavigationMaxPage) throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be between 1 and {NavigationMaxPage}.");
    }

    private static IEntity? FindTypeSystemSymbol(MetadataModule module, MetadataReader reader, string assemblyId, string symbolId)
    {
        foreach (var type in module.TypeDefinitions)
        {
            if (SymbolId(assemblyId, type.MetadataToken).Equals(symbolId, StringComparison.Ordinal))
                return type;
            foreach (var member in type.Members)
                if (!member.MetadataToken.IsNil && SymbolId(assemblyId, member.MetadataToken).Equals(symbolId, StringComparison.Ordinal))
                    return member;
        }
        foreach (var methodHandle in reader.MethodDefinitions)
            if (SymbolId(assemblyId, (EntityHandle)methodHandle).Equals(symbolId, StringComparison.Ordinal))
                return module.ResolveEntity(methodHandle, default);
        return null;
    }

    private static async Task<NavigationMetadata> LoadNavigationMetadataAsync(AssemblyEntry entry, CancellationToken cancellationToken)
    {
        var bytes = entry.ScanBytes ?? await ReadEntryBytesAsync(entry.Root, entry.FullPath, entry.Origin, cancellationToken);
        if (entry.ScanBytes is null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.ContentHash, StringComparison.Ordinal))
            throw new AssemblyChangedException($"Assembly '{entry.Origin}/{entry.RelativePath}' changed during navigation; rerun api_assemblies and use its fresh IDs.");
        var stream = new MemoryStream(bytes, writable: false);
        try { return new NavigationMetadata(new PEReader(stream), stream); }
        catch { stream.Dispose(); throw; }
    }

    private static ResolvedNavigationTarget? ResolveReference(CatalogSnapshot snapshot, MetadataModule module, EntityHandle handle)
    {
        try
        {
            var entity = module.ResolveEntity(handle, default);
            IEntity? definition = entity switch
            {
                IMember member => member.MemberDefinition,
                IType type => type.GetDefinition(),
                _ => null,
            };
            var entry = FindEntry(snapshot, definition?.ParentModule?.MetadataFile?.FileName);
            if (entry is null || definition is null || definition.MetadataToken.IsNil) return null;
            var token = MetadataTokens.GetToken(definition.MetadataToken);
            return new ResolvedNavigationTarget(entry.Id, token, new[] { token }, Array.Empty<NavigationTargetKey>());
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentException or InvalidOperationException) { return null; }
    }

    private sealed class NavigationMetadata : IDisposable
    {
        private readonly MemoryStream _stream;
        public PEReader PeReader { get; }
        public MetadataReader Reader => PeReader.GetMetadataReader();
        public NavigationMetadata(PEReader peReader, MemoryStream stream) { PeReader = peReader; _stream = stream; }
        public void Dispose() { PeReader.Dispose(); _stream.Dispose(); }
    }

    private async Task<ResolvedNavigationTarget> ResolveApiEntityAsync(CatalogSnapshot snapshot, AssemblyEntry entry,
        string symbolId, bool requireMethod, bool includePotentialVirtualDispatch, CancellationToken cancellationToken)
    {
        using var pe = await LoadPeAsync(entry, cancellationToken);
        using var resolver = new RootedAssemblyResolver(snapshot, entry, pe);
        var typeSystem = new DecompilerTypeSystem(pe.File, resolver, CreateDecompilerSettings());
        using var metadata = await LoadNavigationMetadataAsync(entry, cancellationToken);
        var entity = FindTypeSystemSymbol(typeSystem.MainModule, metadata.Reader, entry.Id, symbolId);
        if (entity is null)
            throw MissingSymbol("symbolId", entry.Id, symbolId, "type or member");
        if (requireMethod && entity is not IMethod)
            await ThrowNavigationWrongKindAsync(snapshot, entry, symbolId, "api_find_callers", cancellationToken).ConfigureAwait(false);
        IEnumerable<IMember> definitions = entity switch
        {
            IProperty property => new[] { property.Getter, property.Setter }.Where(accessor => accessor is not null).Cast<IMember>(),
            IEvent @event => new[] { @event.AddAccessor, @event.RemoveAccessor, @event.InvokeAccessor }.Where(accessor => accessor is not null).Cast<IMember>(),
            IMember member => new[] { member.MemberDefinition },
            _ => Array.Empty<IMember>(),
        };
        var members = definitions.ToArray();
        var tokens = members.Select(member => MetadataTokens.GetToken(member.MetadataToken)).Distinct().ToArray();
        if (tokens.Length == 0) tokens = new[] { MetadataTokens.GetToken(entity.MetadataToken) };
        var directTargets = tokens.Select(token => new NavigationTargetKey(entry.Id, token)).ToHashSet();
        var potentialTargets = includePotentialVirtualDispatch
            ? ResolvePotentialVirtualDispatchTargets(snapshot, members.OfType<IMethod>(), directTargets)
            : Array.Empty<NavigationTargetKey>();
        return new(entry.Id, tokens[0], tokens, potentialTargets);
    }
    private static NavigationTargetKey[] ResolvePotentialVirtualDispatchTargets(CatalogSnapshot snapshot,
        IEnumerable<IMethod> methods, HashSet<NavigationTargetKey> directTargets)
    {
        var targets = new HashSet<NavigationTargetKey>();
        foreach (var method in methods)
        {
            var current = method.MemberDefinition;
            var visited = new HashSet<IMember>();
            while (current.IsOverride && visited.Add(current))
            {
                if (InheritanceHelper.GetBaseMember(current) is not IMethod baseMethod)
                    break;
                var definition = baseMethod.MemberDefinition;
                var entry = FindEntry(snapshot, definition.ParentModule?.MetadataFile?.FileName);
                if (entry is not null && !definition.MetadataToken.IsNil)
                {
                    var target = new NavigationTargetKey(entry.Id, MetadataTokens.GetToken(definition.MetadataToken));
                    if (!directTargets.Contains(target))
                        targets.Add(target);
                }
                current = definition;
            }
        }
        return targets.ToArray();
    }
    private async Task ThrowNavigationWrongKindAsync(CatalogSnapshot snapshot, AssemblyEntry entry, string symbolId,
        string expectedTool, CancellationToken cancellationToken)
    {
        var symbol = await FindIndexedSymbolByIdAsync(snapshot, entry, symbolId, cancellationToken).ConfigureAwait(false);
        if (symbol is not null)
            throw WrongKind(entry, symbol.Hit, expectedTool);
        throw new ApiDiscoveryException("symbol.wrong_kind",
            $"symbolId is not a valid target for {expectedTool}.",
            new { tool = "api_assemblies", arguments = new { name = entry.Name, origin = entry.Origin, offset = 0, limit = 50, snapshotId = snapshot.Id } });
    }

    private static MethodBodyBlock? ReadMethodBody(PEReader pe, MetadataReader reader, MethodDefinitionHandle handle)
    {
        var definition = reader.GetMethodDefinition(handle);
        return definition.RelativeVirtualAddress == 0 ? null : pe.GetMethodBody(definition.RelativeVirtualAddress);
    }

    private static DecodedReferenceEnumerator DecodeReferences(MethodBodyBlock body, CancellationToken cancellationToken,
        List<string> diagnostics, NavigationBudget? budget = null) =>
        new(body.GetILReader(), cancellationToken, diagnostics, budget);

    private static int OperandSize(IlOperandType type, ref BlobReader il) => type switch
    {
        IlOperandType.InlineNone => 0,
        IlOperandType.ShortInlineBrTarget or IlOperandType.ShortInlineI or IlOperandType.ShortInlineVar => 1,
        IlOperandType.InlineVar => 2,
        IlOperandType.InlineI or IlOperandType.ShortInlineR or IlOperandType.InlineBrTarget or IlOperandType.InlineField or IlOperandType.InlineMethod or IlOperandType.InlineSig or IlOperandType.InlineString or IlOperandType.InlineTok or IlOperandType.InlineType => 4,
        IlOperandType.InlineI8 or IlOperandType.InlineR => 8,
        IlOperandType.InlineSwitch => InlineSwitchSize(ref il),
        _ => throw new BadImageFormatException($"unsupported operand kind {type}.")
    };

    private static int InlineSwitchSize(ref BlobReader il)
    {
        var start = il.Offset;
        if (il.RemainingBytes < 4) throw new BadImageFormatException("truncated switch count.");
        var count = il.ReadInt32();
        if (count < 0 || count > il.RemainingBytes / 4) throw new BadImageFormatException("invalid switch target count.");
        il.Offset = start;
        return checked(4 + count * 4);
    }

    private ref struct DecodedReferenceEnumerator
    {
        private BlobReader _il;
        private readonly CancellationToken _cancellationToken;
        private readonly List<string> _diagnostics;
        private readonly NavigationBudget? _budget;
        private int _instructions;

        public DecodedReference Current { get; private set; }

        public DecodedReferenceEnumerator(BlobReader il, CancellationToken cancellationToken,
            List<string> diagnostics, NavigationBudget? budget)
        {
            _il = il;
            _cancellationToken = cancellationToken;
            _diagnostics = diagnostics;
            _budget = budget;
            _instructions = 0;
            Current = default;
        }

        public bool MoveNext()
        {
            while (_il.Offset < _il.Length)
            {
                if (++_instructions > NavigationMaxInstructionsPerBody || _budget is not null && !_budget.TryConsume())
                {
                    AddDiagnostic(_diagnostics, _budget?.Exhausted == true
                        ? $"IL scan exceeded the {_budget.MaximumInstructions} instruction safety limit."
                        : $"IL body exceeded the {NavigationMaxInstructionsPerBody} instruction safety limit.");
                    return false;
                }
                _cancellationToken.ThrowIfCancellationRequested();
                var start = _il.Offset;
                ushort value = _il.ReadByte();
                if (value == 0xfe)
                {
                    if (_il.RemainingBytes == 0) { AddDiagnostic(_diagnostics, "Malformed IL: truncated two-byte opcode."); return false; }
                    value = (ushort)(0xfe00 | _il.ReadByte());
                }
                if (!NavigationOpCodes.TryGetValue(value, out var opcode))
                {
                    AddDiagnostic(_diagnostics, $"Malformed IL: unknown opcode at IL_{start:X4}.");
                    return false;
                }
                int size;
                try { size = OperandSize(opcode.OperandType, ref _il); }
                catch (BadImageFormatException exception)
                {
                    AddDiagnostic(_diagnostics, $"Malformed IL at IL_{start:X4}: {exception.Message}");
                    return false;
                }
                if (size < 0 || size > _il.RemainingBytes)
                {
                    AddDiagnostic(_diagnostics, $"Malformed IL: truncated operand at IL_{start:X4}.");
                    return false;
                }
                var operandStart = _il.Offset;
                var operandKind = opcode.OperandType;
                if (operandKind is IlOperandType.InlineMethod or IlOperandType.InlineField or IlOperandType.InlineTok or IlOperandType.InlineType)
                {
                    var token = _il.ReadInt32();
                    _il.Offset = operandStart + size;
                    EntityHandle handle;
                    try { handle = MetadataTokens.EntityHandle(token); }
                    catch (ArgumentException)
                    {
                        AddDiagnostic(_diagnostics, $"Malformed metadata token at IL_{start:X4}.");
                        continue;
                    }
                    var kind = operandKind == IlOperandType.InlineType ? "type" :
                        operandKind == IlOperandType.InlineField ? "field" :
                        operandKind == IlOperandType.InlineMethod ? "method" : "token";
                    Current = new DecodedReference(handle, kind, opcode.Name ?? "unknown", start, operandKind);
                    return true;
                }
                _il.Offset = operandStart + size;
            }
            return false;
        }
    }

    private static string DescribeMemberReference(MetadataReader reader, MemberReferenceHandle handle)
    {
        var member = reader.GetMemberReference(handle);
        var parent = member.Parent;
        var typeName = parent.Kind switch
        {
            HandleKind.TypeDefinition => DescribeTypeDefinition(reader, (TypeDefinitionHandle)parent),
            HandleKind.TypeReference => DescribeTypeReference(reader, (TypeReferenceHandle)parent),
            _ => $"{parent.Kind}:{MetadataTokens.GetToken(parent):X8}",
        };
        return ClipNavigationText($"{typeName}.{reader.GetString(member.Name)}", 512);
    }

    private static string DescribeTypeDefinition(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var ns = reader.GetString(type.Namespace);
        return ClipNavigationText((ns.Length == 0 ? "" : ns + ".") + reader.GetString(type.Name), 512);
    }

    private static string DescribeTypeReference(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var ns = reader.GetString(type.Namespace);
        return ClipNavigationText((ns.Length == 0 ? "" : ns + ".") + reader.GetString(type.Name), 512);
    }

    private static string ClipNavigationText(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";

    private static string DescribeReference(MetadataReader reader, EntityHandle handle)
    {
        try
        {
            return handle.Kind switch
            {
                HandleKind.MethodDefinition => GetMethodDisplay(reader, (MethodDefinitionHandle)handle),
                HandleKind.MemberReference => DescribeMemberReference(reader, (MemberReferenceHandle)handle),
                HandleKind.FieldDefinition => ClipNavigationText(reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name), 512),
                HandleKind.TypeDefinition => DescribeTypeDefinition(reader, (TypeDefinitionHandle)handle),
                HandleKind.TypeReference => DescribeTypeReference(reader, (TypeReferenceHandle)handle),
                _ => $"{handle.Kind} {MetadataTokens.GetToken(handle):X8}"
            };
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentException) { return $"unresolved {MetadataTokens.GetToken(handle):X8}"; }
    }
    private static string? ReferencedAssembly(MetadataReader reader, EntityHandle handle)
    {
        try
        {
            if (handle.Kind == HandleKind.MethodSpecification)
                handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            if (handle.Kind != HandleKind.MemberReference) return null;
            var parent = reader.GetMemberReference((MemberReferenceHandle)handle).Parent;
            if (parent.Kind != HandleKind.TypeReference) return null;
            var scope = reader.GetTypeReference((TypeReferenceHandle)parent).ResolutionScope;
            return scope.Kind == HandleKind.AssemblyReference
                ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
                : null;
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentException) { return null; }
    }
    private static string? ReferencedAssemblyVersion(MetadataReader reader, EntityHandle handle)
    {
        try
        {
            if (handle.Kind == HandleKind.MethodSpecification)
                handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
            if (handle.Kind != HandleKind.MemberReference) return null;
            var parent = reader.GetMemberReference((MemberReferenceHandle)handle).Parent;
            if (parent.Kind != HandleKind.TypeReference) return null;
            var scope = reader.GetTypeReference((TypeReferenceHandle)parent).ResolutionScope;
            return scope.Kind == HandleKind.AssemblyReference
                ? reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Version.ToString()
                : null;
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentException) { return null; }
    }

    private static string GetMethodDisplay(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        var type = reader.GetTypeDefinition(method.GetDeclaringType());
        return ClipNavigationText(reader.GetString(type.Name) + "." + reader.GetString(method.Name), 512);
    }


    private sealed record NavigationReferenceIndex(string Key, IReadOnlyDictionary<NavigationTargetKey, NavigationEdge[]> ByTarget,
        NavigationAssembly[] Assemblies, string[] SearchedAssemblies, string[] Diagnostics, int UnresolvedReferences,
        bool LimitReached, bool Complete);
    private readonly record struct NavigationTargetKey(string AssemblyId, int Token);
    private sealed record ResolvedNavigationTarget(string AssemblyId, int Token, int[] Tokens, NavigationTargetKey[] PotentialTargets);
    private sealed record NavigationResult(string assemblyId, string assemblyName, string origin, string relativePath,
        string symbolId, string method, string opcode, int ilOffset, string operandKind, string matchKind);
    internal readonly record struct NavigationEdge(int AssemblyIndex, int MethodToken, string opcode, int ilOffset,
        IlOperandType operandKind, bool potentialVirtualDispatch = false);
    private readonly record struct DecodedReference(EntityHandle Token, string Kind, string opcode, int ilOffset, IlOperandType operandKind);
    private sealed record NavigationAssembly(string Id, string Name, string Origin, string RelativePath);
    private sealed record CalleeResult(string target, int referenceToken, string referenceHandleKind,
        string? targetAssemblyId, string? targetSymbolId, string? referencedAssembly, string? referencedAssemblyVersion,
        string? targetOrigin, string opcode, int ilOffset, string operandKind, bool resolved);
    private sealed record NavigationQueryScan(NavigationEdge[] Edges, string[] SearchedAssemblies,
        string[] Diagnostics, int UnresolvedReferences, bool Complete);
    private sealed record NavigationOverflowCache(string Key, NavigationQueryScan Scan);
    private sealed class NavigationBudget(int maximumInstructions)
    {
        private int _instructions;
        public int MaximumInstructions { get; } = maximumInstructions;
        public bool Exhausted { get; private set; }

        public bool TryConsume()
        {
            if (++_instructions <= MaximumInstructions) return true;
            Exhausted = true;
            return false;
        }
    }
}
