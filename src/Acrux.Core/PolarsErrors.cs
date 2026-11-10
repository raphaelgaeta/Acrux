namespace Acrux;

/// <summary>
/// Polars errors for people. When a query fails to resolve (a missing column,
/// a type that doesn't fit), Polars appends the plan it had resolved so far,
/// starting with "Resolved plan until failure:". The one line that says what
/// went wrong ends up above a plan that can list every selected column (seen
/// in the C# terminal, 2026-11-10). Format checked on Polars.NET 1.0.1 for
/// select, filter and group_by on a missing column, and str + int.
/// </summary>
public static class PolarsErrors
{
    private const string PlanMarker = "\n\nResolved plan until failure";

    /// <summary>The message without the plan Polars appends; any other message as is.</summary>
    public static string Brief(Exception ex)
    {
        var message = ex.Message;
        int plan = message.IndexOf(PlanMarker, StringComparison.Ordinal);
        return plan < 0 ? message : message[..plan];
    }

    /// <summary>What a window shows: <see cref="Brief"/>, or with ACRUX_DEBUG=1 the whole exception.</summary>
    public static string Describe(Exception ex) =>
        Environment.GetEnvironmentVariable("ACRUX_DEBUG") == "1" ? ex.ToString() : Brief(ex);
}
