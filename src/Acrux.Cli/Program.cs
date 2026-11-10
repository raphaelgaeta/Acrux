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
        ["rows", var file, .. var rest] when TryOptions(rest, out var where, out var n) && n is null => await ShowRowNumber(file, where),
        ["type", var file] => ShowFormat(file),
        ["version"] => ShowVersion(),
        ["column", var file, var column] => await ShowSpecificColumn(file, column, 10),
        ["column", var file, var column, "-n", var n] when int.TryParse(n, out var rows) && rows > 0 => await ShowSpecificColumn(file, column, rows),
        ["values", var file, var column, .. var rest] when TryOptions(rest, out var where, out var n) => await ShowValues(file, column, where, n ?? 100),
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

// A local deploy (scripts/deploy-cli.sh) leaves its commit next to the binary;
// otherwise the build's own version: the release tag, or 0.0.0-dev
// (Directory.Build.props), plus the commit the SDK appends after a "+"
static int ShowVersion()
{
    var stamp = Path.Combine(AppContext.BaseDirectory, "COMMIT");
    if (File.Exists(stamp))
    {
        Console.WriteLine(File.ReadAllText(stamp).Trim());
        return 0;
    }
    var info = System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "unknown";
    var parts = info.Split('+', 2);
    Console.WriteLine(parts.Length == 2 ? $"{parts[0]} ({parts[1][..Math.Min(7, parts[1].Length)]})" : parts[0]);
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

// Without --where, parquet answers from its metadata: no data is read.
// SQLite: one count per table (a sum across tables means nothing), and with
// --where, only the tables that have its columns.
static async Task<int> ShowRowNumber(string file, Dictionary<string, ColumnFilter> where)
{
    await ForEachTable(file, [.. where.Keys], async (table, parquet) =>
    {
        var count = await FilterEngine.CountRowsAsync(parquet, FilterEngine.BuildPredicate(where));
        Console.WriteLine(table is null ? $"{count}" : $"{table} -> {count}");
    });
    return 0;
}

// Distinct values of a column, one per line, sorted as text: the exact text
// --where matches. Every --where applies, the column's own included, unlike
// the grid's dropdown, which keeps a filtered column's other values on show
// so they can be checked back.
static async Task<int> ShowValues(string file, string column, Dictionary<string, ColumnFilter> where, int limit)
{
    await ForEachTable(file, [column, .. where.Keys], async (table, parquet) =>
    {
        if (table is not null)
            Console.WriteLine($"== {table}");
        var (values, hasBlanks, capped) = await FilterEngine.GetDistinctValuesAsync(
            parquet, column, FilterEngine.BuildPredicate(where), limit);
        foreach (var value in values)
            Console.WriteLine(value.ReplaceLineEndings(" "));
        var shown = $"{values.Length} value{(values.Length == 1 ? "" : "s")}{(hasBlanks ? " and null" : "")}";
        Console.WriteLine(capped ? $"(first {shown}; there are more: -n raises the limit)" : $"({shown})");
    });
    return 0;
}

// --where col=value, repeatable: the same column again is OR, another column
// is AND, and an empty value means null. The column ends at the first "=".
// Also -n N. Anything else is a usage error.
static bool TryOptions(string[] args, out Dictionary<string, ColumnFilter> where, out int? rows)
{
    where = [];
    rows = null;
    if (args.Length % 2 != 0)
        return false;

    var kept = new Dictionary<string, (HashSet<string> Values, bool Blanks)>();
    for (int i = 0; i < args.Length; i += 2)
    {
        var (option, value) = (args[i], args[i + 1]);
        int eq = value.IndexOf('=');
        if (option == "-n" && rows is null && int.TryParse(value, out var n) && n > 0)
            rows = n;
        else if (option == "--where" && eq > 0)
        {
            var (column, text) = (value[..eq], value[(eq + 1)..]);
            var (values, blanks) = kept.GetValueOrDefault(column, (new HashSet<string>(), false));
            if (text.Length == 0)
                blanks = true;
            else
                values.Add(text);
            kept[column] = (values, blanks);
        }
        else
            return false;
    }
    where = kept.ToDictionary(c => c.Key, c => new ColumnFilter(c.Value.Values, c.Value.Blanks));
    return true;
}

// Runs `action` on the file as parquet: once (table = null), or once per
// SQLite table that has every column in `needs`. Checked against the schema
// first: Polars' own error dumps the whole query plan.
static async Task ForEachTable(string file, string[] needs, Func<string?, string, Task> action)
{
    string[] tables = DataFrameProvider.DetectFormat(file) == SourceFormat.Sqlite
        ? DataFrameProvider.GetTableNames(file)
        : [];

    var seen = new List<string>();
    bool any = false;
    for (int i = 0; i < Math.Max(tables.Length, 1); i++)
    {
        var (parquet, isTemp) = await DataFrameProvider.EnsureParquetAsync(file, tableIndex: i);
        try
        {
            var columns = await DataFrameProvider.GetColumnNamesAsync(parquet);
            seen.AddRange(columns);
            if (!needs.All(columns.Contains))
                continue;
            any = true;
            await action(tables.Length > 0 ? tables[i] : null, parquet);
        }
        finally
        {
            if (isTemp) File.Delete(parquet);
        }
    }

    if (!any)
        throw new ArgumentException(needs.FirstOrDefault(c => !seen.Contains(c)) is { } missing
            ? ColumnNotFound(missing, seen)
            : $"no table has all of: {string.Join(", ", needs.Distinct())}");
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

// Two passes, so a wide file never sits in memory whole. The count and the
// newest-first positions read only the time column (TimeFilter.NewestRows);
// then each row is read where it lives (Slice), turned into text at once and
// its decoded block dropped: holding the slices kept ~13 MB per row alive on
// a 700-column file. Measured on 700 columns × 5M rows, the whole year in
// the window: the old filter-sort-limit plan passed 8 GB; this one stays
// near 0.6 GB, at ~28 ms per row shown (~4 ms with 5 columns).
static void PrintWindow(Func<LazyFrame> source, string column, DateTimeOffset since, DateTimeOffset until, int rows)
{
    DataType type;
    string[] names;
    using (var probe = source())
    {
        var schema = probe.Schema.ToList();
        type = schema.First(c => c.Name == column).Type;
        names = [.. schema.Select(c => c.Name)];
    }

    long total;
    using (var lf = source())
    using (var matching = lf.Filter(TimeFilter.Between(column, type, since, until)))
    using (var count = matching.Select(Col(column).Count()).Collect())
    using (var countBatch = count.ToArrow())
        total = Convert.ToInt64(PolarsTableAdapter.GetCellValue(countBatch.Column(0), 0), CultureInfo.InvariantCulture);

    long[] positions;
    using (var lf = source())
        positions = TimeFilter.NewestRows(lf, column, type, since, until, (uint)rows);

    var lines = new List<string[]>(positions.Length);
    foreach (var position in positions)
    {
        using var lf = source();
        using var one = lf.Slice(position, 1);
        using var df = one.Collect();
        using var batch = df.ToArrow();
        lines.Add(FormatRow(batch, 0));
    }
    PrintTable(names, lines);
    Console.WriteLine($"{total} matching rows");
}

static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);


static void PrintBatch(Apache.Arrow.RecordBatch batch) => PrintTable(
    [.. batch.Schema.FieldsList.Select(f => f.Name)],
    [.. Enumerable.Range(0, batch.Length).Select(r => FormatRow(batch, r))]);

static string[] FormatRow(Apache.Arrow.RecordBatch batch, int row) =>
    [.. Enumerable.Range(0, batch.ColumnCount).Select(c => FormatCell(
        PolarsTableAdapter.GetCellValue(batch.Column(c), row),
        // the column's type, not the value, says whether a DateTime is a date or a moment
        batch.Column(c) is Apache.Arrow.Date32Array or Apache.Arrow.Date64Array))];

static void PrintTable(string[] names, IReadOnlyList<string[]> cells)
{
    const int MaxCellWidth = 30;
    var widths = names
        .Select((name, c) => Math.Min(MaxCellWidth, cells.Select(row => row[c].Length).Prepend(name.Length).Max()))
        .ToArray();
    Console.WriteLine(string.Join(" | ", names.Select((name, c) => Fit(name, widths[c]))));
    Console.WriteLine(string.Join("-+-", widths.Select(w => new string('-', w))));
    foreach (var row in cells)
        Console.WriteLine(string.Join(" | ", row.Select((value, c) => Fit(value, widths[c]))));
    Console.WriteLine($"({cells.Count} rows × {names.Length} columns)");
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
    Console.Error.WriteLine("usage: acrux <schema|columns|head [-n N]|type> <file> | acrux rows <file> [--where col=value]... | acrux values <file> <column> [--where col=value]... [-n N] | acrux column <file> <name> [-n N] | acrux filter <file> <column> --last <span> [-n N] | acrux version");
    return 2;
}
