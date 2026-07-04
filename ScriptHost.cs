using Apache.Arrow;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Polars.CSharp;

namespace PolarsGridViewer;

/// <summary>
/// Globals exposed to terminal scripts. Must be public so Roslyn can bind to it.
/// </summary>
public class ScriptGlobals
{
    /// <summary>LazyFrame the script operates on (fresh scan, stateless).</summary>
    public LazyFrame lf = null!;
}

/// <summary>
/// Runs the terminal's C# Polars scripts through Roslyn. Stateless: every
/// execution starts from a fresh <c>LazyFrame.ScanParquet</c>; each script
/// must end in a <c>LazyFrame</c> or <c>DataFrame</c> expression, which becomes
/// the RecordBatch shown in the grid (same zero-materialization path as the
/// rest of the app).
/// </summary>
public static class ScriptHost
{
    /// <summary>Row cap applied to the result when the user leaves the limit on.</summary>
    public const uint RowCap = 1_000_000;

    // Lazy instead of a static ctor: if building the options fails, the real
    // message reaches the terminal instead of an opaque TypeInitializationException.
    // NOTE: Roslyn's scripting API resolves references (including the corlib,
    // internally) through Assembly.Location, which is empty in a pure
    // single-file bundle — every script fails with NotSupportedException
    // (dotnet/roslyn#50719). That's why the csproj publishes with
    // IncludeAllContentForSelfExtract=true; do not remove it.
    private static readonly Lazy<ScriptOptions> Options = new(() => ScriptOptions.Default
        .WithReferences(typeof(LazyFrame).Assembly, typeof(RecordBatch).Assembly)
        .WithImports("System", "System.Linq", "Polars.CSharp", "Polars.CSharp.Polars"));

    /// <summary>
    /// Replays the script chain: step 1 receives the file scan in <c>lf</c> and
    /// every following step receives the previous step's LazyFrame. Nothing is
    /// collected in between — the steps compose a single lazy plan, collected
    /// only at the end (optionally capped at <see cref="RowCap"/> rows).
    /// Returns the result RecordBatch (caller owns it). Throws
    /// <see cref="CompilationErrorException"/> for syntax errors and the Polars
    /// exception for query errors (e.g. unknown column).
    /// </summary>
    public static async Task<RecordBatch> RunChainAsync(
        IReadOnlyList<string> steps, string parquetPath, bool applyRowCap, CancellationToken cancellationToken)
    {
        if (steps.Count == 0)
            throw new ArgumentException("Empty script chain.", nameof(steps));

        return await Task.Run(async () =>
        {
            var lf = LazyFrame.ScanParquet(parquetPath);
            try
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    var state = await CSharpScript.RunAsync(
                        steps[i], Options.Value, new ScriptGlobals { lf = lf }, typeof(ScriptGlobals), cancellationToken);

                    var next = state.ReturnValue switch
                    {
                        LazyFrame result => result,
                        // step ended in .Collect(): back to lazy via df.Lazy() —
                        // probe-validated that the plan survives the DataFrame's
                        // Dispose; the collected data stays materialized inside
                        // the plan for the rest of the replay (prefer lazy steps)
                        DataFrame df => Relazify(df),
                        null => throw new InvalidOperationException(
                            $"Step {i + 1} returned nothing. End with a LazyFrame or DataFrame expression (no trailing semicolon)."),
                        var other => throw new InvalidOperationException(
                            $"Step {i + 1} must end in a LazyFrame or DataFrame expression; got {other.GetType().Name}.")
                    };

                    // probe-validated: deriving/collecting after disposing the source is safe
                    if (!ReferenceEquals(next, lf))
                    {
                        lf.Dispose();
                        lf = next;
                    }
                }

                var query = applyRowCap ? lf.Limit(RowCap) : lf;
                try
                {
                    using var df = query.Collect();
                    return df.ToArrow();
                }
                finally
                {
                    if (!ReferenceEquals(query, lf))
                        query.Dispose();
                }
            }
            finally
            {
                lf.Dispose();
            }
        }, cancellationToken);
    }

    private static LazyFrame Relazify(DataFrame df)
    {
        using (df)
            return df.Lazy();
    }
}
