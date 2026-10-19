using System.Diagnostics;
using System.Text.Json;
using System.Threading;

internal static class ToolTiming
{
    private const double MaxMilliseconds = 60_000;
    private const long MaxGaugeBytes = 1L << 30;
    private static readonly AsyncLocal<TimingState?> Current = new();
    private static readonly IDisposable Noop = new NoopScope();
    private static readonly HashSet<string> TimingNames = new(StringComparer.Ordinal)
    {
        "hostTotalMs", "hostHandlerMs", "responseConstructionMs", "installationResolutionMs",
        "bridgeRoundTripMs", "bridgeRequestTotalMs", "bridgeConfigMs", "bridgeConnectMs",
        "bridgeWriteMs", "bridgeReadMs", "bridgeResponseParseMs",
        "compilerQueueWaitMs", "referenceInitializationMs", "compilationMs", "parseMs",
        "emitMs", "assemblyLoadMs", "mainThreadQueueWaitMs", "invocationMs", "serializationMs",
        "apiIndexBuildMs", "apiIndexPersistentLoadMs", "apiInventoryEnumerationMs", "apiFileValidationMs",
        "apiSearchMs", "apiLookupRankingMs", "apiTypeExpansionMs",
        "decompileMs", "apiLookupMs", "apiScanMs", "catalogReadMs", "catalogSearchMs",
        "catalogArchiveOpenMs", "catalogDecompressMs", "catalogParseMs", "catalogArchiveHashMs",
        "catalogIndexBuildMs", "catalogPersistentLoadMs", "catalogPersistentWriteMs",
    };
    private static readonly HashSet<string> CounterNames = new(StringComparer.Ordinal)
    {
        "host.textBytes", "host.responseContentBlocks",
        "bridge.requests", "bridge.requestBytes", "bridge.responseBytes",
        "api.scan.bytesHashed", "api.scan.filesConsidered", "api.index.memoryHit", "api.index.diskHit", "api.index.miss",
        "api.index.build", "api.index.persistFailure", "api.index.eviction",
        "api.index.buildFailure",
        "api.index.nonPublicOmitted", "api.index.tokenlessOmitted", "api.index.nonPublicTokenFallback",
        "api.index.tokenlessPublic", "api.index.tokenlessGenerated", "api.index.syntheticSkipped",
        "api.index.typeMapHit", "api.index.typeMapMiss", "api.index.typeMapWrite", "api.index.typeMapSkip",
        "api.index.directoryHit", "api.index.directoryMiss", "api.index.directoryWrite", "api.index.directorySkip",
        "api.index.limitReached",
        "api.search.nonPublicFallback", "api.type.onDemandMembers",
        "api.search.overflowFallback", "api.type.overflowFallback",
        "api.lookup.resultsConsidered", "api.lookup.resultsReturned", "api.lookup.relatedConsidered",
        "api.lookup.truncated", "api.source.cacheHit", "api.source.cacheMiss", "api.source.decompileBuild",
        "catalogArchiveBytesHashed", "catalogCacheHit", "catalogCacheMiss", "catalogPersistentHit",
        "catalogJsonParseCount", "catalogSearchFallbackParses", "catalogRecordsConsidered",
        "catalogResultsReturned", "catalogCacheCorrupt", "catalogUncachedStringRecords",
        "catalogIndexBuildCount",
    };
    private static readonly HashSet<string> GaugeNames = new(StringComparer.Ordinal)
    {
        "api.index.memoryBytes", "api.index.diskBytes", "api.source.memoryBytes",
        "catalog.index.memoryBytes", "catalog.index.diskBytes",
    };
    private static readonly bool diagnosticsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("THUNDERROAD_MCP_DIAGNOSTICS"), "1", StringComparison.Ordinal);

    public static bool Enabled => diagnosticsEnabled;

    public static IDisposable BeginInvocation()
    {
        if (!diagnosticsEnabled)
            return Noop;

        var previous = Current.Value;
        var state = new TimingState(Guid.NewGuid().ToString("N"));
        Current.Value = state;
        return new InvocationScope(state, previous);
    }

    public static IDisposable Measure(string stageName)
    {
        if (!diagnosticsEnabled || !TimingNames.Contains(stageName) || Current.Value is not { } state)
            return Noop;
        return new MeasurementScope(state, stageName, Stopwatch.GetTimestamp());
    }

    public static void Add(string stageName, double milliseconds)
    {
        if (diagnosticsEnabled && double.IsFinite(milliseconds) && milliseconds >= 0 &&
            milliseconds <= MaxMilliseconds && TimingNames.Contains(stageName) && Current.Value is { } state)
            state.Add(stageName, milliseconds);
    }

    public static void Count(string counterName, long amount = 1)
    {
        if (diagnosticsEnabled && amount > 0 && CounterNames.Contains(counterName) &&
            Current.Value is { } state)
            state.Count(counterName, amount);
    }

    public static void Gauge(string gaugeName, long value)
    {
        if (diagnosticsEnabled && value is >= 0 and <= MaxGaugeBytes &&
            GaugeNames.Contains(gaugeName) && Current.Value is { } state)
            state.Gauge(gaugeName, value);
    }

    public static void SetBridgeRequestId(string requestId)
    {
        if (diagnosticsEnabled && requestId.Length <= 64 && Current.Value is { } state)
            state.SetBridgeRequestId(requestId);
    }

    public static Dictionary<string, object> Snapshot() =>
        Current.Value?.Snapshot() ?? new Dictionary<string, object>(StringComparer.Ordinal);

    public static void WriteDiagnostics()
    {
        if (diagnosticsEnabled && Current.Value is { } state)
            Console.Error.WriteLine(JsonSerializer.Serialize(state.Snapshot()));
    }

    private sealed class TimingState(string requestId)
    {
        private readonly object gate = new();
        private readonly Dictionary<string, double> timings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> gauges = new(StringComparer.Ordinal);
        private string? bridgeRequestId;

        public void Add(string name, double milliseconds)
        {
            lock (gate)
                timings[name] = Math.Min(MaxMilliseconds, timings.GetValueOrDefault(name) + milliseconds);
        }

        public void Count(string name, long amount)
        {
            lock (gate)
            {
                var current = counts.GetValueOrDefault(name);
                counts[name] = amount > long.MaxValue - current ? long.MaxValue : current + amount;
            }
        }

        public void Gauge(string name, long value)
        {
            lock (gate)
                gauges[name] = value;
        }

        public void SetBridgeRequestId(string id)
        {
            lock (gate)
                bridgeRequestId = id;
        }

        public Dictionary<string, object> Snapshot()
        {
            lock (gate)
            {
                var snapshot = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["requestId"] = requestId,
                    ["timings"] = new Dictionary<string, double>(timings, StringComparer.Ordinal),
                    ["counts"] = new Dictionary<string, long>(counts, StringComparer.Ordinal),
                    ["gauges"] = new Dictionary<string, long>(gauges, StringComparer.Ordinal),
                };
                if (bridgeRequestId is not null)
                    snapshot["bridgeRequestId"] = bridgeRequestId;
                return snapshot;
            }
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
