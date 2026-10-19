using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed partial class BaseCatalogDataCatalog
{
    private const int CatalogCacheSchema = 1;
    private const long MaxCatalogIndexMemoryBytes = 64L * 1024 * 1024;
    private const long MaxCachedSearchStringMemoryBytes = 64L * 1024 * 1024;
    private const long MaxCachedStringBytesPerRecord = 2L * 1024 * 1024;
    private const int MaxCachedStringsPerRecord = 100000;
    private const long MaxCatalogCacheDiskBytes = 128L * 1024 * 1024;
    private const int CatalogCacheDigestBytes = 32;
    private const int MaxCatalogCacheStringBytes = MaxMemberBytes;
    private static readonly byte[] CatalogCacheMagic = "TRMC-CATIDX"u8.ToArray();
    // ponytail: serialize operations and retain one index to bound active/build memory at 64 MiB; use per-snapshot leases if measured throughput requires parallel readers.
    private readonly SemaphoreSlim _catalogOperationGate = new(1, 1);
    private CatalogIndex? _catalogMemory;
    private string? _catalogMemoryKey;
    private long _catalogMemoryBytes;

    private async ValueTask<IDisposable> EnterCatalogOperationAsync(CancellationToken cancellationToken)
    {
        await _catalogOperationGate.WaitAsync(cancellationToken);
        return new SemaphoreLease(_catalogOperationGate);
    }

    private async Task<CatalogIndex?> GetIndexAsync(OpenedArchive opened, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = GetCatalogCacheKey(opened.ArchivePath, opened.SnapshotId);
        if (TryGetMemoryIndex(key, out var cached))
        {
            ToolTiming.Count("catalogCacheHit", 1);
            return cached;
        }

        ToolTiming.Count("catalogCacheMiss", 1);
        ClearMemoryIndexes();
        var cachePath = GetPersistentCachePath(key);
        var diskIndex = TryReadPersistentIndex(cachePath, key, opened.SnapshotId, cancellationToken);
        if (diskIndex is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddMemoryIndex(key, diskIndex);
            ToolTiming.Count("catalogCacheHit", 1);
            ToolTiming.Count("catalogPersistentHit", 1);
            if (ToolTiming.Enabled && TryGetPersistentCacheBytes(Path.GetDirectoryName(cachePath)!, out var diskBytes))
                ToolTiming.Gauge("catalog.index.diskBytes", diskBytes);
            return diskIndex;
        }

        ToolTiming.Count("catalogIndexBuildCount", 1);
        cancellationToken.ThrowIfCancellationRequested();
        CatalogIndex? index;
        using (ToolTiming.Measure("catalogIndexBuildMs"))
            index = await BuildIndexAsync(opened, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (index is null)
            return null;
        AddMemoryIndex(key, index);
        TryWritePersistentIndex(cachePath, key, opened.SnapshotId, index, cancellationToken);
        return index;
    }

    private void ClearMemoryIndexes()
    {
        _catalogMemory = null;
        _catalogMemoryKey = null;
        _catalogMemoryBytes = 0;
        ToolTiming.Gauge("catalog.index.memoryBytes", 0);
    }

    private async Task<CatalogIndex?> BuildIndexAsync(OpenedArchive opened, CancellationToken cancellationToken)
    {
        var archive = opened.GetArchive();
        var members = GetJsonMembers(archive);
        var records = new List<CatalogIndexedRecord>(members.Length);
        var diagnostics = new List<string>();
        var memoryBytes = 128L;
        var searchableBytes = 0L;
        long expandedBytes = 0;
        var catalogTruncated = false;
        var isComplete = true;
        var stopped = false;

        for (var position = 0; position < members.Length; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var member = members[position];
            var path = member.Entry.FullName;
            CatalogIndexedRecord record;
            if (stopped)
            {
                record = new CatalogIndexedRecord(member.Index, path.Length > MaxMemberPathChars ? "" : path,
                    member.Entry.Length, CatalogRecordState.Unscanned, null, null);
                isComplete = false;
            }
            else if (path.Length > MaxMemberPathChars)
            {
                catalogTruncated = true;
                isComplete = false;
                AddDiagnostic(diagnostics, $"Skipped a JSON member with a path longer than {MaxMemberPathChars} characters.");
                record = new CatalogIndexedRecord(member.Index, path, member.Entry.Length, CatalogRecordState.SkippedPath, null, null);
            }
            else if (member.Entry.Length < 0 || member.Entry.Length > MaxMemberBytes)
            {
                catalogTruncated = true;
                isComplete = false;
                AddDiagnostic(diagnostics, $"Skipped {path}: member exceeds the {MaxMemberBytes} byte limit.");
                record = new CatalogIndexedRecord(member.Index, path, member.Entry.Length, CatalogRecordState.SkippedSize, null, null);
            }
            else if (member.Entry.Length > MaxExpandedBytes - expandedBytes)
            {
                catalogTruncated = true;
                isComplete = false;
                AddDiagnostic(diagnostics, $"Stopped scanning after reaching the {MaxExpandedBytes} byte expanded-data limit.");
                stopped = true;
                record = new CatalogIndexedRecord(member.Index, path, member.Entry.Length, CatalogRecordState.Unscanned, null, null);
            }
            else
            {
                expandedBytes += member.Entry.Length;
                try
                {
                    var bytes = await ReadMemberBytesAsync(member.Entry, cancellationToken);
                    ToolTiming.Count("catalogJsonParseCount", 1);
                    using var document = JsonDocument.Parse(bytes, DocumentOptions);
                    var metadata = ReadMetadata(document.RootElement);
                    var strings = CollectSearchableStrings(document.RootElement, cancellationToken);
                    var stringBytes = strings is null ? 0 : EstimateStringListMemory(strings);
                    if (strings is not null && searchableBytes + stringBytes > MaxCachedSearchStringMemoryBytes)
                        strings = null;
                    if (strings is null)
                    {
                        ToolTiming.Count("catalogUncachedStringRecords", 1);
                    }
                    else
                    {
                        searchableBytes += stringBytes;
                    }
                    record = new CatalogIndexedRecord(member.Index, path, member.Entry.Length, CatalogRecordState.Valid,
                        metadata, strings);
                }
                catch (JsonException exception)
                {
                    isComplete = false;
                    var diagnostic = $"Ignored malformed JSON member {path}: {exception.Message}";
                    AddDiagnostic(diagnostics, diagnostic);
                    record = new CatalogIndexedRecord(member.Index, path, member.Entry.Length, CatalogRecordState.Malformed, null, null);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    isComplete = false;
                    var diagnostic = $"Could not read JSON member {path}: {exception.Message}";
                    AddDiagnostic(diagnostics, diagnostic);
                    record = new CatalogIndexedRecord(member.Index, path, member.Entry.Length, CatalogRecordState.Unreadable, null, null);
                }
            }

            var weight = EstimateRecordMemory(record);
            if (memoryBytes + weight > MaxCatalogIndexMemoryBytes && record.SearchableStrings is not null)
            {
                searchableBytes -= EstimateStringListMemory(record.SearchableStrings);
                ToolTiming.Count("catalogUncachedStringRecords", 1);
                record = record with { SearchableStrings = null };
                weight = EstimateRecordMemory(record);
            }
            if (memoryBytes + weight > MaxCatalogIndexMemoryBytes)
                return null;
            memoryBytes += weight;
            records.Add(record);
        }

        foreach (var diagnostic in diagnostics)
            memoryBytes += 32 + diagnostic.Length * sizeof(char);
        if (memoryBytes > MaxCatalogIndexMemoryBytes)
            return null;
        cancellationToken.ThrowIfCancellationRequested();
        return new CatalogIndex(archive.Entries.Count, records.ToArray(), diagnostics.ToArray(),
            catalogTruncated, isComplete, memoryBytes);
    }

    private bool TryGetMemoryIndex(string key, out CatalogIndex index)
    {
        if (_catalogMemory is not null && string.Equals(_catalogMemoryKey, key, StringComparison.Ordinal))
        {
            index = _catalogMemory;
            return true;
        }
        index = null!;
        return false;
    }

    private void AddMemoryIndex(string key, CatalogIndex index)
    {
        if (index.EstimatedMemoryBytes > MaxCatalogIndexMemoryBytes)
            return;
        _catalogMemoryKey = key;
        _catalogMemory = index;
        _catalogMemoryBytes = index.EstimatedMemoryBytes;
        ToolTiming.Gauge("catalog.index.memoryBytes", _catalogMemoryBytes);
    }

    private static bool TryGetPersistentCacheBytes(string directory, out long bytes)
    {
        bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "catalog-*", SearchOption.TopDirectoryOnly))
            {
                if (!IsPersistentCacheFile(file))
                    continue;
                var length = new FileInfo(file).Length;
                if (length > MaxCatalogCacheDiskBytes - bytes)
                {
                    bytes = MaxCatalogCacheDiskBytes + 1;
                    return true;
                }
                bytes += length;
            }
            return true;
        }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            bytes = 0;
            return false;
        }
    }

    private static bool IsPersistentCacheFile(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith("catalog-", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Length == 76 && name.EndsWith(".idx", StringComparison.OrdinalIgnoreCase))
            return IsHex(name.AsSpan(8, 64));
        return name.Length == 113 && name.AsSpan(72, 5).Equals(".idx.".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && IsHex(name.AsSpan(8, 64)) &&
               IsHex(name.AsSpan(77, 32));
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (!char.IsAsciiHexDigit(character))
                return false;
        return true;
    }

    private static CacheMutexLease? AcquireCacheMutex(string directory, CancellationToken cancellationToken)
    {
        Mutex? mutex = null;
        var ownsMutex = false;
        try
        {
            var root = Path.GetFullPath(directory);
            if (OperatingSystem.IsWindows())
                root = root.ToUpperInvariant();
            var name = "Local\\ThunderRoadMcpCatalog-" +
                       Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
            mutex = new Mutex(false, name);
            try
            {
                ownsMutex = WaitHandle.WaitAny(new WaitHandle[] { mutex, cancellationToken.WaitHandle }) == 0;
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (!ownsMutex)
            {
                mutex.Dispose();
                return null;
            }
            return new CacheMutexLease(mutex);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                                          InvalidOperationException or NotSupportedException or ApplicationException)
        {
            if (ownsMutex)
                try { mutex?.ReleaseMutex(); } catch (ApplicationException) { }
            mutex?.Dispose();
            return null;
        }
    }

    

    private static string GetCatalogCacheKey(string archivePath, string snapshotId)
    {
        var normalizedPath = Path.GetFullPath(archivePath);
        if (OperatingSystem.IsWindows())
            normalizedPath = normalizedPath.ToUpperInvariant();
        var descriptor = string.Concat(CatalogCacheSchema.ToString(CultureInfo.InvariantCulture), "\0",
            RuntimeInformation.FrameworkDescription, "\0", typeof(JsonDocument).Assembly.GetName().Version?.ToString(), "\0",
            normalizedPath, "\0", snapshotId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
    }

    private static string GetCatalogCacheRoot()
    {
        var configured = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR");
        var root = configured is not null
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThunderRoadMcp", "cache");
        return Path.Combine(root, "catalog");
    }

    private static string GetPersistentCachePath(string key) => Path.Combine(GetCatalogCacheRoot(), "catalog-" + key + ".idx");

    private static CatalogIndex? TryReadPersistentIndex(string path, string key, string snapshotId,
        CancellationToken cancellationToken)
    {
        using var mutex = AcquireCacheMutex(Path.GetDirectoryName(path)!, cancellationToken);
        if (mutex is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return TryReadPersistentIndexUnderMutex(path, key, snapshotId, cancellationToken);
    }

    private static CatalogIndex? TryReadPersistentIndexUnderMutex(string path, string key, string snapshotId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        using var timing = ToolTiming.Measure("catalogPersistentLoadMs");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is < 64 or > MaxCatalogCacheDiskBytes)
                throw new InvalidDataException("Catalog cache size is invalid.");
            var bodyLength = file.Length - CatalogCacheDigestBytes;
            using var hashing = new HashingReadStream(file, bodyLength);
            using var reader = new BinaryReader(hashing, Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(CatalogCacheMagic.Length).AsSpan().SequenceEqual(CatalogCacheMagic) ||
                reader.ReadInt32() != CatalogCacheSchema || ReadCacheString(reader, 128) != key ||
                ReadCacheString(reader, 128) != snapshotId)
                throw new InvalidDataException("Catalog cache identity does not match.");
            var cachedEntryCount = reader.ReadInt32();
            var recordCount = reader.ReadInt32();
            if (cachedEntryCount is < 0 or > MaxArchiveEntries || recordCount is < 0 or > MaxArchiveEntries)
                throw new InvalidDataException("Catalog cache inventory is invalid.");
            var catalogTruncated = reader.ReadBoolean();
            var isComplete = reader.ReadBoolean();
            var diagnosticCount = reader.ReadInt32();
            if (diagnosticCount is < 0 or > MaxDiagnostics)
                throw new InvalidDataException("Catalog cache diagnostics are invalid.");
            var diagnostics = new string[diagnosticCount];
            for (var index = 0; index < diagnosticCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                diagnostics[index] = ReadCacheString(reader, MaxDiagnosticChars * 4);
            }

            var records = new CatalogIndexedRecord[recordCount];
            var estimatedMemoryBytes = 128L;
            var searchableBytes = 0L;
            for (var index = 0; index < recordCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryIndex = reader.ReadInt32();
                var memberPath = ReadCacheString(reader, MaxCatalogCacheStringBytes);
                var size = reader.ReadInt64();
                var state = (CatalogRecordState)reader.ReadByte();
                if (entryIndex < 0 || entryIndex >= cachedEntryCount || size < -1 ||
                    !Enum.IsDefined(state))
                    throw new InvalidDataException("Catalog cache record is invalid.");
                CatalogMetadata? metadata = null;
                if (reader.ReadBoolean())
                    metadata = new CatalogMetadata(ReadNullableCacheString(reader), ReadNullableCacheString(reader),
                        ReadNullableCacheString(reader), ReadNullableCacheString(reader));
                string[]? searchableStrings = null;
                if (reader.ReadBoolean())
                {
                    var stringCount = reader.ReadInt32();
                    if (stringCount is < 0 or > MaxCachedStringsPerRecord)
                        throw new InvalidDataException("Catalog cache string inventory is invalid.");
                    searchableStrings = new string[stringCount];
                    long recordStringBytes = 0;
                    for (var stringIndex = 0; stringIndex < stringCount; stringIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var value = ReadCacheString(reader, MaxCatalogCacheStringBytes);
                        recordStringBytes += (long)value.Length * sizeof(char);
                        if (recordStringBytes > MaxCachedStringBytesPerRecord)
                            throw new InvalidDataException("Catalog cache record exceeds its searchable-string budget.");
                        searchableStrings[stringIndex] = value;
                    }
                    searchableBytes += EstimateStringListMemory(searchableStrings);
                }
                var record = new CatalogIndexedRecord(entryIndex, memberPath, size, state, metadata, searchableStrings);
                estimatedMemoryBytes += EstimateRecordMemory(record);
                if (estimatedMemoryBytes > MaxCatalogIndexMemoryBytes || searchableBytes > MaxCachedSearchStringMemoryBytes)
                    throw new InvalidDataException("Catalog cache exceeds its memory budget.");
                records[index] = record;
            }
            foreach (var diagnostic in diagnostics)
                estimatedMemoryBytes += 32 + diagnostic.Length * sizeof(char);
            if (estimatedMemoryBytes > MaxCatalogIndexMemoryBytes || file.Position != bodyLength)
                throw new InvalidDataException("Catalog cache body length or memory budget is invalid.");
            var expectedDigest = new byte[CatalogCacheDigestBytes];
            file.ReadExactly(expectedDigest);
            if (file.Position != file.Length || !CryptographicOperations.FixedTimeEquals(hashing.FinishHash(), expectedDigest))
                throw new InvalidDataException("Catalog cache digest is invalid.");
            return new CatalogIndex(cachedEntryCount, records, diagnostics, catalogTruncated, isComplete, estimatedMemoryBytes);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or DecoderFallbackException or OverflowException)
        {
            ToolTiming.Count("catalogCacheCorrupt", 1);
            try { File.Delete(path); } catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException) { }
            if (ToolTiming.Enabled &&
                TryGetPersistentCacheBytes(Path.GetDirectoryName(path)!, out var diskBytes))
                ToolTiming.Gauge("catalog.index.diskBytes", diskBytes);
            return null;
        }
    }

    private static void TryWritePersistentIndex(string path, string key, string snapshotId, CatalogIndex index,
        CancellationToken cancellationToken)
    {
        using var timing = ToolTiming.Measure("catalogPersistentWriteMs");
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            var serializedBytes = GetPersistentIndexBytes(key, snapshotId, index);
            if (serializedBytes > MaxCatalogCacheDiskBytes)
                return;
            Directory.CreateDirectory(directory);
            using var mutex = AcquireCacheMutex(directory, cancellationToken);
            if (mutex is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                if (ToolTiming.Enabled && TryGetPersistentCacheBytes(directory, out var existingBytes))
                    ToolTiming.Gauge("catalog.index.diskBytes", existingBytes);
                return;
            }

            var allowedExistingBytes = MaxCatalogCacheDiskBytes - serializedBytes;
            if (!TryGetPersistentCacheBytes(directory, out var currentBytes))
                return;
            if (currentBytes > allowedExistingBytes)
            {
                TrimPersistentCacheFiles(directory, path, allowedExistingBytes);
                if (!TryGetPersistentCacheBytes(directory, out currentBytes) || currentBytes > allowedExistingBytes)
                    return;
            }

            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       65536, FileOptions.SequentialScan))
            using (var hashing = new HashingWriteStream(file, serializedBytes - CatalogCacheDigestBytes))
            using (var writer = new BinaryWriter(hashing, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(CatalogCacheMagic);
                writer.Write(CatalogCacheSchema);
                WriteCacheString(writer, key);
                WriteCacheString(writer, snapshotId);
                writer.Write(index.ArchiveEntryCount);
                writer.Write(index.Records.Length);
                writer.Write(index.CatalogTruncated);
                writer.Write(index.IsComplete);
                writer.Write(index.Diagnostics.Length);
                foreach (var diagnostic in index.Diagnostics)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteCacheString(writer, diagnostic);
                }
                foreach (var record in index.Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(record.EntryIndex);
                    WriteCacheString(writer, record.Path);
                    writer.Write(record.Size);
                    writer.Write((byte)record.State);
                    writer.Write(record.Metadata is not null);
                    if (record.Metadata is { } metadata)
                    {
                        WriteNullableCacheString(writer, metadata.Id);
                        WriteNullableCacheString(writer, metadata.Name);
                        WriteNullableCacheString(writer, metadata.FullType);
                        WriteNullableCacheString(writer, metadata.Type);
                    }
                    writer.Write(record.SearchableStrings is not null);
                    if (record.SearchableStrings is { } values)
                    {
                        writer.Write(values.Length);
                        foreach (var value in values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            WriteCacheString(writer, value);
                        }
                    }
                }
                writer.Flush();
                file.Write(hashing.FinishHash());
                file.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(temporaryPath).Length != serializedBytes)
                throw new InvalidDataException("Catalog cache serialized size did not match its reservation.");
            File.Move(temporaryPath, path, true);
            temporaryPath = null;
            if (ToolTiming.Enabled && TryGetPersistentCacheBytes(directory, out var diskBytes))
                ToolTiming.Gauge("catalog.index.diskBytes", diskBytes);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
        {
            // Persistent cache is an optimization; the complete in-memory index remains usable.
        }
        finally
        {
            if (temporaryPath is not null)
                try { File.Delete(temporaryPath); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static long GetPersistentIndexBytes(string key, string snapshotId, CatalogIndex index)
    {
        checked
        {
            var bytes = (long)CatalogCacheMagic.Length + sizeof(int) +
                        SerializedCacheStringBytes(key) + SerializedCacheStringBytes(snapshotId) +
                        2L * sizeof(int) + 2 + sizeof(int) + CatalogCacheDigestBytes;
            foreach (var diagnostic in index.Diagnostics)
                bytes += SerializedCacheStringBytes(diagnostic);
            foreach (var record in index.Records)
            {
                bytes += sizeof(int) + SerializedCacheStringBytes(record.Path) + sizeof(long) + 1 + 1;
                if (record.Metadata is { } metadata)
                {
                    bytes += SerializedNullableCacheStringBytes(metadata.Id) +
                             SerializedNullableCacheStringBytes(metadata.Name) +
                             SerializedNullableCacheStringBytes(metadata.FullType) +
                             SerializedNullableCacheStringBytes(metadata.Type);
                }
                bytes += 1;
                if (record.SearchableStrings is { } values)
                {
                    bytes += sizeof(int);
                    foreach (var value in values)
                        bytes += SerializedCacheStringBytes(value);
                }
            }
            return bytes;
        }
    }

    private static long SerializedCacheStringBytes(string value) =>
        sizeof(int) + Encoding.UTF8.GetByteCount(value);

    private static long SerializedNullableCacheStringBytes(string? value) =>
        1 + (value is null ? 0 : SerializedCacheStringBytes(value));

    private static void TrimPersistentCacheFiles(string directory, string currentPath, long targetBytes)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, "catalog-*", SearchOption.TopDirectoryOnly)
                .Where(IsPersistentCacheFile)
                .Select(file => new FileInfo(file))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToArray();
            long totalBytes = 0;
            foreach (var file in files)
                totalBytes = file.Length > long.MaxValue - totalBytes ? long.MaxValue : totalBytes + file.Length;
            foreach (var file in files)
            {
                if (totalBytes <= targetBytes)
                    break;
                if (string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var size = file.Length;
                    file.Delete();
                    totalBytes -= size;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static long EstimateRecordMemory(CatalogIndexedRecord record)
    {
        var bytes = 96L + record.Path.Length * sizeof(char);
        if (record.Metadata is { } metadata)
            bytes += EstimateStringMemory(metadata.Id) + EstimateStringMemory(metadata.Name) +
                     EstimateStringMemory(metadata.FullType) + EstimateStringMemory(metadata.Type);
        if (record.SearchableStrings is { } values)
            bytes += EstimateStringListMemory(values);
        return bytes;
    }

    private static long EstimateStringListMemory(string[] strings)
    {
        long bytes = 24L + (long)strings.Length * IntPtr.Size;
        foreach (var value in strings)
            bytes += EstimateStringMemory(value);
        return bytes;
    }

    private static long EstimateStringMemory(string? value) => value is null ? 0 : 24L + (long)value.Length * sizeof(char);

    private static void WriteNullableCacheString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            WriteCacheString(writer, value);
    }

    private static string? ReadNullableCacheString(BinaryReader reader) =>
        reader.ReadBoolean() ? ReadCacheString(reader, MaxCatalogCacheStringBytes) : null;

    private static void WriteCacheString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxCatalogCacheStringBytes)
            throw new InvalidDataException("Catalog cache string exceeds its limit.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadCacheString(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length is < 0 || length > maximumBytes)
            throw new InvalidDataException("Catalog cache string length is invalid.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return StrictUtf8.GetString(bytes);
    }

    private sealed class HashingWriteStream(Stream inner, long maximumBytes) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytesWritten;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytesWritten;
        public override long Position { get => _bytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            inner.Write(buffer, offset, count);
            _hash.AppendData(buffer, offset, count);
            _bytesWritten += count;
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
            _hash.AppendData(buffer);
            _bytesWritten += buffer.Length;
        }
        public byte[] FinishHash() => _hash.GetHashAndReset();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
        private void EnsureCapacity(int count)
        {
            if (count < 0 || _bytesWritten > maximumBytes - count)
                throw new IOException("Catalog cache exceeds its disk budget.");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class HashingReadStream(Stream inner, long hashLength) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => hashLength;
        public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var limit = (int)Math.Min(count, hashLength - _bytesRead);
            if (limit <= 0) return 0;
            var read = inner.Read(buffer, offset, limit);
            if (read > 0)
            {
                _hash.AppendData(buffer, offset, read);
                _bytesRead += read;
            }
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var limit = (int)Math.Min(buffer.Length, hashLength - _bytesRead);
            if (limit <= 0) return 0;
            var read = inner.Read(buffer[..limit]);
            if (read > 0)
            {
                _hash.AppendData(buffer[..read]);
                _bytesRead += read;
            }
            return read;
        }
        public byte[] FinishHash() => _hash.GetHashAndReset();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CacheMutexLease(Mutex mutex) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                semaphore.Release();
        }
    }

    private sealed class OpenedArchive(string archivePath, FileStream stream, string snapshotId) : IAsyncDisposable
    {
        private readonly object _archiveGate = new();
        private ZipArchive? _archive;
        private int _disposed;
        public string ArchivePath { get; } = archivePath;
        private FileStream Stream { get; } = stream;
        public string SnapshotId { get; } = snapshotId;
        public ZipArchive GetArchive()
        {
            lock (_archiveGate)
            {
                if (_disposed != 0)
                    throw new ObjectDisposedException(nameof(OpenedArchive));
                return _archive ??= new ZipArchive(Stream, ZipArchiveMode.Read, leaveOpen: true);
            }
        }
        public ValueTask DisposeAsync()
        {
            DisposeCore();
            return ValueTask.CompletedTask;
        }
        private void DisposeCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_archiveGate)
            {
                try { _archive?.Dispose(); }
                finally { Stream.Dispose(); }
            }
        }
    }

    private sealed record CatalogIndex(int ArchiveEntryCount, CatalogIndexedRecord[] Records, string[] Diagnostics,
        bool CatalogTruncated, bool IsComplete, long EstimatedMemoryBytes);
    private sealed record CatalogIndexedRecord(int EntryIndex, string Path, long Size, CatalogRecordState State,
        CatalogMetadata? Metadata, string[]? SearchableStrings);
    private enum CatalogRecordState : byte { Valid, Malformed, Unreadable, SkippedPath, SkippedSize, Unscanned }
}
