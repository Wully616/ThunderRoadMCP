using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

public sealed partial class BaseCatalogDataCatalog
{
    private const int MaxArchiveBytes = 512 * 1024 * 1024;
    private const int MaxArchiveEntries = 20000;
    private const int MaxMemberBytes = 4 * 1024 * 1024;
    private const long MaxExpandedBytes = 256L * 1024 * 1024;
    private const int MaxMemberPathChars = 4096;
    private const int MaxPageSize = 100;
    private const int MaxLineCount = 500;
    private const int MaxDiagnostics = 100;
    private const int MaxDiagnosticChars = 500;
    private const int MaxTaskCatalogFieldChars = 512;
    private const int MaxTaskQueryTerms = 16;
    private const int MaxResponseChars = 32 * 1024 * 1024;
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64 };
    private static readonly HashSet<string> TaskStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "build", "can", "catalog", "create", "data", "do", "does", "file", "files",
        "for", "from", "game", "how", "i", "in", "into", "is", "json", "make", "mod", "need", "of",
        "on", "please", "record", "the", "to", "use", "using", "want", "what", "with",
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<string> SearchAsync(string? query, string? catalogType, string? id, int offset, int limit,
        string? snapshotId, CancellationToken cancellationToken)
    {
        using var timing = ToolTiming.Measure("catalogSearchMs");
        ValidatePage(offset, limit);
        if (query?.Length > 128)
            throw new ArgumentException("query may contain at most 128 characters.", nameof(query));
        if (catalogType?.Length > 256)
            throw new ArgumentException("catalogType may contain at most 256 characters.", nameof(catalogType));
        if (id?.Length > 512)
            throw new ArgumentException("id may contain at most 512 characters.", nameof(id));
        query = query?.Length == 0 ? null : query;
        using var operation = await EnterCatalogOperationAsync(cancellationToken);

        await using var opened = await OpenArchiveAsync(cancellationToken);
        CheckSnapshot(opened.SnapshotId, snapshotId);
        var index = await GetIndexAsync(opened, cancellationToken);
        if (index is null)
            return await SearchUncachedAsync(opened, query, catalogType, id, offset, limit, cancellationToken);

        var diagnostics = index.Diagnostics.ToList();
        var results = new List<CatalogRecord>();
        foreach (var member in index.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member.State != CatalogRecordState.Valid || member.Metadata is null)
                continue;
            if (id is not null && !string.Equals(id, member.Metadata.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            if (catalogType is not null && !MatchesType(catalogType, member.Metadata.FullType, member.Metadata.Type))
                continue;
            if (query is not null && !await MatchesQueryAsync(query, opened, member, cancellationToken, diagnostics))
                continue;

            results.Add(new CatalogRecord(
                CreateEntryId(opened.SnapshotId, member.EntryIndex, member.Path),
                member.Metadata.Id,
                member.Metadata.Name,
                member.Metadata.Type,
                member.Metadata.FullType,
                member.Path,
                member.Size));
        }

        ToolTiming.Count("catalogRecordsConsidered", index.Records.Length);
        ToolTiming.Count("catalogResultsReturned", Math.Min(limit, Math.Max(0, results.Count - offset)));
        var page = results.Skip(offset).Take(limit).ToArray();
        if (offset > results.Count)
            throw new ArgumentOutOfRangeException(nameof(offset), $"offset {offset} exceeds the result count {results.Count}.");
        var hasMore = offset + page.Length < results.Count;
        return SerializeResponse(new
        {
            results = page.Select(record => new
            {
                entryId = record.EntryId,
                id = record.Id,
                catalogType = record.Type,
                fullType = record.FullType,
                apiLookup = ApiLookup(record.FullType),
                name = record.Name,
                memberPath = record.MemberPath,
                size = record.Size,
            }),
            total = results.Count,
            offset,
            limit,
            hasMore,
            nextOffset = hasMore ? offset + page.Length : (int?)null,
            snapshotId = opened.SnapshotId,
            catalogTruncated = index.CatalogTruncated,
            diagnostics,
        });
    }

    public async Task<string> ReadAsync(string entryId, string snapshotId, int startLine, int lineCount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entryId) || entryId.Length > 80)
            throw new ArgumentException("entryId must be a current catalog-issued ID.", nameof(entryId));
        if (string.IsNullOrWhiteSpace(snapshotId) || snapshotId.Length > 80)
            throw new ArgumentException("snapshotId must be the ID returned by catalog_search.", nameof(snapshotId));
        if (startLine < 1)
            throw new ArgumentOutOfRangeException(nameof(startLine), "startLine must be at least 1.");
        if (lineCount is < 1 or > MaxLineCount)
            throw new ArgumentOutOfRangeException(nameof(lineCount), $"lineCount must be between 1 and {MaxLineCount}.");
        using var timing = ToolTiming.Measure("catalogReadMs");
        using var operation = await EnterCatalogOperationAsync(cancellationToken);

        await using var opened = await OpenArchiveAsync(cancellationToken);
        CheckSnapshot(opened.SnapshotId, snapshotId);
        var index = await GetIndexAsync(opened, cancellationToken);
        CatalogIndexedRecord? indexed = null;
        ZipMember? uncached = null;
        if (index is null)
        {
            uncached = GetJsonMembers(opened.GetArchive()).FirstOrDefault(candidate =>
                CreateEntryId(opened.SnapshotId, candidate.Index, candidate.Entry.FullName).Equals(entryId, StringComparison.Ordinal));
        }
        else
        {
            indexed = index.Records.FirstOrDefault(candidate =>
                CreateEntryId(opened.SnapshotId, candidate.EntryIndex, candidate.Path).Equals(entryId, StringComparison.Ordinal));
        }
        if (indexed is null && uncached is null)
            throw new InvalidOperationException("entryId is invalid or stale; rerun catalog_search and use a current entryId.");

        var entryIndex = indexed?.EntryIndex ?? uncached!.Index;
        var path = indexed?.Path ?? uncached!.Entry.FullName;
        var size = indexed?.Size ?? uncached!.Entry.Length;
        if (size < 0 || size > MaxMemberBytes)
            throw new InvalidOperationException($"Catalog member exceeds the {MaxMemberBytes} byte read limit.");
        var bytes = await ReadMemberBytesAsync(opened.GetArchive().Entries[entryIndex], cancellationToken);
        CatalogMetadata metadata;
        if (indexed is { State: CatalogRecordState.Valid, Metadata: not null })
        {
            metadata = indexed.Metadata;
        }
        else
        {
            ToolTiming.Count("catalogJsonParseCount", 1);
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            metadata = ReadMetadata(document.RootElement);
        }
        var text = StrictUtf8.GetString(bytes);
        var lineStarts = GetLineStarts(text, cancellationToken);
        if (startLine > lineStarts.Length)
            throw new ArgumentOutOfRangeException(nameof(startLine), $"startLine {startLine} exceeds the source line count {lineStarts.Length}.");

        var firstLine = startLine - 1;
        var pageLineCount = Math.Min(lineCount, lineStarts.Length - firstLine);
        var endLine = firstLine + pageLineCount;
        var start = lineStarts[firstLine];
        var end = endLine < lineStarts.Length ? lineStarts[endLine] : text.Length;
        var hasMore = endLine < lineStarts.Length;
        return SerializeResponse(new
        {
            entryId,
            id = metadata.Id,
            catalogType = metadata.Type,
            fullType = metadata.FullType,
            apiLookup = ApiLookup(metadata.FullType),
            name = metadata.Name,
            memberPath = path,
            size,
            text = text[start..end],
            startLine,
            totalLines = lineStarts.Length,
            lineCount = pageLineCount,
            hasMore,
            nextLine = hasMore ? startLine + pageLineCount : (int?)null,
            snapshotId = opened.SnapshotId,
        });
    }

    internal async Task<(string SnapshotId, IReadOnlyList<CatalogTaskRecord> Records, bool IsComplete, IReadOnlyList<string> OmittedTerms)> SearchTaskCatalogAsync(
        string query, int maxRecords, IReadOnlyCollection<string>? preferredApiTypes, CancellationToken cancellationToken)
    {
        using var timing = ToolTiming.Measure("catalogSearchMs");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1024)
            throw new ArgumentException("Task catalog query must contain 1 to 1024 characters.", nameof(query));
        if (maxRecords is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(maxRecords), "maxRecords must be between 1 and 20.");
        var (terms, omittedTerms) = GetTaskTerms(query);
        using var operation = await EnterCatalogOperationAsync(cancellationToken);
        await using var opened = await OpenArchiveAsync(cancellationToken);
        var index = await GetIndexAsync(opened, cancellationToken);
        var candidates = new List<(CatalogIndexedRecord Record, string[] Terms)>();
        var complete = (index?.IsComplete ?? true) && omittedTerms.Length == 0;
        long recordsConsidered = index?.Records.Length ?? 0;
        if (index is not null)
        {
            foreach (var record in index.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.State != CatalogRecordState.Valid || record.Metadata is null)
                    continue;
                var matches = await GetMatchedTermsAsync(terms, opened, record, cancellationToken);
                if (matches is null)
                {
                    complete = false;
                    continue;
                }
                if (matches.Length > 0)
                    candidates.Add((record, matches));
            }
        }
        else
        {
            var expandedBytes = 0L;
            recordsConsidered = 0;
            foreach (var member in GetJsonMembers(opened.GetArchive()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                recordsConsidered++;
                if (member.Entry.FullName.Length > MaxMemberPathChars || member.Entry.Length < 0 ||
                    member.Entry.Length > MaxMemberBytes)
                {
                    complete = false;
                    continue;
                }
                if (member.Entry.Length > MaxExpandedBytes - expandedBytes)
                {
                    complete = false;
                    break;
                }
                expandedBytes += member.Entry.Length;
                try
                {
                    var bytes = await ReadMemberBytesAsync(member.Entry, cancellationToken);
                    ToolTiming.Count("catalogJsonParseCount", 1);
                    using var document = JsonDocument.Parse(bytes, DocumentOptions);
                    var metadata = ReadMetadata(document.RootElement);
                    var record = new CatalogIndexedRecord(member.Index, member.Entry.FullName, member.Entry.Length,
                        CatalogRecordState.Valid, metadata, null);
                    var matches = GetMatchedTerms(terms, record, document.RootElement, cancellationToken);
                    if (matches.Length > 0)
                        candidates.Add((record, matches));
                }
                catch (JsonException)
                {
                    complete = false;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    complete = false;
                }
            }
        }

        var preferred = preferredApiTypes is null
            ? null
            : new HashSet<string>(preferredApiTypes, StringComparer.OrdinalIgnoreCase);
        var ordered = candidates
            .OrderByDescending(candidate => preferred is not null &&
                NormalizeDeclaredApiType(candidate.Record.Metadata?.FullType) is { } declared &&
                preferred.Contains(declared))
            .ThenByDescending(candidate => candidate.Terms.Length)
            .ThenBy(candidate => candidate.Record.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Record.Path, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Record.EntryIndex)
            .ToArray();
        var records = new List<CatalogTaskRecord>(Math.Min(maxRecords, ordered.Length));
        foreach (var candidate in ordered.Take(maxRecords))
        {
            var metadata = candidate.Record.Metadata!;
            records.Add(new CatalogTaskRecord(
                CreateEntryId(opened.SnapshotId, candidate.Record.EntryIndex, candidate.Record.Path),
                BoundTaskField(metadata.Id, ref complete),
                BoundTaskField(metadata.Type, ref complete),
                candidate.Record.Path,
                BoundTaskField(metadata.Name, ref complete),
                NormalizeDeclaredApiType(metadata.FullType),
                candidate.Terms));
        }
        ToolTiming.Count("catalogRecordsConsidered", recordsConsidered);
        ToolTiming.Count("catalogResultsReturned", records.Count);
        return (opened.SnapshotId, records, complete && ordered.Length <= maxRecords, omittedTerms);
    }

    private async Task<string> SearchUncachedAsync(OpenedArchive opened, string? query, string? catalogType, string? id,
        int offset, int limit, CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        var results = new List<CatalogRecord>();
        long expandedBytes = 0;
        var catalogTruncated = false;
        long recordsConsidered = 0;
        foreach (var member in GetJsonMembers(opened.GetArchive()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordsConsidered++;
            var path = member.Entry.FullName;
            if (path.Length > MaxMemberPathChars)
            {
                catalogTruncated = true;
                AddDiagnostic(diagnostics, $"Skipped a JSON member with a path longer than {MaxMemberPathChars} characters.");
                continue;
            }
            if (member.Entry.Length < 0 || member.Entry.Length > MaxMemberBytes)
            {
                catalogTruncated = true;
                AddDiagnostic(diagnostics, $"Skipped {path}: member exceeds the {MaxMemberBytes} byte limit.");
                continue;
            }
            if (member.Entry.Length > MaxExpandedBytes - expandedBytes)
            {
                catalogTruncated = true;
                AddDiagnostic(diagnostics, $"Stopped scanning after reaching the {MaxExpandedBytes} byte expanded-data limit.");
                break;
            }
            expandedBytes += member.Entry.Length;
            try
            {
                var bytes = await ReadMemberBytesAsync(member.Entry, cancellationToken);
                ToolTiming.Count("catalogJsonParseCount", 1);
                using var document = JsonDocument.Parse(bytes, DocumentOptions);
                var metadata = ReadMetadata(document.RootElement);
                if (id is not null && !string.Equals(id, metadata.Id, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (catalogType is not null && !MatchesType(catalogType, metadata.FullType, metadata.Type))
                    continue;
                if (query is not null && !MatchesQuery(query, path, metadata, document.RootElement, cancellationToken))
                    continue;
                results.Add(new CatalogRecord(
                    CreateEntryId(opened.SnapshotId, member.Index, path), metadata.Id, metadata.Name, metadata.Type,
                    metadata.FullType, path, member.Entry.Length));
            }
            catch (JsonException exception)
            {
                AddDiagnostic(diagnostics, $"Ignored malformed JSON member {path}: {exception.Message}");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                AddDiagnostic(diagnostics, $"Could not read JSON member {path}: {exception.Message}");
            }
        }
        ToolTiming.Count("catalogRecordsConsidered", recordsConsidered);
        ToolTiming.Count("catalogResultsReturned", Math.Min(limit, Math.Max(0, results.Count - offset)));
        if (offset > results.Count)
            throw new ArgumentOutOfRangeException(nameof(offset), $"offset {offset} exceeds the result count {results.Count}.");
        var page = results.Skip(offset).Take(limit).ToArray();
        var hasMore = offset + page.Length < results.Count;
        return SerializeResponse(new
        {
            results = page.Select(record => new
            {
                entryId = record.EntryId,
                id = record.Id,
                catalogType = record.Type,
                fullType = record.FullType,
                apiLookup = ApiLookup(record.FullType),
                name = record.Name,
                memberPath = record.MemberPath,
                size = record.Size,
            }),
            total = results.Count,
            offset,
            limit,
            hasMore,
            nextOffset = hasMore ? offset + page.Length : (int?)null,
            snapshotId = opened.SnapshotId,
            catalogTruncated,
            diagnostics,
        });
    }

    private async Task<bool> MatchesQueryAsync(string query, OpenedArchive opened, CatalogIndexedRecord record,
        CancellationToken cancellationToken, List<string> diagnostics)
    {
        var metadata = record.Metadata!;
        if (MatchesQueryPrefix(query, record.Path, metadata))
            return true;
        if (record.SearchableStrings is not null)
            return record.SearchableStrings.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));

        try
        {
            var bytes = await ReadMemberBytesAsync(opened.GetArchive().Entries[record.EntryIndex], cancellationToken);
            ToolTiming.Count("catalogJsonParseCount", 1);
            ToolTiming.Count("catalogSearchFallbackParses", 1);
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            return ContainsStringValue(document.RootElement, query, cancellationToken);
        }
        catch (JsonException exception)
        {
            AddDiagnostic(diagnostics, $"Ignored malformed JSON member {record.Path}: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AddDiagnostic(diagnostics, $"Could not read JSON member {record.Path}: {exception.Message}");
        }
        return false;
    }

    private async Task<string[]?> GetMatchedTermsAsync(string[] terms, OpenedArchive opened, CatalogIndexedRecord record,
        CancellationToken cancellationToken)
    {
        if (terms.Length == 0)
            return Array.Empty<string>();
        var metadata = record.Metadata!;
        var matched = new bool[terms.Length];
        for (var index = 0; index < terms.Length; index++)
            matched[index] = MatchesQueryPrefix(terms[index], record.Path, metadata);
        if (record.SearchableStrings is not null)
        {
            MatchTerms(record.SearchableStrings, terms, matched, cancellationToken);
        }
        else if (matched.Any(value => !value))
        {
            try
            {
                var bytes = await ReadMemberBytesAsync(opened.GetArchive().Entries[record.EntryIndex], cancellationToken);
                ToolTiming.Count("catalogJsonParseCount", 1);
                ToolTiming.Count("catalogSearchFallbackParses", 1);
                using var document = JsonDocument.Parse(bytes, DocumentOptions);
                MatchTerms(document.RootElement, terms, matched, cancellationToken);
            }
            catch (JsonException) { return null; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
        return terms.Where((_, index) => matched[index]).ToArray();
    }

    private static string[] GetMatchedTerms(string[] terms, CatalogIndexedRecord record, JsonElement root,
        CancellationToken cancellationToken)
    {
        if (terms.Length == 0)
            return Array.Empty<string>();
        var matched = new bool[terms.Length];
        for (var index = 0; index < terms.Length; index++)
            matched[index] = MatchesQueryPrefix(terms[index], record.Path, record.Metadata!);
        MatchTerms(root, terms, matched, cancellationToken);
        return terms.Where((_, index) => matched[index]).ToArray();
    }

    private static void MatchTerms(IEnumerable<string> values, string[] terms, bool[] matched, CancellationToken cancellationToken)
    {
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < terms.Length; index++)
                if (!matched[index] && value.Contains(terms[index], StringComparison.OrdinalIgnoreCase))
                    matched[index] = true;
        }
    }

    private static void MatchTerms(JsonElement element, string[] terms, bool[] matched, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (value is not null)
                    for (var index = 0; index < terms.Length; index++)
                        if (!matched[index] && value.Contains(terms[index], StringComparison.OrdinalIgnoreCase))
                            matched[index] = true;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    MatchTerms(property.Value, terms, matched, cancellationToken);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    MatchTerms(item, terms, matched, cancellationToken);
                break;
        }
    }

    private static bool MatchesQueryPrefix(string query, string path, CatalogMetadata metadata) =>
        path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        metadata.Id?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
        metadata.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static (string[] Included, string[] Omitted) GetTaskTerms(string query)
    {
        var included = new List<string>(MaxTaskQueryTerms);
        var omitted = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = -1;
        for (var index = 0; index <= query.Length; index++)
        {
            var isToken = index < query.Length && (char.IsLetterOrDigit(query[index]) || query[index] == '_');
            if (isToken && start < 0) start = index;
            if (!isToken && start >= 0)
            {
                var term = query[start..index];
                if (term.Length >= 2 && !TaskStopWords.Contains(term) && seen.Add(term))
                {
                    if (included.Count < MaxTaskQueryTerms)
                        included.Add(term);
                    else
                        omitted.Add(term);
                }
                start = -1;
            }
        }
        return (included.ToArray(), omitted.ToArray());
    }

    private static async Task<OpenedArchive> OpenArchiveAsync(CancellationToken cancellationToken)
    {
        var options = await GameInstallConfiguration.ReadLaunchOptionsAsync(cancellationToken);
        var path = Path.GetFullPath(GameInstallConfiguration.CatalogArchivePath(options));
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            if (stream.Length > MaxArchiveBytes)
                throw new InvalidOperationException($"bas.jsondb exceeds the {MaxArchiveBytes} byte archive limit.");
            var archiveLength = stream.Length;
            byte[] hash;
            using (ToolTiming.Measure("catalogArchiveHashMs"))
                hash = await SHA256.HashDataAsync(stream, cancellationToken);
            ToolTiming.Count("catalogArchiveBytesHashed", archiveLength);
            stream.Position = 0;
            return new OpenedArchive(path, stream, CreateSnapshotId(path, hash));
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static ZipMember[] GetJsonMembers(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidOperationException($"bas.jsondb contains more than the {MaxArchiveEntries} archive-entry limit.");
        return archive.Entries
            .Select((entry, index) => new ZipMember(entry, index))
            .Where(member => member.Entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(member => member.Entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.Entry.FullName, StringComparer.Ordinal)
            .ThenBy(member => member.Index)
            .ToArray();
    }

    private static async Task<byte[]> ReadMemberBytesAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.Length > MaxMemberBytes)
            throw new InvalidDataException($"Member exceeds the {MaxMemberBytes} byte limit.");
        var bytes = new byte[checked((int)entry.Length)];
        await using var input = entry.Open();
        await input.ReadExactlyAsync(bytes, cancellationToken);
        if (input.ReadByte() != -1)
            throw new InvalidDataException("Member expanded beyond its declared size.");
        return bytes;
    }

    private static CatalogMetadata ReadMetadata(JsonElement root)
    {
        string? fullType = null;
        string? id = null;
        string? name = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (fullType is null && property.Name.Equals("$type", StringComparison.OrdinalIgnoreCase))
                    fullType = PrimitiveText(property.Value);
                else if (id is null && property.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                    id = PrimitiveText(property.Value);
                else if (name is null && property.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
                    name = PrimitiveText(property.Value);
            }
        }
        return new CatalogMetadata(id, name, fullType, SimpleType(fullType));
    }

    private static string? PrimitiveText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
        _ => null,
    };

    private static string? SimpleType(string? fullType)
    {
        if (fullType is null) return null;
        var type = fullType.Split(',', 2)[0].Trim();
        var separator = type.LastIndexOf('.');
        return separator >= 0 ? type[(separator + 1)..] : type;
    }

    private static string? NormalizeDeclaredApiType(string? fullType)
    {
        if (string.IsNullOrWhiteSpace(fullType)) return null;
        var comma = fullType.IndexOf(',');
        var query = (comma < 0 ? fullType : fullType[..comma]).Trim();
        if (query.Length is 0 or > 128 || query.Contains('`') || query.Contains('[') || query.Contains(']') ||
            query.Contains('<') || query.Contains('>'))
            return null;
        return query.Replace('+', '.');
    }

    private static string? BoundTaskField(string? value, ref bool complete)
    {
        if (value is null || value.Length <= MaxTaskCatalogFieldChars)
            return value;
        complete = false;
        return null;
    }

    private static object? ApiLookup(string? fullType)
    {
        var query = NormalizeDeclaredApiType(fullType);
        return query is null ? null : new { query, kind = "type" };
    }

    private static bool MatchesType(string requested, string? fullType, string? simpleType) =>
        string.Equals(requested, simpleType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(requested, fullType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(requested, fullType?.Split(',', 2)[0].Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesQuery(string query, string path, CatalogMetadata metadata, JsonElement root,
        CancellationToken cancellationToken) =>
        MatchesQueryPrefix(query, path, metadata) || ContainsStringValue(root, query, cancellationToken);

    private static bool ContainsStringValue(JsonElement element, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    if (ContainsStringValue(property.Value, query, cancellationToken)) return true;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsStringValue(item, query, cancellationToken)) return true;
                break;
        }
        return false;
    }

    private static string[]? CollectSearchableStrings(JsonElement root, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        var characters = 0;
        var complete = true;
        Collect(root);
        return complete ? values.ToArray() : null;

        void Collect(JsonElement element)
        {
            if (!complete) return;
            cancellationToken.ThrowIfCancellationRequested();
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    if (values.Count >= MaxCachedStringsPerRecord)
                    {
                        complete = false;
                        values.Clear();
                        return;
                    }
                    var value = element.GetString() ?? string.Empty;
                    characters += value.Length;
                    if (characters * sizeof(char) > MaxCachedStringBytesPerRecord)
                    {
                        complete = false;
                        values.Clear();
                        return;
                    }
                    values.Add(value);
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Collect(property.Value);
                        if (!complete) return;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item);
                        if (!complete) return;
                    }
                    break;
            }
        }
    }

    private static int[] GetLineStarts(string text, CancellationToken cancellationToken)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                if (index + 1 < text.Length) starts.Add(index + 1);
            }
            else if (text[index] == '\n' && index + 1 < text.Length)
            {
                starts.Add(index + 1);
            }
        }
        return starts.ToArray();
    }
    private static string CreateSnapshotId(string archivePath, byte[] archiveHash)
    {
        var identityPath = Path.GetFullPath(archivePath);
        if (OperatingSystem.IsWindows())
            identityPath = identityPath.ToUpperInvariant();
        var identity = string.Concat(identityPath, "\0", Convert.ToHexString(archiveHash));
        return "bas_snap_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string CreateEntryId(string snapshotId, int index, string path)
    {
        var identity = string.Concat(snapshotId, "\0", index.ToString(CultureInfo.InvariantCulture), "\0", path);
        return "bas_entry_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }

    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0 || offset > MaxArchiveEntries)
            throw new ArgumentOutOfRangeException(nameof(offset), $"offset must be between 0 and {MaxArchiveEntries}.");
        if (limit < 1 || limit > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be between 1 and {MaxPageSize}.");
    }

    private static void CheckSnapshot(string actual, string? expected)
    {
        if (expected is not null && !expected.Equals(actual, StringComparison.Ordinal))
            throw new InvalidOperationException("snapshotId is not for the current bas.jsondb archive; rerun catalog_search, then use its current entryId and snapshotId together.");
    }

    private static void AddDiagnostic(List<string> diagnostics, string message)
    {
        if (diagnostics.Count >= MaxDiagnostics) return;
        diagnostics.Add(message.Length > MaxDiagnosticChars ? message[..MaxDiagnosticChars] : message);
    }

    private static string SerializeResponse(object value)
    {
        var json = JsonSerializer.Serialize(value);
        if (json.Length > MaxResponseChars)
            throw new InvalidOperationException($"Response exceeds the {MaxResponseChars} character safety limit; request a smaller page.");
        return json;
    }

    private sealed record ZipMember(ZipArchiveEntry Entry, int Index);
    private sealed record CatalogMetadata(string? Id, string? Name, string? FullType, string? Type);
    private sealed record CatalogRecord(string EntryId, string? Id, string? Name, string? Type, string? FullType,
        string MemberPath, long Size);
}

internal sealed record CatalogTaskRecord(string EntryId, string? Id, string? RecordType, string ArchivePath,
    string? Name, string? DeclaredApiType, string[] MatchedTerms);
