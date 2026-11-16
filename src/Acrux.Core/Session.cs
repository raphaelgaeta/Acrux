namespace Acrux;

/// <summary>
/// An open file, with no screen: the file as parquet (converted once, to a
/// temporary file, when it was CSV, xlsx or SQLite), its column cache, the
/// header filters with the rows they keep, and the hidden columns. The
/// desktop app on Avalonia and a terminal UI drive the same session; like the
/// column cache, it is called from one thread, the UI's (a synchronization
/// context). The script chain joins it in the app's next phase (desenho
/// aprovado em 2026-11-15).
/// </summary>
public sealed class Session : IDisposable
{
    private readonly bool _ownsParquet;
    private readonly Dictionary<string, ColumnFilter> _filters = [];
    private CancellationTokenSource? _filtering;

    private Session(string sourcePath, string parquetPath, bool ownsParquet, ColumnCache file)
    {
        SourcePath = sourcePath;
        ParquetPath = parquetPath;
        _ownsParquet = ownsParquet;
        File = file;
    }

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
            return new Session(path, parquet, isTemp, file);
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
