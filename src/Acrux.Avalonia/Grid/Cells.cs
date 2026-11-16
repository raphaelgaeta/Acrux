using System.Globalization;
using Apache.Arrow.Types;
using Avalonia.Media;

namespace Acrux;

/// <summary>A cell as text, from the column cache, as the WinForms grid shows it.</summary>
public static class Cells
{
    /// <summary>
    /// The reading sizes (design approved 2026-11-15): Excel's 11 pt, about 15
    /// px, where the DataGridView drew 9 pt; the header holds the column's name
    /// and, under it, its Polars type.
    /// </summary>
    public const double FontSize = 15, TypeSize = 12, RowHeight = 26, HeaderHeight = 46;

    /// <summary>The text's margin inside a cell, each side.</summary>
    public const double Padding = 6;

    /// <summary>One step of horizontal scrolling: a wheel notch, the scroll bar's arrow.</summary>
    public const double ScrollStep = 100;

    /// <summary>
    /// The widths a column takes by itself: its header's at first, then, when
    /// it loads, the rows on screen too; never past <see cref="MaxAutoFit"/>.
    /// </summary>
    public const double MinFit = 56, MaxAutoFit = 320;

    /// <summary>The header's room for the filter button, on the right, the resize grip included.</summary>
    public const double ButtonRoom = 24;

    public static readonly Typeface Face = new(FontFamily.Default);
    public static readonly Typeface Italic = new(FontFamily.Default, FontStyle.Italic);
    public static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    // Digits of one width, so numbers line up down a column. Segoe UI and Noto
    // Sans draw them so already; GNOME's Adwaita Sans doesn't (measured).
    private static readonly FontFeatureCollection Tabular = FontFeatureCollection.Parse("tnum");

    /// <summary>What a cell holds, for the drawing rules of the app's GridPainting.</summary>
    public enum Kind { Text, Number, Null, Loading, Error }

    /// <summary>
    /// The cell as the grid draws it. Only the drawing changes: a copy and the
    /// tooltip take the data (<see cref="CopyText"/>).
    /// - null: "null", apart from an empty string;
    /// - text with line breaks: one line, a "↵" where each break was;
    /// - a number: its text, which the grid swaps for "###" when it doesn't fit;
    /// - a column not in memory: "…" while it loads, "error" when it could not be read.
    /// </summary>
    public static (string Text, Kind Kind) Display(ColumnCache cache, int column, int row)
    {
        if (cache.TryGet(column) is not { } array)
            return cache.Failure(column) is null ? ("…", Kind.Loading) : ("error", Kind.Error);
        object value = PolarsTableAdapter.GetCellValue(array, row);
        return value switch
        {
            DBNull => ("null", Kind.Null),
            DateTime day when IsDate(cache, column) => (day.ToString("d", CultureInfo.CurrentCulture), Kind.Text),
            sbyte or byte or short or ushort or int or uint or long or ulong or Half or float or double or decimal
                => (((IFormattable)value).ToString(null, CultureInfo.CurrentCulture), Kind.Number),
            string text when text.AsSpan().IndexOfAny('\r', '\n') >= 0 => (text.ReplaceLineEndings(" ↵ "), Kind.Text),
            IFormattable formattable => (formattable.ToString(null, CultureInfo.CurrentCulture), Kind.Text),
            _ => (value.ToString() ?? "", Kind.Text),
        };
    }

    /// <summary>
    /// A date column (Polars' <c>date</c>): it arrives as a DateTime at midnight,
    /// and its "12:00:00 AM" on every row was noise, so only the day is shown and
    /// copied, in the system's short format (11/23/2017, 23/11/2017), as Excel.
    /// </summary>
    private static bool IsDate(ColumnCache cache, int column) => cache.Fields[column].DataType is Date32Type or Date64Type;

    /// <summary>A cell as Ctrl+C copies it: the data, so a null is empty and line breaks stay.</summary>
    public static string CopyText(ColumnCache cache, int column, int row)
    {
        if (cache.TryGet(column) is not { } array) return "";
        object value = PolarsTableAdapter.GetCellValue(array, row);
        return value switch
        {
            DBNull => "",
            DateTime day when IsDate(cache, column) => day.ToString("d", CultureInfo.CurrentCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
            _ => value.ToString() ?? "",
        };
    }

    public static FormattedText Format(string text, Typeface face, IBrush brush, double size = FontSize, bool tabular = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush);
        if (tabular) formatted.SetFontFeatures(Tabular);
        return formatted;
    }

    /// <summary>A cell's text as drawn: numbers with their digits of one width.</summary>
    public static FormattedText Format(string text, Kind kind, IBrush brush) =>
        Format(text, kind is Kind.Text or Kind.Number ? Face : Italic, brush, tabular: kind == Kind.Number);

    /// <summary>Whether the drawn text is cut: a number shows "###", a text ends in "…".</summary>
    public static bool IsCut(string text, Kind kind, double width, double shift = 0) =>
        kind is Kind.Text or Kind.Number && Format(text, kind, Brushes.Black).Width > width - 2 * Padding - 1 - shift;

    /// <summary>
    /// Numbers to the right, as in the WinForms grid: Arrow's NumberType, less
    /// BooleanType (which is one too), plus the decimals (which aren't).
    /// </summary>
    public static bool IsNumeric(ColumnCache cache, int column) => cache.Fields[column].DataType
        is (NumberType and not BooleanType) or Decimal32Type or Decimal64Type or Decimal128Type or Decimal256Type;

    /// <summary>The columns whose decimal separators line up: floats and decimals.</summary>
    public static bool IsFractional(ColumnCache cache, int column) => cache.Fields[column].DataType
        is FloatingPointType or Decimal32Type or Decimal64Type or Decimal128Type or Decimal256Type;

    /// <summary>
    /// Digits after the decimal separator; -1 for a number that has nothing to
    /// line up (an exponent, NaN, infinity), which stays at the right edge.
    /// </summary>
    public static int Decimals(string text)
    {
        if (text.Length == 0 || !char.IsAsciiDigit(text[^1]) || text.AsSpan().IndexOfAny('E', 'e') >= 0) return -1;
        string separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        int at = text.LastIndexOf(separator, StringComparison.Ordinal);
        return at < 0 ? 0 : text.Length - at - separator.Length;
    }

    /// <summary>
    /// How far a number moves left so its decimal separator sits under the
    /// others' (<paramref name="most"/> decimals): the room of the digits it
    /// lacks, and of the separator when it has none. Nothing is added to the
    /// text, so the data on screen is still the data.
    /// </summary>
    public static double DecimalShift(string text, int most)
    {
        int decimals = Decimals(text);
        if (decimals < 0 || decimals >= most) return 0;
        _digit ??= Format("0", Face, Brushes.Black, tabular: true).Width;
        _separator ??= Format(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, Face, Brushes.Black).Width;
        return (most - decimals) * _digit.Value + (decimals == 0 ? _separator.Value : 0);
    }

    private static double? _digit, _separator;   // measured once, on the UI thread, when the first number is drawn

    /// <summary>The width a header asks for: its name, the type under it, the filter's button.</summary>
    public static double HeaderWidth(string name, string type, bool button) =>
        Math.Max(Format(name, Bold, Brushes.Black).Width, Format(type, Face, Brushes.Black, TypeSize).Width)
        + (button ? ButtonRoom - Padding : 0) + 2 * Padding + 1;
}
