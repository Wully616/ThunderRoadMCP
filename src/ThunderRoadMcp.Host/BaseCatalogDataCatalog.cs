using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

public sealed class BaseCatalogDataCatalog
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
    private const int MaxResponseChars = 32 * 1024 * 1024;
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64 };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<string> SearchAsync(string? query, string? catalogType, string? id, int offset, int limit,
        string? snapshotId, CancellationToken cancellationToken)
    {
        ValidatePage(offset, limit);
        if (query?.Length > 128)
            throw new ArgumentException("query may contain at most 128 characters.", nameof(query));
        if (catalogType?.Length > 256)
            throw new ArgumentException("catalogType may contain at most 256 characters.", nameof(catalogType));
        if (id?.Length > 512)
            throw new ArgumentException("id may contain at most 512 characters.", nameof(id));
        query = query?.Length == 0 ? null : query;

        var opened = await OpenArchiveAsync(cancellationToken);
        await using var stream = opened.Stream;
        using var archive = opened.Archive;
        CheckSnapshot(opened.SnapshotId, snapshotId);

        var diagnostics = new List<string>();
        var results = new List<CatalogRecord>();
        long expandedBytes = 0;
        var catalogTruncated = false;
        foreach (var member in GetJsonMembers(archive))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                using var document = JsonDocument.Parse(bytes, DocumentOptions);
                var metadata = ReadMetadata(document.RootElement);
                if (id is not null && !string.Equals(id, metadata.Id, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (catalogType is not null && !MatchesType(catalogType, metadata.FullType, metadata.Type))
                    continue;
                if (query is not null && !MatchesQuery(query, path, metadata, document.RootElement, cancellationToken))
                    continue;

                results.Add(new CatalogRecord(
                    CreateEntryId(opened.SnapshotId, member),
                    metadata.Id,
                    metadata.Name,
                    metadata.Type,
                    metadata.FullType,
                    path,
                    member.Entry.Length));
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

        var opened = await OpenArchiveAsync(cancellationToken);
        await using var stream = opened.Stream;
        using var archive = opened.Archive;
        CheckSnapshot(opened.SnapshotId, snapshotId);
        var member = GetJsonMembers(archive).FirstOrDefault(candidate =>
            CreateEntryId(opened.SnapshotId, candidate).Equals(entryId, StringComparison.Ordinal));
        if (member is null)
            throw new InvalidOperationException("entryId is invalid or stale; rerun catalog_search and use a current entryId.");
        if (member.Entry.Length < 0 || member.Entry.Length > MaxMemberBytes)
            throw new InvalidOperationException($"Catalog member exceeds the {MaxMemberBytes} byte read limit.");

        var bytes = await ReadMemberBytesAsync(member.Entry, cancellationToken);
        using var document = JsonDocument.Parse(bytes, DocumentOptions);
        var metadata = ReadMetadata(document.RootElement);
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
            name = metadata.Name,
            memberPath = member.Entry.FullName,
            size = member.Entry.Length,
            text = text[start..end],
            startLine,
            totalLines = lineStarts.Length,
            lineCount = pageLineCount,
            hasMore,
            nextLine = hasMore ? startLine + pageLineCount : (int?)null,
            snapshotId = opened.SnapshotId,
        });
    }

    private static async Task<(FileStream Stream, ZipArchive Archive, string SnapshotId)> OpenArchiveAsync(CancellationToken cancellationToken)
    {
        var options = await GameInstallConfiguration.ReadLaunchOptionsAsync(cancellationToken);
        var path = GameInstallConfiguration.CatalogArchivePath(options);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            if (stream.Length > MaxArchiveBytes)
                throw new InvalidOperationException($"bas.jsondb exceeds the {MaxArchiveBytes} byte archive limit.");
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            stream.Position = 0;
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var snapshotId = "bas_snap_" + Convert.ToHexString(hash);
            return (stream, archive, snapshotId);
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

    private static bool MatchesType(string requested, string? fullType, string? simpleType) =>
        string.Equals(requested, simpleType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(requested, fullType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(requested, fullType?.Split(',', 2)[0].Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesQuery(string query, string path, CatalogMetadata metadata, JsonElement root,
        CancellationToken cancellationToken) =>
        path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        metadata.Id?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
        metadata.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
        ContainsStringValue(root, query, cancellationToken);

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

    private static string CreateEntryId(string snapshotId, ZipMember member)
    {
        var identity = string.Concat(snapshotId, "\0", member.Index.ToString(CultureInfo.InvariantCulture), "\0", member.Entry.FullName);
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

