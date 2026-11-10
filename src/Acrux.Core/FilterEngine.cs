using Apache.Arrow;
using Polars.CSharp;
using static Polars.CSharp.Polars;

namespace Acrux;

/// <summary>
/// One column's filter, Excel-style: the values kept, as text, and whether
/// blank (null) cells are kept too.
/// </summary>
public sealed record ColumnFilter(IReadOnlySet<string> Values, bool IncludeBlanks);

/// <summary>
/// Filter queries over the parquet file via LazyFrame. Rescans use projection
/// pushdown (only the predicate's columns are read), keeping memory flat
/// regardless of file size. Every query takes the filter as a predicate
/// (<see cref="Expr"/>), whatever built it: <see cref="BuildPredicate"/>,
/// <see cref="TimeFilter.Between"/>, or both joined with <c>&amp;</c>.
/// </summary>
public static class FilterEngine
{
    /// <summary>How many distinct values the grid's filter dropdown lists.</summary>
    public const int DistinctCap = 10_000;

    /// <summary>
    /// The column's distinct values among the rows passing <paramref name="where"/>,
    /// as text (the same text <see cref="BuildPredicate"/> matches), sorted,
    /// at most <paramref name="limit"/> of them. Excel's "the column's own
    /// filter doesn't narrow its list" is the caller's choice: build
    /// <paramref name="where"/> with BuildPredicate(filters, except: column).
    /// </summary>
    public static async Task<(string[] Values, bool HasBlanks, bool Capped)> GetDistinctValuesAsync(
        string parquetPath,
        string column,
        Expr? where = null,
        int limit = DistinctCap,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            using var rows = Scan(parquetPath, where);

            // +2 past the limit: one slot for the null, which sorts first,
            // and one to tell a full list from a truncated one. Engine.Streaming:
            // ~2x faster than Auto/InMemory for this Unique+Sort+Limit shape
            // (benchmarked on a 700col x 5M file; every other query ties)
            using var distinct = rows
                .Select(Col(column).Cast(DataType.String).Alias("v"))
                .Unique()
                .Sort("v")
                .Limit((uint)limit + 2);
            using var df = distinct.Collect(Engine.Streaming);
            using var batch = df.ToArrow();
            var array = batch.Column(0);

            bool hasBlanks = false;
            var values = new List<string>(batch.Length);
            for (int i = 0; i < batch.Length; i++)
            {
                if (array.IsNull(i))
                    hasBlanks = true;
                else
                    values.Add((string)PolarsTableAdapter.GetCellValue(array, i));
            }

            bool capped = values.Count > limit;
            if (capped)
                values.RemoveRange(limit, values.Count - limit);

            return (values.ToArray(), hasBlanks, capped);
        }, cancellationToken);
    }

    /// <summary>
    /// Indices (0-based, file order) of the rows passing <paramref name="where"/>.
    /// Null when there is no filter (the grid shows everything).
    /// </summary>
    public static async Task<int[]?> GetVisibleRowsAsync(
        string parquetPath,
        Expr? where,
        CancellationToken cancellationToken = default)
    {
        if (where is null)
            return null;

        return await Task.Run(() =>
        {
            using var scan = LazyFrame.ScanParquet(parquetPath);
            using var masked = scan.Select(where.Alias("mask"));
            using var df = masked.Collect();
            using var batch = df.ToArrow();
            var mask = (BooleanArray)batch.Column(0);

            var indices = new List<int>();
            for (int i = 0; i < mask.Length; i++)
            {
                if ((i & 0xFFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                // null (blank row in a filtered column without "(Blanks)") counts as false
                if (mask.GetValue(i) == true)
                    indices.Add(i);
            }

            return indices.ToArray();
        }, cancellationToken);
    }

    // The parquet's rows passing `where`, or all of them. The filtered plan
    // outlives its source's Dispose (the script chain relies on it too).
    private static LazyFrame Scan(string parquetPath, Expr? where)
    {
        var scan = LazyFrame.ScanParquet(parquetPath);
        if (where is null)
            return scan;
        using (scan)
            return scan.Filter(where);
    }

    /// <summary>
    /// AND across the filtered columns, skipping <paramref name="except"/>;
    /// within a column, its values OR blank when "(Blanks)" is kept. Values
    /// are matched as text, the way <see cref="GetDistinctValuesAsync"/> lists
    /// them. Null when nothing is left to filter.
    /// </summary>
    public static Expr? BuildPredicate(IReadOnlyDictionary<string, ColumnFilter> filters, string? except = null)
    {
        Expr? all = null;
        foreach (var (column, filter) in filters)
        {
            if (column == except)
                continue;

            var cond = Col(column).Cast(DataType.String).IsIn(filter.Values);
            if (filter.IncludeBlanks)
                cond |= Col(column).IsNull();

            all = all is null ? cond : all & cond;
        }
        return all;
    }
}
