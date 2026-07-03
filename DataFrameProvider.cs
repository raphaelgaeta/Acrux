using Polars.CSharp;

namespace PolarsGridViewer;

public static class DataFrameProvider
{
    /// <summary>
    /// Lê apenas o schema do parquet (metadados — não carrega dados).
    /// </summary>
    public static async Task<string[]> GetColumnNamesAsync(string parquetPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Arquivo .parquet não encontrado.", parquetPath);

        return await Task.Run(() =>
        {
            using var lf = LazyFrame.ScanParquet(parquetPath);
            return lf.Schema.ToFrozenDictionary().Keys.ToArray();
        }, cancellationToken);
    }

    /// <summary>
    /// Coleta do parquet apenas as colunas pedidas (projection pushdown:
    /// o Polars só lê do disco as colunas do Select).
    /// </summary>
    public static async Task<DataFrame> GetDataFrameAsync(string parquetPath, string[] columns, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Arquivo .parquet não encontrado.", parquetPath);

        return await Task.Run(() =>
        {
            using var lf = LazyFrame.ScanParquet(parquetPath).Select(columns);
            return lf.Collect();
        }, cancellationToken);
    }
}
