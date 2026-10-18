using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ThunderRoadMcp.Bridge
{
    /// <summary>
    /// Bridge-owned, main-thread runtime state. Coroutines started here survive the eval call that
    /// created them, and named object references can be retrieved by later eval calls or host tools.
    /// </summary>
    public sealed class McpRuntime : MonoBehaviour
    {
        public const int MaxKeyLength = 128;
        public const int MaxEntries = 128;

        private sealed class ManagedCoroutine
        {
            public Coroutine Handle;
        }

        private static McpRuntime current;
        private readonly Dictionary<string, ManagedCoroutine> coroutines = new Dictionary<string, ManagedCoroutine>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
        private int mainThreadId;

        /// <summary>Null before the bridge starts and after it stops, including after the host object is destroyed.</summary>
        public static McpRuntime Current => current == null ? null : current;

        private void Awake()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        internal static McpRuntime EnsureCreated()
        {
            McpRuntime existing = Current;
            if (existing != null) return existing;
            var host = new GameObject("ThunderRoadMcp.Runtime");
            UnityEngine.Object.DontDestroyOnLoad(host);
            current = host.AddComponent<McpRuntime>();
            current.mainThreadId = Thread.CurrentThread.ManagedThreadId;
            return current;
        }

        internal static void Shutdown()
        {
            McpRuntime instance = Current;
            current = null;
            if (instance == null) return;
            try
            {
                instance.Clear();
            }
            finally
            {
                UnityEngine.Object.Destroy(instance.gameObject);
            }
        }

        /// <summary>Start a bridge-owned coroutine, replacing and stopping any earlier coroutine under <paramref name="key"/>.</summary>
        public Coroutine StartManagedCoroutine(string key, IEnumerator routine)
        {
            EnsureMainThread();
            ValidateKey(key);
            if (routine == null) throw new ArgumentNullException(nameof(routine));

            if (coroutines.TryGetValue(key, out ManagedCoroutine previous))
            {
                coroutines.Remove(key);
                if (previous.Handle != null) StopCoroutine(previous.Handle);
            }
            else if (coroutines.Count >= MaxEntries)
            {
                throw new InvalidOperationException("McpRuntime supports at most " + MaxEntries + " managed coroutines.");
            }

            var entry = new ManagedCoroutine();
            coroutines.Add(key, entry);
            try
            {
                entry.Handle = StartCoroutine(Track(key, entry, routine));
                return entry.Handle;
            }
            catch
            {
                coroutines.Remove(key);
                throw;
            }
        }

        public bool StopManagedCoroutine(string key)
        {
            EnsureMainThread();
            ValidateKey(key);
            if (!coroutines.TryGetValue(key, out ManagedCoroutine entry)) return false;
            coroutines.Remove(key);
            if (entry.Handle != null) StopCoroutine(entry.Handle);
            return true;
        }

        public void StopAllManagedCoroutines()
        {
            EnsureMainThread();
            if (coroutines.Count == 0) return;
            var handles = new List<Coroutine>(coroutines.Count);
            foreach (ManagedCoroutine entry in coroutines.Values)
                if (entry.Handle != null) handles.Add(entry.Handle);
            coroutines.Clear();
            foreach (Coroutine handle in handles)
                StopCoroutine(handle);
        }

        public void Set(string key, object value)
        {
            EnsureMainThread();
            ValidateKey(key);
            if (!values.ContainsKey(key) && values.Count >= MaxEntries)
                throw new InvalidOperationException("McpRuntime supports at most " + MaxEntries + " named object entries.");
            values[key] = value;
        }

        public bool TryGet<T>(string key, out T value) where T : class
        {
            EnsureMainThread();
            ValidateKey(key);
            if (values.TryGetValue(key, out object stored) && stored is T typed)
            {
                value = typed;
                return true;
            }
            value = null;
            return false;
        }

        public T Get<T>(string key) where T : class
        {
            return TryGet(key, out T value) ? value : null;
        }

        public bool Remove(string key)
        {
            EnsureMainThread();
            ValidateKey(key);
            return values.Remove(key);
        }

        /// <summary>Keys and runtime types only, so the result stays JSON-serializable.</summary>
        public object Describe()
        {
            EnsureMainThread();
            var coroutineKeys = new List<string>(coroutines.Keys);
            coroutineKeys.Sort(StringComparer.Ordinal);
            var objectKeys = new List<string>(values.Keys);
            objectKeys.Sort(StringComparer.Ordinal);

            var describedCoroutines = new List<object>(coroutineKeys.Count);
            foreach (string key in coroutineKeys)
                describedCoroutines.Add(new { key, type = typeof(Coroutine).FullName });
            var describedObjects = new List<object>(objectKeys.Count);
            foreach (string key in objectKeys)
            {
                object value = values[key];
                describedObjects.Add(new { key, type = value == null ? null : value.GetType().FullName });
            }
            return new
            {
                coroutines = describedCoroutines,
                objects = describedObjects,
                coroutineCount = describedCoroutines.Count,
                objectCount = describedObjects.Count,
            };
        }

        /// <summary>Stop only coroutines and references owned by this bridge runtime.</summary>
        public void Clear()
        {
            EnsureMainThread();
            try
            {
                StopAllManagedCoroutines();
            }
            finally
            {
                values.Clear();
            }
        }

        private IEnumerator Track(string key, ManagedCoroutine entry, IEnumerator routine)
        {
            try
            {
                while (routine.MoveNext())
                    yield return routine.Current;
            }
            finally
            {
                if (coroutines.TryGetValue(key, out ManagedCoroutine tracked) && ReferenceEquals(tracked, entry))
                    coroutines.Remove(key);
            }
        }

        private void EnsureMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                throw new InvalidOperationException("McpRuntime must be used on the Unity main thread.");
        }

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Runtime keys must be non-empty.", nameof(key));
            if (key.Length > MaxKeyLength)
                throw new ArgumentException("Runtime keys must be at most " + MaxKeyLength + " characters.", nameof(key));
        }
    }
}
