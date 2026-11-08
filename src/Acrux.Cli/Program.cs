using System.Globalization;
using Acrux;
using Polars.CSharp;

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
    SourceFormat.Sqlite => "every table in one DataFrame, tagged by tableName",
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
    using var df = await LoadAsync(file);
    Console.WriteLine(df.Len);
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

static void PrintBatch(Apache.Arrow.RecordBatch batch)
{
    const int MaxCellWidth = 30;
    var names = batch.Schema.FieldsList.Select(f => f.Name).ToArray();
    var cells = new string[batch.Length][];
    for (int r = 0; r < batch.Length; r++)
    {
        cells[r] = new string[names.Length];
        for (int c = 0; c < names.Length; c++)
            cells[r][c] = FormatCell(PolarsTableAdapter.GetCellValue(batch.Column(c), r));
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

static string FormatCell(object value) => value switch
{
    DBNull => "null",
    DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss"),
    DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss zzz"),
    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),   // 3.5, never "3,5"
    _ => (value.ToString() ?? "").ReplaceLineEndings(" "),
};

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

static async Task<int> Schema(string file)
{
    foreach (var name in await ColumnNamesAsync(file))
        Console.WriteLine(name);
    return 0;
}

// The file format is detected once, by content, and picks the reading route.
static async Task<DataFrame> LoadAsync(string file) =>
    DataFrameProvider.DetectFormat(file) switch
    {
        // every table in one DataFrame, tagged by a "tableName" column
        SourceFormat.Sqlite => await DataFrameProvider.ReadSqliteTablesToSingleDataFrameAsync(file),
        // already parquet: read as is
        SourceFormat.Parquet => await DataFrameProvider.LoadDataFrameAsync(file),
        // one-time conversion to a temp parquet (xlsx: first sheet)
        SourceFormat.Csv or SourceFormat.Excel => await LoadViaTempParquetAsync(file),
        // a format added to the enum without a route here
        var format => throw new NotSupportedException($"The CLI has no reading route for {format}."),
    };

static async Task<DataFrame> LoadViaTempParquetAsync(string file)
{
    var (parquet, isTemp) = await DataFrameProvider.EnsureParquetAsync(file);
    try
    {
        return await DataFrameProvider.LoadDataFrameAsync(parquet);
    }
    finally
    {
        if (isTemp) File.Delete(parquet);   // the temp parquet is ours to delete
    }
}

// Same split, but the parquet route reads the schema only: listing the columns
// of a multi-million-row file must not load it.
static async Task<string[]> ColumnNamesAsync(string file)
{
    if (DataFrameProvider.DetectFormat(file) == SourceFormat.Sqlite)
    {
        using var df = await DataFrameProvider.ReadSqliteTablesToSingleDataFrameAsync(file);
        return [.. df.Schema.ToList().Select(c => c.Name)];
    }

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
    Console.Error.WriteLine("usage: acrux <schema|columns|head [-n N]|rows|type> <file>");
    return 2;
}
