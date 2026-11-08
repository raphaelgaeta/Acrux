using System.Globalization;
using Acrux;
using Polars.CSharp;
using static Polars.CSharp.Polars;

DataFrameProvider.CleanStaleTempFiles();   // orphans from runs killed mid-conversion

try
{
    return args switch
    {
        ["schema", var file] => await Schema(file),
        ["head", var file] => await ShowHead(file, 5),
        ["head", var file, "-n", var n] when int.TryParse(n, out var rows) && rows > 0 => await ShowHead(file, rows),
        ["columns", var file] => await ShowColumns(file),
        ["rows", var file] => await ShowRowNumber(file),
        ["type", var file] => ShowFormat(file),
        ["version"] => ShowVersion(),
        ["column", var file, var column] => await ShowSpecificColumn(file, column, 10),
        ["column", var file, var column, "-n", var n] when int.TryParse(n, out var rows) && rows > 0 => await ShowSpecificColumn(file, column, rows),
        ["filter", var file, var column, "--last", var span] => await FilterLast(file, column, span, 10),
        ["filter", var file, var column, "--last", var span, "-n", var n] when int.TryParse(n, out var rows) && rows > 0 => await FilterLast(file, column, span, rows),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    // the operator gets the message, not a stack trace; ACRUX_DEBUG=1 shows everything
    Console.Error.WriteLine(Environment.GetEnvironmentVariable("ACRUX_DEBUG") == "1"
        ? ex.ToString()
        : $"acrux: {ex.Message}");
    return 1;   // 1 = failed while running; 2 = usage error
}

// scripts/deploy-cli.sh leaves the deployed commit next to the binary
static int ShowVersion()
{
    var stamp = Path.Combine(AppContext.BaseDirectory, "COMMIT");
    Console.WriteLine(File.Exists(stamp)
        ? File.ReadAllText(stamp).Trim()
        : "development build (no COMMIT file next to the binary)");
    return 0;
}

// Follows the format value as it travels: DetectFormat returns it, this method
// keeps it in a variable, and DescribeRoute receives it as a parameter.
static int ShowFormat(string file)
{
    SourceFormat format = DataFrameProvider.DetectFormat(file);

    Console.WriteLine($"format: {format}");                       // the value's name
    Console.WriteLine($"type:   {format.GetType().FullName}");    // what kind of thing it is
    Console.WriteLine($"route:  {DescribeRoute(format)}");        // handed to another method

    if (format is SourceFormat.Sqlite or SourceFormat.Excel)
        Console.WriteLine($"tables: {string.Join(", ", DataFrameProvider.GetTableNames(file))}");
    return 0;
}

static string DescribeRoute(SourceFormat format) => format switch
{
    SourceFormat.Sqlite => "each table read into its own DataFrame",
    SourceFormat.Parquet => "read as is",
    SourceFormat.Csv or SourceFormat.Excel => "converted once to a temp parquet",
    var other => $"no route for {other}",
};

static async Task<int> ShowColumns(string file)
{
    SourceFormat format = DataFrameProvider.DetectFormat(file);
    switch (format)
    {
        case SourceFormat.Sqlite:
        {
            // one line per table: table -> col1, col2, ...
            var tables = await DataFrameProvider.ReadSqliteTablesAsync(file);
            try
            {
                foreach (var (table, df) in tables)
                    Console.WriteLine($"{table} -> {string.Join(", ", df.Schema.ToList().Select(c => c.Name))}");
            }
            finally
            {
                foreach (var df in tables.Values)
                    df.Dispose();   // each DataFrame holds native memory
            }
            break;
        }
        case SourceFormat.Parquet:
        case SourceFormat.Csv:
        case SourceFormat.Excel:
            foreach (var name in await ColumnNamesAsync(file))
                Console.WriteLine(name);
            break;
        default:
            throw new NotSupportedException($"The CLI has no reading route for {format}.");
    }
    return 0;
}

static async Task<int> ShowRowNumber(string file)
{
    if (DataFrameProvider.DetectFormat(file) == SourceFormat.Sqlite)
    {
        // one count per table: a sum across tables means nothing
        var tables = await DataFrameProvider.ReadSqliteTablesAsync(file);
        try
        {
            foreach (var (table, df) in tables)
                Console.WriteLine($"{table} -> {df.Len}");
        }
        finally
        {
            foreach (var df in tables.Values)
                df.Dispose();
        }
        return 0;
    }

    var (parquet, isTemp) = await DataFrameProvider.EnsureParquetAsync(file);
    try
    {
        // parquet keeps the row count in its metadata: no data is read
        using var lf = LazyFrame.ScanParquet(parquet);
        using var counted = lf.Select(Len());
        using var count = counted.Collect();
        using var batch = count.ToArrow();
        Console.WriteLine(PolarsTableAdapter.GetCellValue(batch.Column(0), 0));
    }
    finally
    {
        if (isTemp) File.Delete(parquet);
    }
    return 0;
}

// The grid's own path: parquet (converted if needed) → projection + limit →
// Collect → ToArrow, then every cell read straight from the Arrow buffer by
// PolarsTableAdapter.GetCellValue — what the grid's CellValueNeeded does.
static async Task<int> ShowHead(string file, int rows)
{
    // SQLite: the first rows of each table, the way `columns` lists each table
    string[] tables = DataFrameProvider.DetectFormat(file) == SourceFormat.Sqlite
        ? DataFrameProvider.GetTableNames(file)
        : [];

    for (int i = 0; i < Math.Max(tables.Length, 1); i++)
    {
        if (tables.Length > 0)
            Console.WriteLine($"{(i > 0 ? "\n" : "")}== {tables[i]}");

        var (parquet, isTemp) = await DataFrameProvider.EnsureParquetAsync(file, tableIndex: i);
        try
        {
            var columns = await DataFrameProvider.GetColumnNamesAsync(parquet);
            using var df = await DataFrameProvider.GetDataFrameAsync(parquet, columns, limit: (uint)rows);
            using var batch = df.ToArrow();
            PrintBatch(batch);
        }
        finally
        {
            if (isTemp) File.Delete(parquet);
        }
    }
    return 0;
}


// Filtering by time, journalctl-style: acrux filter <file> <column> --last "1 hour" [-n N]
// (the span takes systemd's units: 30min, 1h 30min, 2 days, 1 week...)
static async Task<int> FilterLast(string file, string column, string spanText, int rows)
{
    var span = TimeFilter.ParseSpan(spanText);
    // "the last hour" ends now: rows dated in the future stay out
    var until = DateTimeOffset.Now;
    var since = until - span;
    Console.WriteLine($"from {Stamp(since)} to {Stamp(until)} (--last \"{spanText}\")");

    if (DataFrameProvider.DetectFormat(file) == SourceFormat.Sqlite)
    {
        // the column may live in any table: filter every table that has it
        var tables = await DataFrameProvider.ReadSqliteTablesAsync(file);
        try
        {
            var found = tables.Where(t => ColumnNames(t.Value).Contains(column)).ToList();
            if (found.Count == 0)
                throw new ArgumentException(ColumnNotFound(column, tables.Values.SelectMany(ColumnNames)));
            foreach (var (table, df) in found)
            {
                Console.WriteLine($"== {table}");
                PrintWindow(() => df.Lazy(), column, since, until, rows);
            }
        }
        finally
        {
            foreach (var df in tables.Values)
                df.Dispose();
        }
        return 0;
    }

    var (parquet, isTemp) = await DataFrameProvider.EnsureParquetAsync(file);
    try
    {
        var columns = await DataFrameProvider.GetColumnNamesAsync(parquet);
        if (!columns.Contains(column))
            throw new ArgumentException(ColumnNotFound(column, columns));
        PrintWindow(() => LazyFrame.ScanParquet(parquet), column, since, until, rows);
    }
    finally
    {
        if (isTemp) File.Delete(parquet);
    }
    return 0;
}

// The filter goes into the lazy plan: only the matching rows are read. Rows
// come newest first — "the last hour" reads from now backwards — and Sort +
// Limit lets Polars pick the N most recent without sorting everything. Each
// query builds its own LazyFrame and Expr — Polars.NET 0.6.0 consumed them.
static void PrintWindow(Func<LazyFrame> source, string column, DateTimeOffset since, DateTimeOffset until, int rows)
{
    DataType type;
    using (var probe = source())
        type = probe.Schema.ToList().First(c => c.Name == column).Type;

    long total;
    using (var lf = source())
    using (var matching = lf.Filter(TimeFilter.Between(column, type, since, until)))
    using (var count = matching.Select(Col(column).Count()).Collect())
    using (var countBatch = count.ToArrow())
        total = Convert.ToInt64(PolarsTableAdapter.GetCellValue(countBatch.Column(0), 0), CultureInfo.InvariantCulture);

    using var lf2 = source();
    using var matching2 = lf2.Filter(TimeFilter.Between(column, type, since, until));
    using var newestFirst = matching2.Sort(column, descending: true);
    using var first = newestFirst.Limit((uint)rows);
    using var df = first.Collect();
    using var batch = df.ToArrow();
    PrintBatch(batch);
    Console.WriteLine($"{total} matching rows");
}

static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);


static void PrintBatch(Apache.Arrow.RecordBatch batch)
{
    const int MaxCellWidth = 30;
    var names = batch.Schema.FieldsList.Select(f => f.Name).ToArray();
    var cells = new string[batch.Length][];
    // the column's type, not the value, says whether a DateTime is a date or a moment
    var dateOnly = names.Select((_, c) => batch.Column(c) is Apache.Arrow.Date32Array or Apache.Arrow.Date64Array).ToArray();
    for (int r = 0; r < batch.Length; r++)
    {
        cells[r] = new string[names.Length];
        for (int c = 0; c < names.Length; c++)
            cells[r][c] = FormatCell(PolarsTableAdapter.GetCellValue(batch.Column(c), r), dateOnly[c]);
    }

    var widths = names
        .Select((name, c) => Math.Min(MaxCellWidth, cells.Select(row => row[c].Length).Prepend(name.Length).Max()))
        .ToArray();
    Console.WriteLine(string.Join(" | ", names.Select((name, c) => Fit(name, widths[c]))));
    Console.WriteLine(string.Join("-+-", widths.Select(w => new string('-', w))));
    foreach (var row in cells)
        Console.WriteLine(string.Join(" | ", row.Select((value, c) => Fit(value, widths[c]))));
    Console.WriteLine($"({batch.Length} rows × {names.Length} columns)");
}

// ISO for dates on every machine: 07/11 is November here and July in the US
static string FormatCell(object value, bool dateOnly) => value switch
{
    DBNull => "null",
    DateTime d when dateOnly => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
    DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),   // 3.5, never "3,5"
    _ => (value.ToString() ?? "").ReplaceLineEndings(" "),
};

static async Task<int> ShowSpecificColumn(string file, string column, int rows)
{
    if (DataFrameProvider.DetectFormat(file) == SourceFormat.Sqlite)
    {
        // the column may live in any table: show it from every table that has it
        var tables = await DataFrameProvider.ReadSqliteTablesAsync(file);
        try
        {
            var found = tables.Where(t => ColumnNames(t.Value).Contains(column)).ToList();
            if (found.Count == 0)
                throw new ArgumentException(ColumnNotFound(column, tables.Values.SelectMany(ColumnNames)));
            foreach (var (table, df) in found)
            {
                Console.WriteLine($"== {table}");
                using var only = df.Select(column);
                using var first = only.Head(rows);
                using var batch = first.ToArrow();
                PrintBatch(batch);
            }
        }
        finally
        {
            foreach (var df in tables.Values)
                df.Dispose();
        }
        return 0;
    }

    var (parquet, isTemp) = await DataFrameProvider.EnsureParquetAsync(file);
    try
    {
        // checked against the schema first: Polars' own error dumps the whole query plan
        var columns = await DataFrameProvider.GetColumnNamesAsync(parquet);
        if (!columns.Contains(column))
            throw new ArgumentException(ColumnNotFound(column, columns));

        using var df = await DataFrameProvider.GetDataFrameAsync(parquet, [column], limit: (uint)rows);
        using var batch = df.ToArrow();
        PrintBatch(batch);
    }
    finally
    {
        if (isTemp) File.Delete(parquet);
    }
    return 0;
}

static IEnumerable<string> ColumnNames(DataFrame df) => df.Schema.ToList().Select(c => c.Name);

// column names are case-sensitive; a near miss gets a hint
static string ColumnNotFound(string column, IEnumerable<string> available)
{
    var names = available.Distinct().ToList();
    var hint = names.FirstOrDefault(n => string.Equals(n, column, StringComparison.OrdinalIgnoreCase));
    return $"no column \"{column}\"" + (hint is null ? "" : $" (did you mean \"{hint}\"?)") +
           $"; columns: {string.Join(", ", names)}";
}


// pads or truncates to the column width, never splitting an emoji's surrogate pair
static string Fit(string text, int width)
{
    if (text.Length <= width)
        return text.PadRight(width);
    int cut = width - 1;
    if (char.IsHighSurrogate(text[cut - 1]))
        cut--;
    return text[..cut] + "…";
}

// Name and Polars type of every column; on SQLite, per table. One column per
// line: types like "datetime[μs, UTC]" carry commas, so a comma list would blur.
static async Task<int> Schema(string file)
{
    if (DataFrameProvider.DetectFormat(file) == SourceFormat.Sqlite)
    {
        var tables = await DataFrameProvider.ReadSqliteTablesAsync(file);
        try
        {
            foreach (var (table, df) in tables)
            {
                Console.WriteLine($"== {table}");
                PrintSchema(df.Schema);
            }
        }
        finally
        {
            foreach (var df in tables.Values)
                df.Dispose();
        }
        return 0;
    }

    var (parquet, isTemp) = await DataFrameProvider.EnsureParquetAsync(file);
    try
    {
        using var lf = LazyFrame.ScanParquet(parquet);   // the schema only: no data is read
        PrintSchema(lf.Schema);
    }
    finally
    {
        if (isTemp) File.Delete(parquet);
    }
    return 0;
}

static void PrintSchema(PolarsSchema schema)
{
    var columns = schema.ToList();
    int width = Math.Min(columns.Select(c => c.Name.Length).DefaultIfEmpty(0).Max(), 40);
    foreach (var c in columns)
        Console.WriteLine($"{c.Name.PadRight(width)}  {c.Type}");
}

// The parquet route reads the schema only: listing the columns of a
// multi-million-row file must not load it.
static async Task<string[]> ColumnNamesAsync(string file)
{
    var (parquet, isTemp) = await DataFrameProvider.EnsureParquetAsync(file);
    try
    {
        return await DataFrameProvider.GetColumnNamesAsync(parquet);
    }
    finally
    {
        if (isTemp) File.Delete(parquet);
    }
}

static int Usage()
{
    Console.Error.WriteLine("usage: acrux <schema|columns|head [-n N]|rows|type> <file> | acrux column <file> <name> [-n N] | acrux filter <file> <column> --last <span> [-n N] | acrux version");
    return 2;
}
