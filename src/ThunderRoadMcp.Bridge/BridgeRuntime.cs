using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace ThunderRoadMcp.Bridge
{
    internal sealed class BridgeConfig
    {
        [JsonProperty("enabled")] public bool Enabled { get; set; }
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("port")] public int Port { get; set; }
        [JsonProperty("token")] public string Token { get; set; }

        public void Validate()
        {
            if (!string.Equals(Host, "127.0.0.1", StringComparison.Ordinal))
                throw new InvalidOperationException("Bridge host must be 127.0.0.1.");
            if (Port < 1 || Port > 65535)
                throw new InvalidOperationException("Bridge port must be between 1 and 65535.");
            if (Token == null || Token.Length != 64)
                throw new InvalidOperationException("Bridge token must contain 64 hexadecimal characters (32 random bytes).");
            foreach (char c in Token)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    throw new InvalidOperationException("Bridge token must contain 64 hexadecimal characters (32 random bytes).");
        }
    }

    internal sealed class BridgeLogEntry
    {
        public long sequence { get; set; }
        public string timestamp { get; set; }
        public string level { get; set; }
        public string message { get; set; }
        public string stackTrace { get; set; }
    }

    internal sealed class BridgeLogs
    {
        private const int Capacity = 512;
        private const int ResultCapacity = 3;
        private static readonly object Gate = new object();
        private static readonly Queue<BridgeLogEntry> Entries = new Queue<BridgeLogEntry>();
        private static long sequence;
        private static bool capturing;

        public static void StartCapture()
        {
            if (capturing) return;
            capturing = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        public static void StopCapture()
        {
            if (!capturing) return;
            capturing = false;
            Application.logMessageReceivedThreaded -= OnLog;
        }

        public static void Record(string level, string message)
        {
            OnLog(message, string.Empty, level == "Error" ? LogType.Error : LogType.Log);
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                long next = ++sequence;
                Entries.Enqueue(new BridgeLogEntry
                {
                    sequence = next,
                    timestamp = DateTime.UtcNow.ToString("O"),
                    level = type.ToString(),
                    message = Clip(condition, 512),
                    stackTrace = Clip(stackTrace, 2048)
                });
                while (Entries.Count > Capacity) Entries.Dequeue();
            }
        }

        public static object ReadSince(long since)
        {
            lock (Gate)
            {
                var entries = new List<BridgeLogEntry>(ResultCapacity);
                long next = sequence;
                if (since <= 0)
                {
                    int skip = Math.Max(0, Entries.Count - ResultCapacity);
                    int index = 0;
                    foreach (BridgeLogEntry entry in Entries)
                    {
                        if (index++ < skip) continue;
                        entries.Add(entry);
                    }
                    return new { entries, nextSequence = next };
                }
                foreach (BridgeLogEntry entry in Entries)
                {
                    if (entry.sequence <= since) continue;
                    if (entries.Count == ResultCapacity) break;
                    entries.Add(entry);
                    next = entry.sequence;
                }
                if (entries.Count < ResultCapacity) next = sequence;
                return new { entries, nextSequence = next };
            }
        }

        private static string Clip(string value, int limit)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= limit) return value ?? string.Empty;
            const string marker = " …[truncated]";
            return value.Substring(0, limit - marker.Length) + marker;
        }
    }

    internal sealed class MainThreadWork
    {
        public Func<string> Run;
        public CancellationToken Token;
        public TaskCompletionSource<string> Completion;
    }

    internal static class MainThreadQueue
    {
        private const int Capacity = 8;
        private static readonly object Gate = new object();
        private static readonly Queue<MainThreadWork> Queue = new Queue<MainThreadWork>();

        public static Task<string> Enqueue(Func<string> run, CancellationToken token)
        {
            var work = new MainThreadWork
            {
                Run = run,
                Token = token,
                Completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            lock (Gate)
            {
                token.ThrowIfCancellationRequested();
                if (Queue.Count >= Capacity) throw new InvalidOperationException("Main-thread evaluation queue is full.");
                Queue.Enqueue(work);
            }
            return work.Completion.Task;
        }

        public static void Drain()
        {
            MainThreadWork work;
            lock (Gate)
            {
                if (Queue.Count == 0) return;
                work = Queue.Dequeue();
            }
            if (work.Token.IsCancellationRequested)
            {
                work.Completion.TrySetCanceled();
                return;
            }
            try { work.Completion.TrySetResult(work.Run()); }
            catch (Exception ex) { work.Completion.TrySetException(ex); }
        }

        public static void CancelPending()
        {
            lock (Gate)
                while (Queue.Count > 0)
                    Queue.Dequeue().Completion.TrySetException(new ObjectDisposedException("ThunderRoadMcp bridge"));
        }

    }
}
