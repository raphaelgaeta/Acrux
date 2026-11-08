using Microsoft.Data.Sqlite;
using Polars.CSharp;
namespace Acrux;

/// <summary>Source formats the app reads, as identified by file content.</summary>
public enum SourceFormat { Parquet, Csv, Excel, Sqlite }

public static class DataFrameProvider
{
    /// <summary>
    /// Open-dialog filter. Extensions only narrow the listing: the format is
    /// decided by content (<see cref="DetectFormat"/>), so "All files" works too.
    /// </summary>
    public const string OpenDialogFilter =
        "Data files (*.parquet;*.csv;*.xlsx;*.db;*.sqlite;*.sqlite3)|*.parquet;*.csv;*.xlsx;*.db;*.sqlite;*.sqlite3|" +
        "Parquet files (*.parquet)|*.parquet|" +
        "CSV files (*.csv)|*.csv|" +
        "Excel files (*.xlsx)|*.xlsx|" +
        "SQLite databases (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|" +
        "All files (*.*)|*.*";

    // CSV type-inference samples. Polars' default (100 rows) misfires easily
    // (an int column turning float at row 200 → "could not parse"); 10k costs
    // ~hundreds of ms on a 100 MB CSV (measured). If it still misfires, retry
    // with 1M — expensive (~15 s/100 MB) but only paid when needed.
    // Do NOT use ulong.MaxValue: native "capacity overflow" (validated).
    private const ulong InferSchemaRows = 10_000;
    private const ulong InferSchemaRowsRetry = 1_000_000;

    /// <summary>
    /// Guarantees a parquet source, whatever the input (format detected by
    /// content): parquet passes through; CSV is converted once to a temporary
    /// parquet (ScanCsv → SinkParquet, streaming, RAM-bounded); an xlsx sheet
    /// or a SQLite table/view is materialized and written as parquet. The app
    /// then operates on parquet only — preserving the cheap-rescan design of
    /// filters and scripts. <paramref name="tableIndex"/> picks the sheet or
    /// table (order of <see cref="GetTableNames"/>). Returns the source path
    /// and whether it is temporary (caller deletes).
    /// </summary>
    public static async Task<(string ParquetPath, bool IsTemp)> EnsureParquetAsync(
        string path, IProgress<string>? progress = null, int tableIndex = 0,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);

        var format = await Task.Run(() => DetectFormat(path), cancellationToken);
        if (format == SourceFormat.Parquet)
            return (path, false);

        if (format is SourceFormat.Excel or SourceFormat.Sqlite)
        {
            return await Task.Run(() =>
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "Acrux");
                Directory.CreateDirectory(tempDir);
                var baseName = Path.GetFileNameWithoutExtension(path);
                return (format == SourceFormat.Excel
                    ? ConvertExcel(path, tableIndex, tempDir, baseName)
                    : ConvertSqlite(path, tableIndex, tempDir, baseName), true);
            }, cancellationToken);
        }

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
                string parquet;
                try
                {
                    parquet = ConvertCsv(utf8Csv, separator, decimalComma, InferSchemaRows, tempDir, baseName);
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
                    parquet = ConvertCsv(utf8Csv, separator, decimalComma, InferSchemaRowsRetry, tempDir, baseName);
                }

                // dates arrive as text (ScanCsv's tryParseDates is off: it guesses day/month)
                progress?.Report("Recognizing dates...");
                return (TextDates.ConvertParquet(parquet, tempDir, baseName), true);
            }
            finally
            {
                if (!ReferenceEquals(utf8Csv, path))
                    File.Delete(utf8Csv);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Sheet names in workbook order (the same order calamine indexes) — read
    /// straight from xl/workbook.xml inside the xlsx zip, no extra dependency.
    /// </summary>
    public static string[] GetExcelSheetNames(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var entry = zip.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("Not a valid .xlsx file (xl/workbook.xml missing).");
        using var stream = entry.Open();
        var doc = System.Xml.Linq.XDocument.Load(stream);
        var ns = doc.Root!.GetDefaultNamespace();
        return doc.Descendants(ns + "sheet")
            .Select(s => (string?)s.Attribute("name") ?? "?")
            .ToArray();
    }

    /// <summary>
    /// Identifies the file by its leading bytes, not its extension: agents and
    /// exports name files loosely (.db, .sqlite, none at all), and a wrong
    /// guess would only fail later with a cryptic native error. Plain text
    /// (no NUL bytes, or a UTF-16 BOM) is taken as delimited text (CSV).
    /// </summary>
    public static SourceFormat DetectFormat(string path)
    {
        var head = new byte[8 * 1024];
        int read;
        using (var fs = File.OpenRead(path))
            read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (read == 0)
            throw new InvalidDataException("The file is empty.");
        var bytes = head.AsSpan(0, read);

        if (bytes.StartsWith("PAR1"u8))
            return SourceFormat.Parquet;
        if (bytes.StartsWith("SQLite format 3\0"u8))
            return SourceFormat.Sqlite;
        if (bytes.StartsWith("PK\u0003\u0004"u8))
            return IsXlsx(path) ? SourceFormat.Excel
                : throw new NotSupportedException("ZIP archive that is not an .xlsx workbook (e.g. .docx or .ods).");
        if (bytes.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
            throw new NotSupportedException("Legacy Office file (e.g. Excel 97-2003 .xls): save it as .xlsx.");
        // UTF-16 text is full of NULs; TranscodeToUtf8 honors its BOM
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
            return SourceFormat.Csv;
        if (bytes.IndexOf((byte)0) < 0)
            return SourceFormat.Csv;
        throw new NotSupportedException("Unrecognized binary file. Supported: Parquet, CSV, Excel (.xlsx) and SQLite.");
    }

    private static bool IsXlsx(string path)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            return zip.GetEntry("xl/workbook.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// What the user can pick inside the file: sheets (xlsx) or tables and
    /// views (SQLite). Empty for single-table formats (parquet, CSV).
    /// </summary>
    public static string[] GetTableNames(string path) => DetectFormat(path) switch
    {
        SourceFormat.Excel => GetExcelSheetNames(path),
        SourceFormat.Sqlite => GetSqliteTableNames(path),
        _ => [],
    };

    /// <summary>
    /// The user's data only — tables first, then views, each alphabetically.
    /// Left out: internal sqlite_* tables, and virtual tables (FTS5 search
    /// indexes, R*Tree…) with their shadow tables: an FTS index mirrors a real
    /// table and showed up first in the list as a lone "texto" column.
    /// pragma_table_list needs SQLite 3.37+; the bundled e_sqlite3 is 3.53.
    /// </summary>
    public static string[] GetSqliteTableNames(string path)
    {
        using var conn = OpenSqlite(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM pragma_table_list WHERE schema = 'main' AND type IN ('table', 'view') " +
            "AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY type, name";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    /// <summary>
    /// xlsx → temp parquet. ReadExcel (native calamine engine) materializes
    /// the sheet in RAM, but the xlsx format caps at ~1M rows so the peak is
    /// bounded. Note: Excel stores every number as floating point, so integer
    /// columns arrive as double (probe-validated).
    /// </summary>
    private static string ConvertExcel(string path, int sheetIndex, string tempDir, string baseName)
    {
        var temp = Path.Combine(tempDir, $"{baseName}_pid{Environment.ProcessId}_{Guid.NewGuid():N}.parquet");
        try
        {
            using var df = DataFrame.ReadExcel(path, sheetIndex: (ulong)sheetIndex);
            df.WriteParquet(temp);
            return temp;
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Every table and view of a SQLite database, materialized: name → DataFrame,
    /// in <see cref="GetSqliteTableNames"/> order (tables, then views), names
    /// matched case-insensitively like SQLite does. Meant for one-shot
    /// consumers such as the CLI: the whole database goes to RAM — fine for the
    /// small databases agents create, not for multi-GB ones (the grid keeps
    /// going through <see cref="EnsureParquetAsync"/>). The caller disposes the
    /// DataFrames.
    /// </summary>
      
    public static async Task<OrderedDictionary<string, DataFrame>> ReadSqliteTablesAsync(
        string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);

        return await Task.Run(() =>
        {
            if (DetectFormat(path) != SourceFormat.Sqlite)
                throw new InvalidDataException("Not a SQLite database.");

            var tables = new OrderedDictionary<string, DataFrame>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = OpenSqlite(path);
                foreach (var name in GetSqliteTableNames(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var columns = GetSqliteColumnKinds(conn, name);
                    using var batch = ReadSqliteTable(conn, name, columns);
                    tables.Add(name, DataFrame.FromArrow(batch));
                    // SQLite has no date type: dates live in text columns
                    if (TextDates.Convert(tables[name]) is { } dated)
                    {
                        tables[name].Dispose();
                        tables[name] = dated;
                    }
                }
                return tables;
            }
            catch
            {
                foreach (var df in tables.Values)
                    df.Dispose();
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// SQLite table/view → temp parquet. Not through DataFrame.ReadDatabase:
    /// it trusts the ADO.NET reader's column types, which SQLite derives from
    /// the FIRST row (see <see cref="GetSqliteColumnKinds"/>). The table is read
    /// with typed getters into Arrow and handed over via FromArrow; like the
    /// xlsx path, it is materialized during the conversion.
    /// </summary>
    private static string ConvertSqlite(string path, int tableIndex, string tempDir, string baseName)
    {
        var tables = GetSqliteTableNames(path);
        if (tables.Length == 0)
            throw new InvalidDataException("The SQLite database has no tables or views.");
        if ((uint)tableIndex >= (uint)tables.Length)
            throw new ArgumentOutOfRangeException(nameof(tableIndex),
                $"Table index {tableIndex} is out of range: the database has {tables.Length} tables/views.");

        var temp = Path.Combine(tempDir, $"{baseName}_pid{Environment.ProcessId}_{Guid.NewGuid():N}.parquet");
        try
        {
            using var conn = OpenSqlite(path);
            var table = tables[tableIndex];
            var columns = GetSqliteColumnKinds(conn, table);
            using var batch = ReadSqliteTable(conn, table, columns);
            using var df = DataFrame.FromArrow(batch);
            using var dated = TextDates.Convert(df);   // SQLite has no date type: dates live in text
            (dated ?? df).WriteParquet(temp);
            return temp;
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    private enum SqliteKind { Integer, Real, Text, Blob }

    // Read-only: a viewer never writes to the user's database. No pooling: a
    // pooled connection keeps the file open after Close.
    private static SqliteConnection OpenSqlite(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>
    /// SQLite is dynamically typed, and the ADO.NET reader types each column by
    /// its FIRST row: a later 'abc' in an INTEGER column reads as 0, 3.5 as 3,
    /// and a NULL first row falls back to the declared type — or to binary when
    /// there is none, CASTs included (all silent; probe-validated). So the type
    /// comes from the storage classes actually present in the column, in one
    /// pass: integer → Int64; integer + real → Double; only blob → Binary;
    /// anything else with text → String; only NULLs → the declared type.
    /// </summary>
    private static List<(string Name, SqliteKind Kind)> GetSqliteColumnKinds(SqliteConnection conn, string table)
    {
        var columns = new List<(string Name, string DeclaredType)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, type FROM pragma_table_info($table)";
            cmd.Parameters.AddWithValue("$table", table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                columns.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }
        if (columns.Count == 0)
            throw new InvalidDataException($"Could not read the columns of '{table}'.");

        var classes = new string?[columns.Count];
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT " + string.Join(", ", columns.Select(c =>
                $"group_concat(DISTINCT NULLIF(typeof({QuoteIdentifier(c.Name)}), 'null'))")) +
                $" FROM {QuoteIdentifier(table)}";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            for (int i = 0; i < columns.Count; i++)
                classes[i] = reader.IsDBNull(i) ? null : reader.GetString(i);
        }

        return columns.Select((c, i) => (c.Name, SqliteKindFor(classes[i], c.DeclaredType))).ToList();
    }

    private static SqliteKind SqliteKindFor(string? storageClasses, string declaredType)
    {
        if (storageClasses is null)
        {
            // only NULLs: SQLite's affinity rules on the declared type
            var t = declaredType.ToUpperInvariant();
            return t.Contains("INT") ? SqliteKind.Integer
                : t.Contains("REAL") || t.Contains("FLOA") || t.Contains("DOUB") ? SqliteKind.Real
                : SqliteKind.Text;
        }
        var set = storageClasses.Split(',');
        if (set.All(c => c == "integer")) return SqliteKind.Integer;
        if (set.All(c => c is "integer" or "real")) return SqliteKind.Real;
        if (set.All(c => c == "blob")) return SqliteKind.Blob;
        return SqliteKind.Text;
    }

    private static Apache.Arrow.RecordBatch ReadSqliteTable(
        SqliteConnection conn, string table, List<(string Name, SqliteKind Kind)> columns)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", columns.Select(c => QuoteIdentifier(c.Name)))} FROM {QuoteIdentifier(table)}";
        using var reader = cmd.ExecuteReader();

        var append = new Action<SqliteDataReader, int>[columns.Count];
        var build = new Func<Apache.Arrow.IArrowArray>[columns.Count];
        var fields = new List<Apache.Arrow.Field>();
        for (int i = 0; i < columns.Count; i++)
        {
            switch (columns[i].Kind)
            {
                case SqliteKind.Integer:
                {
                    var b = new Apache.Arrow.Int64Array.Builder();
                    append[i] = (r, k) => { if (r.IsDBNull(k)) b.AppendNull(); else b.Append(r.GetInt64(k)); };
                    build[i] = () => b.Build();
                    fields.Add(new(columns[i].Name, Apache.Arrow.Types.Int64Type.Default, nullable: true));
                    break;
                }
                case SqliteKind.Real:
                {
                    var b = new Apache.Arrow.DoubleArray.Builder();
                    append[i] = (r, k) => { if (r.IsDBNull(k)) b.AppendNull(); else b.Append(r.GetDouble(k)); };
                    build[i] = () => b.Build();
                    fields.Add(new(columns[i].Name, Apache.Arrow.Types.DoubleType.Default, nullable: true));
                    break;
                }
                case SqliteKind.Blob:
                {
                    var b = new Apache.Arrow.BinaryArray.Builder();
                    append[i] = (r, k) => { if (r.IsDBNull(k)) b.AppendNull(); else b.Append((ReadOnlySpan<byte>)r.GetFieldValue<byte[]>(k)); };
                    build[i] = () => b.Build();
                    fields.Add(new(columns[i].Name, Apache.Arrow.Types.BinaryType.Default, nullable: true));
                    break;
                }
                default:
                {
                    // text column: numbers keep their SQLite text form ("10", "3.5"); stray blobs show as hex
                    var b = new Apache.Arrow.StringArray.Builder();
                    append[i] = (r, k) =>
                    {
                        if (r.IsDBNull(k)) b.AppendNull();
                        else if (r.GetFieldType(k) == typeof(byte[])) b.Append("0x" + Convert.ToHexString(r.GetFieldValue<byte[]>(k)));
                        else b.Append(r.GetString(k));
                    };
                    build[i] = () => b.Build();
                    fields.Add(new(columns[i].Name, Apache.Arrow.Types.StringType.Default, nullable: true));
                    break;
                }
            }
        }

        int rows = 0;
        while (reader.Read())
        {
            for (int k = 0; k < append.Length; k++)
                append[k](reader, k);
            rows++;
        }

        var schema = new Apache.Arrow.Schema(fields, null);
        return new Apache.Arrow.RecordBatch(schema, build.Select(f => f()), rows);
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Best-effort sweep of %TEMP%\Acrux leftovers from runs that died before
    /// their own cleanup. Temp names embed the owner PID: files whose process
    /// is still alive are skipped, so concurrent instances are safe.
    /// </summary>
    public static void CleanStaleTempFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Acrux");
        if (!Directory.Exists(tempDir))
            return;

        foreach (var file in Directory.EnumerateFiles(tempDir))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                Path.GetFileName(file), @"_pid(\d+)_");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var pid))
            {
                try
                {
                    System.Diagnostics.Process.GetProcessById(pid);
                    continue;   // owner still running
                }
                catch (ArgumentException) { /* owner is gone */ }
            }
            else if (File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-1))
            {
                // old naming scheme without PID: only sweep after a day
                continue;
            }

            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string ConvertCsv(
        string utf8Csv, char separator, bool decimalComma, ulong inferRows, string tempDir, string baseName)
    {
        var temp = Path.Combine(tempDir, $"{baseName}_pid{Environment.ProcessId}_{Guid.NewGuid():N}.parquet");
        try
        {
            // tryParseDates off: Polars' inference always reads 01/02/2026 as day/month,
            // so a US file turned January 2 into February 1 silently (tested).
            // TextDates converts dates afterwards, only when unambiguous.
            using var lf = LazyFrame.ScanCsv(
                utf8Csv, separator: separator, decimalComma: decimalComma,
                tryParseDates: false, inferSchemaLength: inferRows);
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

        var temp = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(path)}_pid{Environment.ProcessId}_{Guid.NewGuid():N}_utf8.csv");
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
            // ToList keeps the file's column order; ToFrozenDictionary scrambles it (validated)
            return lf.Schema.ToList().Select(c => c.Name).ToArray();
        }, cancellationToken);
    }

    /// <summary>
    /// Loads the entire parquet into RAM — small files only: unlike
    /// <see cref="GetDataFrameAsync"/> there is no projection, so every
    /// column and row is read.
    /// </summary>
    public static async Task<DataFrame> LoadDataFrameAsync(string parquetPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Parquet file not found.", parquetPath);

        return await Task.Run(() => DataFrame.ReadParquet(parquetPath), cancellationToken);
    }

    /// <summary>
    /// Collects only the requested columns (projection pushdown: Polars reads
    /// just the selected columns from disk).
    /// </summary>
    public static async Task<DataFrame> GetDataFrameAsync(
        string parquetPath, string[] columns, uint? limit = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Parquet file not found.", parquetPath);

        return await Task.Run(() =>
        {
            using var lf = LazyFrame.ScanParquet(parquetPath).Select(columns);
            if (limit is null)
                return lf.Collect();
            using var limited = lf.Limit(limit.Value);   // the scan stops after `limit` rows
            return limited.Collect();
        }, cancellationToken);
    }
}
