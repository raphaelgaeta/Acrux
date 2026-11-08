using System.Globalization;
using System.Text.RegularExpressions;
using Polars.CSharp;
using static Polars.CSharp.Polars;

namespace Acrux;

/// <summary>
/// Relative time filters in journalctl's spirit: `--last "1 hour"` keeps the
/// rows whose time column falls in the last hour.
/// </summary>
public static class TimeFilter
{
    // systemd.time(7) span units. Single letters are case-sensitive, as in
    // systemd: m = minute, M = month; whole words match in any case.
    private static readonly (string[] Names, TimeSpan Unit)[] Units =
    [
        (["us", "usec", "µs"], TimeSpan.FromMicroseconds(1)),
        (["ms", "msec"], TimeSpan.FromMilliseconds(1)),
        (["s", "sec", "second", "seconds"], TimeSpan.FromSeconds(1)),
        (["m", "min", "minute", "minutes"], TimeSpan.FromMinutes(1)),
        (["h", "hr", "hour", "hours"], TimeSpan.FromHours(1)),
        (["d", "day", "days"], TimeSpan.FromDays(1)),
        (["w", "week", "weeks"], TimeSpan.FromDays(7)),
        (["M", "month", "months"], TimeSpan.FromDays(30.44)),
        (["y", "year", "years"], TimeSpan.FromDays(365.25)),
    ];

    private const string Part = @"(\d+(?:\.\d+)?)\s*([A-Za-zµ]+)";

    /// <summary>
    /// A systemd-style time span: "1 hour", "30min", "1h 30min", "2 days",
    /// "1.5h", with an optional trailing "ago". Throws FormatException naming
    /// the accepted units.
    /// </summary>
    public static TimeSpan ParseSpan(string text)
    {
        var body = Regex.Replace(text.Trim(), @"\s+ago$", "", RegexOptions.IgnoreCase);
        var parts = Regex.Matches(body, Part);
        if (parts.Count == 0 || Regex.Replace(body, Part, "").Trim().Length > 0)
            throw new FormatException($"cannot read the time span \"{text}\"; use e.g. \"1 hour\", \"30min\", \"1h 30min\", \"2 days\"");

        var total = TimeSpan.Zero;
        foreach (Match part in parts)
        {
            var unit = FindUnit(part.Groups[2].Value)
                ?? throw new FormatException($"unknown time unit \"{part.Groups[2].Value}\"; use one of: " +
                    string.Join(", ", Units.Select(u => string.Join("/", u.Names))));
            total += unit * double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return total > TimeSpan.Zero
            ? total
            : throw new FormatException($"the time span \"{text}\" must be longer than zero");
    }

    private static TimeSpan? FindUnit(string name)
    {
        foreach (var (names, unit) in Units)
            if (names.Contains(name))
                return unit;
        foreach (var (names, unit) in Units)
            if (names.Any(n => n.Length > 1 && n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return unit;
        return null;
    }

    /// <summary>
    /// Rows whose time column falls between <paramref name="since"/> and
    /// <paramref name="until"/> (both included), compared as instants
    /// (microseconds since 1970) so a zoned column never meets a zoneless
    /// literal:
    /// - a zoned datetime against the instants themselves;
    /// - a datetime without a zone is a local wall clock, the way the grid
    ///   shows it, so it meets their local times;
    /// - a date by local day, from <paramref name="since"/>'s day to
    ///   <paramref name="until"/>'s, both included.
    /// "The last hour" ends now: rows dated in the future are left out.
    /// </summary>
    public static Expr Between(string column, DataType type, DateTimeOffset since, DateTimeOffset until)
    {
        long from = Micros(column, type, since), to = Micros(column, type, until);
        // two Col() calls, not one shared: Polars.NET 0.6.0 consumes an Expr when it is combined
        return (Instant(column) >= Lit(from)) & (Instant(column) <= Lit(to));
    }

    private static Expr Instant(string column) => Col(column).Dt.Timestamp(TimeUnit.Microseconds);

    private static long Micros(string column, DataType type, DateTimeOffset at)
    {
        var name = type.ToString();
        DateTime cutoff;
        if (name == "date")
            cutoff = at.ToLocalTime().Date;
        else if (name.StartsWith("datetime[", StringComparison.Ordinal))
            cutoff = name.Contains(',') ? at.UtcDateTime : at.ToLocalTime().DateTime;
        else
            throw new ArgumentException($"column \"{column}\" is {name}, not a date or time column");
        return (cutoff.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;
    }
}
