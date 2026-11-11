namespace Acrux;

/// <summary>
/// Whether a query returns its rows in the same order every time it runs,
/// read from the optimized plan (<c>LazyFrame.Explain(true)</c>). A script
/// result is computed one column at a time, each column by its own run of the
/// plan; if the order changed between runs, the columns would line up wrong,
/// with no error. Checked against Polars.NET 1.0.1 (probe, 2026-11-11): a
/// filter, a sort with millions of ties and a group by gave the same order in
/// 9 runs, alone, beside other columns and in parallel; a Unique gave a
/// different order in each of 7.
/// </summary>
public static class PlanOrder
{
    // The plan's nodes, and the lines under them, whose order repeats. A plan
    // with anything else is computed in one run: an unknown node costs memory,
    // a wrong guess costs wrong data.
    private static readonly string[] Stable =
    [
        "Parquet SCAN", "PROJECT ", "SELECTION:", "SLICE:", "ROW_INDEX:", "ESTIMATED ROWS:",
        "DF [",                        // a step that ended in Collect()
        "SLICE[", "FILTER ", "FROM", "SELECT [", "simple π", "WITH_COLUMNS:",
        "SORT BY [",                   // ties kept their order in every run measured
        "AGGREGATE[maintain_order: true]",   // Polars.NET's GroupBy keeps the order by default
        "UNIQUE[maintain_order: true",
        "EXPLODE [", "UNPIVOT ",
        "[",                           // a node's expressions, on the line under it
    ];

    // Expressions whose result changes order (or values) from run to run.
    private static readonly (string Text, string Reason)[] Unstable =
    [
        ("maintain_order: false", "no maintainOrder"),
        (".unique()", "Unique() without maintainOrder"),
        (".shuffle(", "Shuffle()"),
        (".sample(", "Sample()"),
        (".value_counts(", "ValueCounts()"),
        (".mode()", "Mode()"),
    ];

    /// <summary>
    /// Null when the rows come in the same order every run; otherwise what in
    /// the plan can change it, for a message.
    /// </summary>
    public static string? WhyUnstable(string explain)
    {
        foreach (var raw in explain.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            foreach (var (text, reason) in Unstable)
                if (line.Contains(text, StringComparison.Ordinal))
                    return line.StartsWith("AGGREGATE", StringComparison.Ordinal) ? "GroupBy without maintainOrder"
                        : line.StartsWith("UNIQUE", StringComparison.Ordinal) ? "Unique without maintainOrder"
                        : reason;

            if (!Stable.Any(head => line.StartsWith(head, StringComparison.Ordinal)))
                return line.Contains("JOIN", StringComparison.Ordinal) ? "a join"
                    : line.Split(' ', '[', ':')[0];
        }
        return null;
    }
}
