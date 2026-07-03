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
            var tempDir = Path.Combine(Path.GetTempPath(), "PolarsGridViewer");
            Directory.CreateDirectory(tempDir);

            // O leitor CSV do Polars exige UTF-8. Excel salvo como "CSV" simples
            // usa Windows-1252 (ç/ã viram "invalid utf-8 sequence"); o
            // CsvEncoding.LossyUTF8 leria, mas destrói os acentos (validado por
            // probe) — então transcodificamos para um CSV UTF-8 temporário.
            var utf8Csv = IsValidUtf8(path) ? path : TranscodeToUtf8(path, tempDir);
            try
            {
                var separator = DetectCsvSeparator(utf8Csv);
                var temp = Path.Combine(
                    tempDir, $"{Path.GetFileNameWithoutExtension(path)}_{Guid.NewGuid():N}.parquet");

                // decimalComma acompanha o ';' (CSV Excel-BR usa "1.234,56")
                using var lf = LazyFrame.ScanCsv(
                    utf8Csv, separator: separator, decimalComma: separator == ';', tryParseDates: true);
                lf.SinkParquet(temp);
                return (temp, true);
            }
            finally
            {
                if (!ReferenceEquals(utf8Csv, path))
                    File.Delete(utf8Csv);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Valida uma amostra do arquivo como UTF-8 (4 MB bastam: o primeiro
    /// acento em cp1252 já falha). Recua até 3 bytes no fim da amostra para
    /// não condenar um caractere multi-byte cortado ao meio.
    /// </summary>
    private static bool IsValidUtf8(string path)
    {
        using var fs = File.OpenRead(path);
        var buffer = new byte[Math.Min(4 * 1024 * 1024, fs.Length)];
        int len = fs.Read(buffer, 0, buffer.Length);

        if (len == buffer.Length && len > 3)
        {
            int end = len;
            while (end > 0 && (buffer[end - 1] & 0b1100_0000) == 0b1000_0000)
                end--;                        // continuações de um char cortado
            if (end > 0 && buffer[end - 1] >= 0b1100_0000)
                end--;                        // o byte líder do char cortado
            len = end;
        }

        return System.Text.Unicode.Utf8.IsValid(buffer.AsSpan(0, len));
    }

    /// <summary>
    /// Reescreve o CSV como UTF-8, em streaming. BOM UTF-16 é honrado pelo
    /// StreamReader; sem BOM, assume Windows-1252 (o "ANSI" do Excel pt-BR).
    /// </summary>
    private static string TranscodeToUtf8(string path, string tempDir)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var fallback = System.Text.Encoding.GetEncoding(1252);

        var temp = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(path)}_{Guid.NewGuid():N}_utf8.csv");
        using var reader = new StreamReader(path, fallback, detectEncodingFromByteOrderMarks: true);
        using var writer = new StreamWriter(temp, append: false, new System.Text.UTF8Encoding(false));

        var buffer = new char[64 * 1024];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            writer.Write(buffer, 0, read);
        return temp;
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
