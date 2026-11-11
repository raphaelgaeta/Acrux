using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Apache.Arrow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Polars.CSharp;

namespace Acrux;

/// <summary>
/// Globals exposed to terminal scripts. Must be public so Roslyn can bind to it.
/// </summary>
public class ScriptGlobals
{
    /// <summary>
    /// The table the step works on: the previous step's result, or the file as
    /// the grid shows it. Named df because that is the name people who work
    /// with data reach for, but it is a LazyFrame: a plan, with no data yet.
    /// </summary>
    public LazyFrame df = null!;

    /// <summary>The same plan as <see cref="df"/>, under its earlier name, so older scripts still run.</summary>
    public LazyFrame lf => df;
}

/// <summary>
/// Runs the terminal's C# Polars scripts through Roslyn. Stateless: every
/// execution starts from a fresh <c>LazyFrame.ScanParquet</c>; each script
/// must end in a <c>LazyFrame</c> or <c>DataFrame</c> expression. The result
/// is a plan, not data: <see cref="ColumnCache.FromPlanAsync"/> computes it
/// column by column as the grid reaches each one.
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
    /// Replays the script chain: step 1 receives in <c>df</c> the file *as
    /// shown in the grid* — base column selection and base filters applied as
    /// an implicit step 0 — and every following step receives the previous
    /// step's LazyFrame. Nothing is collected: the steps compose a single
    /// lazy plan (optionally capped at <see cref="RowCap"/> rows), returned
    /// for the caller to own and run. Throws
    /// <see cref="CompilationErrorException"/> for syntax errors and the
    /// Polars exception for query errors (e.g. unknown column), which may
    /// also surface only when the plan runs.
    /// </summary>
    public static async Task<LazyFrame> BuildChainAsync(
        IReadOnlyList<string> steps,
        string parquetPath,
        string[]? baseColumns,
        IReadOnlyDictionary<string, ColumnFilter>? baseFilters,
        bool applyRowCap,
        CancellationToken cancellationToken)
    {
        if (steps.Count == 0)
            throw new ArgumentException("Empty script chain.", nameof(steps));

        return await Task.Run(async () =>
        {
            var lf = LazyFrame.ScanParquet(parquetPath);
            try
            {
                // implicit step 0: the grid's visual state. Filter first: a
                // filter can sit on a column the grid hides.
                if (baseFilters is { Count: > 0 } &&
                    FilterEngine.BuildPredicate(baseFilters) is { } predicate)
                {
                    var filtered = lf.Filter(predicate);
                    lf.Dispose();
                    lf = filtered;
                }

                if (baseColumns is { Length: > 0 })
                {
                    var selected = lf.Select(baseColumns);
                    lf.Dispose();
                    lf = selected;
                }

                for (int i = 0; i < steps.Count; i++)
                {
                    var state = await CSharpScript.RunAsync(
                        steps[i], Options.Value, new ScriptGlobals { df = lf }, typeof(ScriptGlobals), cancellationToken);

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

                if (!applyRowCap)
                    return lf;
                using (lf)
                    return lf.Limit(RowCap);
            }
            catch
            {
                lf.Dispose();
                throw;
            }
        }, cancellationToken);
    }

    private static LazyFrame Relazify(DataFrame df)
    {
        using (df)
            return df.Lazy();
    }

    /// <summary>
    /// The compiler's errors, one per line, plus a hint when the script used a
    /// DataFrame member on a LazyFrame: whoever works with data expects
    /// <c>df</c> to hold data, and here it is a plan.
    /// </summary>
    public static string Describe(CompilationErrorException ex)
    {
        var lines = ex.Diagnostics.Select(d => d.ToString()).ToList();
        if (ex.Diagnostics.Select(DataFrameOnly).FirstOrDefault(m => m is not null) is { } use)
            lines.Add($"df is lazy: a LazyFrame, a plan with no data yet. {use.What} belongs to DataFrame, so "
                + $"collect the columns you need first, e.g. df.Select(\"a\", \"b\").Collect(){use.Example}");
        return string.Join(Environment.NewLine, lines);
    }

    // Read in the invariant culture: Roslyn translates its messages (pt-BR on
    // a Portuguese Windows), but not the type name.
    private static readonly Regex MissingMember = new(
        @"^'(?:Polars\.CSharp\.)?LazyFrame' does not contain a definition for '(\w+)'");

    // The example goes on with the member only when it gives a table: a step
    // that ends in a number (Height) or a Series ([]) fails on its own.
    private static (string What, string Example)? DataFrameOnly(Diagnostic d)
    {
        var message = d.GetMessage(CultureInfo.InvariantCulture);
        if (d.Id == "CS0021" && message.EndsWith("of type 'LazyFrame'", StringComparison.Ordinal))
            return ("Indexing with []", "");
        if (d.Id != "CS1061" || MissingMember.Match(message) is not { Success: true } m)
            return null;
        var name = m.Groups[1].Value;
        var members = typeof(DataFrame).GetMember(name, BindingFlags.Public | BindingFlags.Instance);
        if (members.Length == 0)
            return null;
        return members.Any(x => x is MethodInfo { ReturnType: var t } && t == typeof(DataFrame)) ? (name, $".{name}(…)")
            : members.Any(x => x is PropertyInfo { PropertyType: var t } && t == typeof(DataFrame)) ? (name, $".{name}")
            : (name, "");
    }
}
