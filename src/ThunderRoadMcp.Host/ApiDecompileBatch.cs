using System.Text.Json;

public sealed partial class InstalledApiCatalog
{
    private const int MaxBatchItems = 20;
    private const int MaxBatchResponseChars = 64000;

    public async Task<string> DecompileBatchAsync(ApiDecompileBatchItem[] items, string? snapshotId, int maxChars,
        CancellationToken cancellationToken)
    {
        if (items is null || items.Length is < 1 or > MaxBatchItems)
            throw new ArgumentException($"items must contain 1 to {MaxBatchItems} exact symbol requests.", nameof(items));
        if (maxChars is < 2000 or > MaxBatchResponseChars)
            throw new ArgumentOutOfRangeException(nameof(maxChars), $"maxChars must be between 2000 and {MaxBatchResponseChars}.");
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.AssemblyId) || string.IsNullOrWhiteSpace(item.SymbolId) ||
                item.AssemblyId.Length > 512 || item.SymbolId.Length > 512 || item.StartLine < 1 ||
                item.LineCount is < 1 or > 500)
                throw new ArgumentException("Each item requires bounded exact IDs, startLine >= 1, and lineCount 1..500.", nameof(items));
        }

        using var snapshot = await ScanAsync(cancellationToken);
        CheckSnapshot(snapshot, snapshotId);
        static object ItemArgument(ApiDecompileBatchItem item) => new
        {
            assemblyId = item.AssemblyId,
            symbolId = item.SymbolId,
            startLine = item.StartLine,
            lineCount = item.LineCount,
        };
        object[] itemArguments = items.Select(ItemArgument).ToArray();
        ApiDiscoveryException BudgetError() => new("batch.budget_too_small",
            "maxChars cannot fit a complete batch response or continuation; increase maxChars.",
            new { tool = "api_decompile_batch", arguments = new { items = itemArguments, snapshotId = snapshot.Id, maxChars = Math.Min(MaxBatchResponseChars, maxChars * 2) } });
        if (JsonSerializer.Serialize(new { snapshotId = snapshot.Id, partial = true, truncated = true, results = Array.Empty<object>(), nextItems = itemArguments }).Length > maxChars)
            throw BudgetError();

        var results = new List<object>();
        var nextItems = new List<ApiDecompileBatchItem>();

        bool Fits(object result, ApiDecompileBatchItem? sourceContinuation, IEnumerable<ApiDecompileBatchItem> remaining)
        {
            var continuation = new List<ApiDecompileBatchItem>(nextItems);
            if (sourceContinuation is not null)
                continuation.Add(sourceContinuation);
            continuation.AddRange(remaining);
            var serializedContinuations = continuation.Select(item => (object)new
            {
                assemblyId = item.AssemblyId,
                symbolId = item.SymbolId,
                startLine = item.StartLine,
                lineCount = item.LineCount,
            }).ToArray();
            return JsonSerializer.Serialize(new
            {
                snapshotId = snapshot.Id,
                partial = continuation.Count > 0,
                truncated = continuation.Count > 0,
                results = results.Append(result),
                nextItems = serializedContinuations,
            }).Length <= maxChars;
        }
        ApiDiscoveryException SplitBudgetError(ApiDecompileBatchItem item, int index) => new(
            "batch.split_required",
            "The source and remaining request IDs cannot fit together; split the batch into the returned requests.",
            new
            {
                firstRequest = new { tool = "api_decompile_batch", arguments = new { items = new[] { ItemArgument(item) }, snapshotId = snapshot.Id, maxChars } },
                remainingRequest = index + 1 < items.Length
                    ? new { tool = "api_decompile_batch", arguments = new { items = items.Skip(index + 1).Select(ItemArgument).ToArray(), snapshotId = snapshot.Id, maxChars } }
                    : null,
            });
        for (var index = 0; index < items.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            try
            {
                var entry = SelectAssembly(snapshot, item.AssemblyId);
                var count = item.LineCount;
                object? result = null;
                ApiDecompileBatchItem? sourceContinuation = null;
                while (true)
                {
                    var (_, source) = await DecompileSnapshotAsync(snapshot, entry, item.SymbolId, item.StartLine, count, includeHit: false, cancellationToken);
                    var pageContinuation = source.hasMore
                        ? new ApiDecompileBatchItem(item.AssemblyId, item.SymbolId, source.nextLine!.Value, item.LineCount)
                        : null;
                    var success = new { assemblyId = item.AssemblyId, symbolId = item.SymbolId, success = true, source };
                    if (Fits(success, pageContinuation, items.Skip(index + 1)))
                    {
                        result = success;
                        sourceContinuation = pageContinuation;
                        break;
                    }
                    if (count == 1)
                        break;
                    count = Math.Max(1, count / 2);
                }

                if (result is null)
                {
                    var oneLine = await DecompileSnapshotAsync(snapshot, entry, item.SymbolId, item.StartLine, 1, includeHit: false, cancellationToken);
                    var oneLineContinuation = oneLine.Source.hasMore
                        ? new ApiDecompileBatchItem(item.AssemblyId, item.SymbolId, oneLine.Source.nextLine!.Value, item.LineCount)
                        : null;
                    var minimalSuccess = new { assemblyId = item.AssemblyId, symbolId = item.SymbolId, success = true, source = oneLine.Source };
                    var minimalContinuations = oneLineContinuation is null ? Array.Empty<object>() : new[] { ItemArgument(oneLineContinuation) };
                    if (JsonSerializer.Serialize(new
                        {
                            snapshotId = snapshot.Id,
                            partial = oneLineContinuation is not null,
                            truncated = oneLineContinuation is not null,
                            results = new[] { minimalSuccess },
                            nextItems = minimalContinuations,
                        }).Length <= maxChars)
                    {
                        if (results.Count == 0)
                            throw SplitBudgetError(item, index);
                        nextItems.Add(item);
                        nextItems.AddRange(items.Skip(index + 1));
                        break;
                    }

                    result = new
                    {
                        assemblyId = item.AssemblyId,
                        symbolId = item.SymbolId,
                        success = false,
                        error = new ApiDiscoveryException("batch.member_exceeds_budget",
                            "Even one source line cannot fit maxChars; request the source separately with api_decompile.",
                            new { tool = "api_decompile", arguments = new { assemblyId = item.AssemblyId, symbolId = item.SymbolId, startLine = item.StartLine, lineCount = 1, snapshotId = snapshot.Id } }).ToJson(),
                    };
                    if (!Fits(result, null, items.Skip(index + 1)))
                        throw BudgetError();
                }

                if (!Fits(result, sourceContinuation, items.Skip(index + 1)))
                {
                    nextItems.Add(item);
                    nextItems.AddRange(items.Skip(index + 1));
                    break;
                }
                results.Add(result!);
                if (sourceContinuation is not null)
                    nextItems.Add(sourceContinuation);
            }
            catch (ApiDiscoveryException exception) when (exception.Code is not "batch.budget_too_small" and not "batch.split_required")
            {
                var failure = new { assemblyId = item.AssemblyId, symbolId = item.SymbolId, success = false, error = exception.ToJson() };
                if (!Fits(failure, null, items.Skip(index + 1)))
                {
                    nextItems.Add(item);
                    nextItems.AddRange(items.Skip(index + 1));
                    break;
                }
                results.Add(failure);
            }
            catch (ArgumentOutOfRangeException)
            {
                var failure = new
                {
                    assemblyId = item.AssemblyId,
                    symbolId = item.SymbolId,
                    success = false,
                    error = new ApiDiscoveryException("source.invalid_page",
                        "startLine is beyond this symbol's source; restart at line 1.",
                        new { tool = "api_decompile_batch", arguments = new { items = new[] { new { assemblyId = item.AssemblyId, symbolId = item.SymbolId, startLine = 1, lineCount = item.LineCount } }, snapshotId = snapshot.Id, maxChars } }).ToJson(),
                };
                if (!Fits(failure, null, items.Skip(index + 1)))
                {
                    nextItems.Add(item);
                    nextItems.AddRange(items.Skip(index + 1));
                    break;
                }
                results.Add(failure);
            }
        }
        var continuations = nextItems.Select(item => (object)new
        {
            assemblyId = item.AssemblyId,
            symbolId = item.SymbolId,
            startLine = item.StartLine,
            lineCount = item.LineCount,
        }).ToArray();
        var response = new { snapshotId = snapshot.Id, partial = nextItems.Count > 0, truncated = nextItems.Count > 0, results, nextItems = continuations };
        var json = JsonSerializer.Serialize(response);
        if (json.Length > maxChars)
            throw BudgetError();
        return json;
    }
}
