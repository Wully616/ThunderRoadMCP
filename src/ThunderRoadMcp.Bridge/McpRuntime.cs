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
        private const int MaxRecentFailures = 16;

        private sealed class ManagedCoroutine
        {
            public Coroutine Handle;
            public IEnumerator Tracked;
            public string Routine;
            public DateTime StartedUtc;
            public int Steps;
        }

        private sealed class ManagedRoutine : IEnumerator, IDisposable
        {
            private readonly McpRuntime runtime;
            private readonly string key;
            private readonly ManagedCoroutine entry;
            private readonly List<IEnumerator> iterators = new List<IEnumerator>();
            private bool disposed;
            private bool failureRecorded;

            public ManagedRoutine(McpRuntime runtime, string key, ManagedCoroutine entry, IEnumerator routine)
            {
                this.runtime = runtime;
                this.key = key;
                this.entry = entry;
                iterators.Add(routine);
            }

            public object Current { get; private set; }

            public bool MoveNext()
            {
                if (disposed) return false;
                try
                {
                    while (iterators.Count > 0)
                    {
                        IEnumerator iterator = iterators[iterators.Count - 1];
                        if (!iterator.MoveNext())
                        {
                            iterators.RemoveAt(iterators.Count - 1);
                            (iterator as IDisposable)?.Dispose();
                            continue;
                        }

                        object yielded = iterator.Current;
                        entry.Steps++;
                        if (yielded is IEnumerator nested &&
                            !(yielded is YieldInstruction) &&
                            !(yielded is CustomYieldInstruction))
                        {
                            for (int index = 0; index < iterators.Count; index++)
                            {
                                if (ReferenceEquals(iterators[index], nested))
                                    throw new InvalidOperationException("A managed coroutine cannot yield an IEnumerator already active in its nested iterator chain.");
                            }
                            iterators.Add(nested);
                            continue;
                        }

                        Current = yielded;
                        return true;
                    }

                    Exception disposalFailure = DisposeOwned();
                    Complete();
                    if (disposalFailure != null) throw disposalFailure;
                    return false;
                }
                catch (Exception exception)
                {
                    DisposeOwned();
                    Complete();
                    RecordFailure(exception);
                    throw;
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                Exception disposalFailure = DisposeOwned();
                Complete();
                if (disposalFailure != null) RecordFailure(disposalFailure);
            }

            public void Reset()
            {
                throw new NotSupportedException();
            }

            private Exception DisposeOwned()
            {
                if (disposed) return null;
                disposed = true;
                Current = null;
                Exception failure = null;
                for (int index = iterators.Count - 1; index >= 0; index--)
                {
                    IEnumerator iterator = iterators[index];
                    try
                    {
                        (iterator as IDisposable)?.Dispose();
                    }
                    catch (Exception exception)
                    {
                        if (failure == null) failure = exception;
                    }
                }
                iterators.Clear();
                return failure;
            }

            private void RecordFailure(Exception exception)
            {
                if (failureRecorded) return;
                failureRecorded = true;
                runtime.RecordCoroutineFailure(key, entry, exception);
            }

            private void Complete()
            {
                if (runtime.coroutines.TryGetValue(key, out ManagedCoroutine tracked) && ReferenceEquals(tracked, entry))
                    runtime.coroutines.Remove(key);
            }
        }

        private sealed class CoroutineFailure
        {
            public string key { get; set; }
            public string routine { get; set; }
            public string error { get; set; }
            public int steps { get; set; }
            public string startedAtUtc { get; set; }
            public string failedAtUtc { get; set; }
        }

        private static McpRuntime current;
        private readonly Dictionary<string, ManagedCoroutine> coroutines = new Dictionary<string, ManagedCoroutine>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Queue<CoroutineFailure> recentFailures = new Queue<CoroutineFailure>();
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
                StopEntry(previous);
            }
            else if (coroutines.Count >= MaxEntries)
            {
                throw new InvalidOperationException("McpRuntime supports at most " + MaxEntries + " managed coroutines.");
            }

            var entry = new ManagedCoroutine
            {
                Routine = BoundedText(routine.GetType().FullName, MaxTypeNameLength, out _),
                StartedUtc = DateTime.UtcNow
            };
            coroutines.Add(key, entry);
            try
            {
                entry.Tracked = Track(key, entry, routine);
                entry.Handle = StartCoroutine(entry.Tracked);
                return entry.Handle;
            }
            catch
            {
                if (coroutines.TryGetValue(key, out ManagedCoroutine tracked) && ReferenceEquals(tracked, entry))
                    coroutines.Remove(key);
                StopEntry(entry);
                throw;
            }
        }

        public bool StopManagedCoroutine(string key)
        {
            EnsureMainThread();
            ValidateKey(key);
            if (!coroutines.TryGetValue(key, out ManagedCoroutine entry)) return false;
            coroutines.Remove(key);
            StopEntry(entry);
            return true;
        }

        public void StopAllManagedCoroutines()
        {
            EnsureMainThread();
            if (coroutines.Count == 0) return;
            var entries = new List<ManagedCoroutine>(coroutines.Values);
            coroutines.Clear();
            Exception failure = null;
            foreach (ManagedCoroutine entry in entries)
            {
                try
                {
                    StopEntry(entry);
                }
                catch (Exception exception)
                {
                    if (failure == null) failure = exception;
                }
            }
            if (failure != null) throw failure;
        }

        private void StopEntry(ManagedCoroutine entry)
        {
            try
            {
                if (entry.Handle != null) StopCoroutine(entry.Handle);
            }
            finally
            {
                (entry.Tracked as IDisposable)?.Dispose();
            }
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
            bool found = TryGetValue(key, out T stored);
            value = stored;
            return found;
        }

        /// <summary>Retrieve a value type or reference type without converting stored values.</summary>
        public bool TryGetValue<T>(string key, out T value)
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

        public T Get<T>(string key) where T : class
        {
            return TryGet(key, out T value) ? value : default(T);
        }

        public T GetValue<T>(string key)
        {
            return TryGetValue(key, out T value) ? value : default(T);
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
                value = found ? DescribeValue(value, MaxInspectionTextLength, true) : null
            };
        }
        private const int MaxCollectionElements = 16;
        private const int MaxCollectionElementTextLength = 64;
        private const long MaxJsonSafeInteger = 9007199254740991L;

        internal static object DescribeValue(object value, int maxStringLength, bool includeCollections)
        {
            if (ReferenceEquals(value, null))
                return new { kind = "null" };

            Type type = value.GetType();
            if (type == typeof(string))
            {
                string text = BoundedText((string)value, maxStringLength, out bool truncated);
                return new { kind = "scalar", value = text, truncated };
            }
            if (type == typeof(bool))
                return new { kind = "boolean", value = (bool)value };
            if (type == typeof(char))
                return new { kind = "character", value = ((char)value).ToString() };
            if (IsInteger(type))
                return DescribeInteger(value, type);
            if (type == typeof(float))
                return DescribeFloatingPoint((float)value);
            if (type == typeof(double))
                return DescribeFloatingPoint((double)value);
            if (type == typeof(decimal))
                return new { kind = "decimal", value = ((decimal)value).ToString(System.Globalization.CultureInfo.InvariantCulture), representation = "decimalString" };
            if (type.IsEnum)
                return new
                {
                    kind = "enum",
                    name = Enum.GetName(type, value) ?? value.ToString(),
                    numericValue = EnumNumericString(value, type),
                    representation = "decimalString"
                };

            if (value is Vector2 vector2)
                return new { kind = "unityValue", value = new { x = SafeFloat(vector2.x), y = SafeFloat(vector2.y) }, nonFiniteComponentsAreStrings = true };
            if (value is Vector3 vector3)
                return new { kind = "unityValue", value = new { x = SafeFloat(vector3.x), y = SafeFloat(vector3.y), z = SafeFloat(vector3.z) }, nonFiniteComponentsAreStrings = true };
            if (value is Vector4 vector4)
                return new { kind = "unityValue", value = new { x = SafeFloat(vector4.x), y = SafeFloat(vector4.y), z = SafeFloat(vector4.z), w = SafeFloat(vector4.w) }, nonFiniteComponentsAreStrings = true };
            if (value is Quaternion quaternion)
                return new { kind = "unityValue", value = new { x = SafeFloat(quaternion.x), y = SafeFloat(quaternion.y), z = SafeFloat(quaternion.z), w = SafeFloat(quaternion.w) }, nonFiniteComponentsAreStrings = true };
            if (value is Color color)
                return new { kind = "unityValue", value = new { r = SafeFloat(color.r), g = SafeFloat(color.g), b = SafeFloat(color.b), a = SafeFloat(color.a) }, nonFiniteComponentsAreStrings = true };

            UnityEngine.Object unityObject = value as UnityEngine.Object;
            if (!ReferenceEquals(unityObject, null))
            {
                bool destroyed = unityObject == null;
                bool nameTruncated = false;
                string name = destroyed ? null : BoundedText(unityObject.name, maxStringLength, out nameTruncated);
                int? instanceId = destroyed ? (int?)null : unityObject.GetInstanceID();
                return new { kind = "unityObject", name, instanceId, destroyed, nameTruncated };
            }

            if (includeCollections && TryDescribeCollection(value, maxStringLength, out object collection))
                return collection;
            return new { kind = "opaque" };
        }

        private static bool IsInteger(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);
        }

        private static object DescribeInteger(object value, Type type)
        {
            if (type == typeof(long))
            {
                long number = (long)value;
                if (number < -MaxJsonSafeInteger || number > MaxJsonSafeInteger)
                    return new { kind = "integer", value = number.ToString(System.Globalization.CultureInfo.InvariantCulture), representation = "decimalString" };
            }
            else if (type == typeof(ulong))
            {
                ulong number = (ulong)value;
                if (number > (ulong)MaxJsonSafeInteger)
                    return new { kind = "integer", value = number.ToString(System.Globalization.CultureInfo.InvariantCulture), representation = "decimalString" };
            }
            return new { kind = "integer", value };
        }

        private static object DescribeFloatingPoint(float value)
        {
            if (float.IsNaN(value)) return new { kind = "number", value = "NaN", representation = "string" };
            if (float.IsPositiveInfinity(value)) return new { kind = "number", value = "+Infinity", representation = "string" };
            if (float.IsNegativeInfinity(value)) return new { kind = "number", value = "-Infinity", representation = "string" };
            return new { kind = "number", value };
        }

        private static object DescribeFloatingPoint(double value)
        {
            if (double.IsNaN(value)) return new { kind = "number", value = "NaN", representation = "string" };
            if (double.IsPositiveInfinity(value)) return new { kind = "number", value = "+Infinity", representation = "string" };
            if (double.IsNegativeInfinity(value)) return new { kind = "number", value = "-Infinity", representation = "string" };
            return new { kind = "number", value };
        }

        private static object SafeFloat(float value)
        {
            if (float.IsNaN(value)) return "NaN";
            if (float.IsPositiveInfinity(value)) return "+Infinity";
            if (float.IsNegativeInfinity(value)) return "-Infinity";
            return value;
        }

        private static string EnumNumericString(object value, Type enumType)
        {
            Type underlying = Enum.GetUnderlyingType(enumType);
            if (underlying == typeof(ulong) || underlying == typeof(uint) || underlying == typeof(ushort) || underlying == typeof(byte))
                return Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryDescribeCollection(object value, int maxStringLength, out object description)
        {
            Type type = value.GetType();
            Type elementType;
            Array array = null;
            IList list = null;
            int count;
            if (type.IsArray && type.GetArrayRank() == 1)
            {
                array = (Array)value;
                elementType = type.GetElementType();
                if (type != elementType.MakeArrayType())
                {
                    description = null;
                    return false;
                }
                count = array.Length;
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                elementType = type.GetGenericArguments()[0];
                list = (IList)value;
                count = ((ICollection)list).Count;
            }
            else
            {
                description = null;
                return false;
            }

            if (!IsSafeCollectionElement(elementType))
            {
                description = null;
                return false;
            }

            int included = Math.Min(count, MaxCollectionElements);
            var elements = new List<object>(included);
            int elementTextLength = Math.Min(maxStringLength, MaxCollectionElementTextLength);
            for (int index = 0; index < included; index++)
            {
                object element = array != null ? array.GetValue(index) : list[index];
                elements.Add(DescribeValue(element, elementTextLength, false));
            }
            bool typeTruncated = false;
            string elementTypeName = BoundedText(elementType.FullName, MaxTypeNameLength, out typeTruncated);
            description = new
            {
                kind = "collection",
                elementType = elementTypeName,
                elementTypeTruncated = typeTruncated,
                count,
                elements,
                truncated = included < count
            };
            return true;
        }

        private static bool IsSafeCollectionElement(Type type)
        {
            return type == typeof(string) || type == typeof(bool) || type == typeof(char) || IsInteger(type) ||
                type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type.IsEnum ||
                type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) ||
                type == typeof(Quaternion) || type == typeof(Color);
        }


        public bool Remove(string key)
        {
            EnsureMainThread();
            ValidateKey(key);
            return values.Remove(key);
        }

        /// <summary>Keys, runtime types, safe bounded value summaries, and recent coroutine failures.</summary>
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
            // Failures are always listed in full, so their estimated size comes off the budget first.
            CoroutineFailure[] failures = recentFailures.ToArray();
            foreach (CoroutineFailure failure in failures)
                remaining -= EstimateListEntryLength(failure.key, failure.routine) + JsonStringLength(failure.error);
            bool coroutinesTruncated = false;
            bool objectsTruncated = false;
            string coroutineType = typeof(Coroutine).FullName;
            int maxEntries = Math.Max(coroutineKeys.Count, objectKeys.Count);
            for (int index = 0; index < maxEntries; index++)
            {
                if (index < coroutineKeys.Count)
                {
                    string key = coroutineKeys[index];
                    ManagedCoroutine entry = coroutines[key];
                    int size = EstimateListEntryLength(key, coroutineType) + JsonStringLength(entry.Routine) + 96;
                    if (size <= remaining)
                    {
                        describedCoroutines.Add(new
                        {
                            key,
                            type = coroutineType,
                            routine = entry.Routine,
                            startedAtUtc = entry.StartedUtc.ToString("o"),
                            steps = entry.Steps
                        });
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
                        describedObjects.Add(new { key, type, typeTruncated, value = DescribeValue(value, MaxListTextLength, false) });
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
                recentFailures = failures,
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

        internal static string BoundedText(string value, int maxLength, out bool truncated)
        {
            truncated = value.Length > maxLength;
            if (!truncated) return value;
            int length = maxLength - TruncationMarker.Length;
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length) + TruncationMarker;
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
                recentFailures.Clear();
            }
        }

        private IEnumerator Track(string key, ManagedCoroutine entry, IEnumerator routine)
        {
            return new ManagedRoutine(this, key, entry, routine);
        }

        private void RecordCoroutineFailure(string key, ManagedCoroutine entry, Exception exception)
        {
            recentFailures.Enqueue(new CoroutineFailure
            {
                key = key,
                routine = entry.Routine,
                error = BoundedText(exception.GetType().FullName + ": " + exception.Message, 256, out _),
                steps = entry.Steps,
                startedAtUtc = entry.StartedUtc.ToString("o"),
                failedAtUtc = DateTime.UtcNow.ToString("o")
            });
            while (recentFailures.Count > MaxRecentFailures)
                recentFailures.Dequeue();
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
