using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Acrux;

/// <summary>
/// The drawn grid's colors, as the app's Theme.cs: each brand color in the
/// mode where it reads (WCAG: 4.5:1 for text, 3:1 for the rest). Black on blue
/// is 13.6:1 and white on purple 4.9:1; white on blue would be 1.5:1. The
/// secondary text sits 60% of the way from the background to the text (~5.7:1
/// on white, ~6.6:1 on #202020); Firebrick, 6.7:1 on white, is 2.4:1 on dark,
/// hence a lighter red there. The chrome (buttons, popup, terminal box) is
/// Avalonia's Fluent theme, which follows the same variant.
///
/// The reading tints (design approved 2026-11-15): the selection, the current
/// row and column, and the headers of the selection are the mode's brand color
/// mixed into the background, light enough that every text keeps 4.5:1 on
/// them (the gray included: 4.5 to 4.7:1 on the strongest, the selection);
/// the selection's outline is the other brand color, 4:1 and up against them.
/// </summary>
public sealed class Palette
{
    private static readonly Color Purple = Color.Parse("#8c5ea4"), Blue = Color.Parse("#acd7e7");

    public static readonly Palette Light = new(Colors.White, Colors.Black, Color.Parse("#f0eff3"), Color.Parse("#dcdcdc"),
        accentBack: Blue, accentText: Colors.Black, error: Color.Parse("#b22222"),
        tint: Blue, edge: Purple, selection: 0.5, focus: 0.2, lit: 0.3);

    public static readonly Palette Dark = new(Color.Parse("#202020"), Colors.White, Color.Parse("#2b2b2b"), Color.Parse("#3a3a3a"),
        accentBack: Purple, accentText: Colors.White, error: Color.Parse("#f48771"),
        tint: Purple, edge: Blue, selection: 0.35, focus: 0.16, lit: 0.25);

    public static Palette For(ThemeVariant? variant) => variant == ThemeVariant.Dark ? Dark : Light;

    private Palette(Color background, Color text, Color header, Color line, Color accentBack, Color accentText, Color error,
        Color tint, Color edge, double selection, double focus, double lit)
    {
        Background = Brush(background);
        Text = Brush(text);
        Muted = Brush(Blend(background, text, 0.6));
        Error = Brush(error);
        Header = Brush(header);
        HeaderLit = Brush(Blend(header, tint, lit));
        var lineBrush = Brush(line);
        LineBrush = lineBrush;
        Line = new ImmutablePen(lineBrush, 1);
        AccentBack = Brush(accentBack);
        AccentText = Brush(accentText);
        Selection = Brush(Blend(background, tint, selection));
        SelectionEdge = new ImmutablePen(Brush(edge), 2);
        Focus = Brush(Blend(background, tint, focus));
    }

    public IBrush Background { get; }
    public IBrush Text { get; }
    /// <summary>Nulls, a column loading, the types under the names, the row numbers.</summary>
    public IBrush Muted { get; }
    public IBrush Error { get; }
    public IBrush Header { get; }
    /// <summary>The header of a selected column and the number of a selected row.</summary>
    public IBrush HeaderLit { get; }
    public IBrush LineBrush { get; }
    public IPen Line { get; }
    /// <summary>A filtered column's header.</summary>
    public IBrush AccentBack { get; }
    public IBrush AccentText { get; }
    /// <summary>Under the selected cells; their text keeps its color, as in Excel.</summary>
    public IBrush Selection { get; }
    /// <summary>Around the selection, and where it is on the headers and row numbers.</summary>
    public IPen SelectionEdge { get; }
    /// <summary>The current row and column: a row followed across hundreds of columns (Excel's "Focus Cell").</summary>
    public IBrush Focus { get; }

    private static ImmutableSolidColorBrush Brush(Color color) => new(color);

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}
