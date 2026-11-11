using Apache.Arrow;
using Apache.Arrow.Types;
using Polars.CSharp;
using Field = Apache.Arrow.Field;

namespace Acrux;

/// <summary>
/// A table's columns, loaded as the screen reaches them, so a table of any
/// width opens whole: 700 columns × 5M rows ask for ~25 GB, and loading them
/// at once ended the process (the Rust allocator aborts, no exception). The
/// table is a parquet file (<see cref="OpenAsync"/>) or a script's result
/// (<see cref="FromPlanAsync"/>).
/// <list type="bullet">
/// <item>Each column comes whole (every row) from its own <c>Collect</c>, so
/// dropping one gives its memory back: a batch imported from Polars is freed
/// as a unit, and one column of a 15-column batch frees nothing (probe,
/// 2026-11-10). Separate Collects in parallel also beat one Collect of the
/// same columns (174 against 299 ms on 8 cores).</item>
/// <item>A script result's column is its own run of the plan, which reads
/// only the columns that column needs. Computed whole, the same result kept
/// every row that matched, with every column, before cutting, and died past
/// 8 GB (2026-11-11). Four run at a time: each run repeats the plan's work
/// and Polars already spreads it over every core.</item>
/// <item>The memory is the shared <see cref="MemoryBudget"/>. Columns past
/// it leave least recently used first, before a load and not after, so the
/// peak stays near the limit. The columns of the last
/// <see cref="EnsureAsync"/> are pinned: what the screen shows always loads,
/// even past the limit.</item>
/// </list>
/// One thread: call it from the UI thread. Loads run on the thread pool, and
/// their results are stored (and old columns disposed) back on the caller's
/// synchronization context, so a cell read never races a Dispose. Without a
/// synchronization context, the continuations run on the pool and race.
/// </summary>
public sealed class ColumnCache : IDisposable
{
    /// <summary>How many columns of a script result load at once.</summary>
    public const int PlanConcurrency = 4;

    private sealed class Entry(RecordBatch batch, long bytes, LinkedListNode<int> node)
    {
        public readonly RecordBatch Batch = batch;
        public readonly long Bytes = bytes;
        public readonly LinkedListNode<int> Node = node;
    }

    private readonly Entry?[] _loaded;
    private readonly LinkedList<int> _recent = new();   // most recently used first
    private readonly Dictionary<int, Task> _inflight = new();
    private readonly Dictionary<int, Exception> _failed = new();
    private readonly Dictionary<ArrowTypeId, (long Bytes, long Rows)> _sizes = new();   // learned per type
    private readonly Func<string, RecordBatch>? _load;   // null: the whole table is in _whole
    private readonly SemaphoreSlim? _gate;
    private readonly IDisposable? _source;   // the script's plan, disposed after the last load
    private readonly RecordBatch? _whole;    // a result computed in one run
    private HashSet<int> _pinned = [];
    private long _inflightBytes;
    private int _running;
    private volatile bool _disposed;

    /// <summary>The memory this cache shares with the others.</summary>
    public MemoryBudget Memory { get; }

    /// <summary>The table's columns, in order, with their Arrow types.</summary>
    public IReadOnlyList<Field> Fields { get; }

    public int RowCount { get; }

    /// <summary>Bytes of the Arrow buffers in memory.</summary>
    public long HeldBytes { get; private set; }

    public int LoadedCount { get; private set; }

    public int LoadingCount => _inflight.Count;

    /// <summary>
    /// A column arrived (error null) or failed to load; a failed column is not
    /// tried again. Raised on the caller's synchronization context.
    /// </summary>
    public event Action<ColumnCache, int, Exception?>? ColumnLoaded;

    internal long InflightBytes => _inflightBytes;

    internal bool HasPins => _pinned.Count > 0;

    private ColumnCache(IReadOnlyList<Field> fields, int rowCount, MemoryBudget memory,
        Func<string, RecordBatch>? load, int concurrency, IDisposable? source, RecordBatch? whole)
    {
        Fields = fields;
        RowCount = rowCount;
        Memory = memory;
        _loaded = new Entry?[fields.Count];
        _load = load;
        _gate = concurrency > 0 ? new SemaphoreSlim(concurrency) : null;
        _source = source;
        _whole = whole;
        if (whole is not null)
        {
            HeldBytes = Enumerable.Range(0, whole.ColumnCount).Sum(i => Size(whole.Column(i).Data));
            LoadedCount = fields.Count;
        }
        memory.Add(this);
    }

    /// <summary>
    /// A parquet file: reads the schema and the row count from its metadata;
    /// no column is loaded.
    /// </summary>
    public static async Task<ColumnCache> OpenAsync(string parquetPath, MemoryBudget memory)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Parquet file not found.", parquetPath);

        var fields = await Task.Run(() =>
        {
            using var lf = LazyFrame.ScanParquet(parquetPath);
            return lf.Schema.ToArrowSchema().FieldsList;   // file order
        });
        var rows = await FilterEngine.CountRowsAsync(parquetPath);
        CheckRows(rows);

        return new ColumnCache(fields, (int)rows, memory,
            column => LoadFromFile(parquetPath, column), concurrency: 0, source: null, whole: null);
    }

    /// <summary>
    /// A script's result. When its rows come in the same order every run
    /// (<see cref="PlanOrder"/>), counts them and computes each column as the
    /// screen reaches it. Otherwise computes it whole, in one run, if it fits
    /// even with as many rows as <paramref name="sourceRows"/> (the file's):
    /// when it doesn't, throws <see cref="InvalidOperationException"/> with
    /// the sizes and what to do. Takes ownership of <paramref name="plan"/>.
    /// </summary>
    public static async Task<ColumnCache> FromPlanAsync(LazyFrame plan, MemoryBudget memory, long sourceRows)
    {
        try
        {
            var (fields, unstable) = await Task.Run(() =>
            {
                using var schema = plan.CollectSchema();
                return (schema.ToArrowSchema().FieldsList, PlanOrder.WhyUnstable(plan.Explain(true)));
            });

            if (unstable is null)
            {
                long rows = await Task.Run(() =>
                {
                    using var count = plan.Select(Polars.CSharp.Polars.Len());
                    using var df = count.Collect();
                    return Convert.ToInt64(df[0, 0]);
                });
                CheckRows(rows);
                return new ColumnCache(fields, (int)rows, memory,
                    column => LoadFromPlan(plan, column), PlanConcurrency, source: plan, whole: null);
            }

            // a single run: every column at once, so it must fit before it runs
            long expected = fields.Sum(f => Estimate(f.DataType, sourceRows));
            memory.Refresh();
            if (expected > memory.Limit)
                throw new InvalidOperationException(
                    $"This result uses {unstable}, whose row order can change from run to run, so it is computed in one go. "
                    + $"Its {fields.Count:N0} columns could take up to {FormatBytes(expected)} "
                    + $"(with as many rows as the file), and the memory budget is {FormatBytes(memory.Limit)} now. "
                    + "Select the columns you need first, e.g. lf.Select(\"a\", \"b\").");

            var whole = await Task.Run(() =>
            {
                using var df = plan.Collect();
                return df.ToArrow();
            });
            plan.Dispose();
            return new ColumnCache(whole.Schema.FieldsList, whole.Length, memory, load: null, concurrency: 0, source: null, whole);
        }
        catch
        {
            plan.Dispose();
            throw;
        }
    }

    /// <summary>The column if it is in memory; null while it is not.</summary>
    public IArrowArray? TryGet(int column) =>
        _whole is not null ? _whole.Column(column) : _loaded[column]?.Batch.Column(0);

    /// <summary>Why the column failed to load, or null.</summary>
    public Exception? Failure(int column) => _failed.GetValueOrDefault(column);

    /// <summary>
    /// Loads the columns not yet in memory, in the given order (the visible
    /// ones first), and completes when all of them are in. They stay pinned
    /// until the next call or <see cref="Unpin"/>. Room is made before the
    /// loads start, by the size each column is expected to take. Never throws
    /// for a column: a failure goes to <see cref="ColumnLoaded"/>.
    /// </summary>
    public Task EnsureAsync(IReadOnlyList<int> columns)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_whole is not null) return Task.CompletedTask;

        _pinned = [.. columns];
        for (int i = columns.Count - 1; i >= 0; i--)   // the first one ends up the most recent
            if (_loaded[columns[i]] is { } entry)
            {
                _recent.Remove(entry.Node);
                _recent.AddFirst(entry.Node);
            }

        var missing = columns
            .Where(c => _loaded[c] is null && !_inflight.ContainsKey(c) && !_failed.ContainsKey(c))
            .Distinct()
            .ToList();
        if (missing.Count > 0)
        {
            Memory.Refresh();
            Memory.MakeRoom(this, Memory.InflightBytes + missing.Sum(Estimate));
            foreach (var column in missing)
            {
                var load = LoadAsync(column);
                if (!load.IsCompleted)
                    _inflight[column] = load;
            }
        }

        var waits = columns.Distinct()
            .Select(c => _inflight.GetValueOrDefault(c))
            .OfType<Task>()
            .ToArray();
        return waits.Length == 0 ? Task.CompletedTask : Task.WhenAll(waits);
    }

    /// <summary>Releases the pins: the grid shows something else (a script result).</summary>
    public void Unpin() => _pinned = [];

    /// <summary>
    /// Bytes the columns take together: their real size when loaded, the
    /// expected one when not.
    /// </summary>
    public long SizeOf(IEnumerable<int> columns) => columns.Distinct().Sum(Estimate);

    /// <summary>Whether the columns fit in the memory budget together, read again now.</summary>
    public bool Fits(IEnumerable<int> columns)
    {
        Memory.Refresh();
        return SizeOf(columns) <= Memory.Limit;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Memory.Remove(this);
        for (int i = 0; i < _loaded.Length; i++)
        {
            _loaded[i]?.Batch.Dispose();
            _loaded[i] = null;
        }
        _whole?.Dispose();
        _recent.Clear();
        HeldBytes = 0;
        LoadedCount = 0;
        if (_running == 0)
            _source?.Dispose();   // otherwise the last load in flight does it
    }

    // Drops unpinned columns, least recently used first, until `bytes` are
    // free; returns what it freed. A result computed in one run frees nothing
    // by the column.
    internal long Release(long bytes)
    {
        long freed = 0;
        var node = _recent.Last;
        while (freed < bytes && node is not null)
        {
            var previous = node.Previous;
            if (!_pinned.Contains(node.Value))
            {
                var entry = _loaded[node.Value]!;
                _recent.Remove(node);
                _loaded[node.Value] = null;
                HeldBytes -= entry.Bytes;
                LoadedCount--;
                freed += entry.Bytes;
                entry.Batch.Dispose();
            }
            node = previous;
        }
        return freed;
    }

    private async Task LoadAsync(int column)
    {
        long expected = Estimate(column);
        _inflightBytes += expected;
        _running++;
        Exception? error = null;
        try
        {
            var name = Fields[column].Name;
            var batch = await Task.Run(async () =>
            {
                if (_gate is not null)
                    await _gate.WaitAsync();
                try
                {
                    // closed while waiting its turn: the plan may be gone
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return _load!(name);
                }
                finally
                {
                    _gate?.Release();
                }
            });
            if (_disposed)
            {
                batch.Dispose();
                return;
            }
            long bytes = Size(batch.Column(0).Data);
            var type = Fields[column].DataType.TypeId;
            var seen = _sizes.GetValueOrDefault(type);
            _sizes[type] = (seen.Bytes + bytes, seen.Rows + RowCount);
            _loaded[column] = new Entry(batch, bytes, _recent.AddFirst(column));
            HeldBytes += bytes;
            LoadedCount++;
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            error = ex;
            _failed[column] = ex;
        }
        finally
        {
            _inflightBytes -= expected;
            _inflight.Remove(column);
            if (--_running == 0 && _disposed)
                _source?.Dispose();
        }

        Memory.MakeRoom(this, Memory.InflightBytes);
        ColumnLoaded?.Invoke(this, column, error);
    }

    // a column alone in its Collect: dropping it later frees all of its memory
    private static RecordBatch LoadFromFile(string parquetPath, string column)
    {
        using var scan = LazyFrame.ScanParquet(parquetPath);
        using var selected = scan.Select(new[] { column });
        using var df = selected.Collect();
        return df.ToArrow();
    }

    // one run of the plan for one column: Polars reads only what it needs
    private static RecordBatch LoadFromPlan(LazyFrame plan, string column)
    {
        using var selected = plan.Select(new[] { column });
        using var df = selected.Collect();
        return df.ToArrow();
    }

    private static void CheckRows(long rows)
    {
        if (rows > int.MaxValue)
            throw new NotSupportedException($"The table has {rows:N0} rows; the grid shows at most {int.MaxValue:N0}.");
    }

    // Before the first column of a type arrives: the type's width. After it:
    // the average of that type so far.
    private long Estimate(int column)
    {
        if (_loaded[column] is { } entry)
            return entry.Bytes;
        var type = Fields[column].DataType;
        if (_sizes.TryGetValue(type.TypeId, out var seen) && seen.Rows > 0)
            return (long)((double)seen.Bytes / seen.Rows * RowCount);
        return Estimate(type, RowCount);
    }

    // The type's width, or 16 bytes a row for text and other variable types
    // (a string view is 16 bytes, its text inline up to 12), plus validity.
    private static long Estimate(IArrowType type, long rows) =>
        (type is FixedWidthType fixedWidth ? rows * fixedWidth.BitWidth / 8 : rows * 16) + rows / 8;

    private static long Size(ArrayData data) =>
        (data.Buffers?.Sum(b => (long)b.Length) ?? 0)
        + (data.Children?.Sum(Size) ?? 0)
        + (data.Dictionary is null ? 0 : Size(data.Dictionary));

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20:N0} MB";
}
