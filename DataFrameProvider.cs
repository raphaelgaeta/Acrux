using Polars.CSharp;

namespace PolarsGridViewer;

public static class DataFrameProvider
{
    /// <summary>Filtro do diálogo de abertura (parquet + csv).</summary>
    public const string OpenDialogFilter =
        "Arquivos de dados (*.parquet;*.csv)|*.parquet;*.csv|" +
        "Arquivos Parquet (*.parquet)|*.parquet|" +
        "Arquivos CSV (*.csv)|*.csv|" +
        "Todos os arquivos (*.*)|*.*";

    /// <summary>
    /// Garante uma fonte parquet: .parquet passa direto; .csv é convertido uma
    /// única vez para um parquet temporário (ScanCsv → SinkParquet, streaming,
    /// sem materializar na RAM) e o app segue operando 100% sobre parquet —
    /// preservando o princípio de re-scan barato dos filtros e scripts.
    /// Retorna o caminho da fonte e se ela é temporária (chamador apaga).
    /// </summary>
    public static async Task<(string ParquetPath, bool IsTemp)> EnsureParquetAsync(
        string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Arquivo não encontrado.", path);

        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return (path, false);

        return await Task.Run(() =>
        {
            var separator = DetectCsvSeparator(path);
            var temp = Path.Combine(
                Path.GetTempPath(), "PolarsGridViewer",
                $"{Path.GetFileNameWithoutExtension(path)}_{Guid.NewGuid():N}.parquet");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);

            // decimalComma acompanha o ';' (CSV Excel-BR usa "1.234,56")
            using var lf = LazyFrame.ScanCsv(
                path, separator: separator, decimalComma: separator == ';', tryParseDates: true);
            lf.SinkParquet(temp);
            return (temp, true);
        }, cancellationToken);
    }

    /// <summary>Heurística: separador com mais ocorrências na 1ª linha.</summary>
    private static char DetectCsvSeparator(string path)
    {
        var header = File.ReadLines(path).FirstOrDefault() ?? "";
        var best = new[] { ';', ',', '\t' }
            .Select(c => (Sep: c, Count: header.Count(ch => ch == c)))
            .OrderByDescending(t => t.Count)
            .First();
        return best.Count > 0 ? best.Sep : ',';
    }

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
