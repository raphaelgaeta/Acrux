using Polars.CSharp;
using static Polars.CSharp.Polars;

namespace Acrux;

/// <summary>
/// Turns text columns that hold dates into real Date/Datetime columns, only
/// when the reading is unambiguous for the WHOLE column:
/// - ISO 8601 (2026-11-07, 2026-11-07T19:47:04[.fff][Z|±hh:mm]): always;
/// - dd/mm/yyyy vs mm/dd/yyyy (optionally with hh:mm[:ss]): only when the
///   data proves the order — a first number above 12 means day/month, a
///   second one above 12 means month/day. All ≤ 12 stays text: 07/11 is
///   November in Brazil and July in the US, and a guess corrupts silently
///   (Polars' own inference always assumes day/month — tested);
/// - all or nothing: if any non-null value fails to convert (31/02/2026),
///   the column stays text.
/// Used for sources without a date type (CSV, SQLite); parquet and xlsx
/// keep their native types.
/// </summary>
public static class TextDates
{
    // a non-matching value in these first rows already rules a shape out
    private const uint HeadRows = 1000;

    private enum Order { None, DayFirst, MonthFirst }

    private sealed record Shape(string Regex, bool NeedsOrder, Func<string, Order, Expr> Parse);

    private static readonly Shape[] Shapes =
    [
        new(@"^\d{4}-\d{2}-\d{2}$", false,
            (c, _) => Col(c).Str.ToDate("%Y-%m-%d", false, true, true)),
        new(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?$", false,
            (c, _) => Iso(c).Str.ToDatetime(format: "%Y-%m-%dT%H:%M:%S%.f", strict: false)),
        new(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})$", false,
            (c, _) => Iso(c).Str.ToDatetime(format: "%Y-%m-%dT%H:%M:%S%.f%:z", strict: false)),
        new(@"^(\d{1,2})/(\d{1,2})/\d{4}$", true,
            (c, o) => Col(c).Str.ToDate(o == Order.DayFirst ? "%d/%m/%Y" : "%m/%d/%Y", false, true, true)),
        new(@"^(\d{1,2})/(\d{1,2})/\d{4} \d{2}:\d{2}$", true,
            (c, o) => Col(c).Str.ToDatetime(format: o == Order.DayFirst ? "%d/%m/%Y %H:%M" : "%m/%d/%Y %H:%M", strict: false)),
        new(@"^(\d{1,2})/(\d{1,2})/\d{4} \d{2}:\d{2}:\d{2}$", true,
            (c, o) => Col(c).Str.ToDatetime(format: o == Order.DayFirst ? "%d/%m/%Y %H:%M:%S" : "%m/%d/%Y %H:%M:%S", strict: false)),
    ];

    // one format covers the ISO variants: " " → "T", trailing "Z" → "+00:00"
    private static Expr Iso(string column) =>
        Col(column).Str.Replace(" ", "T", true, 1).Str.Replace("Z$", "+00:00", false, 1);

    /// <summary>
    /// The DataFrame with its date text columns converted, or null when there
    /// is nothing to convert. The caller disposes both.
    /// </summary>
    public static DataFrame? Convert(DataFrame df)
    {
        var conversions = Detect(() => df.Lazy(), TextColumns(df.Schema));
        if (conversions.Count == 0)
            return null;
        using var lf = df.Lazy();
        using var converted = lf.WithColumns(conversions.Select(c => c.Build()));
        return converted.Collect();
    }

    /// <summary>
    /// Rewrites a parquet file with its date text columns converted, streaming.
    /// Returns the new path (the old file is deleted), or the same path when
    /// there is nothing to convert. Dates are a convenience: if the rewrite
    /// fails, the original file is kept with its text columns.
    /// </summary>
    public static string ConvertParquet(string path, string tempDir, string baseName)
    {
        PolarsSchema schema;
        using (var probe = LazyFrame.ScanParquet(path))
            schema = probe.Schema;
        var conversions = Detect(() => LazyFrame.ScanParquet(path), TextColumns(schema));
        if (conversions.Count == 0)
            return path;

        var output = Path.Combine(tempDir, $"{baseName}_pid{Environment.ProcessId}_{Guid.NewGuid():N}.parquet");
        try
        {
            using var lf = LazyFrame.ScanParquet(path);
            using var converted = lf.WithColumns(conversions.Select(c => c.Build()));
            converted.SinkParquet(output);
        }
        catch
        {
            try { File.Delete(output); } catch (IOException) { }
            return path;
        }
        File.Delete(path);
        return output;
    }

    private static List<string> TextColumns(PolarsSchema schema) =>
        [.. schema.ToList().Where(c => c.Type.ToString() == "str").Select(c => c.Name)];

    private sealed record Conversion(string Column, Shape Shape, Order Order)
    {
        // Exprs are built fresh for every use: Polars.NET 0.6.0 consumed them (1.0 no longer does)
        public Expr Build() => Shape.Parse(Column, Order).Alias(Column);
    }

    private static List<Conversion> Detect(Func<LazyFrame> source, List<string> columns)
    {
        if (columns.Count == 0)
            return [];

        // 1. first rows: keep only the (column, shape) pairs every value there matches
        var pairs = columns.SelectMany(c => Shapes.Select(s => (Column: c, Shape: s))).ToList();
        var inHead = Query(source, lf => lf.Limit(HeadRows),
            pairs.Select(p => AllMatch(p.Column, p.Shape)));
        var candidates = pairs.Where((_, i) => inHead[i] is true).ToList();
        if (candidates.Count == 0)
            return [];

        // 2. whole column: the shape must hold for every value; slash dates collect
        // the evidence for the day/month order (largest first and second number)
        var whole = Query(source, lf => lf, candidates.SelectMany(p => new[]
        {
            AllMatch(p.Column, p.Shape) & (Col(p.Column).Count() > Lit(0)),
            First(p.Column, p.Shape, 1).Max(),
            First(p.Column, p.Shape, 2).Max(),
        }));
        var chosen = new List<Conversion>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var (column, shape) = candidates[i];
            if (whole[3 * i] is not true || chosen.Any(c => c.Column == column))
                continue;
            var order = Order.None;
            if (shape.NeedsOrder)
            {
                int first = System.Convert.ToInt32(whole[3 * i + 1] ?? 0);
                int second = System.Convert.ToInt32(whole[3 * i + 2] ?? 0);
                order = (first > 12, second > 12) switch
                {
                    (true, false) => Order.DayFirst,
                    (false, true) => Order.MonthFirst,
                    _ => Order.None,   // all ≤ 12: ambiguous; both > 12: not a date
                };
                if (order == Order.None)
                    continue;
            }
            chosen.Add(new Conversion(column, shape, order));
        }
        if (chosen.Count == 0)
            return [];

        // 3. all or nothing: the conversion must not turn any value into null
        var lossless = Query(source, lf => lf,
            chosen.Select(c => c.Shape.Parse(c.Column, c.Order).Count() == Col(c.Column).Count()));
        return [.. chosen.Where((_, i) => lossless[i] is true)];
    }

    private static Expr AllMatch(string column, Shape shape) =>
        Col(column).Str.Contains(shape.Regex, false, true).Sum() == Col(column).Count();

    // the first or second number of a slash date; null for other shapes
    private static Expr First(string column, Shape shape, int group) => shape.NeedsOrder
        ? Col(column).Str.Extract(shape.Regex, group).Cast(DataType.Int32)
        : Lit(0);

    // one aggregate row; values in expression order (bool, int or null)
    private static object?[] Query(Func<LazyFrame> source, Func<LazyFrame, LazyFrame> narrow, IEnumerable<Expr> expressions)
    {
        var named = expressions.Select((e, i) => e.Alias($"q{i}")).ToArray();
        using var lf = source();
        using var narrowed = narrow(lf);
        using var query = narrowed.Select(named);
        using var df = query.Collect();
        using var row = df.ToArrow();
        return [.. Enumerable.Range(0, row.ColumnCount).Select(i =>
            PolarsTableAdapter.GetCellValue(row.Column(i), 0) is var v && v is DBNull ? null : v)];
    }
}
