using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
internal static class PlayerLogReader
{
    private const int MaxBytes = 128 * 1024;
    private static readonly string LogDirectory = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_LOG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "WarpFrog", "BladeAndSorcery");

    public static string Read(string source, string fileName, int tailLines, long? beforeByteOffset, string? snapshotId)
    {
        if (tailLines is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(tailLines), "tailLines must be between 1 and 2000.");
        if (beforeByteOffset is not null && snapshotId is null)
            throw new ArgumentException("Pass snapshotId returned by the previous page with beforeByteOffset.");
        var path = Path.Combine(LogDirectory, fileName);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException exception)
        {
            throw new FileNotFoundException($"The requested {source} log ({fileName}) was not found at {path}.", path, exception);
        }

        using (stream)
        {
            var length = stream.Length;
            var sampleCount = (int)Math.Min(length, 256);
            if (snapshotId is not null)
            {
                var separator = snapshotId.IndexOf(':');
                if (separator <= 0 ||
                    !int.TryParse(snapshotId.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out sampleCount) ||
                    sampleCount is < 0 or > 256 || length < sampleCount)
                    throw new IOException($"The {source} log snapshot ID is invalid or stale; restart pagination.");
            }
            var header = new byte[sampleCount];
            stream.Seek(0, SeekOrigin.Begin);
            stream.ReadExactly(header);
            var currentSnapshotId = $"{sampleCount}:{Convert.ToHexString(SHA256.HashData(header))}";
            if (snapshotId is not null && snapshotId != currentSnapshotId)
                throw new IOException($"The {source} log session changed since the previous page; restart pagination.");
            var before = beforeByteOffset ?? length;
            if (before < 0 || before > length)
                throw new ArgumentOutOfRangeException(nameof(beforeByteOffset), $"Offset {before} is outside the {source} log snapshot of {length} bytes.");

            var baseOffset = Math.Max(0, before - MaxBytes);
            var count = (int)(before - baseOffset);
            stream.Seek(baseOffset, SeekOrigin.Begin);
            var bytes = new byte[count];
            stream.ReadExactly(bytes);

            var start = 0;
            var partialFirstLine = false;
            if (baseOffset > 0 && count > 0)
            {
                stream.Seek(baseOffset - 1, SeekOrigin.Begin);
                if (stream.ReadByte() != '\n')
                {
                    var newline = Array.IndexOf(bytes, (byte)'\n');
                    if (newline >= 0 && newline != count - 1)
                        start = newline + 1;
                    else
                    {
                        partialFirstLine = true;
                        while (start < count && (bytes[start] & 0xc0) == 0x80)
                            start++;
                    }
                }
            }

            var end = count;
            if (end > start && bytes[end - 1] == '\n')
                end--;
            var alignedStart = start;
            var lineCount = 1;
            for (var i = end - 1; i >= start; i--)
            {
                if (bytes[i] == '\n' && ++lineCount > tailLines)
                {
                    start = i + 1;
                    break;
                }
            }
            partialFirstLine &= start == alignedStart;
            if (end > start)
            {
                var lead = end - 1;
                while (lead >= start && (bytes[lead] & 0xc0) == 0x80)
                    lead--;
                if (lead >= start)
                {
                    var first = bytes[lead];
                    var needed = first is >= 0xc2 and <= 0xdf ? 2 : first is >= 0xe0 and <= 0xef ? 3 : first is >= 0xf0 and <= 0xf4 ? 4 : 1;
                    if (end - lead < needed)
                        end = lead;
                }
            }
            var startOffset = baseOffset + start;
            var truncated = startOffset > 0 || before < length || lineCount > tailLines || partialFirstLine;
            var tail = Encoding.UTF8.GetString(bytes, start, end - start);
            if (tail.EndsWith('\r'))
                tail = tail[..^1];
            return JsonSerializer.Serialize(new
            {
                source,
                path,
                fileLength = length,
                snapshotId = currentSnapshotId,
                startByteOffset = startOffset,
                endByteOffset = before,
                nextBeforeByteOffset = startOffset > 0 && startOffset < before ? startOffset : (long?)null,
                truncated,
                partialFirstLine,
                text = tail
            });
        }
    }
}

