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

        public bool TryGet<T>(string key, out T value)
        {
            EnsureMainThread();
            ValidateKey(key);
            if (values.TryGetValue(key, out object stored))
            {
                if (stored is T typed)
                {
                    value = typed;
                    return true;
                }
                if (stored == null && ReferenceEquals(default(T), null))
                {
                    value = default(T);
                    return true;
                }
            }
            value = default(T);
            return false;
        }

        public T Get<T>(string key)
        {
            return TryGet(key, out T value) ? value : default(T);
        }

        internal object Inspect(string key)
        {
            EnsureMainThread();
            ValidateKey(key);
            bool found = values.TryGetValue(key, out object value);
            bool typeTruncated = false;
            string type = found && value != null ? BoundedText(value.GetType().FullName, MaxInspectionTextLength, out typeTruncated) : null;
            return new
            {
                action = "inspect",
                key,
                found,
                type,
                typeTruncated,
                value = found ? DescribeValue(value, MaxInspectionTextLength) : null
            };
        }

        public bool Remove(string key)
        {
            EnsureMainThread();
            ValidateKey(key);
            return values.Remove(key);
        }

        /// <summary>Keys, runtime types, and safe bounded value summaries.</summary>
        public object Describe()
        {
            EnsureMainThread();
            var coroutineKeys = new List<string>(coroutines.Keys);
            coroutineKeys.Sort(StringComparer.Ordinal);
            var objectKeys = new List<string>(values.Keys);
            objectKeys.Sort(StringComparer.Ordinal);

            var describedCoroutines = new List<object>(coroutineKeys.Count);
            var describedObjects = new List<object>(objectKeys.Count);
            // Reserve space for the root object, counts, flags, and array delimiters.
            int remaining = BridgeRequestHandler.MaxLineBytes - 512 - 1024;
            bool coroutinesTruncated = false;
            bool objectsTruncated = false;
            string coroutineType = typeof(Coroutine).FullName;
            int maxEntries = Math.Max(coroutineKeys.Count, objectKeys.Count);
            for (int index = 0; index < maxEntries; index++)
            {
                if (index < coroutineKeys.Count)
                {
                    string key = coroutineKeys[index];
                    int size = EstimateListEntryLength(key, coroutineType);
                    if (size <= remaining)
                    {
                        describedCoroutines.Add(new { key, type = coroutineType });
                        remaining -= size;
                    }
                    else
                    {
                        coroutinesTruncated = true;
                    }
                }
                if (index < objectKeys.Count)
                {
                    string key = objectKeys[index];
                    object value = values[key];
                    bool typeTruncated = false;
                    string type = value == null ? null : BoundedText(value.GetType().FullName, MaxTypeNameLength, out typeTruncated);
                    int size = EstimateListEntryLength(key, type);
                    if (size <= remaining)
                    {
                        describedObjects.Add(new { key, type, typeTruncated, value = DescribeValue(value, MaxListTextLength) });
                        remaining -= size;
                    }
                    else
                    {
                        objectsTruncated = true;
                    }
                }
            }
            return new
            {
                coroutines = describedCoroutines,
                objects = describedObjects,
                coroutineCount = coroutineKeys.Count,
                objectCount = objectKeys.Count,
                coroutinesTruncated,
                objectsTruncated,
            };
        }

        private static int EstimateListEntryLength(string key, string type)
        {
            // Conservative JSON overhead; raise it if adding summary fields or widening previews.
            return 352 + JsonStringLength(key) + JsonStringLength(type);
        }

        private static int JsonStringLength(string value)
        {
            if (value == null) return 4;
            int length = 2;
            foreach (char character in value)
            {
                if (character == '"' || character == '\\' || character == '\b' || character == '\t' ||
                    character == '\n' || character == '\f' || character == '\r')
                    length += 2;
                else if (character < ' ' || character > '~')
                    length += 6;
                else
                    length++;
            }
            return length;
        }

        private const int MaxInspectionTextLength = 256;
        private const int MaxListTextLength = 32;
        private const int MaxTypeNameLength = 128;
        private const string TruncationMarker = "…[truncated]";

        private static string BoundedText(string value, int maxLength, out bool truncated)
        {
            truncated = value.Length > maxLength;
            return truncated
                ? value.Substring(0, maxLength - TruncationMarker.Length) + TruncationMarker
                : value;
        }

        private static object DescribeValue(object value, int maxStringLength)
        {
            if (ReferenceEquals(value, null))
                return new { kind = "null" };

            Type type = value.GetType();
            if (type == typeof(string))
            {
                string text = BoundedText((string)value, maxStringLength, out bool truncated);
                return new { kind = "scalar", value = text, truncated };
            }
            if ((type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr)) ||
                type == typeof(decimal) || type.IsEnum)
                return new { kind = "scalar", value, truncated = false };

            if (value is Vector2 vector2)
                return new { kind = "unityValue", value = new { x = vector2.x, y = vector2.y } };
            if (value is Vector3 vector3)
                return new { kind = "unityValue", value = new { x = vector3.x, y = vector3.y, z = vector3.z } };
            if (value is Vector4 vector4)
                return new { kind = "unityValue", value = new { x = vector4.x, y = vector4.y, z = vector4.z, w = vector4.w } };
            if (value is Quaternion quaternion)
                return new { kind = "unityValue", value = new { x = quaternion.x, y = quaternion.y, z = quaternion.z, w = quaternion.w } };
            if (value is Color color)
                return new { kind = "unityValue", value = new { r = color.r, g = color.g, b = color.b, a = color.a } };

            UnityEngine.Object unityObject = value as UnityEngine.Object;
            if (!ReferenceEquals(unityObject, null))
            {
                bool destroyed = unityObject == null;
                bool nameTruncated = false;
                string name = destroyed ? null : BoundedText(unityObject.name, maxStringLength, out nameTruncated);
                int? instanceId = destroyed ? (int?)null : unityObject.GetInstanceID();
                return new { kind = "unityObject", name, instanceId, destroyed, nameTruncated };
            }

            return new { kind = "opaque" };
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
