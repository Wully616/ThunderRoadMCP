using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

public sealed partial class InstalledApiCatalog
{
    private const int ApiIndexSchemaVersion = 6;
    private const string ApiFormatterVersion = "signature-v11+xml-docs-v1";
    private const int MaxApiIndexFileBytes = 32 * 1024 * 1024;
    private const long MaxApiIndexMemoryBytes = 384L * 1024 * 1024;
    private const long MaxApiIndexDiskBytes = 384L * 1024 * 1024;
    private const long ApiIndexBaseEstimatedBytes = 1024;
    private const long ApiIndexSymbolMapOverheadBytes = 64;
    private const long ApiIndexTypeMapOverheadBytes = 128;
    private const long ApiIndexTypeNameMapOverheadBytes = 128;
    private const int ApiIndexEstimatedArrayOverheadBytes = 32;
    private const long MaxSourceMemoryBytes = 32L * 1024 * 1024;
    private const int SourceLineIndexStride = 256;
    private static long _lastApiDiskGaugeTicks;
    private static readonly long MaxSourceDocumentBytes = MaxDecompiledChars * sizeof(char) +
        ((MaxDecompiledChars + SourceLineIndexStride - 1) / SourceLineIndexStride + 1) * sizeof(int) + 128 * 1024;
    private readonly object _apiCacheGate = new();
    private readonly object _directoryGate = new();
    private string? _directorySnapshotId;
    private ApiHit[]? _directoryHits;
    private bool _directoryComplete;
    private long _directoryEstimatedBytes;
    private readonly Dictionary<string, ApiIndexCacheEntry> _apiIndexes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _apiIndexLru = new();
    private readonly Dictionary<string, Task<ApiAssemblyIndex>> _apiIndexBuilds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _apiIndexBuildSlots = new(1, 1);
    private readonly Dictionary<string, SourceCacheEntry> _sourceDocuments = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _sourceLru = new();
    private readonly Dictionary<string, Task<SourceDocument>> _sourceBuilds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sourcePendingWaiters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sourceBuildReservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceCacheEntry> _sourceLifetimes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sourceMemoryAvailable = new(0);
    private long _sourceBuildReservedBytes;
    private readonly Dictionary<string, int> _apiIndexPendingWaiters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApiIndexLifetime> _apiIndexLifetimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _apiIndexBuildReservations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _apiIndexMemoryAvailable = new(0);
    private long _apiIndexBuildReservedBytes;
    private readonly SemaphoreSlim _sourceBuildSlots = new(1, 1);
    private long _apiIndexMemoryBytes;
    private long _sourceMemoryBytes;

    private readonly long _apiIndexMemoryBudgetBytes;

    private long ApiMemoryGaugeBytes =>
        _apiIndexMemoryBytes + _apiIndexBuildReservedBytes + Volatile.Read(ref _directoryEstimatedBytes);

    public InstalledApiCatalog() : this(MaxApiIndexMemoryBytes) { }

    private InstalledApiCatalog(long apiIndexMemoryBudgetBytes)
    {
        if (apiIndexMemoryBudgetBytes is <= 0 or > MaxApiIndexMemoryBytes)
            throw new ArgumentOutOfRangeException(nameof(apiIndexMemoryBudgetBytes));
        _apiIndexMemoryBudgetBytes = apiIndexMemoryBudgetBytes;
    }

    private async Task<ApiIndexLease> GetApiIndexAsync(CatalogSnapshot snapshot, AssemblyEntry entry, CancellationToken cancellationToken)
    {
        var identity = ApiIndexIdentity(entry);
        Task<ApiAssemblyIndex> build;
        lock (_apiCacheGate)
        {
            if (_apiIndexes.TryGetValue(identity, out var cached))
            {
                Touch(_apiIndexLru, cached.Node);
                ToolTiming.Count("api.index.memoryHit");
                _apiIndexLifetimes[identity].ActiveReaders++;
                return new ApiIndexLease(cached.Index, () => ReleaseApiIndex(identity));
            }
            if (_apiIndexLifetimes.TryGetValue(identity, out var retained))
            {
                ToolTiming.Count("api.index.memoryHit");
                retained.ActiveReaders++;
                return new ApiIndexLease(retained.Index, () => ReleaseApiIndex(identity));
            }
            if (!_apiIndexBuilds.TryGetValue(identity, out build!))
            {
                ToolTiming.Count("api.index.miss");
                var buildSnapshot = snapshot.DetachScanBytesExcept(entry);
                build = Task.Run(() => BuildApiIndexFlightAsync(buildSnapshot, entry, identity));
                _apiIndexBuilds.Add(identity, build);
            }
            _apiIndexPendingWaiters[identity] = _apiIndexPendingWaiters.GetValueOrDefault(identity) + 1;
        }

        try
        {
            var index = await build.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_apiCacheGate)
            {
                var lifetime = _apiIndexLifetimes[identity];
                lifetime.ActiveReaders++;
                CompleteApiIndexWaiter(identity);
                return new ApiIndexLease(index, () => ReleaseApiIndex(identity));
            }
        }
        catch
        {
            lock (_apiCacheGate)
                CompleteApiIndexWaiter(identity);
            throw;
        }
    }

    private async Task<ApiAssemblyIndex> BuildApiIndexFlightAsync(CatalogSnapshot snapshot, AssemblyEntry entry, string identity)
    {
        await _apiIndexBuildSlots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var persisted = await ReadApiIndexAsync(identity, entry.Id).ConfigureAwait(false);
            if (persisted is not null)
            {
                ToolTiming.Count("api.index.diskHit");
                CacheApiIndex(identity, persisted);
                return persisted;
            }

            ToolTiming.Count("api.index.build");
            ApiAssemblyIndex index;
            try
            {
                index = await BuildApiIndexAsync(identity, snapshot, entry).ConfigureAwait(false);
            }
            catch
            {
                ToolTiming.Count("api.index.buildFailure");
                throw;
            }
            PersistApiIndex(identity, entry.Id, index);
            CacheApiIndex(identity, index);
            return index;
        }
        finally
        {
            ReleaseApiIndexBuildReservation(identity);
            _apiIndexBuildSlots.Release();
            lock (_apiCacheGate)
                _apiIndexBuilds.Remove(identity);
            snapshot.Dispose();
        }
    }

    private async Task<ApiAssemblyIndex?> ReadApiIndexAsync(string identity, string assemblyId)
    {
        using var timing = ToolTiming.Measure("apiIndexPersistentLoadMs");
        string? path = null;
        var reservationHeld = false;
        var returnedIndex = false;
        try
        {
            path = ApiIndexFilePath(identity);
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            if (info.Length is < 1 or > MaxApiIndexFileBytes)
            {
                TryDelete(path);
                return null;
            }
            var predictedBytes = Math.Min(_apiIndexMemoryBudgetBytes, Math.Max(512L, info.Length * 4));
            await ReserveApiIndexBuildBytesAsync(identity, predictedBytes).ConfigureAwait(false);
            reservationHeld = true;
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            if (bytes.LongLength > MaxApiIndexFileBytes)
            {
                TryDelete(path);
                return null;
            }
            var document = JsonSerializer.Deserialize<ApiIndexDocument>(bytes);
            if (document is null || !IsValidApiIndexDocument(document, identity, assemblyId))
            {
                TryDelete(path);
                return null;
            }
            var symbolsById = document.Symbols.ToDictionary(symbol => symbol.Hit.symbolId, StringComparer.Ordinal);
            var types = document.Types.Select(type => new ApiTypeRecord(
                symbolsById[type.SymbolId], type.FullName, type.DirectBaseTypeNames, type.GenericConstraints,
                type.MemberSymbolIds.Select(symbolId => symbolsById[symbolId]).ToArray(), type.EnumValues)).ToArray();
            var index = new ApiAssemblyIndex(assemblyId, document.Symbols, types,
                EstimateIndexBytes(document.Symbols, types), document.Complete, document.OmittedDeclarations,
                document.OmittedPublicDeclarations, document.LimitReached);
            await ReserveApiIndexBuildBytesAsync(identity, index.EstimatedBytes).ConfigureAwait(false);
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            returnedIndex = true;
            if (ToolTiming.Enabled && Environment.TickCount64 - Interlocked.Read(ref _lastApiDiskGaugeTicks) > 2000)
            {
                Interlocked.Exchange(ref _lastApiDiskGaugeTicks, Environment.TickCount64);
                GaugeApiIndexDiskBytes(GetCacheRoot());
            }
            return index;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException or OverflowException or FormatException)
        {
            if (path is not null)
                TryDelete(path);
            return null;
        }
        finally
        {
            if (reservationHeld && !returnedIndex)
                ReleaseApiIndexBuildReservation(identity);
        }
    }

    private static bool IsValidApiIndexDocument(ApiIndexDocument document, string identity, string assemblyId)
    {
        if (document.Schema != ApiIndexSchemaVersion || !string.Equals(document.Identity, identity, StringComparison.Ordinal) ||
            !string.Equals(document.AssemblyId, assemblyId, StringComparison.Ordinal) ||
            document.OmittedDeclarations < 0 || document.OmittedPublicDeclarations < 0 ||
            document.OmittedPublicDeclarations > document.OmittedDeclarations ||
            document.Complete != (document.OmittedPublicDeclarations == 0 && !document.LimitReached) ||
            document.Symbols is null || document.Types is null ||
            document.Symbols.Length > MaxResults || document.Types.Length > MaxResults)
            return false;

        var symbolsById = new Dictionary<string, ApiIndexedSymbol>(StringComparer.Ordinal);
        foreach (var symbol in document.Symbols)
        {
            if (symbol is null || symbol.Hit is null || symbol.MetadataToken <= 0 || symbol.RelatedTypeNames is null ||
                symbol.ParameterSignatures is null || symbol.GenericConstraints is null || symbol.Hit.warnings is null ||
                !IsValidApiHitAttributes(symbol.Hit.attributes) || !IsValidXmlDocumentation(symbol.Hit.xmlDocumentation))
                return false;
            var hit = symbol.Hit;
            if (string.IsNullOrEmpty(hit.assemblyId) || !hit.assemblyId.Equals(assemblyId, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(hit.assemblyName) || string.IsNullOrEmpty(hit.origin) ||
                string.IsNullOrEmpty(hit.relativePath) || string.IsNullOrEmpty(hit.symbolId) ||
                string.IsNullOrEmpty(hit.kind) || string.IsNullOrEmpty(hit.qualifiedName) ||
                string.IsNullOrEmpty(hit.signature) || string.IsNullOrEmpty(hit.accessibility))
                return false;
            var expectedId = "sym_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{assemblyId}:{symbol.MetadataToken:X8}")))[..32];
            if (!hit.symbolId.Equals(expectedId, StringComparison.Ordinal) ||
                !symbolsById.TryAdd(hit.symbolId, symbol) ||
                symbol.RelatedTypeNames.Any(string.IsNullOrEmpty) || symbol.ParameterSignatures.Any(string.IsNullOrEmpty) ||
                symbol.GenericConstraints.Any(string.IsNullOrEmpty) || hit.warnings.Any(string.IsNullOrEmpty))
                return false;
        }

        var typeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in document.Types)
        {
            if (type is null || string.IsNullOrEmpty(type.SymbolId) || string.IsNullOrEmpty(type.FullName) ||
                !typeIds.Add(type.SymbolId) ||
                type.DirectBaseTypeNames is null || type.GenericConstraints is null ||
                type.MemberSymbolIds is null || type.EnumValues is null ||
                !symbolsById.TryGetValue(type.SymbolId, out var indexedType) ||
                indexedType.Hit.kind is not ("class" or "struct" or "interface" or "enum" or "delegate" or "type") ||
                type.DirectBaseTypeNames.Any(string.IsNullOrEmpty) || type.GenericConstraints.Any(string.IsNullOrEmpty) ||
                type.EnumValues.Any(string.IsNullOrEmpty) || type.MemberSymbolIds.Length > MaxResults ||
                type.MemberSymbolIds.Any(memberId => string.IsNullOrEmpty(memberId) ||
                    !symbolsById.TryGetValue(memberId, out var indexedMember) ||
                    !string.Equals(indexedMember.Hit.declaringType, indexedType.Hit.qualifiedName, StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    private static bool IsValidApiHitAttributes(ApiAttribute[]? attributes)
    {
        if (attributes is null || attributes.Length > MaxApiAttributes)
            return false;
        long totalChars = 0;
        foreach (var attribute in attributes)
        {
            if (attribute is null || string.IsNullOrEmpty(attribute.Name) || attribute.FixedArguments is null ||
                attribute.NamedArguments is null || attribute.FixedArguments.Length > MaxApiAttributeArguments ||
                attribute.NamedArguments.Length > MaxApiAttributeArguments || attribute.Target is { Length: 0 })
                return false;
            totalChars += attribute.Name.Length + (attribute.Target?.Length ?? 0);
            if (totalChars > MaxApiAttributeChars)
                return false;
            foreach (var argument in attribute.FixedArguments)
            {
                if (argument is null || argument.Name is not null ||
                    !TryGetApiAttributeValueLength(argument.Value, out var valueLength))
                    return false;
                totalChars += valueLength;
                if (totalChars > MaxApiAttributeChars)
                    return false;
            }
            foreach (var argument in attribute.NamedArguments)
            {
                if (argument is null || string.IsNullOrEmpty(argument.Name) ||
                    !TryGetApiAttributeValueLength(argument.Value, out var valueLength))
                    return false;
                totalChars += argument.Name.Length + valueLength;
                if (totalChars > MaxApiAttributeChars)
                    return false;
            }
        }
        return true;
    }

    private static bool TryGetApiAttributeValueLength(object? value, out int length)
    {
        if (value is null)
        {
            length = 0;
            return true;
        }
        if (value is string text)
        {
            length = text.Length;
            return true;
        }
        if (value is JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Null:
                    length = 0;
                    return true;
                case JsonValueKind.True:
                    length = 4;
                    return true;
                case JsonValueKind.False:
                    length = 5;
                    return true;
                case JsonValueKind.Number:
                    length = element.GetRawText().Length;
                    return true;
                case JsonValueKind.String:
                    length = element.GetString()?.Length ?? 0;
                    return true;
                default:
                    length = 0;
                    return false;
            }
        }
        if (value is bool boolean)
        {
            length = boolean ? 4 : 5;
            return true;
        }
        if (value is byte || value is sbyte || value is short || value is ushort || value is int ||
            value is uint || value is long || value is ulong || value is float || value is double || value is decimal)
        {
            length = value.ToString()?.Length ?? 0;
            return true;
        }
        length = 0;
        return false;
    }

    private void PersistApiIndex(string identity, string assemblyId, ApiAssemblyIndex index)
    {
        string? path = null;
        string? temporaryPath = null;
        try
        {
            path = ApiIndexFilePath(identity);
            var types = index.Types.Select(type => new ApiTypeDocument(
                type.Symbol.Hit.symbolId, type.FullName, type.DirectBaseTypeNames, type.GenericConstraints,
                type.Members.Select(member => member.Hit.symbolId).ToArray(), type.EnumValues)).ToArray();
            var document = new ApiIndexDocument(ApiIndexSchemaVersion, identity, assemblyId, index.Complete,
                index.OmittedDeclarations, index.OmittedPublicDeclarations, index.LimitReached, index.Symbols, types);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            if (bytes.Length > MaxApiIndexFileBytes)
            {
                ToolTiming.Count("api.index.persistFailure");
                return;
            }
            var directory = Path.GetDirectoryName(path)!;
            var cacheRoot = GetCacheRoot();
            Directory.CreateDirectory(directory);
            using var cacheLock = AcquireApiCacheMutex(cacheRoot);
            if (!EvictApiIndexFiles(cacheRoot, bytes.Length))
            {
                ToolTiming.Count("api.index.persistFailure");
                return;
            }
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, overwrite: true);
            EvictApiIndexFiles(cacheRoot, 0);
            GaugeApiIndexDiskBytes(cacheRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ToolTiming.Count("api.index.persistFailure");
        }
        finally
        {
            if (temporaryPath is not null)
                TryDelete(temporaryPath);
        }
    }

    private async Task<ApiAssemblyIndex> BuildApiIndexAsync(string identity, CatalogSnapshot snapshot, AssemblyEntry entry)
    {
        using var timing = ToolTiming.Measure("apiIndexBuildMs");
        await ReserveApiIndexBuildBytesAsync(identity, ApiIndexBaseEstimatedBytes).ConfigureAwait(false);
        long estimatedBytes = ApiIndexBaseEstimatedBytes;
        using var main = await LoadPeAsync(entry, CancellationToken.None).ConfigureAwait(false);
        using var resolver = new MainAssemblyResolver(main);
        var typeSystem = new DecompilerTypeSystem(main.File, resolver, CreateDecompilerSettings());
        var documentationProvider = CreateXmlDocumentationProvider(entry);
        var symbols = new List<ApiIndexedSymbol>();
        var types = new List<ApiTypeRecord>();
        long nonPublicMembersOmitted = 0;
        var omittedDeclarations = 0;
        var omittedPublicDeclarations = 0;
        long omittedGeneratedDeclarations = 0;
        long syntheticSkipped = 0;
        var limitReached = false;
        foreach (var type in typeSystem.MainModule.TypeDefinitions)
        {
            if (type.MetadataToken.IsNil)
            {
                omittedDeclarations++;
                if (IsCompilerGeneratedName(type.Name))
                    omittedGeneratedDeclarations++;
                else if (type.EffectiveAccessibility() == Accessibility.Public)
                    omittedPublicDeclarations++;
                continue;
            }
            var rawTypeName = type.FullTypeName.ReflectionName;
            var typeName = FormatTypeName(rawTypeName);
            var typeHit = CreateHit(entry, type, TypeKindName(type.Kind), typeName,
                type.DeclaringTypeDefinition is null ? null : FormatTypeName(type.DeclaringTypeDefinition.FullTypeName.FullName),
                FormatTypeDeclaration(type), type.EffectiveAccessibility(), type.IsStatic, ObsoleteWarnings(type)) with
            {
                xmlDocumentation = ReadXmlDocumentation(type, documentationProvider),
            };
            var directBaseTypeNames = type.DirectBaseTypes.Select(FormatType).ToArray();
            var constraints = FormatConstraints(type.TypeParameters);
            var members = new List<ApiIndexedSymbol>();
            foreach (var member in DeclaredSearchMembers(type, includeConstructors: true))
            {
                if (member.MetadataToken.IsNil)
                {
                    if (member is IMethod { IsConstructor: true } &&
                        type.Kind is TypeKind.Struct or TypeKind.Enum)
                    {
                        syntheticSkipped++;
                        continue;
                    }
                    omittedDeclarations++;
                    if (IsCompilerGeneratedName(member.Name))
                        omittedGeneratedDeclarations++;
                    else if (member.EffectiveAccessibility() == Accessibility.Public)
                        omittedPublicDeclarations++;
                    continue;
                }
                if (member.EffectiveAccessibility() != Accessibility.Public)
                {
                    nonPublicMembersOmitted++;
                    continue;
                }
                if (symbols.Count >= MaxResults)
                {
                    limitReached = true;
                    break;
                }
                var memberWarnings = new List<string>();
                var hit = CreateMemberHit(snapshot, entry, type, member, memberWarnings);
                if (hit is null)
                    continue;
                hit = hit with { xmlDocumentation = ReadXmlDocumentation(member.MemberDefinition, documentationProvider) };
                var indexed = CreateIndexedSymbol(hit, MetadataTokens.GetToken(member.MetadataToken), member);
                var increment = EstimateIndexedSymbolBytes(indexed) + IntPtr.Size + ApiIndexSymbolMapOverheadBytes;
                if (estimatedBytes + increment > _apiIndexMemoryBudgetBytes)
                {
                    limitReached = true;
                    break;
                }
                await ReserveApiIndexBuildBytesAsync(identity, estimatedBytes + increment).ConfigureAwait(false);
                estimatedBytes += increment;
                members.Add(indexed);
                symbols.Add(indexed);
            }
            if (limitReached)
                break;
            if (symbols.Count >= MaxResults)
            {
                limitReached = true;
                break;
            }
            var indexedType = CreateIndexedSymbol(typeHit, MetadataTokens.GetToken(type.MetadataToken), type);
            var enumValues = type.Kind == TypeKind.Enum
                ? type.Fields.Where(field => field.IsConst).OrderBy(field => field.Name, StringComparer.Ordinal)
                    .Select(field => field.Name + " = " + FormatConstant(field.GetConstantValue())).ToArray()
                : Array.Empty<string>();
            var typeRecord = new ApiTypeRecord(indexedType, rawTypeName, directBaseTypeNames, constraints,
                members.ToArray(), enumValues);
            var typeIncrement = EstimateIndexedSymbolBytes(indexedType) + IntPtr.Size + ApiIndexSymbolMapOverheadBytes +
                EstimateApiTypeRecordBytes(typeRecord) + IntPtr.Size + ApiIndexTypeMapOverheadBytes +
                IntPtr.Size + ApiIndexTypeNameMapOverheadBytes;
            if (estimatedBytes + typeIncrement > _apiIndexMemoryBudgetBytes)
            {
                limitReached = true;
                break;
            }
            await ReserveApiIndexBuildBytesAsync(identity, estimatedBytes + typeIncrement).ConfigureAwait(false);
            estimatedBytes += typeIncrement;
            symbols.Add(indexedType);
            types.Add(typeRecord);
        }
        ToolTiming.Count("api.index.nonPublicOmitted", nonPublicMembersOmitted);
        ToolTiming.Count("api.index.tokenlessOmitted", omittedDeclarations);
        ToolTiming.Count("api.index.tokenlessPublic", omittedPublicDeclarations);
        ToolTiming.Count("api.index.tokenlessGenerated", omittedGeneratedDeclarations);
        ToolTiming.Count("api.index.syntheticSkipped", syntheticSkipped);
        if (limitReached)
            ToolTiming.Count("api.index.limitReached");
        symbols.Sort(ApiIndexedSymbolComparer.Instance);
        types.Sort((left, right) => StringComparer.Ordinal.Compare(left.Symbol.Hit.symbolId, right.Symbol.Hit.symbolId));
        return new ApiAssemblyIndex(entry.Id, symbols.ToArray(), types.ToArray(), estimatedBytes,
            omittedPublicDeclarations == 0 && !limitReached, omittedDeclarations, omittedPublicDeclarations, limitReached);
    }
    private const int MaxXmlDocumentationChars = 4 * 1024;

    private static XmlDocumentationProvider? CreateXmlDocumentationProvider(AssemblyEntry entry)
    {
        if (entry.XmlDocumentationStatus != "available" ||
            entry.XmlDocumentationPath is null ||
            entry.XmlDocumentationContentHash is null)
            return null;

        var path = Path.ChangeExtension(entry.FullPath, ".xml");
        if (!File.Exists(path))
            throw new AssemblyChangedException($"XML documentation for '{entry.Origin}/{entry.RelativePath}' changed during the request; rerun discovery.");
        var canonicalPath = ResolveCanonicalPath(path);
        if (!PathComparer.Equals(canonicalPath, entry.XmlDocumentationPath))
            throw new AssemblyChangedException($"XML documentation for '{entry.Origin}/{entry.RelativePath}' changed during the request; rerun discovery.");

        try
        {
            using var stream = new FileStream(canonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            var contentHash = Convert.ToHexString(SHA256.HashData(stream));
            if (stream.Length != length || !contentHash.Equals(entry.XmlDocumentationContentHash, StringComparison.Ordinal))
                throw new AssemblyChangedException($"XML documentation for '{entry.Origin}/{entry.RelativePath}' changed during the request; rerun discovery.");
            return new XmlDocumentationProvider(canonicalPath);
        }
        catch (AssemblyChangedException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                                          InvalidOperationException or NotSupportedException or System.Xml.XmlException)
        {
            throw new AssemblyChangedException($"XML documentation for '{entry.Origin}/{entry.RelativePath}' changed or became invalid during the request; rerun discovery.", exception);
        }
    }

    private static ApiXmlDocumentation? ReadXmlDocumentation(IEntity entity, XmlDocumentationProvider? provider)
    {
        var documentationText = provider?.GetDocumentation(entity);
        if (documentationText is null)
            return null;
        var documentationRoot = XElement.Parse($"<doc>{documentationText}</doc>");
        var documentation = new XmlDocumentationElement(documentationRoot, entity as IMember, null);

        var remaining = MaxXmlDocumentationChars;
        var truncated = false;
        string? ReadSingle(string name)
        {
            var element = documentation.Children.FirstOrDefault(child => child.Name == name);
            return element is null ? null : TakeXmlDocumentationText(element.TextContent, ref remaining, ref truncated);
        }

        var summary = ReadSingle("summary");
        var remarks = ReadSingle("remarks");
        List<ApiXmlDocumentationParameter>? parameters = null;
        foreach (var parameter in documentation.Children.Where(child => child.Name == "param"))
        {
            var name = TakeXmlDocumentationText(parameter.GetAttribute("name"), ref remaining, ref truncated);
            var description = TakeXmlDocumentationText(parameter.TextContent, ref remaining, ref truncated);
            if (!string.IsNullOrEmpty(name) && description is not null)
                (parameters ??= new()).Add(new ApiXmlDocumentationParameter(name, description));
        }
        var returns = ReadSingle("returns");
        var value = ReadSingle("value");
        List<string>? examples = null;
        foreach (var example in documentation.Children.Where(child => child.Name == "example"))
        {
            var text = TakeXmlDocumentationText(example.TextContent, ref remaining, ref truncated);
            if (text is not null)
                (examples ??= new()).Add(text);
        }

        if (summary is null && remarks is null && parameters is null && returns is null && value is null && examples is null)
            return null;
        return new ApiXmlDocumentation(summary, remarks, parameters?.ToArray(), returns, value, examples?.ToArray(),
            truncated ? true : null);
    }

    private static string? TakeXmlDocumentationText(string? text, ref int remaining, ref bool truncated)
    {
        var normalized = NormalizeXmlDocumentationText(text);
        if (normalized.Length == 0)
            return null;
        if (normalized.Length <= remaining)
        {
            remaining -= normalized.Length;
            return normalized;
        }

        var length = remaining;
        if (length > 0 && length < normalized.Length &&
            char.IsHighSurrogate(normalized[length - 1]) && char.IsLowSurrogate(normalized[length]))
            length--;
        remaining = 0;
        truncated = true;
        return length == 0 ? null : normalized[..length].TrimEnd();
    }

    private static string NormalizeXmlDocumentationText(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsValidXmlDocumentation(ApiXmlDocumentation? documentation)
    {
        if (documentation is null)
            return true;
        if (documentation.Truncated is not (null or true) ||
            documentation.Parameters is { Length: > MaxXmlDocumentationChars } ||
            documentation.Examples is { Length: > MaxXmlDocumentationChars })
            return false;
        var textLength = (documentation.Summary?.Length ?? 0) + (documentation.Remarks?.Length ?? 0) +
            (documentation.Returns?.Length ?? 0) + (documentation.Value?.Length ?? 0);
        if (documentation.Parameters is not null)
        {
            foreach (var parameter in documentation.Parameters)
            {
                if (parameter is null || string.IsNullOrEmpty(parameter.Name) || string.IsNullOrEmpty(parameter.Description))
                    return false;
                textLength += parameter.Name.Length + parameter.Description.Length;
            }
        }
        if (documentation.Examples is not null)
        {
            foreach (var example in documentation.Examples)
            {
                if (string.IsNullOrEmpty(example))
                    return false;
                textLength += example.Length;
            }
        }
        return textLength <= MaxXmlDocumentationChars;
    }


    private static bool IsCompilerGeneratedName(string name) =>
        name.Length == 0 || name[0] is '<' or '$' ||
        name.Contains("<>", StringComparison.Ordinal) || name.Contains("k__BackingField", StringComparison.Ordinal);

    private static ApiIndexedSymbol CreateIndexedSymbol(ApiHit hit, int metadataToken, IEntity entity)
    {
        var related = new RelatedTypeNameCollector();
        string? returnType = null;
        var parameters = Array.Empty<string>();
        if (entity is ITypeDefinition type)
        {
            foreach (var baseType in type.DirectBaseTypes)
                related.Add(baseType);
            related.AddConstraints(type.TypeParameters);
        }
        else if (entity is IMember member)
        {
            switch (member)
            {
                case IMethod method:
                    returnType = FormatType(method.ReturnType);
                    related.Add(method.ReturnType);
                    parameters = method.Parameters.Select(parameter => FormatParameter(parameter)).ToArray();
                    foreach (var parameter in method.Parameters)
                        related.Add(parameter.Type);
                    related.AddConstraints(method.TypeParameters);
                    break;
                case IProperty property:
                    returnType = FormatType(property.ReturnType);
                    related.Add(property.ReturnType);
                    parameters = property.Parameters.Select(parameter => FormatParameter(parameter)).ToArray();
                    foreach (var parameter in property.Parameters)
                        related.Add(parameter.Type);
                    break;
                case IField field:
                    returnType = FormatType(field.ReturnType);
                    related.Add(field.ReturnType);
                    break;
                case IEvent @event:
                    returnType = FormatType(@event.ReturnType);
                    related.Add(@event.ReturnType);
                    break;
            }
        }
        var genericConstraints = entity switch
        {
            ITypeDefinition typeDefinition => FormatConstraints(typeDefinition.TypeParameters),
            IMethod methodDefinition => FormatConstraints(methodDefinition.TypeParameters),
            _ => Array.Empty<string>(),
        };
        return new ApiIndexedSymbol(hit, metadataToken, related.Names.ToArray(), parameters,
            returnType, genericConstraints);
    }

    private sealed class RelatedTypeNameCollector
    {
        private const int MaxDepth = 64;
        private const int MaxTypes = 256;
        private readonly HashSet<IType> _visited = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _seenNames = new(StringComparer.Ordinal);

        public List<string> Names { get; } = new();

        public void AddConstraints(IReadOnlyList<ITypeParameter> parameters)
        {
            foreach (var parameter in parameters)
                foreach (var constraint in parameter.TypeConstraints)
                    Add(constraint.Type, 0);
        }

        public void Add(IType type) => Add(type, 0);

        private void Add(IType type, int depth)
        {
            if (depth > MaxDepth || _visited.Count >= MaxTypes || !_visited.Add(type))
                return;

            switch (type)
            {
                case ByReferenceType byReference:
                    Add(byReference.ElementType, depth + 1);
                    break;
                case ArrayType array:
                    Add(array.ElementType, depth + 1);
                    break;
                case PointerType pointer:
                    Add(pointer.ElementType, depth + 1);
                    break;
                case ParameterizedType parameterized:
                    Add(parameterized.GenericType, depth + 1);
                    foreach (var argument in parameterized.TypeArguments)
                        Add(argument, depth + 1);
                    break;
                case ITypeParameter parameter:
                    foreach (var constraint in parameter.TypeConstraints)
                        Add(constraint.Type, depth + 1);
                    break;
                default:
                    if (type.FullName.Length > 0 && !type.FullName.StartsWith("<", StringComparison.Ordinal))
                    {
                        var name = FormatTypeName(type.FullName);
                        if (_seenNames.Add(name))
                            Names.Add(name);
                    }
                    break;
            }
        }
    }


    private void CacheApiIndex(string identity, ApiAssemblyIndex index)
    {
        lock (_apiCacheGate)
        {
            if (_apiIndexes.ContainsKey(identity))
                return;
            var reserved = _apiIndexBuildReservations.GetValueOrDefault(identity);
            _apiIndexBuildReservations.Remove(identity);
            _apiIndexBuildReservedBytes -= reserved;
            var lifetime = new ApiIndexLifetime(index, _apiIndexPendingWaiters.GetValueOrDefault(identity))
            {
                IsCached = true,
            };
            _apiIndexLifetimes.Add(identity, lifetime);
            _apiIndexMemoryBytes += index.EstimatedBytes;
            var node = _apiIndexLru.AddFirst(identity);
            _apiIndexes.Add(identity, new ApiIndexCacheEntry(index, node));
            ToolTiming.Gauge("api.index.memoryBytes", ApiMemoryGaugeBytes);
        }
    }

    private async Task ReserveApiIndexBuildBytesAsync(string identity, long requestedBytes)
    {
        if (requestedBytes is < 0 || requestedBytes > _apiIndexMemoryBudgetBytes)
            throw new InvalidOperationException($"API metadata index exceeds the {_apiIndexMemoryBudgetBytes} byte process cache budget.");
        while (true)
        {
            var wait = false;
            lock (_apiCacheGate)
            {
                var current = _apiIndexBuildReservations.GetValueOrDefault(identity);
                var difference = requestedBytes - current;
                if (difference <= 0)
                {
                    _apiIndexBuildReservedBytes += difference;
                    if (requestedBytes == 0) _apiIndexBuildReservations.Remove(identity);
                    else _apiIndexBuildReservations[identity] = requestedBytes;
                    if (difference < 0)
                        _apiIndexMemoryAvailable.Release();
                    if (difference != 0)
                        ToolTiming.Gauge("api.index.memoryBytes", ApiMemoryGaugeBytes);
                    return;
                }

                while (ApiMemoryGaugeBytes + difference > _apiIndexMemoryBudgetBytes &&
                       _apiIndexLru.Last is { } last)
                    EvictApiIndexCacheEntry(last.Value);
                if (ApiMemoryGaugeBytes + difference <= _apiIndexMemoryBudgetBytes)
                {
                    _apiIndexBuildReservedBytes += difference;
                    _apiIndexBuildReservations[identity] = requestedBytes;
                    ToolTiming.Gauge("api.index.memoryBytes", ApiMemoryGaugeBytes);
                    return;
                }
                wait = true;
            }
            if (wait)
                await _apiIndexMemoryAvailable.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void ReleaseApiIndexBuildReservation(string identity)
    {
        lock (_apiCacheGate)
        {
            if (_apiIndexBuildReservations.Remove(identity, out var reserved))
            {
                _apiIndexBuildReservedBytes -= reserved;
                _apiIndexMemoryAvailable.Release();
                ToolTiming.Gauge("api.index.memoryBytes", ApiMemoryGaugeBytes);
            }
        }
    }

    private void CompleteApiIndexWaiter(string identity)
    {
        if (_apiIndexPendingWaiters.TryGetValue(identity, out var pending))
        {
            if (pending <= 1) _apiIndexPendingWaiters.Remove(identity);
            else _apiIndexPendingWaiters[identity] = pending - 1;
        }
        if (!_apiIndexLifetimes.TryGetValue(identity, out var lifetime))
            return;
        lifetime.PendingWaiters--;
        ReleaseApiIndexLifetimeIfUnused(identity, lifetime);
    }

    private void ReleaseApiIndex(string identity)
    {
        lock (_apiCacheGate)
        {
            if (!_apiIndexLifetimes.TryGetValue(identity, out var lifetime))
                return;
            lifetime.ActiveReaders--;
            ReleaseApiIndexLifetimeIfUnused(identity, lifetime);
        }
    }

    private void ReleaseApiIndexLifetimeIfUnused(string identity, ApiIndexLifetime lifetime)
    {
        if (lifetime.IsCached || lifetime.ActiveReaders != 0 || lifetime.PendingWaiters != 0)
            return;
        _apiIndexLifetimes.Remove(identity);
        _apiIndexMemoryBytes -= lifetime.Index.EstimatedBytes;
        _apiIndexMemoryAvailable.Release();
        ToolTiming.Gauge("api.index.memoryBytes", ApiMemoryGaugeBytes);
    }

    private void EvictApiIndexCacheEntry(string identity)
    {
        if (!_apiIndexes.Remove(identity, out var removed))
            return;
        _apiIndexLru.Remove(removed.Node);
        if (_apiIndexLifetimes.TryGetValue(identity, out var lifetime))
        {
            lifetime.IsCached = false;
            ReleaseApiIndexLifetimeIfUnused(identity, lifetime);
        }
        ToolTiming.Count("api.index.eviction");
    }

    private async Task<SourceDocumentLease> GetSourceDocumentAsync(CatalogSnapshot snapshot, AssemblyEntry entry,
        ApiIndexedSymbol symbol, CancellationToken cancellationToken)
    {
        var identity = SourceIdentity(snapshot, entry, symbol.Hit.symbolId);
        Task<SourceDocument> build;
        lock (_apiCacheGate)
        {
            if (_sourceDocuments.TryGetValue(identity, out var cached))
            {
                Touch(_sourceLru, cached.Node);
                ToolTiming.Count("api.source.cacheHit");
                cached.ActiveReaders++;
                return new SourceDocumentLease(cached.Document, () => ReleaseSourceDocument(identity));
            }
            if (_sourceLifetimes.TryGetValue(identity, out var retained))
            {
                ToolTiming.Count("api.source.cacheHit");
                retained.ActiveReaders++;
                return new SourceDocumentLease(retained.Document, () => ReleaseSourceDocument(identity));
            }
            if (!_sourceBuilds.TryGetValue(identity, out build!))
            {
                ToolTiming.Count("api.source.cacheMiss");
                var buildSnapshot = snapshot.DetachScanBytesExcept(entry);
                build = Task.Run(() => BuildSourceFlightAsync(buildSnapshot, entry, symbol, identity));
            }
            _sourcePendingWaiters[identity] = _sourcePendingWaiters.GetValueOrDefault(identity) + 1;
        }
        try
        {
            var document = await build.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_apiCacheGate)
            {
                var cached = _sourceLifetimes[identity];
                cached.ActiveReaders++;
                CompleteSourceWaiter(identity);
                return new SourceDocumentLease(document, () => ReleaseSourceDocument(identity));
            }
        }
        catch
        {
            lock (_apiCacheGate)
                CompleteSourceWaiter(identity);
            throw;
        }
    }

    private async Task<SourceDocument> BuildSourceFlightAsync(CatalogSnapshot snapshot, AssemblyEntry entry,
        ApiIndexedSymbol symbol, string identity)
    {
        await _sourceBuildSlots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await ReserveSourceBuildBytesAsync(identity, MaxSourceDocumentBytes).ConfigureAwait(false);
            ToolTiming.Count("api.source.decompileBuild");
            using var main = await LoadPeAsync(entry, CancellationToken.None).ConfigureAwait(false);
            using var resolver = new RootedAssemblyResolver(snapshot, entry, main);
            var settings = CreateDecompilerSettings();
            var documentationProvider = CreateXmlDocumentationProvider(entry);
            var typeSystem = new DecompilerTypeSystem(main.File, resolver, settings);
            var entity = FindEntity(typeSystem.MainModule, symbol.MetadataToken)
                ?? throw new InvalidOperationException("symbolId is invalid, stale, or does not identify a type/member in assemblyId.");
            var warnings = ObsoleteWarnings(entity);
            string source;
            if (entity is IMethod method && !method.HasBody)
            {
                source = FormatMemberSignature(method) + ";";
                warnings.Add("The selected method has no managed IL body (for example, it is abstract, extern, or has no implementation); no body was reconstructed.");
            }
            else
            {
                var decompiler = new CSharpDecompiler(typeSystem, settings)
                {
                    CancellationToken = CancellationToken.None,
                    DocumentationProvider = documentationProvider,
                };
                source = entity is ITypeDefinition definition
                    ? decompiler.DecompileTypeAsString(definition.FullTypeName)
                    : decompiler.DecompileAsString(entity.MetadataToken);
                AddWarnings(warnings, decompiler.Errors.Select(CSharpDecompiler.GetErrorHeadline));
            }
            if (source.Length > MaxDecompiledChars)
                throw new InvalidOperationException($"Reconstructed source exceeds the {MaxDecompiledChars} character safety limit; select a smaller member.");
            AddWarnings(warnings, resolver.Warnings);
            var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var starts = new List<int> { 0 };
            var totalLines = 1;
            for (var i = 0; i < normalized.Length; i++)
            {
                if (normalized[i] != '\n')
                    continue;
                totalLines++;
                if ((totalLines - 1) % SourceLineIndexStride == 0)
                    starts.Add(i + 1);
            }
            var document = new SourceDocument(normalized, starts.ToArray(), totalLines, warnings.ToArray());
            await ReserveSourceBuildBytesAsync(identity, EstimateSourceDocumentBytes(document)).ConfigureAwait(false);
            CacheSourceDocument(identity, document);
            return document;
        }
        finally
        {
            ReleaseSourceBuildReservation(identity);
            _sourceBuildSlots.Release();
            lock (_apiCacheGate)
                _sourceBuilds.Remove(identity);
            snapshot.Dispose();
        }
    }

    private ApiSourcePage CreateSourcePage(CatalogSnapshot snapshot, AssemblyEntry entry, ApiIndexedSymbol symbol,
        SourceDocument document, int startLine, int lineCount)
    {
        var first = startLine - 1;
        if (first >= document.TotalLines)
            throw new ArgumentOutOfRangeException(nameof(startLine), $"startLine {startLine} exceeds the source line count {document.TotalLines}.");
        var count = Math.Min(lineCount, document.TotalLines - first);
        var lines = new string[count];
        var indexedLine = first / SourceLineIndexStride * SourceLineIndexStride;
        var lineStart = document.LineStarts[first / SourceLineIndexStride];
        while (indexedLine < first)
        {
            var newline = document.Source.IndexOf('\n', lineStart);
            lineStart = newline + 1;
            indexedLine++;
        }
        for (var i = 0; i < count; i++)
        {
            var newline = document.Source.IndexOf('\n', lineStart);
            var end = newline < 0 ? document.Source.Length : newline;
            lines[i] = document.Source[lineStart..end];
            lineStart = newline < 0 ? document.Source.Length : newline + 1;
        }
        var hasMore = first + count < document.TotalLines;
        var hit = symbol.Hit;
        return new ApiSourcePage(entry.Id, entry.Name, entry.Origin, entry.RelativePath,
            hit.symbolId, hit.qualifiedName, "reconstructed-csharp",
            new { origin = entry.Origin, relativePath = entry.RelativePath, assemblyName = entry.Name, assemblyId = entry.Id, symbolId = hit.symbolId },
            string.Join('\n', lines), startLine, document.TotalLines, count, hasMore,
            hasMore ? startLine + count : null, snapshot.Id, document.Warnings, snapshot.Diagnostics);
    }

    private void CacheSourceDocument(string identity, SourceDocument document)
    {
        var bytes = EstimateSourceDocumentBytes(document);
        if (bytes > MaxSourceMemoryBytes)
            throw new InvalidOperationException($"Reconstructed source index exceeds the {MaxSourceMemoryBytes / (1024 * 1024)} MiB source cache budget.");
        lock (_apiCacheGate)
        {
            var reserved = _sourceBuildReservations.GetValueOrDefault(identity);
            _sourceBuildReservations.Remove(identity);
            _sourceBuildReservedBytes -= reserved;
            var node = _sourceLru.AddFirst(identity);
            var cached = new SourceCacheEntry(document, bytes, node, _sourcePendingWaiters.GetValueOrDefault(identity));
            _sourceDocuments.Add(identity, cached);
            _sourceLifetimes.Add(identity, cached);
            _sourceMemoryBytes += bytes;
            ToolTiming.Gauge("api.source.memoryBytes", _sourceMemoryBytes + _sourceBuildReservedBytes);
        }
    }

    private static long EstimateSourceDocumentBytes(SourceDocument document) =>
        (long)document.Source.Length * sizeof(char) + (long)document.LineStarts.Length * sizeof(int) +
        document.Warnings.Sum(warning => 32L + (long)warning.Length * sizeof(char)) + 256;

    private async Task ReserveSourceBuildBytesAsync(string identity, long requestedBytes)
    {
        if (requestedBytes is < 0 or > MaxSourceMemoryBytes)
            throw new InvalidOperationException($"Source document exceeds the {MaxSourceMemoryBytes / (1024 * 1024)} MiB source cache budget.");
        while (true)
        {
            lock (_apiCacheGate)
            {
                var current = _sourceBuildReservations.GetValueOrDefault(identity);
                var difference = requestedBytes - current;
                if (difference <= 0)
                {
                    _sourceBuildReservedBytes += difference;
                    if (requestedBytes == 0) _sourceBuildReservations.Remove(identity);
                    else _sourceBuildReservations[identity] = requestedBytes;
                    if (difference < 0)
                        _sourceMemoryAvailable.Release();
                    return;
                }
                while (_sourceMemoryBytes + _sourceBuildReservedBytes + difference > MaxSourceMemoryBytes &&
                       _sourceLru.Last is { } last)
                    EvictSourceCacheEntry(last.Value);
                if (_sourceMemoryBytes + _sourceBuildReservedBytes + difference <= MaxSourceMemoryBytes)
                {
                    _sourceBuildReservedBytes += difference;
                    _sourceBuildReservations[identity] = requestedBytes;
                    return;
                }
            }
            await _sourceMemoryAvailable.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void ReleaseSourceBuildReservation(string identity)
    {
        lock (_apiCacheGate)
        {
            if (_sourceBuildReservations.Remove(identity, out var reserved))
            {
                _sourceBuildReservedBytes -= reserved;
                _sourceMemoryAvailable.Release();
                ToolTiming.Gauge("api.source.memoryBytes", _sourceMemoryBytes + _sourceBuildReservedBytes);
            }
        }
    }

    private void CompleteSourceWaiter(string identity)
    {
        if (_sourcePendingWaiters.TryGetValue(identity, out var pending))
        {
            if (pending <= 1) _sourcePendingWaiters.Remove(identity);
            else _sourcePendingWaiters[identity] = pending - 1;
        }
        if (!_sourceLifetimes.TryGetValue(identity, out var lifetime))
            return;
        lifetime.PendingWaiters--;
        ReleaseSourceLifetimeIfUnused(identity, lifetime);
    }

    private void ReleaseSourceDocument(string identity)
    {
        lock (_apiCacheGate)
        {
            if (!_sourceLifetimes.TryGetValue(identity, out var lifetime))
                return;
            lifetime.ActiveReaders--;
            ReleaseSourceLifetimeIfUnused(identity, lifetime);
        }
    }

    private void ReleaseSourceLifetimeIfUnused(string identity, SourceCacheEntry lifetime)
    {
        if (lifetime.IsCached || lifetime.ActiveReaders != 0 || lifetime.PendingWaiters != 0)
            return;
        _sourceLifetimes.Remove(identity);
        _sourceMemoryBytes -= lifetime.EstimatedBytes;
        _sourceMemoryAvailable.Release();
        ToolTiming.Gauge("api.source.memoryBytes", _sourceMemoryBytes + _sourceBuildReservedBytes);
    }

    private void EvictSourceCacheEntry(string identity)
    {
        if (!_sourceDocuments.Remove(identity, out var removed))
            return;
        _sourceLru.Remove(removed.Node);
        removed.IsCached = false;
        ReleaseSourceLifetimeIfUnused(identity, removed);
    }

    private static string ApiIndexIdentity(AssemblyEntry entry)
    {
        var value = string.Join("\n", "api-index", ApiIndexSchemaVersion.ToString(), ApiFormatterVersion,
            ApiLookupRanker.Version, typeof(CSharpDecompiler).Assembly.GetName().Version?.ToString() ?? "unknown",
            Environment.Version.ToString(), entry.Id, entry.XmlDocumentationStatus, entry.XmlDocumentationContentHash ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string SourceIdentity(CatalogSnapshot snapshot, AssemblyEntry entry, string symbolId)
    {
        var value = string.Join("\n", "api-source", ApiIndexSchemaVersion.ToString(), ApiFormatterVersion,
            typeof(CSharpDecompiler).Assembly.GetName().Version?.ToString() ?? "unknown", Environment.Version.ToString(),
            snapshot.Id, entry.Id, symbolId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ApiIndexFilePath(string identity) => Path.Combine(GetCacheRoot(), "api-index-v" + ApiIndexSchemaVersion,
        identity + ".json");

    private const int TypeMapSchemaVersion = 2;
    private const int MaxTypeMapEntries = 200_000;
    private const int MaxTypeMapFileBytes = 16 * 1024 * 1024;
    private const int ApiDirectorySchemaVersion = 1;
    private const int MaxApiDirectoryHits = MaxResults * 8;
    private const int MaxApiDirectoryFileBytes = 160 * 1024 * 1024;

    private static string ApiDirectoryFilePath(string snapshotId) => Path.Combine(GetCacheRoot(),
        "api-index-v" + ApiIndexSchemaVersion, "symbols-" + snapshotId + ".json");

    private async Task<(ApiHit[]? Hits, bool Complete)> ReadApiDirectoryAsync(CatalogSnapshot snapshot)
    {
        using var timing = ToolTiming.Measure("apiIndexPersistentLoadMs");
        lock (_directoryGate)
        {
            if (string.Equals(_directorySnapshotId, snapshot.Id, StringComparison.Ordinal) && _directoryHits is { } cached)
            {
                ToolTiming.Gauge("api.index.memoryBytes", ApiMemoryGaugeBytes);
                ToolTiming.Count("api.index.directoryHit");
                return (cached, _directoryComplete);
            }
            _directorySnapshotId = null;
            _directoryHits = null;
            _directoryComplete = false;
            _directoryEstimatedBytes = 0;
        }
        var path = ApiDirectoryFilePath(snapshot.Id);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 1 or > MaxApiDirectoryFileBytes)
            {
                ToolTiming.Count("api.index.directoryMiss");
                return (null, false);
            }
            ApiDirectoryDocument? document;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                       64 * 1024, FileOptions.SequentialScan))
                document = await JsonSerializer.DeserializeAsync<ApiDirectoryDocument>(stream).ConfigureAwait(false);
            if (document is null || document.Schema != ApiDirectorySchemaVersion ||
                !string.Equals(document.SnapshotId, snapshot.Id, StringComparison.Ordinal) ||
                !string.Equals(document.Formatter, ApiFormatterVersion, StringComparison.Ordinal) ||
                document.Hits is null || document.Hits.Length > MaxApiDirectoryHits)
                return (null, false);
            var assemblyIds = snapshot.Assemblies.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var hit in document.Hits)
            {
                if (hit is null || string.IsNullOrEmpty(hit.symbolId) || string.IsNullOrEmpty(hit.assemblyId) ||
                    !assemblyIds.Contains(hit.assemblyId) || string.IsNullOrEmpty(hit.kind) ||
                    string.IsNullOrEmpty(hit.qualifiedName) || string.IsNullOrEmpty(hit.signature) ||
                    string.IsNullOrEmpty(hit.accessibility) || hit.attributes is not null ||
                    hit.warnings is null || hit.warnings.Length != 0 || !IsValidXmlDocumentation(hit.xmlDocumentation))
                    return (null, false);
            }
            lock (_directoryGate)
            {
                _directorySnapshotId = snapshot.Id;
                _directoryHits = document.Hits;
                _directoryComplete = document.Complete;
                _directoryEstimatedBytes = EstimateApiDirectoryBytes(document.Hits);
            }
            ToolTiming.Gauge("api.index.memoryBytes", ApiMemoryGaugeBytes);
            ToolTiming.Count("api.index.directoryHit");
            return (document.Hits, document.Complete);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            ToolTiming.Count("api.index.directoryMiss");
            TryDelete(path);
            return (null, false);
        }
    }

    private void WriteApiDirectory(CatalogSnapshot snapshot, IReadOnlyList<ApiHit> hits, bool complete)
    {
        if (hits.Count == 0 || hits.Count > MaxApiDirectoryHits)
        {
            ToolTiming.Count("api.index.directorySkip");
            return;
        }
        string? temporaryPath = null;
        try
        {
            var document = new ApiDirectoryDocument(ApiDirectorySchemaVersion, snapshot.Id, ApiFormatterVersion,
                complete, hits.ToArray());
            var path = ApiDirectoryFilePath(snapshot.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporaryPath = Path.Combine(GetCacheRoot(), "symbols-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.SequentialScan))
                JsonSerializer.Serialize(stream, document);
            var length = new FileInfo(temporaryPath).Length;
            if (length > MaxApiDirectoryFileBytes)
            {
                ToolTiming.Count("api.index.directorySkip");
                return;
            }
            using var cacheLock = AcquireApiCacheMutex(GetCacheRoot());
            if (!EvictApiIndexFiles(GetCacheRoot(), length))
            {
                ToolTiming.Count("api.index.directorySkip");
                return;
            }
            File.Move(temporaryPath, path, overwrite: true);
            lock (_directoryGate)
            {
                _directorySnapshotId = snapshot.Id;
                _directoryHits = document.Hits;
                _directoryComplete = complete;
                _directoryEstimatedBytes = EstimateApiDirectoryBytes(document.Hits);
            }
            ToolTiming.Gauge("api.index.memoryBytes", ApiMemoryGaugeBytes);
            ToolTiming.Count("api.index.directoryWrite");
            GaugeApiIndexDiskBytes(GetCacheRoot());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ToolTiming.Count("api.index.directorySkip");
        }
        finally
        {
            if (temporaryPath is not null)
                TryDelete(temporaryPath);
        }
    }

    private static long EstimateApiDirectoryBytes(IReadOnlyList<ApiHit> hits)
    {
        long bytes = 1024L + (long)hits.Count * 512L;
        foreach (var hit in hits)
            bytes += EstimateXmlDocumentationBytes(hit.xmlDocumentation);
        return bytes;
    }

    private sealed record ApiDirectoryDocument(int Schema, string SnapshotId, string Formatter, bool Complete, ApiHit[] Hits);

    private static string ApiTypeMapFilePath(string snapshotId) => Path.Combine(GetCacheRoot(),
        "api-index-v" + ApiIndexSchemaVersion, "type-map-" + snapshotId + ".json");

    private async Task<Dictionary<string, string[]>?> ReadApiTypeMapAsync(CatalogSnapshot snapshot)
    {
        using var timing = ToolTiming.Measure("apiIndexPersistentLoadMs");
        var path = ApiTypeMapFilePath(snapshot.Id);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 1 or > MaxTypeMapFileBytes)
            {
                ToolTiming.Count("api.index.typeMapMiss");
                return null;
            }
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<ApiTypeMapDocument>(bytes);
            if (document is null || document.Schema != TypeMapSchemaVersion ||
                !document.Complete || !string.Equals(document.SnapshotId, snapshot.Id, StringComparison.Ordinal) ||
                document.Entries is null || document.Entries.Length > MaxTypeMapEntries)
                return null;
            var assemblyIds = snapshot.Assemblies.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.Entries)
            {
                if (entry is null || string.IsNullOrEmpty(entry.Name) || entry.Name.Length > 512 ||
                    entry.AssemblyIds is null || entry.AssemblyIds.Length == 0 ||
                    entry.AssemblyIds.Length > snapshot.Assemblies.Count ||
                    entry.AssemblyIds.Any(id => string.IsNullOrEmpty(id) || !assemblyIds.Contains(id)))
                    return null;
                var ids = entry.AssemblyIds.Distinct(StringComparer.Ordinal).ToArray();
                if (ids.Length == 0 || !map.TryAdd(entry.Name, ids))
                    return null;
            }
            ToolTiming.Count("api.index.typeMapHit");
            return map;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            ToolTiming.Count("api.index.typeMapMiss");
            TryDelete(path);
            return null;
        }
    }

    private void WriteApiTypeMap(CatalogSnapshot snapshot, IReadOnlyDictionary<string, HashSet<string>> map)
    {
        if (map.Count == 0 || map.Count > MaxTypeMapEntries)
        {
            ToolTiming.Count("api.index.typeMapSkip");
            return;
        }
        string? temporaryPath = null;
        try
        {
            var entries = map.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new ApiTypeMapEntry(pair.Key,
                    pair.Value.OrderBy(id => id, StringComparer.Ordinal).ToArray())).ToArray();
            var document = new ApiTypeMapDocument(TypeMapSchemaVersion, snapshot.Id, true, entries);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            if (bytes.Length > MaxTypeMapFileBytes)
            {
                ToolTiming.Count("api.index.typeMapSkip");
                return;
            }
            var path = ApiTypeMapFilePath(snapshot.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var cacheLock = AcquireApiCacheMutex(GetCacheRoot());
            if (!EvictApiIndexFiles(GetCacheRoot(), bytes.Length))
            {
                ToolTiming.Count("api.index.typeMapSkip");
                return;
            }
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, overwrite: true);
            ToolTiming.Count("api.index.typeMapWrite");
            GaugeApiIndexDiskBytes(GetCacheRoot());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ToolTiming.Count("api.index.typeMapSkip");
        }
        finally
        {
            if (temporaryPath is not null)
                TryDelete(temporaryPath);
        }
    }

    private sealed record ApiTypeMapEntry(string Name, string[] AssemblyIds);
    private sealed record ApiTypeMapDocument(int Schema, string SnapshotId, bool Complete, ApiTypeMapEntry[] Entries);

    private static string GetCacheRoot()
    {
        var configured = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThunderRoadMcp", "cache")
            : configured);
    }
    private static IDisposable AcquireApiCacheMutex(string cacheRoot)
    {
        var name = "ThunderRoadMcp.ApiIndexCache-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheRoot)))[..32];
        var mutex = new Mutex(false, name);
        try
        {
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
            return new MutexReleaser(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class MutexReleaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private static long EstimateIndexBytes(IReadOnlyList<ApiIndexedSymbol> symbols, IReadOnlyList<ApiTypeRecord> types)
    {
        long bytes = ApiIndexBaseEstimatedBytes +
            (long)symbols.Count * (IntPtr.Size + ApiIndexSymbolMapOverheadBytes) +
            (long)types.Count * (IntPtr.Size + ApiIndexTypeMapOverheadBytes +
                IntPtr.Size + ApiIndexTypeNameMapOverheadBytes);
        foreach (var symbol in symbols)
            bytes += EstimateIndexedSymbolBytes(symbol);
        foreach (var type in types)
            bytes += EstimateApiTypeRecordBytes(type);
        return bytes;
    }

    private static long EstimateIndexedSymbolBytes(ApiIndexedSymbol symbol)
    {
        var hit = symbol.Hit;
        long stringChars = (long)hit.assemblyId.Length + hit.assemblyName.Length + hit.origin.Length +
            hit.relativePath.Length + hit.symbolId.Length + hit.kind.Length + hit.qualifiedName.Length +
            hit.signature.Length + hit.accessibility.Length + (hit.declaringType?.Length ?? 0) +
            (symbol.ReturnType?.Length ?? 0);
        long bytes = 512 + stringChars * 2 +
            4L * ApiIndexEstimatedArrayOverheadBytes +
            (long)(symbol.RelatedTypeNames.Length + symbol.ParameterSignatures.Length +
                symbol.GenericConstraints.Length + hit.warnings.Length) * IntPtr.Size;
        foreach (var value in symbol.RelatedTypeNames)
            bytes += 32L + (long)value.Length * 2;
        foreach (var value in symbol.ParameterSignatures)
            bytes += 32L + (long)value.Length * 2;
        foreach (var value in symbol.GenericConstraints)
            bytes += 32L + (long)value.Length * 2;
        foreach (var value in hit.warnings)
            bytes += 32L + (long)value.Length * 2;

        var attributes = hit.attributes ?? Array.Empty<ApiAttribute>();
        bytes += ApiIndexEstimatedArrayOverheadBytes +
            (long)attributes.Length * IntPtr.Size;
        foreach (var attribute in attributes)
        {
            bytes += 96 + (long)(attribute.Name.Length + (attribute.Target?.Length ?? 0)) * 2;
            bytes += ApiIndexEstimatedArrayOverheadBytes +
                (long)attribute.FixedArguments.Length * IntPtr.Size;
            foreach (var argument in attribute.FixedArguments)
                bytes += EstimateApiAttributeArgumentBytes(argument);
            bytes += ApiIndexEstimatedArrayOverheadBytes +
                (long)attribute.NamedArguments.Length * IntPtr.Size;
            foreach (var argument in attribute.NamedArguments)
                bytes += EstimateApiAttributeArgumentBytes(argument);
        }
        bytes += EstimateXmlDocumentationBytes(hit.xmlDocumentation);
        return bytes;
    }
    private static long EstimateXmlDocumentationBytes(ApiXmlDocumentation? documentation)
    {
        if (documentation is null)
            return 0;
        long bytes = 96;
        static long StringBytes(string? value) => value is null ? 0 : 32L + (long)value.Length * 2;
        bytes += StringBytes(documentation.Summary) + StringBytes(documentation.Remarks) +
            StringBytes(documentation.Returns) + StringBytes(documentation.Value);
        if (documentation.Parameters is not null)
        {
            bytes += ApiIndexEstimatedArrayOverheadBytes +
                (long)documentation.Parameters.Length * IntPtr.Size;
            foreach (var parameter in documentation.Parameters)
                bytes += 48 + StringBytes(parameter.Name) + StringBytes(parameter.Description);
        }
        if (documentation.Examples is not null)
        {
            bytes += ApiIndexEstimatedArrayOverheadBytes +
                (long)documentation.Examples.Length * IntPtr.Size;
            foreach (var example in documentation.Examples)
                bytes += StringBytes(example);
        }
        return bytes;
    }


    private static long EstimateApiAttributeArgumentBytes(ApiAttributeArgument argument)
    {
        long bytes = 64 + (long)(argument.Name?.Length ?? 0) * 2;
        var valueBytes = argument.Value switch
        {
            null => 0,
            string text => 32 + (long)text.Length * 2,
            JsonElement element when element.ValueKind == JsonValueKind.String =>
                32 + (long)(element.GetString()?.Length ?? 0) * 2,
            JsonElement element when element.ValueKind == JsonValueKind.Number =>
                64 + (long)element.GetRawText().Length * 2,
            _ => 64
        };
        return bytes + valueBytes;
    }

    private static long EstimateApiTypeRecordBytes(ApiTypeRecord type)
    {
        long bytes = 192 + 4L * ApiIndexEstimatedArrayOverheadBytes + (long)type.FullName.Length * 2 +
            (long)(type.Members.Length + type.DirectBaseTypeNames.Length +
                type.GenericConstraints.Length + type.EnumValues.Length) * IntPtr.Size;
        foreach (var value in type.DirectBaseTypeNames)
            bytes += 32L + (long)value.Length * 2;
        foreach (var value in type.GenericConstraints)
            bytes += 32L + (long)value.Length * 2;
        foreach (var value in type.EnumValues)
            bytes += 32L + (long)value.Length * 2;
        return bytes;
    }


    private static bool EvictApiIndexFiles(string cacheRoot, long incomingBytes)
    {
        if (incomingBytes < 0 || incomingBytes > MaxApiIndexDiskBytes)
            return false;
        try
        {
            if (!Directory.Exists(cacheRoot))
                return true;
            var files = Directory.EnumerateDirectories(cacheRoot, "api-index-v*", SearchOption.TopDirectoryOnly)
                .SelectMany(directory => Directory.EnumerateFiles(directory))
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.LastWriteTimeUtc).ToArray();
            long total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (total + incomingBytes <= MaxApiIndexDiskBytes)
                    break;
                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return total + incomingBytes <= MaxApiIndexDiskBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void GaugeApiIndexDiskBytes(string cacheRoot)
    {
        if (!ToolTiming.Enabled)
            return;
        try
        {
            var total = Directory.EnumerateDirectories(cacheRoot, "api-index-v*", SearchOption.TopDirectoryOnly)
                .SelectMany(directory => Directory.EnumerateFiles(directory))
                .Sum(path => new FileInfo(path).Length);
            ToolTiming.Gauge("api.index.diskBytes", total);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Touch(LinkedList<string> lru, LinkedListNode<string> node)
    {
        lru.Remove(node);
        lru.AddFirst(node);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ApiAssemblyIndex(string assemblyId, ApiIndexedSymbol[] symbols, ApiTypeRecord[] types,
        long estimatedBytes, bool complete, int omittedDeclarations, int omittedPublicDeclarations, bool limitReached)
    {
        public string AssemblyId { get; } = assemblyId;
        public ApiIndexedSymbol[] Symbols { get; } = symbols;
        public ApiTypeRecord[] Types { get; } = types;
        public long EstimatedBytes { get; } = estimatedBytes;
        public bool Complete { get; } = complete;
        public int OmittedDeclarations { get; } = omittedDeclarations;
        public int OmittedPublicDeclarations { get; } = omittedPublicDeclarations;
        public bool LimitReached { get; } = limitReached;
        public Dictionary<string, ApiIndexedSymbol> SymbolsById { get; } = symbols.ToDictionary(symbol => symbol.Hit.symbolId, StringComparer.Ordinal);
        public Dictionary<string, ApiTypeRecord> TypesById { get; } = types.ToDictionary(type => type.Symbol.Hit.symbolId, StringComparer.Ordinal);
        public Dictionary<string, ApiTypeRecord[]> TypesByName { get; } = types.GroupBy(type => type.Symbol.Hit.qualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ApiIndexedSymbol(ApiHit Hit, int MetadataToken, string[] RelatedTypeNames, string[] ParameterSignatures,
        string? ReturnType, string[] GenericConstraints);
    private sealed record ApiTypeRecord(ApiIndexedSymbol Symbol, string FullName, string[] DirectBaseTypeNames,
        string[] GenericConstraints, ApiIndexedSymbol[] Members, string[] EnumValues);
    private sealed record ApiTypeDocument(string SymbolId, string FullName, string[] DirectBaseTypeNames,
        string[] GenericConstraints, string[] MemberSymbolIds, string[] EnumValues);
    private sealed record ApiIndexDocument(int Schema, string Identity, string AssemblyId, bool Complete,
        int OmittedDeclarations, int OmittedPublicDeclarations, bool LimitReached,
        ApiIndexedSymbol[] Symbols, ApiTypeDocument[] Types);
    private sealed class ApiIndexLease(ApiAssemblyIndex index, Action release) : IDisposable
    {
        private Action? _release = release;
        public ApiAssemblyIndex Index { get; } = index;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class ApiIndexLifetime(ApiAssemblyIndex index, int pendingWaiters)
    {
        public ApiAssemblyIndex Index { get; } = index;
        public int PendingWaiters { get; set; } = pendingWaiters;
        public int ActiveReaders { get; set; }
        public bool IsCached { get; set; }
    }
    private sealed record ApiIndexCacheEntry(ApiAssemblyIndex Index, LinkedListNode<string> Node);
    private sealed class SourceCacheEntry(SourceDocument document, long estimatedBytes, LinkedListNode<string> node, int pendingWaiters)
    {
        public SourceDocument Document { get; } = document;
        public long EstimatedBytes { get; } = estimatedBytes;
        public LinkedListNode<string> Node { get; } = node;
        public int PendingWaiters { get; set; } = pendingWaiters;
        public int ActiveReaders { get; set; }
        public bool IsCached { get; set; } = true;
    }
    private sealed class SourceDocumentLease(SourceDocument document, Action release) : IDisposable
    {
        private Action? _release = release;
        public SourceDocument Document { get; } = document;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    private sealed record SourceDocument(string Source, int[] LineStarts, int TotalLines, string[] Warnings);

    private sealed class ApiIndexedSymbolComparer : IComparer<ApiIndexedSymbol>
    {
        public static ApiIndexedSymbolComparer Instance { get; } = new();
        public int Compare(ApiIndexedSymbol? left, ApiIndexedSymbol? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var result = StringComparer.OrdinalIgnoreCase.Compare(left.Hit.qualifiedName, right.Hit.qualifiedName);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Hit.symbolId, right.Hit.symbolId);
        }
    }
    private sealed class MainAssemblyResolver : IAssemblyResolver, IDisposable
    {
        private readonly LoadedPe _main;
        private readonly string _assemblyName;

        public MainAssemblyResolver(LoadedPe main)
        {
            _main = main;
            var metadata = main.File.Metadata;
            _assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        }

        public MetadataFile? Resolve(IAssemblyReference reference) =>
            reference.Name.Equals(_assemblyName, StringComparison.OrdinalIgnoreCase) ? _main.File : null;

        public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) => null;
        public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));
        public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult<MetadataFile?>(null);
        public IDisposable? BeginSnapshot() => null;
        public void Dispose() { }
    }
}
