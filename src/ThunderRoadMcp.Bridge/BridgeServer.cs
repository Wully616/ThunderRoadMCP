using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            BridgeRequestTimings timings = null;
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

                timings = BridgeRequestTimings.Create(request.diagnostics);
                object result;
                using (var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
                {
                    // A pending NetworkStream read cannot be canceled portably; disposing the client
                    // after the response ends the monitor, so it must not be awaited here.
                    _ = MonitorClientDisconnect(stream, requestLifetime);
                    result = await requestHandler.DispatchAsync(request, requestLifetime.Token, timings).ConfigureAwait(false);
                }
                timings.Complete();
                await WriteSuccess(stream, request.id, result, timings.Snapshot(), client, request.method == "evaluate").ConfigureAwait(false);
            }
            catch (BridgeEvaluationException exception)
            {
                BridgeLogs.Record("Error", exception.Message);
                if (stream != null)
                {
                    try
                    {
                        timings?.Complete();
                        await WriteFailure(stream, requestId, exception, timings?.Snapshot(), client).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException)
            {
                if (!shutdown.IsCancellationRequested && stream != null)
                {
                    try
                    {
                        timings?.Complete();
                        await WriteFailure(stream, requestId, "Request timed out.", timings?.Snapshot(), client).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                BridgeLogs.Record("Error", ex.ToString());
                if (stream != null)
                {
                    try
                    {
                        timings?.Complete();
                        await WriteFailure(stream, requestId, ex.ToString(), timings?.Snapshot(), client).ConfigureAwait(false);
                    }
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

        private static async Task MonitorClientDisconnect(Stream stream, CancellationTokenSource requestLifetime)
        {
            var buffer = new byte[1];
            try
            {
                while (await stream.ReadAsync(buffer, 0, 1).ConfigureAwait(false) != 0) { }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            try
            {
                requestLifetime.Cancel();
            }
            catch (ObjectDisposedException) { }
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

        private static async Task WriteSuccess(Stream stream, string id, object result, Dictionary<string, double> timings,
            TcpClient client, bool evaluationResult)
        {
            var jsonResult = result as BridgeJsonResult;
            if (jsonResult != null)
            {
                await WriteRawSuccess(stream, id, jsonResult.Json, timings, client, evaluationResult).ConfigureAwait(false);
                return;
            }
            if (timings == null)
            {
                await WriteResponse(stream, id, new { id, success = true, result }, client).ConfigureAwait(false);
                return;
            }
            await WriteResponse(stream, id, new { id, success = true, result, timings }, client, timings).ConfigureAwait(false);
        }

        private static async Task WriteFailure(Stream stream, string id, string error, Dictionary<string, double> timings, TcpClient client)
        {
            if (error == null) error = "Request failed.";
            if (error.Length > 4096)
            {
                const string marker = " …[truncated]";
                error = error.Substring(0, 4096 - marker.Length) + marker;
            }
            object response = timings is null
                ? new { id, success = false, error }
                : (object)new { id, success = false, error, timings };
            await WriteResponse(stream, id, response, client, timings).ConfigureAwait(false);
        }
        internal static async Task WriteFailure(Stream stream, string id, BridgeEvaluationException failure,
            Dictionary<string, double> timings, TcpClient client)
        {
            string error = failure.Message ?? "Evaluation failed.";
            if (error.Length > 4096)
            {
                const string marker = " …[truncated]";
                error = error.Substring(0, 4096 - marker.Length) + marker;
            }
            var response = new Dictionary<string, object>
            {
                ["id"] = id,
                ["success"] = false,
                ["error"] = error,
                ["errorCode"] = failure.errorCode,
                ["executionOutcome"] = failure.executionOutcome,
                ["diagnostics"] = failure.diagnostics
            };
            if (timings != null)
                response["timings"] = timings;
            await WriteResponse(stream, id, response, client, timings).ConfigureAwait(false);
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


        private static async Task WriteResponse(Stream stream, string id, object response, TcpClient client, Dictionary<string, double> timings = null)
        {
            var output = new BoundedStringWriter(MaxLineBytes);
            using (var writer = new JsonTextWriter(output))
            {
                writer.CloseOutput = false;
                JsonSerializer.Create(new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                }).Serialize(writer, response);
                writer.Flush();
            }
            byte[] bytes = Utf8.GetBytes(output.ToString() + "\n");
            if (bytes.Length > MaxLineBytes)
            {
                object failure = timings is null
                    ? new { id, success = false, error = "Response exceeds 64 KiB." }
                    : (object)new { id, success = false, error = "Response exceeds 64 KiB.", timings };
                bytes = Utf8.GetBytes(JsonConvert.SerializeObject(failure) + "\n");
            }
            await WriteBounded(stream, bytes, client).ConfigureAwait(false);
        }

        private static async Task WriteRawSuccess(Stream stream, string id, string resultJson, Dictionary<string, double> timings,
            TcpClient client, bool evaluationResult)
        {
            await WriteBounded(stream, BuildRawSuccessResponse(id, resultJson, timings, evaluationResult), client).ConfigureAwait(false);
        }

        internal static byte[] BuildRawSuccessResponse(string id, string resultJson, Dictionary<string, double> timings,
            bool evaluationResult = false)
        {
            if (TrySerializeRawSuccess(id, resultJson, timings, out var bytes) && bytes.Length <= MaxLineBytes)
                return bytes;
            if (timings != null && TrySerializeRawSuccess(id, resultJson, null, out bytes) && bytes.Length <= MaxLineBytes)
                return bytes;
            return OversizedRawResponse(id, timings, evaluationResult);
        }

        private static bool TrySerializeRawSuccess(string id, string resultJson, Dictionary<string, double> timings, out byte[] bytes)
        {
            try
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
                    if (timings != null)
                    {
                        writer.WritePropertyName("timings");
                        writer.WriteStartObject();
                        foreach (var timing in timings)
                        {
                            writer.WritePropertyName(timing.Key);
                            writer.WriteValue(timing.Value);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                    writer.Flush();
                }
                string json = output.ToString();
                int byteCount = Utf8.GetByteCount(json);
                if (byteCount + 1 > MaxLineBytes)
                {
                    bytes = null;
                    return false;
                }
                bytes = new byte[byteCount + 1];
                Utf8.GetBytes(json, 0, json.Length, bytes, 0);
                bytes[byteCount] = (byte)'\n';
                return true;
            }
            catch (InvalidDataException)
            {
                bytes = null;
                return false;
            }
        }

        private static byte[] OversizedRawResponse(string id, Dictionary<string, double> timings, bool evaluationResult)
        {
            var response = new Dictionary<string, object>
            {
                ["id"] = id,
                ["success"] = false,
                ["error"] = "Response exceeds 64 KiB."
            };
            if (evaluationResult)
            {
                response["errorCode"] = "evaluation.result_unavailable";
                response["executionOutcome"] = "completed_result_unavailable";
            }
            if (timings != null)
                response["timings"] = timings;
            var bytes = Utf8.GetBytes(JsonConvert.SerializeObject(response) + "\n");
            if (bytes.Length <= MaxLineBytes)
                return bytes;
            response.Remove("timings");
            return Utf8.GetBytes(JsonConvert.SerializeObject(response) + "\n");
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
    internal sealed class BridgeRequestTimings
    {
        public static bool DiagnosticsEnabled { get; } =
            string.Equals(Environment.GetEnvironmentVariable("THUNDERROAD_MCP_DIAGNOSTICS"), "1", StringComparison.Ordinal);
        private static readonly BridgeRequestTimings Disabled = new BridgeRequestTimings(false);
        private static readonly IDisposable Noop = new NoopMeasurement();
        private readonly bool enabled;
        private readonly object gate;
        private readonly Stopwatch elapsed;
        private readonly Dictionary<string, double> timings;
        private bool completed;

        private BridgeRequestTimings(bool enabled)
        {
            this.enabled = enabled;
            if (!enabled) return;
            gate = new object();
            elapsed = Stopwatch.StartNew();
            timings = new Dictionary<string, double>(StringComparer.Ordinal);
        }

        public bool IsEnabled => enabled;
        public static BridgeRequestTimings Create(bool requested) => requested || DiagnosticsEnabled ? new BridgeRequestTimings(true) : Disabled;
        internal static BridgeRequestTimings CreateForSelfTest() => new BridgeRequestTimings(true);
        public IDisposable Measure(string name) => enabled ? (IDisposable)new TimingMeasurement(this, name) : Noop;

        public void Add(string name, double milliseconds)
        {
            if (!enabled) return;
            lock (gate)
                timings[name] = timings.TryGetValue(name, out double current) ? current + milliseconds : milliseconds;
        }

        public void Complete()
        {
            if (!enabled) return;
            lock (gate)
            {
                if (completed) return;
                timings["bridgeRequestTotalMs"] = elapsed.Elapsed.TotalMilliseconds;
                completed = true;
            }
        }

        public Dictionary<string, double> Snapshot()
        {
            if (!enabled) return null;
            lock (gate)
                return new Dictionary<string, double>(timings, StringComparer.Ordinal);
        }

        private sealed class NoopMeasurement : IDisposable
        {
            public void Dispose() { }
        }

        private sealed class TimingMeasurement : IDisposable
        {
            private readonly BridgeRequestTimings owner;
            private readonly string name;
            private readonly Stopwatch elapsed = Stopwatch.StartNew();

            public TimingMeasurement(BridgeRequestTimings owner, string name)
            {
                this.owner = owner;
                this.name = name;
            }

            public void Dispose() => owner.Add(name, elapsed.Elapsed.TotalMilliseconds);
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
