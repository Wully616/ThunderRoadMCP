using System.Collections;
using System.Reflection;
using System.Security.Cryptography;

public static class ApiIndexAdmissionCheck
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    public static async Task RunAsync(Action<bool, string> assert, ApiReuseContext context)
    {
        var workRoot = Path.Combine(context.WorkDirectory, "api-index-admission-" + Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(workRoot, "cache");
        var previousCacheRoot = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR");
        var previousDiagnostics = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_DIAGNOSTICS");
        var timingType = typeof(InstalledApiCatalog).Assembly.GetType("ToolTiming", throwOnError: true)!;
        var leases = new List<IDisposable>();
        var snapshots = new List<IDisposable>();

        try
        {
            Directory.CreateDirectory(cacheRoot);
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR", cacheRoot);
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_DIAGNOSTICS", "1");

            var fixtureGame = Path.Combine(context.FixtureOutputDirectory, "game", "Fixture.Shared.dll");
            var fixtureChanged = Path.Combine(context.FixtureOutputDirectory, "changed", "Fixture.Shared.dll");
            var fixtureBase = Path.Combine(context.FixtureOutputDirectory, "game", "Fixture.Base.dll");
            var probeAPath = CopyFixture(fixtureGame, Path.Combine(workRoot, "probe", "a"));
            var probeBPath = CopyFixture(fixtureChanged, Path.Combine(workRoot, "probe", "b"));
            var probeRetryPath = CopyFixture(fixtureBase, Path.Combine(workRoot, "probe", "retry"));
            var probeA = CreateEntry(Guid.NewGuid().ToString("N"), fixtureGame, probeAPath, "game");
            var probeB = CreateEntry(Guid.NewGuid().ToString("N"), fixtureChanged, probeBPath, "mod");
            var probeRetry = CreateEntry(Guid.NewGuid().ToString("N"), fixtureBase, probeRetryPath, "game");
            var probeSnapshot = CreateSnapshot(probeA, probeB, probeRetry);
            snapshots.Add((IDisposable)probeSnapshot);
            long estimateA;
            long estimateB;
            long estimateRetry;

            using (BeginTimingInvocation(timingType))
            {
                var probeCatalog = new InstalledApiCatalog();
                var leaseA = await AcquireIndexAsync(probeCatalog, probeSnapshot, probeA, CancellationToken.None).ConfigureAwait(false);
                try { estimateA = EstimatedBytes(leaseA); }
                finally { DisposeLease(leaseA); }
                var leaseB = await AcquireIndexAsync(probeCatalog, probeSnapshot, probeB, CancellationToken.None).ConfigureAwait(false);
                try { estimateB = EstimatedBytes(leaseB); }
                finally { DisposeLease(leaseB); }
                var leaseRetry = await AcquireIndexAsync(probeCatalog, probeSnapshot, probeRetry, CancellationToken.None).ConfigureAwait(false);
                try { estimateRetry = EstimatedBytes(leaseRetry); }
                finally { DisposeLease(leaseRetry); }
            }

            var budget = Math.Max(estimateA, Math.Max(estimateB, estimateRetry));
            Check(assert, estimateA > 0 && estimateB > 0 && estimateRetry > 0,
                "fixture API index estimates are positive");
            Check(assert, estimateA + estimateB > budget,
                "injected API-index budget fits either fixture index but not both together");

            var actualAPath = CopyFixture(fixtureGame, Path.Combine(workRoot, "build", "a"));
            var actualBPath = CopyFixture(fixtureChanged, Path.Combine(workRoot, "build", "b"));
            var retryRoot = Path.Combine(workRoot, "build", "retry");
            Directory.CreateDirectory(retryRoot);
            var retryPath = Path.Combine(retryRoot, Path.GetFileName(fixtureBase));
            var actualA = CreateEntry(Guid.NewGuid().ToString("N"), fixtureGame, actualAPath, "game");
            var actualB = CreateEntry(Guid.NewGuid().ToString("N"), fixtureChanged, actualBPath, "mod");
            var retryEntry = CreateEntry(Guid.NewGuid().ToString("N"), fixtureBase, retryPath, "game");
            var snapshot = CreateSnapshot(actualA, actualB, retryEntry);
            snapshots.Add((IDisposable)snapshot);
            var catalog = CreateCatalog(budget);

            using (BeginTimingInvocation(timingType))
            {
                var slots = (SemaphoreSlim)GetField(catalog, "_apiIndexBuildSlots")!;
                var heldSlots = 0;
                var slotsHeld = true;
                try
                {
                    while (slots.Wait(0))
                        heldSlots++;
                    Check(assert, heldSlots == 1, "API index admission has exactly one build slot");

                    var identityA = ApiIndexIdentity(actualA);
                    var identityB = ApiIndexIdentity(actualB);
                    using var cancelled = new CancellationTokenSource();
                    var cancelledWaiter = AcquireIndexAsync(catalog, snapshot, actualA, cancelled.Token);
                    var waiterA = AcquireIndexAsync(catalog, snapshot, actualA, CancellationToken.None);
                    var waiterA2 = AcquireIndexAsync(catalog, snapshot, actualA, CancellationToken.None);
                    var waiterB = AcquireIndexAsync(catalog, snapshot, actualB, CancellationToken.None);

                    var builds = GetDictionary(catalog, "_apiIndexBuilds");
                    var pending = GetDictionary(catalog, "_apiIndexPendingWaiters");
                    Check(assert, builds.Count == 2 && builds.Contains(identityA) && builds.Contains(identityB),
                        "two distinct index builds are queued while same-identity requests share one flight");
                    Check(assert, Convert.ToInt32(pending[identityA]) == 3,
                        "same-content requests join the shared build before it starts");

                    cancelled.Cancel();
                    var cancellationObserved = false;
                    try { await cancelledWaiter.ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancellationObserved = true; }
                    Check(assert, cancellationObserved && builds.Contains(identityA) && Convert.ToInt32(pending[identityA]) == 2,
                        "canceling one waiter leaves the shared build and remaining waiters intact");

                    slots.Release(heldSlots);
                    slotsHeld = false;

                    var firstTask = await Task.WhenAny(waiterA, waiterB).ConfigureAwait(false);
                    var firstLease = await firstTask.ConfigureAwait(false);
                    leases.Add((IDisposable)firstLease);
                    object? leaseA = null;
                    object? leaseA2 = null;
                    object? leaseB = null;

                    if (ReferenceEquals(firstTask, waiterA))
                    {
                        leaseA = firstLease;
                        leaseA2 = await waiterA2.ConfigureAwait(false);
                        leases.Add((IDisposable)leaseA2);
                        Check(assert, ReferenceEquals(Index(leaseA), Index(leaseA2)),
                            "same-content waiters receive the identical built index");
                        Check(assert, !waiterB.IsCompleted,
                            "the second distinct build waits while the first index lease is held");
                        AssertHeldLease(assert, catalog, actualA, leaseA, 2, budget, timingType,
                            "first index remains accounted while its coalesced leases are active");
                        DisposeLease(leaseA);
                        DisposeLease(leaseA2);
                        leaseB = await waiterB.ConfigureAwait(false);
                        leases.Add((IDisposable)leaseB);
                    }
                    else
                    {
                        leaseB = firstLease;
                        Check(assert, !waiterA.IsCompleted && !waiterA2.IsCompleted,
                            "the second distinct build waits while the first index lease is held");
                        AssertHeldLease(assert, catalog, actualB, leaseB, 1, budget, timingType,
                            "first index remains accounted while its lease is active");
                        DisposeLease(leaseB);
                        leaseA = await waiterA.ConfigureAwait(false);
                        leases.Add((IDisposable)leaseA);
                        leaseA2 = await waiterA2.ConfigureAwait(false);
                        leases.Add((IDisposable)leaseA2);
                        Check(assert, ReferenceEquals(Index(leaseA), Index(leaseA2)),
                            "same-content waiters receive the identical built index");
                        DisposeLease(leaseA2);
                    }

                    var indexA = Index(leaseA!);
                    var indexB = Index(leaseB!);
                    Check(assert, !ReferenceEquals(indexA, indexB) && EstimatedBytes(leaseA!) == estimateA &&
                        EstimatedBytes(leaseB!) == estimateB,
                        "both distinct fixture indexes finish with their measured estimates");
                    AssertWithinBudget(assert, catalog, budget, timingType,
                        "concurrent fixture builds keep estimated memory and gauge within budget");

                    var heldLease = !IsLeaseDisposed(leaseA!) ? leaseA! : leaseB!;
                    var heldEntry = ReferenceEquals(heldLease, leaseA) ? actualA : actualB;
                    if (ReferenceEquals(heldLease, leaseA))
                        DisposeLease(leaseB!);
                    else
                        DisposeLease(leaseA!);
                    AssertHeldLease(assert, catalog, heldEntry, heldLease, 1, budget, timingType,
                        "a held index lease remains alive after the other fixture is released");

                    var usedBytes = ReadLongField(catalog, "_apiIndexMemoryBytes") +
                        ReadLongField(catalog, "_apiIndexBuildReservedBytes");
                    var waitingIdentity = "admission-check-" + Guid.NewGuid().ToString("N");
                    var waitingBytes = budget - usedBytes + 1;
                    var waitingReservation = ReserveIndexBytesAsync(catalog, waitingIdentity, waitingBytes);
                    Check(assert, !waitingReservation.IsCompleted,
                        "an unrelated reservation waits instead of reclaiming a held lease");
                    AssertHeldLease(assert, catalog, heldEntry, heldLease, 1, budget, timingType,
                        "memory pressure does not release the object behind an active lease");

                    DisposeLease(heldLease);
                    await waitingReservation.ConfigureAwait(false);
                    AssertWithinBudget(assert, catalog, budget, timingType,
                        "released lease bytes admit the waiting reservation within budget");
                    ReleaseIndexReservation(catalog, waitingIdentity);
                    AssertWithinBudget(assert, catalog, budget, timingType,
                        "reservation cleanup restores the bounded cache gauge");
                }
                finally
                {
                    if (slotsHeld && heldSlots > 0)
                        slots.Release(heldSlots);
                }

                var retryIdentity = ApiIndexIdentity(retryEntry);
                var failed = false;
                try
                {
                    var unexpectedLease = await AcquireIndexAsync(catalog, snapshot, retryEntry, CancellationToken.None)
                        .ConfigureAwait(false);
                    leases.Add((IDisposable)unexpectedLease);
                    DisposeLease(unexpectedLease);
                }
                catch (IOException) { failed = true; }
                Check(assert, failed, "a missing fixture path fails its index build");
                Check(assert, !GetDictionary(catalog, "_apiIndexBuilds").Contains(retryIdentity) &&
                    !GetDictionary(catalog, "_apiIndexBuildReservations").Contains(retryIdentity),
                    "failed index build removes its flight and reservation");
                AssertWithinBudget(assert, catalog, budget, timingType,
                    "failed build cleanup leaves memory and gauge within budget");

                File.Copy(fixtureBase, retryPath);
                var retryLease = await AcquireIndexAsync(catalog, snapshot, retryEntry, CancellationToken.None)
                    .ConfigureAwait(false);
                leases.Add((IDisposable)retryLease);
                Check(assert, EstimatedBytes(retryLease) == estimateRetry,
                    "restoring the fixture path permits retry of the same index identity");
                AssertWithinBudget(assert, catalog, budget, timingType,
                    "successful retry remains within the injected budget");
                DisposeLease(retryLease);
            }
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
            for (var i = snapshots.Count - 1; i >= 0; i--)
                snapshots[i].Dispose();
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_CACHE_DIR", previousCacheRoot);
            Environment.SetEnvironmentVariable("THUNDERROAD_MCP_DIAGNOSTICS", previousDiagnostics);
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, recursive: true);
        }
    }

    private static string CopyFixture(string sourcePath, string directory)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destination);
        var sourceDocumentation = Path.ChangeExtension(sourcePath, ".xml");
        if (File.Exists(sourceDocumentation))
            File.Copy(sourceDocumentation, Path.ChangeExtension(destination, ".xml"));
        return Path.GetFullPath(destination);
    }

    private static object CreateEntry(string id, string sourcePath, string fullPath, string origin)
    {
        var assemblyName = AssemblyName.GetAssemblyName(sourcePath);
        var root = Path.GetDirectoryName(fullPath)!;
        var documentationPath = Path.ChangeExtension(fullPath, ".xml");
        var hasDocumentation = origin == "game" && File.Exists(documentationPath);
        var constructor = typeof(InstalledApiCatalog).GetNestedType("AssemblyEntry", BindingFlags.NonPublic)!
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 14);
        var arguments = new object?[]
        {
            id,
            assemblyName.Name!,
            assemblyName.Version?.ToString() ?? "0.0.0.0",
            "00000000-0000-0000-0000-000000000001",
            origin,
            root,
            Path.GetFileName(fullPath),
            fullPath,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))),
            hasDocumentation ? documentationPath : null,
            hasDocumentation ? "available" : "missing",
            null,
            hasDocumentation ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(documentationPath))) : null,
            null,
        };
        return constructor.Invoke(arguments);
    }

    private static object CreateSnapshot(params object[] entries)
    {
        var entryType = typeof(InstalledApiCatalog).GetNestedType("AssemblyEntry", BindingFlags.NonPublic)!;
        var assemblyArray = Array.CreateInstance(entryType, entries.Length);
        for (var i = 0; i < entries.Length; i++)
            assemblyArray.SetValue(entries[i], i);
        var snapshotType = typeof(InstalledApiCatalog).GetNestedType("CatalogSnapshot", BindingFlags.NonPublic)!;
        var lifetimeType = typeof(InstalledApiCatalog).GetNestedType("SnapshotBufferLifetime", BindingFlags.NonPublic)!;
        var lifetimeConstructor = lifetimeType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 1);
        var lifetime = lifetimeConstructor.Invoke([(Action)(static () => { })]);
        var constructor = snapshotType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 5);
        return constructor.Invoke(["admission-check-" + Guid.NewGuid().ToString("N"), assemblyArray,
            new List<string>(), false, lifetime]);
    }

    private static object CreateCatalog(long budget)
    {
        var constructor = typeof(InstalledApiCatalog).GetConstructor(PrivateInstance, null, [typeof(long)], null)!;
        return constructor.Invoke([budget]);
    }

    private static IDisposable BeginTimingInvocation(Type timingType) =>
        (IDisposable)timingType.GetMethod("BeginInvocation", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;

    private static async Task<object> AcquireIndexAsync(object catalog, object snapshot, object entry, CancellationToken cancellationToken)
    {
        var method = typeof(InstalledApiCatalog).GetMethod("GetApiIndexAsync", PrivateInstance)!;
        var task = (Task)method.Invoke(catalog, [snapshot, entry, cancellationToken])!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static Task ReserveIndexBytesAsync(object catalog, string identity, long bytes)
    {
        var method = typeof(InstalledApiCatalog).GetMethod("ReserveApiIndexBuildBytesAsync", PrivateInstance)!;
        return (Task)method.Invoke(catalog, [identity, bytes])!;
    }

    private static void ReleaseIndexReservation(object catalog, string identity) =>
        typeof(InstalledApiCatalog).GetMethod("ReleaseApiIndexBuildReservation", PrivateInstance)!.Invoke(catalog, [identity]);

    private static string ApiIndexIdentity(object entry) =>
        (string)typeof(InstalledApiCatalog).GetMethod("ApiIndexIdentity", PrivateStatic)!.Invoke(null, [entry])!;

    private static object Index(object lease) =>
        lease.GetType().GetProperty("Index", BindingFlags.Instance | BindingFlags.Public)!.GetValue(lease)!;

    private static long EstimatedBytes(object lease) =>
        (long)Index(lease).GetType().GetProperty("EstimatedBytes", BindingFlags.Instance | BindingFlags.Public)!.GetValue(Index(lease))!;

    private static void DisposeLease(object lease) => ((IDisposable)lease).Dispose();

    private static bool IsLeaseDisposed(object lease) =>
        lease.GetType().GetField("_release", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease) is null;

    private static object? GetField(object instance, string name) =>
        instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance);

    private static IDictionary GetDictionary(object instance, string name) => (IDictionary)GetField(instance, name)!;

    private static long ReadLongField(object instance, string name) => (long)GetField(instance, name)!;

    private static object? GetProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance);

    private static void AssertHeldLease(Action<bool, string> assert, object catalog, object entry, object lease,
        int expectedReaders, long budget, Type timingType, string scenario)
    {
        var identity = ApiIndexIdentity(entry);
        var index = Index(lease);
        var gate = GetField(catalog, "_apiCacheGate")!;
        lock (gate)
        {
            var lifetimes = GetDictionary(catalog, "_apiIndexLifetimes");
            var lifetime = lifetimes[identity];
            Check(assert, lifetime is not null && ReferenceEquals(GetProperty(lifetime!, "Index"), index) &&
                Convert.ToInt32(GetProperty(lifetime!, "ActiveReaders")) == expectedReaders && !IsLeaseDisposed(lease), scenario);
            Check(assert, ReadLongField(catalog, "_apiIndexMemoryBytes") >= EstimatedBytes(lease),
                "active API index lease remains included in estimated memory");
        }
        AssertWithinBudget(assert, catalog, budget, timingType, scenario + " (budget)");
    }

    private static void AssertWithinBudget(Action<bool, string> assert, object catalog, long budget,
        Type timingType, string scenario)
    {
        var gate = GetField(catalog, "_apiCacheGate")!;
        lock (gate)
        {
            var total = ReadLongField(catalog, "_apiIndexMemoryBytes") +
                ReadLongField(catalog, "_apiIndexBuildReservedBytes");
            var gauge = ReadGauge(timingType);
            Check(assert, total <= budget, scenario + ": estimated cache bytes and in-flight reservations fit the budget");
            Check(assert, gauge is >= 0 && gauge <= budget && gauge == total,
                scenario + ": api.index.memoryBytes gauge matches the bounded estimate");
        }
    }

    private static long ReadGauge(Type timingType)
    {
        var snapshot = (IDictionary<string, object>)timingType.GetMethod("Snapshot", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
        var gauges = (IDictionary<string, long>)snapshot["gauges"];
        return gauges.TryGetValue("api.index.memoryBytes", out var value) ? value : -1;
    }

    private static void Check(Action<bool, string> assert, bool condition, string scenario)
    {
        assert(condition, scenario);
        if (!condition)
            throw new InvalidOperationException("API index admission check failed: " + scenario);
    }
}
