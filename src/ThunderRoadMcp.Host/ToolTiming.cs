using System.Diagnostics;
using System.Threading;

internal static class ToolTiming
{
    private static readonly AsyncLocal<TimingState?> Current = new();
    private static readonly IDisposable Noop = new NoopScope();

    public static IDisposable BeginInvocation()
    {
        var previous = Current.Value;
        var state = new TimingState();
        Current.Value = state;
        return new InvocationScope(state, previous);
    }

    public static IDisposable Measure(string stageName)
    {
        var state = Current.Value;
        return state is null ? Noop : new MeasurementScope(state, stageName, Stopwatch.GetTimestamp());
    }

    public static void Add(string stageName, double milliseconds) => Current.Value?.Add(stageName, milliseconds);

    public static Dictionary<string, double> Snapshot() => Current.Value?.Snapshot() ?? new Dictionary<string, double>();

    private sealed class TimingState
    {
        private readonly object gate = new();
        private readonly Dictionary<string, double> timings = new(StringComparer.Ordinal);

        public void Add(string name, double milliseconds)
        {
            lock (gate)
                timings[name] = timings.GetValueOrDefault(name) + milliseconds;
        }

        public Dictionary<string, double> Snapshot()
        {
            lock (gate)
                return new Dictionary<string, double>(timings, StringComparer.Ordinal);
        }
    }

    private sealed class InvocationScope(TimingState state, TimingState? previous) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, state))
                Current.Value = previous;
        }
    }

    private sealed class MeasurementScope(TimingState state, string stageName, long started) : IDisposable
    {
        public void Dispose() => state.Add(stageName, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private sealed class NoopScope : IDisposable
    {
        public void Dispose() { }
    }
}
