using Polars.CSharp;

namespace Acrux;

public static class DataFrameProvider
{
    /// <summary>Open-dialog filter (parquet + csv).</summary>
    public const string OpenDialogFilter =
        "Data files (*.parquet;*.csv)|*.parquet;*.csv|" +
        "Parquet files (*.parquet)|*.parquet|" +
        "CSV files (*.csv)|*.csv|" +
        "All files (*.*)|*.*";

    // CSV type-inference samples. Polars' default (100 rows) misfires easily
    // (an int column turning float at row 200 → "could not parse"); 10k costs
    // ~hundreds of ms on a 100 MB CSV (measured). If it still misfires, retry
    // with 1M — expensive (~15 s/100 MB) but only paid when needed.
    // Do NOT use ulong.MaxValue: native "capacity overflow" (validated).
    private const ulong InferSchemaRows = 10_000;
    private const ulong InferSchemaRowsRetry = 1_000_000;

    /// <summary>
    /// Guarantees a parquet source: .parquet passes through; .csv is converted
    /// once to a temporary parquet (ScanCsv → SinkParquet, streaming, RAM-
    /// bounded) so the app keeps operating on parquet only — preserving the
    /// cheap-rescan design of filters and scripts. Returns the source path and
    /// whether it is temporary (caller deletes).
    /// </summary>
    public static async Task<(string ParquetPath, bool IsTemp)> EnsureParquetAsync(
        string path, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);

        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return (path, false);

        return await Task.Run(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "Acrux");
            Directory.CreateDirectory(tempDir);

            // Polars' CSV reader requires strict UTF-8. Excel's plain "CSV"
            // save uses Windows-1252 (ç/ã → "invalid utf-8 sequence"), and
            // CsvEncoding.LossyUTF8 would read it but destroys accents
            // (probe-validated) — so transcode to a temporary UTF-8 CSV instead.
            var utf8Csv = IsValidUtf8(path) ? path : TranscodeToUtf8(path, tempDir);
            try
            {
                var separator = DetectCsvSeparator(utf8Csv);
                var decimalComma = DetectDecimalComma(utf8Csv, separator);
                var baseName = Path.GetFileNameWithoutExtension(path);
                try
                {
                    return (ConvertCsv(utf8Csv, separator, decimalComma, InferSchemaRows, tempDir, baseName), true);
                }
                catch (Exception ex) when (
                    ex.Message.Contains("could not parse") ||
                    ex.Message.Contains("invalid primitive value"))
                {
                    // The sample got a type wrong (e.g. an int column that turns
                    // float later) — retry with a much larger sample. The failed
                    // value comes in the message: if it contradicts the chosen
                    // decimal convention (e.g. `8.1` with decimalComma on), flip
                    // it — otherwise the column would silently become a string.
                    var offender = System.Text.RegularExpressions.Regex
                        .Match(ex.Message, "could not parse `\"?([^`\"]+)\"?`").Groups[1].Value;
                    if (decimalComma && System.Text.RegularExpressions.Regex.IsMatch(offender, @"^-?\d+\.\d+$"))
                        decimalComma = false;
                    else if (!decimalComma && System.Text.RegularExpressions.Regex.IsMatch(offender, @"^-?\d+,\d+$"))
                        decimalComma = true;

                    progress?.Report("Refining CSV types (larger sample)...");
                    return (ConvertCsv(utf8Csv, separator, decimalComma, InferSchemaRowsRetry, tempDir, baseName), true);
                }
            }
            finally
            {
                if (!ReferenceEquals(utf8Csv, path))
                    File.Delete(utf8Csv);
            }
        }, cancellationToken);
    }

    private static string ConvertCsv(
        string utf8Csv, char separator, bool decimalComma, ulong inferRows, string tempDir, string baseName)
    {
        var temp = Path.Combine(tempDir, $"{baseName}_{Guid.NewGuid():N}.parquet");
        try
        {
            using var lf = LazyFrame.ScanCsv(
                utf8Csv, separator: separator, decimalComma: decimalComma,
                tryParseDates: true, inferSchemaLength: inferRows);
            lf.SinkParquet(temp);
            return temp;
        }
        catch
        {
            // a failed sink can leave a partial/empty parquet behind (the
            // native layer attempts an eager-write fallback before throwing)
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Heuristic: the separator with the most hits on the header line. The
    /// header is the cleanest line in the file — data fields may contain
    /// separators inside quotes.
    /// </summary>
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
    /// Sniffs the decimal convention from the data (don't infer it from the
    /// separator: `;` files with dot decimals exist — and with the wrong
    /// decimalComma the column becomes a string SILENTLY). Counts "12,34" vs
    /// "12.34" fields over ~200 lines; majority wins, ties fall back to the
    /// separator convention (`;` → comma). A `,` separator can't carry unquoted
    /// decimal commas → always false.
    /// </summary>
    private static bool DetectDecimalComma(string path, char separator)
    {
        if (separator == ',')
            return false;

        int comma = 0, dot = 0;
        foreach (var line in File.ReadLines(path).Skip(1).Take(200))
        {
            foreach (var field in line.Split(separator))
            {
                var f = field.Trim().Trim('"');
                if (System.Text.RegularExpressions.Regex.IsMatch(f, @"^-?\d+,\d+$"))
                    comma++;
                else if (System.Text.RegularExpressions.Regex.IsMatch(f, @"^-?\d+\.\d+$"))
                    dot++;
            }
        }

        if (comma != dot)
            return comma > dot;
        return separator == ';';
    }

    /// <summary>
    /// Validates a sample of the file as UTF-8 (4 MB is plenty: the first
    /// cp1252 accent already fails). Backs off up to 3 bytes at the end of the
    /// sample so a multi-byte char cut at the boundary isn't misjudged.
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
                end--;                        // continuation bytes of a cut char
            if (end > 0 && buffer[end - 1] >= 0b1100_0000)
                end--;                        // the cut char's lead byte
            len = end;
        }

        return System.Text.Unicode.Utf8.IsValid(buffer.AsSpan(0, len));
    }

    /// <summary>
    /// Rewrites the CSV as UTF-8, streaming. UTF-16 BOMs are honored by the
    /// StreamReader; without a BOM, assumes Windows-1252 (Excel's "ANSI").
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

    /// <summary>
    /// Reads only the parquet schema (metadata — no data is loaded).
    /// </summary>
    public static async Task<string[]> GetColumnNamesAsync(string parquetPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Parquet file not found.", parquetPath);

        return await Task.Run(() =>
        {
            using var lf = LazyFrame.ScanParquet(parquetPath);
            return lf.Schema.ToFrozenDictionary().Keys.ToArray();
        }, cancellationToken);
    }

    /// <summary>
    /// Collects only the requested columns (projection pushdown: Polars reads
    /// just the selected columns from disk).
    /// </summary>
    public static async Task<DataFrame> GetDataFrameAsync(string parquetPath, string[] columns, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Parquet file not found.", parquetPath);

        return await Task.Run(() =>
        {
            using var lf = LazyFrame.ScanParquet(parquetPath).Select(columns);
            return lf.Collect();
        }, cancellationToken);
    }
}
