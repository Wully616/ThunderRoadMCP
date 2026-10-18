using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

public sealed class BridgeClient
{
    private const int MaxLineBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(35);
    private static readonly string BridgeConfigPath = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_CONFIG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThunderRoadMcp", "bridge.json");
    private static readonly JsonSerializerOptions ConfigJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<string> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await RequestCoreAsync(method, parameters, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var requestTimeout = method == "status" ? TimeSpan.FromSeconds(3) : RequestTimeout;
            throw new TimeoutException($"Bridge {method} request exceeded {requestTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<string> RequestCoreAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var config = await ReadBridgeConfigAsync(cancellationToken);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestTimeout = method == "status" ? TimeSpan.FromSeconds(3) : RequestTimeout;
        timeout.CancelAfter(requestTimeout);
        var token = timeout.Token;
        await client.ConnectAsync(IPAddress.Loopback, config.Port, token);
        await using var stream = client.GetStream();
        var id = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.SerializeToUtf8Bytes(new { id, token = config.Token, method, parameters });
        if (request.Length + 1 > MaxLineBytes)
            throw new InvalidOperationException("Bridge request exceeds the 64 KiB line limit.");
        await stream.WriteAsync(request, token);
        await stream.WriteAsync("\n"u8.ToArray(), token);

        var responseBytes = new ArrayBufferWriter<byte>(1024);
        var buffer = new byte[4096];
        var hasLineEnd = false;
        while (responseBytes.WrittenCount < MaxLineBytes)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0)
                throw new IOException("Bridge closed the connection before responding.");
            var lineEnd = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = lineEnd < 0 ? read : lineEnd;
            var lineLength = responseBytes.WrittenCount + count + (lineEnd >= 0 ? 1 : 0);
            if (lineLength > MaxLineBytes)
                throw new IOException("Bridge response exceeds the 64 KiB line limit.");
            buffer.AsSpan(0, count).CopyTo(responseBytes.GetSpan(count));
            responseBytes.Advance(count);
            if (lineEnd >= 0)
            {
                hasLineEnd = true;
                break;
            }
        }
        if (!hasLineEnd)
            throw new IOException("Bridge response exceeds the 64 KiB line limit.");
        using var document = JsonDocument.Parse(responseBytes.WrittenMemory);
        var root = document.RootElement;
        if (root.GetProperty("id").GetString() != id)
            throw new IOException("Bridge response ID did not match the request.");
        if (!root.GetProperty("success").GetBoolean())
            throw new InvalidOperationException(root.GetProperty("error").GetString() ?? "Bridge request failed.");
        return root.GetProperty("result").GetRawText();
    }


    private static async Task<BridgeConfig> ReadBridgeConfigAsync(CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(BridgeConfigPath);
        var config = await JsonSerializer.DeserializeAsync<BridgeConfig>(input, ConfigJsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Bridge configuration '{BridgeConfigPath}' is empty.");
        if (!config.Enabled || config.Host != "127.0.0.1" || config.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(config.Token) || config.Token.Length != 64 || !config.Token.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Bridge configuration must be enabled with host 127.0.0.1, a valid port, and a 64-character hex token.");
        return config;
    }


    private sealed record BridgeConfig(bool Enabled, string Host, int Port, string Token);
}
