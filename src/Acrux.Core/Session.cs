namespace Acrux;

/// <summary>
/// An open file, with no screen: the file as parquet (converted once, to a
/// temporary file, when it was CSV, xlsx or SQLite) and its column cache. The
/// desktop app on Avalonia and a terminal UI drive the same session; like the
/// column cache, it is called from one thread, the UI's (a synchronization
/// context). The header filters and the script chain join it as the app
/// grows (desenho aprovado em 2026-11-15).
/// </summary>
public sealed class Session : IDisposable
{
    private readonly bool _ownsParquet;

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
