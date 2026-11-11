using Apache.Arrow;
using Apache.Arrow.Types;
using Polars.CSharp;
using Field = Apache.Arrow.Field;

namespace Acrux;

/// <summary>
/// The file's columns, loaded as the screen reaches them, so a file of any
/// width opens whole: 700 columns × 5M rows ask for ~25 GB, and loading them
/// at once ended the process (the Rust allocator aborts, no exception).
/// <list type="bullet">
/// <item>Each column comes whole (every row) from its own <c>Collect</c>, so
/// dropping one gives its memory back: a batch imported from Polars is freed
/// as a unit, and one column of a 15-column batch frees nothing (probe,
/// 2026-11-10). Separate Collects in parallel also beat one Collect of the
/// same columns (174 against 299 ms on 8 cores).</item>
/// <item>The budget is half of what the cache could use: the memory free now
/// plus what the cache already holds. Counting itself keeps the budget from
/// shrinking as the cache grows. It is read again before a load and by
/// <see cref="Trim"/>, so the cache gives memory back when other programs
/// take it, and there is no fixed ceiling: a machine with room keeps the
/// whole file.</item>
/// <item>Columns past the budget leave least recently used first, before a
/// load and not after, so the peak stays near the budget. The columns of the
/// last <see cref="EnsureAsync"/> are pinned: what the screen shows always
/// loads, even past the budget.</item>
/// </list>
/// One thread: call it from the UI thread. Loads run on the thread pool, and
/// their results are stored (and old columns disposed) back on the caller's
/// synchronization context, so a cell read never races a Dispose. Without a
/// synchronization context, the continuations run on the pool and race.
/// </summary>
public sealed class ColumnCache : IDisposable
{
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
    private readonly Func<long, long> _budget;
    private HashSet<int> _pinned = [];
    private long _inflightBytes;
    private bool _disposed;

    /// <summary>Half of the memory free now plus what the cache holds.</summary>
    public static long HalfOfFree(long held) => (SystemMemory.Available() + held) / 2;

    public string ParquetPath { get; }

    /// <summary>The file's columns, in file order, with their Arrow types.</summary>
    public IReadOnlyList<Field> Fields { get; }

    public int RowCount { get; }

    /// <summary>Bytes of the Arrow buffers in memory.</summary>
    public long HeldBytes { get; private set; }

    /// <summary>The budget as last computed (before a load or on <see cref="Trim"/>).</summary>
    public long Budget { get; private set; }

    public int LoadedCount { get; private set; }

    public int LoadingCount => _inflight.Count;

    /// <summary>
    /// A column arrived (error null) or failed to load; a failed column is not
    /// tried again. Raised on the caller's synchronization context.
    /// </summary>
    public event Action<int, Exception?>? ColumnLoaded;

    private ColumnCache(string parquetPath, IReadOnlyList<Field> fields, int rowCount, Func<long, long> budget)
    {
        ParquetPath = parquetPath;
        Fields = fields;
        RowCount = rowCount;
        _loaded = new Entry?[fields.Count];
        _budget = budget;
        Budget = budget(0);
    }

    /// <summary>
    /// Reads the schema and the row count from the parquet's metadata; no
    /// column is loaded. <paramref name="budget"/> maps the bytes held to the
    /// budget (default: <see cref="HalfOfFree"/>).
    /// </summary>
    public static async Task<ColumnCache> OpenAsync(string parquetPath, Func<long, long>? budget = null)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Parquet file not found.", parquetPath);

        var fields = await Task.Run(() =>
        {
            using var lf = LazyFrame.ScanParquet(parquetPath);
            return lf.Schema.ToArrowSchema().FieldsList;   // file order
        });
        var rows = await FilterEngine.CountRowsAsync(parquetPath);
        if (rows > int.MaxValue)
            throw new NotSupportedException($"The file has {rows:N0} rows; the grid shows at most {int.MaxValue:N0}.");

        return new ColumnCache(parquetPath, fields, (int)rows, budget ?? HalfOfFree);
    }

    /// <summary>The column if it is in memory; null while it is not.</summary>
    public IArrowArray? TryGet(int column) => _loaded[column]?.Batch.Column(0);

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
            RefreshBudget();
            EvictTo(Budget - _inflightBytes - missing.Sum(Estimate));
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
    /// Reads the free memory again and drops the columns past the new budget,
    /// never the pinned ones. Call it now and then.
    /// </summary>
    public void Trim()
    {
        if (_disposed || _inflight.Count > 0)   // a load in flight lowers the free memory for a moment
            return;
        RefreshBudget();
        EvictTo(Budget);
    }

    /// <summary>
    /// Bytes the columns take together: their real size when loaded, the
    /// expected one when not.
    /// </summary>
    public long SizeOf(IEnumerable<int> columns) => columns.Distinct().Sum(Estimate);

    /// <summary>Whether the columns fit in the budget together, read again now.</summary>
    public bool Fits(IEnumerable<int> columns)
    {
        RefreshBudget();
        return SizeOf(columns) <= Budget;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = 0; i < _loaded.Length; i++)
        {
            _loaded[i]?.Batch.Dispose();
            _loaded[i] = null;
        }
        _recent.Clear();
        HeldBytes = 0;
        LoadedCount = 0;
    }

    private async Task LoadAsync(int column)
    {
        long expected = Estimate(column);
        _inflightBytes += expected;
        Exception? error = null;
        try
        {
            var name = Fields[column].Name;
            var batch = await Task.Run(() => Load(ParquetPath, name));
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
        }

        EvictTo(Budget - _inflightBytes);
        ColumnLoaded?.Invoke(column, error);
    }

    // a column alone in its Collect: dropping it later frees all of its memory
    private static RecordBatch Load(string parquetPath, string column)
    {
        using var scan = LazyFrame.ScanParquet(parquetPath);
        using var selected = scan.Select(new[] { column });
        using var df = selected.Collect();
        return df.ToArrow();
    }

    // Before the first column of a type arrives: the type's width, or 16 bytes
    // a row for text and other variable types (a string view is 16 bytes, its
    // text inline up to 12). After it: the average of that type so far.
    private long Estimate(int column)
    {
        if (_loaded[column] is { } entry)
            return entry.Bytes;
        var type = Fields[column].DataType;
        if (_sizes.TryGetValue(type.TypeId, out var seen) && seen.Rows > 0)
            return (long)((double)seen.Bytes / seen.Rows * RowCount);
        long rows = RowCount;
        long validity = rows / 8;
        return type is FixedWidthType fixedWidth
            ? rows * fixedWidth.BitWidth / 8 + validity
            : rows * 16 + validity;
    }

    private void RefreshBudget()
    {
        if (_inflight.Count == 0)   // otherwise the loads' working memory reads as taken
            Budget = _budget(HeldBytes);
    }

    private void EvictTo(long limit)
    {
        var node = _recent.Last;
        while (HeldBytes > limit && node is not null)
        {
            var previous = node.Previous;
            if (!_pinned.Contains(node.Value))
            {
                var entry = _loaded[node.Value]!;
                _recent.Remove(node);
                _loaded[node.Value] = null;
                HeldBytes -= entry.Bytes;
                LoadedCount--;
                entry.Batch.Dispose();
            }
            node = previous;
        }
    }

    private static long Size(ArrayData data) =>
        (data.Buffers?.Sum(b => (long)b.Length) ?? 0)
        + (data.Children?.Sum(Size) ?? 0)
        + (data.Dictionary is null ? 0 : Size(data.Dictionary));
}
