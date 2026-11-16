namespace Acrux;

/// <summary>
/// An open file, with no screen: the file as parquet (converted once, to a
/// temporary file, when it was CSV, xlsx or SQLite), its column cache, the
/// header filters with the rows they keep, the hidden columns, and the C#
/// terminal's chain of steps with its result. The desktop app on Avalonia and
/// a terminal UI drive the same session; like the column cache, it is called
/// from one thread, the UI's (a synchronization context). Desenho aprovado
/// em 2026-11-15.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly bool _ownsParquet;
    private readonly Dictionary<string, ColumnFilter> _filters = [];
    private CancellationTokenSource? _filtering;
    private readonly List<string> _chain = [];
    private string[]? _chainBaseColumns;
    private Dictionary<string, ColumnFilter>? _chainBaseFilters;
    private CancellationTokenSource? _scripting;

    private Session(string sourcePath, string parquetPath, bool ownsParquet, ColumnCache file, MemoryBudget memory)
    {
        SourcePath = sourcePath;
        ParquetPath = parquetPath;
        _ownsParquet = ownsParquet;
        File = file;
        Memory = memory;
    }

    /// <summary>The cap the file's columns and a script's result share.</summary>
    public MemoryBudget Memory { get; }

    /// <summary>The terminal's steps so far, each one's <c>df</c> being the step before's result.</summary>
    public IReadOnlyList<string> Chain => _chain;

    /// <summary>The chain's result, column by column; null while the screen shows the file.</summary>
    public ColumnCache? Result { get; private set; }

    /// <summary>How many header filters the chain took in as its base, at its first step.</summary>
    public int InheritedFilters => _chainBaseFilters?.Count ?? 0;

    /// <summary>
    /// Runs the chain again from the file, with <paramref name="step"/> at the
    /// end when there is one (the replay of ScriptHost). The first step's
    /// <c>df</c> is the file as shown, its visible columns and its header
    /// filters, and that base stays frozen for the later steps: the replay
    /// runs it every time, so it must not drift. A step that fails stays out
    /// of the chain. A newer run cancels this one: null when it took over.
    /// Throws the compiler's error (CompilationErrorException) or Polars'.
    /// </summary>
    public async Task<ColumnCache?> RunAsync(string? step, bool rowCap)
    {
        _scripting?.Cancel();
        var cts = _scripting = new CancellationTokenSource();
        var steps = new List<string>(_chain);
        if (step is not null) steps.Add(step);
        bool starting = _chain.Count == 0;
        var baseColumns = starting ? VisibleColumnNames() : _chainBaseColumns;
        var baseFilters = starting ? new Dictionary<string, ColumnFilter>(_filters) : _chainBaseFilters;

        ColumnCache result;
        try
        {
            var plan = await ScriptHost.BuildChainAsync(steps, ParquetPath, baseColumns, baseFilters, rowCap, cts.Token);
            // counts the rows; the columns are computed as the screen reaches them
            result = await ColumnCache.FromPlanAsync(plan, Memory, File.RowCount);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        if (cts.IsCancellationRequested)
        {
            result.Dispose();
            return null;
        }
        if (step is not null)
        {
            _chain.Add(step);
            if (starting)
            {
                _chainBaseColumns = baseColumns;
                _chainBaseFilters = baseFilters;
            }
        }
        File.Unpin();   // the file's columns yield memory to the result first
        Result?.Dispose();
        Result = result;
        return result;
    }

    /// <summary>
    /// Drops the last step. The caller runs the chain again (<see cref="RunAsync"/>
    /// with no step), or restores the file when the chain is empty.
    /// </summary>
    public void UndoStep()
    {
        if (_chain.Count > 0) _chain.RemoveAt(_chain.Count - 1);
    }

    /// <summary>
    /// Back to the file: the chain and its result go. The file's cache stayed
    /// alive under the result, so what is still in memory shows at once.
    /// </summary>
    public void Restore()
    {
        _scripting?.Cancel();
        _chain.Clear();
        _chainBaseColumns = null;
        _chainBaseFilters = null;
        Result?.Dispose();
        Result = null;
    }

    // the columns shown, in file order; null when none is hidden (no Select)
    private string[]? VisibleColumnNames() =>
        HiddenColumns.Count == 0 ? null : [.. File.Fields.Select(f => f.Name).Where(n => !HiddenColumns.Contains(n))];

    /// <summary>The file the user chose.</summary>
    public string SourcePath { get; }

    /// <summary>What the session reads: the file itself, or its converted copy.</summary>
    public string ParquetPath { get; }

    /// <summary>The file's columns, read as the screen reaches them.</summary>
    public ColumnCache File { get; }

    /// <summary>The header filters, by column: the values each column keeps (Excel's rule).</summary>
    public IReadOnlyDictionary<string, ColumnFilter> Filters => _filters;

    /// <summary>
    /// The rows the filters keep, as indexes into the file, never a copy of
    /// the data (CLAUDE.md, princípio 2); null keeps them all.
    /// </summary>
    public int[]? Rows { get; private set; }

    /// <summary>How many rows the screen shows: the filters', or the file's.</summary>
    public int RowCount => Rows?.Length ?? File.RowCount;

    /// <summary>
    /// The columns the screen hides ("Columns"). A hidden column is never
    /// read, and the others keep their index, so a column's index is always
    /// the file's.
    /// </summary>
    public HashSet<string> HiddenColumns { get; } = [];

    /// <summary>
    /// A column's values for its filter list, its own filter left out, as in
    /// Excel: the list shows what the other filters let through. Capped at
    /// <see cref="FilterEngine.DistinctCap"/>.
    /// </summary>
    public Task<(string[] Values, bool HasBlanks, bool Capped)> ValuesAsync(string column, CancellationToken cancellationToken = default) =>
        FilterEngine.GetDistinctValuesAsync(ParquetPath, column, FilterEngine.BuildPredicate(_filters, except: column),
            cancellationToken: cancellationToken);

    /// <summary>Sets a column's filter, or removes it with null. The rows follow on <see cref="ApplyFiltersAsync"/>.</summary>
    public void SetFilter(string column, ColumnFilter? filter)
    {
        if (filter is null) _filters.Remove(column);
        else _filters[column] = filter;
    }

    public void ClearFilters() => _filters.Clear();

    /// <summary>
    /// The rows again, from the file and every active filter, never on top of
    /// the previous result (princípio 2). A newer call cancels this one:
    /// false when it took over, and <see cref="Rows"/> is then its business.
    /// </summary>
    public async Task<bool> ApplyFiltersAsync()
    {
        _filtering?.Cancel();
        var cts = _filtering = new CancellationTokenSource();
        try
        {
            var rows = await FilterEngine.GetVisibleRowsAsync(ParquetPath, FilterEngine.BuildPredicate(_filters), cts.Token);
            if (cts.IsCancellationRequested) return false;
            Rows = rows;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a file of any supported format, the format told by its content.
    /// <paramref name="tableIndex"/> picks an xlsx sheet or an SQLite table
    /// (<see cref="DataFrameProvider.GetTableNames"/>). Only metadata is read:
    /// the schema and the row count.
    /// </summary>
    public static async Task<Session> OpenAsync(string path, MemoryBudget memory, int tableIndex = 0,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var (parquet, isTemp) = await DataFrameProvider.EnsureParquetAsync(path, progress, tableIndex, cancellationToken);
        try
        {
            progress?.Report("Reading schema...");
            var file = await ColumnCache.OpenAsync(parquet, memory);
            return new Session(path, parquet, isTemp, file, memory);
        }
        catch
        {
            if (isTemp) TryDelete(parquet);   // a failed open leaves no converted copy behind
            throw;
        }
    }

    /// <summary>Frees the columns and deletes the converted copy, if there is one.</summary>
    public void Dispose()
    {
        _filtering?.Cancel();
        Restore();
        File.Dispose();
        if (_ownsParquet) TryDelete(ParquetPath);
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); }
        catch (IOException) { /* in use: CleanStaleTempFiles takes it on a later start */ }
        catch (UnauthorizedAccessException) { }
    }
}
