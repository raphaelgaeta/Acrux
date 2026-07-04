using Apache.Arrow;
using Polars.CSharp;
using static Polars.CSharp.Polars;

namespace Acrux;

/// <summary>Active filter for one column (Excel-style: checked values + blanks).</summary>
public sealed class ColumnFilter
{
    public required HashSet<string> SelectedValues { get; init; }
    public bool IncludeBlanks { get; init; }
}

/// <summary>
/// Filter queries over the parquet file via LazyFrame. Rescans use projection
/// pushdown (only the predicate's columns are read), keeping the app's memory
/// flat regardless of file size.
/// </summary>
public static class FilterEngine
{
    public const int DistinctCap = 10_000;

    /// <summary>
    /// Distinct column values for the dropdown, stringified and sorted.
    /// Excel semantics: filters on the OTHER columns apply before collecting.
    /// </summary>
    public static async Task<(string[] Values, bool HasBlanks, bool Capped)> GetDistinctValuesAsync(
        string parquetPath,
        string column,
        IReadOnlyDictionary<string, ColumnFilter> activeFilters,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var lf = LazyFrame.ScanParquet(parquetPath);

            var predicate = BuildPredicate(activeFilters, excludeColumn: column);
            if (predicate is not null)
                lf = lf.Filter(predicate);

            // +1 past the cap to detect truncation. Engine.Streaming: ~2x
            // faster than Auto/InMemory for this Unique+Sort+Limit shape
            // (benchmarked on a 700col x 5M file; every other query ties)
            using var df = lf
                .Select(Col(column).Cast(DataType.String).Alias("v"))
                .Unique()
                .Sort("v")
                .Limit((uint)DistinctCap + 1)
                .Collect(Engine.Streaming);
            var batch = df.ToArrow();
            var array = batch.Column(0);

            bool hasBlanks = false;
            var values = new List<string>(batch.Length);
            for (int i = 0; i < batch.Length; i++)
            {
                if (array.IsNull(i))
                {
                    hasBlanks = true;
                    continue;
                }
                values.Add((string)PolarsTableAdapter.GetCellValue(array, i));
            }

            bool capped = values.Count > DistinctCap;
            if (capped)
                values.RemoveRange(DistinctCap, values.Count - DistinctCap);

            return (values.ToArray(), hasBlanks, capped);
        }, cancellationToken);
    }

    /// <summary>
    /// Indices (0-based, file order) of the rows passing ALL active filters.
    /// Returns null when no filter is active (grid shows everything).
    /// </summary>
    public static async Task<int[]?> GetVisibleRowsAsync(
        string parquetPath,
        IReadOnlyDictionary<string, ColumnFilter> activeFilters,
        CancellationToken cancellationToken = default)
    {
        if (activeFilters.Count == 0)
            return null;

        return await Task.Run(() =>
        {
            var predicate = BuildPredicate(activeFilters, excludeColumn: null)!;

            using var df = LazyFrame.ScanParquet(parquetPath)
                .Select(predicate.Alias("mask"))
                .Collect();

            var batch = df.ToArrow();
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

    /// <summary>
    /// AND across the filtered columns; within each column,
    /// IsIn(values) OR IsNull() when "(Blanks)" is checked.
    /// </summary>
    private static Expr? BuildPredicate(
        IReadOnlyDictionary<string, ColumnFilter> filters,
        string? excludeColumn)
    {
        Expr? acc = null;

        foreach (var (column, filter) in filters)
        {
            if (column == excludeColumn)
                continue;

            Expr cond = Col(column)
                .Cast(DataType.String)
                .IsIn(Lit(Series.From("__values", filter.SelectedValues.ToArray())).Implode());

            if (filter.IncludeBlanks)
                cond = cond | Col(column).IsNull();

            acc = acc is null ? cond : acc & cond;
        }

        return acc;
    }
}
