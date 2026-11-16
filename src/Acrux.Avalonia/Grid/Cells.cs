using System.Globalization;
using Apache.Arrow.Types;
using Avalonia.Media;

namespace Acrux;

/// <summary>A cell as text, from the column cache, as the WinForms grid shows it.</summary>
public static class Cells
{
    public const double RowHeight = 24, HeaderHeight = 28, FontSize = 13;

    /// <summary>The text's margin inside a cell, each side.</summary>
    public const double Padding = 6;

    public static readonly Typeface Face = new(FontFamily.Default);
    public static readonly Typeface Italic = new(FontFamily.Default, FontStyle.Italic);
    public static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

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
            sbyte or byte or short or ushort or int or uint or long or ulong or Half or float or double or decimal
                => (((IFormattable)value).ToString(null, CultureInfo.CurrentCulture), Kind.Number),
            string text when text.AsSpan().IndexOfAny('\r', '\n') >= 0 => (text.ReplaceLineEndings(" ↵ "), Kind.Text),
            IFormattable formattable => (formattable.ToString(null, CultureInfo.CurrentCulture), Kind.Text),
            _ => (value.ToString() ?? "", Kind.Text),
        };
    }

    /// <summary>The drawn text, for the DataGrid's binding.</summary>
    public static string Text(ColumnCache cache, int column, int row) => Display(cache, column, row).Text;

    /// <summary>A cell as Ctrl+C copies it: the data, so a null is empty and line breaks stay.</summary>
    public static string CopyText(ColumnCache cache, int column, int row)
    {
        if (cache.TryGet(column) is not { } array) return "";
        object value = PolarsTableAdapter.GetCellValue(array, row);
        return value switch
        {
            DBNull => "",
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
            _ => value.ToString() ?? "",
        };
    }

    public static FormattedText Format(string text, Typeface face, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, FontSize, brush);

    /// <summary>Whether the drawn text is cut: a number shows "###", a text ends in "…".</summary>
    public static bool IsCut(string text, Kind kind, double width) =>
        kind is Kind.Text or Kind.Number && Format(text, Face, Brushes.Black).Width > width - 2 * Padding - 1;

    /// <summary>Numbers to the right; in Arrow, BooleanType is a NumberType too.</summary>
    public static bool IsNumeric(ColumnCache cache, int column) =>
        cache.Fields[column].DataType is NumberType and not BooleanType;

    public static double Width(int column) => column == 0 ? 90 : 120;
}
