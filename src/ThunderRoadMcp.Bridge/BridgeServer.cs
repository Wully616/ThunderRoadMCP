using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ThunderRoadMcp.Bridge
{

    internal sealed class BridgeServer : IDisposable
    {
        private const int MaxLineBytes = BridgeRequestHandler.MaxLineBytes;
        private const int MaxClients = 4;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly TcpListener listener;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly SemaphoreSlim clients = new SemaphoreSlim(MaxClients, MaxClients);
        private readonly BridgeRequestHandler requestHandler;
        private Task acceptTask;
        private bool disposed;
        private readonly object activeGate = new object();
        private readonly HashSet<TcpClient> active = new HashSet<TcpClient>();

        public BridgeServer(BridgeConfig config)
        {
            requestHandler = new BridgeRequestHandler(config);
            listener = new TcpListener(IPAddress.Loopback, config.Port);
        }

        public void Start()
        {
            listener.Start(MaxClients);
            acceptTask = AcceptClients();
        }

        private async Task AcceptClients()
        {
            while (!shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { if (shutdown.IsCancellationRequested) break; else continue; }

                if (!clients.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                lock (activeGate) active.Add(client);
                ProcessClient(client);
            }
        }

        private async void ProcessClient(TcpClient client)
        {
            string requestId = null;
            NetworkStream stream = null;
            try
            {
                client.NoDelay = true;
                client.SendTimeout = 5000;
                stream = client.GetStream();
                string text;
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
                using (readTimeout.Token.Register(client.Dispose))
                {
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    text = await ReadLine(stream, readTimeout.Token).ConfigureAwait(false);
                    readTimeout.CancelAfter(Timeout.Infinite);
                }
                if (text == null) return;
                BridgeRequest request;
                try { request = JsonConvert.DeserializeObject<BridgeRequest>(text); }
                catch (JsonException ex)
                {
                    await WriteFailure(stream, null, ex.ToString(), client).ConfigureAwait(false);
                    return;
                }
                requestId = request == null ? null : request.id;

                if (request == null || string.IsNullOrEmpty(request.id) || request.id.Length > 128 ||
                    string.IsNullOrEmpty(request.method) || !requestHandler.IsAuthenticated(request.token))
                {
                    await WriteFailure(stream, requestId, "Invalid request or authentication token.", client).ConfigureAwait(false);
                    return;
                }

                object result = await requestHandler.DispatchAsync(request, shutdown.Token).ConfigureAwait(false);
                await WriteSuccess(stream, request.id, result, client).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!shutdown.IsCancellationRequested && stream != null)
                {
                    try { await WriteFailure(stream, requestId, "Request timed out.", client).ConfigureAwait(false); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                BridgeLogs.Record("Error", ex.ToString());
                if (stream != null)
                {
                    try { await WriteFailure(stream, requestId, ex.ToString(), client).ConfigureAwait(false); }
                    catch { }
                }
            }
            finally
            {
                client.Dispose();
                lock (activeGate) active.Remove(client);
                clients.Release();
            }
        }


        private static async Task<string> ReadLine(Stream stream, CancellationToken token)
        {
            byte[] buffer = new byte[MaxLineBytes];
            int length = 0;
            while (length < MaxLineBytes)
            {
                int count = await stream.ReadAsync(buffer, length, buffer.Length - length, token).ConfigureAwait(false);
                if (count == 0)
                {
                    if (length == 0) return null;
                    throw new InvalidDataException("Request line must end with newline.");
                }
                int newline = Array.IndexOf(buffer, (byte)'\n', length, count);
                if (newline >= 0)
                {
                    int jsonLength = newline;
                    if (jsonLength > 0 && buffer[jsonLength - 1] == (byte)'\r') jsonLength--;
                    return Utf8.GetString(buffer, 0, jsonLength);
                }
                length += count;
            }
            throw new InvalidDataException("Request line exceeds 64 KiB.");
        }

        private static async Task WriteSuccess(Stream stream, string id, object result, TcpClient client)
        {
            var jsonResult = result as BridgeJsonResult;
            if (jsonResult != null)
            {
                await WriteRawSuccess(stream, id, jsonResult.Json, client).ConfigureAwait(false);
                return;
            }
            await WriteResponse(stream, id, new { id, success = true, result }, client).ConfigureAwait(false);
        }

        private static async Task WriteFailure(Stream stream, string id, string error, TcpClient client)
        {
            if (error == null) error = "Request failed.";
            if (error.Length > 4096)
            {
                const string marker = " …[truncated]";
                error = error.Substring(0, 4096 - marker.Length) + marker;
            }
            await WriteResponse(stream, id, new { id, success = false, error }, client).ConfigureAwait(false);
        }


        private static async Task WriteResponse(Stream stream, string id, object response, TcpClient client)
        {
            var output = new BoundedStringWriter(MaxLineBytes);
            using (var writer = new JsonTextWriter(output))
            {
                writer.CloseOutput = false;
                JsonSerializer.Create(new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    MaxDepth = 16
                }).Serialize(writer, response);
                writer.Flush();
            }
            byte[] bytes = Utf8.GetBytes(output.ToString() + "\n");
            if (bytes.Length > MaxLineBytes)
                bytes = Utf8.GetBytes(JsonConvert.SerializeObject(new { id, success = false, error = "Response exceeds 64 KiB." }) + "\n");
            await WriteBounded(stream, bytes, client).ConfigureAwait(false);
        }

        private static async Task WriteRawSuccess(Stream stream, string id, string resultJson, TcpClient client)
        {
            var output = new BoundedStringWriter(MaxLineBytes);
            using (var writer = new JsonTextWriter(output))
            {
                writer.CloseOutput = false;
                writer.WriteStartObject();
                writer.WritePropertyName("id");
                writer.WriteValue(id);
                writer.WritePropertyName("success");
                writer.WriteValue(true);
                writer.WritePropertyName("result");
                writer.WriteRawValue(resultJson);
                writer.WriteEndObject();
                writer.Flush();
            }
            byte[] bytes = Utf8.GetBytes(output.ToString() + "\n");
            if (bytes.Length > MaxLineBytes)
            {
                await WriteFailure(stream, id, "Response exceeds 64 KiB.", client).ConfigureAwait(false);
                return;
            }
            await WriteBounded(stream, bytes, client).ConfigureAwait(false);
        }

        private static async Task WriteBounded(Stream stream, byte[] bytes, TcpClient client)
        {
            Task write = stream.WriteAsync(bytes, 0, bytes.Length);
            if (await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != write)
            {
                client.Dispose();
                _ = write.ContinueWith(t => { if (t.IsFaulted) { var ignored = t.Exception; } },
                    TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException("Bridge response write timed out.");
            }
            await write.ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            shutdown.Cancel();
            listener.Stop();
            List<TcpClient> toClose;
            lock (activeGate) toClose = new List<TcpClient>(active);
            foreach (TcpClient client in toClose) client.Dispose();
            if (acceptTask != null) acceptTask.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
    internal sealed class BoundedStringWriter : StringWriter
    {
        private readonly int limit;
        private int count;

        public BoundedStringWriter(int limit) : base(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture)
        {
            this.limit = limit;
        }

        public override void Write(char value)
        {
            EnsureCapacity(1);
            base.Write(value);
            count++;
        }

        public override void Write(string value)
        {
            if (value == null) return;
            EnsureCapacity(value.Length);
            base.Write(value);
            count += value.Length;
        }

        public override void Write(char[] buffer, int index, int count)
        {
            EnsureCapacity(count);
            base.Write(buffer, index, count);
            this.count += count;
        }

        private void EnsureCapacity(int amount)
        {
            if (amount < 0 || count > limit - amount)
                throw new InvalidDataException("Response exceeds 64 KiB.");
        }
    }
}
