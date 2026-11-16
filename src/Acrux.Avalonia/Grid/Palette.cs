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
/// hence a lighter red there.
///
/// The reading tints (design approved 2026-11-15): the selection, the current
/// row and column, and the headers of the selection are the mode's brand color
/// mixed into the background, light enough that every text keeps 4.5:1 on
/// them (the gray included: 4.5 to 4.7:1 on the strongest, the selection);
/// the selection's outline is the other brand color, 4:1 and up against them.
///
/// In high contrast the brand gives way to the system's scheme (<see cref="HighContrast"/>),
/// with the choices of the "Paletas da grade" page (approved 2026-11-15): nulls
/// in the text's color, in italics (Microsoft keeps GrayText for what is
/// disabled); lines in GrayText; no mixed tints, which no contrast theme has, so
/// no band and no lit headers; headers in the button colors, since a header is
/// clicked. The selection is the system's Highlight with its text; the current
/// cell, clear, with a border in the text's color.
/// </summary>
public sealed class Palette
{
    private static readonly Color Purple = Color.Parse("#8c5ea4"), Blue = Color.Parse("#acd7e7");

    public static readonly Palette Light = Brand(Colors.White, Colors.Black, Color.Parse("#f0eff3"), Color.Parse("#dcdcdc"),
        accentBack: Blue, accentText: Colors.Black, error: Color.Parse("#b22222"),
        tint: Blue, edge: Purple, selection: 0.5, focus: 0.2, lit: 0.3);

    public static readonly Palette Dark = Brand(Color.Parse("#202020"), Colors.White, Color.Parse("#2b2b2b"), Color.Parse("#3a3a3a"),
        accentBack: Purple, accentText: Colors.White, error: Color.Parse("#f48771"),
        tint: Purple, edge: Blue, selection: 0.35, focus: 0.16, lit: 0.25);

    /// <summary>The grid's colors: the system's scheme in high contrast, the brand's otherwise.</summary>
    public static Palette For(ThemeVariant? variant) =>
        HighContrast.For(variant) is { } scheme ? FromScheme(scheme) : Chrome(variant);

    /// <summary>
    /// The brand's colors, for what sits on Avalonia's Fluent surfaces (the
    /// terminal, the filter dropdown): Fluent has no high contrast palette, so
    /// text in the system's colors could land on a background it never chose.
    /// </summary>
    public static Palette Chrome(ThemeVariant? variant) => variant == ThemeVariant.Dark ? Dark : Light;

    private static (object Scheme, Palette Palette)? _contrast;   // the last scheme's palette: Render asks every frame

    private static Palette FromScheme(HighContrast.Scheme s)
    {
        if (_contrast is { } cached && Equals(cached.Scheme, s)) return cached.Palette;
        var palette = new Palette(s.Window, s.WindowText, muted: s.WindowText, error: s.WindowText,
            header: s.ButtonFace, headerText: s.ButtonText, headerMuted: s.ButtonText, headerLit: s.ButtonFace, line: s.GrayText,
            accentBack: s.Highlight, accentText: s.HighlightText, selection: s.Highlight, selectionText: s.HighlightText,
            edge: s.ButtonText, outline: false, currentEdge: s.WindowText, focus: null, pressedFill: null, pressedEdge: s.ButtonText);
        _contrast = (s, palette);
        return palette;
    }

    private static Palette Brand(Color background, Color text, Color header, Color line, Color accentBack, Color accentText, Color error,
        Color tint, Color edge, double selection, double focus, double lit)
    {
        var muted = Blend(background, text, 0.6);
        return new Palette(background, text, muted, error, header, headerText: text, headerMuted: muted,
            headerLit: Blend(header, tint, lit), line, accentBack, accentText,
            selection: Blend(background, tint, selection), selectionText: null, edge, outline: true, currentEdge: null,
            focus: Blend(background, tint, focus), pressedFill: Blend(background, tint, selection), pressedEdge: null);
    }

    private Palette(Color background, Color text, Color muted, Color error, Color header, Color headerText, Color headerMuted,
        Color headerLit, Color line, Color accentBack, Color accentText, Color selection, Color? selectionText,
        Color edge, bool outline, Color? currentEdge, Color? focus, Color? pressedFill, Color? pressedEdge)
    {
        Background = Brush(background);
        Text = Brush(text);
        Muted = Brush(muted);
        Error = Brush(error);
        Header = Brush(header);
        HeaderText = Brush(headerText);
        HeaderMuted = Brush(headerMuted);
        HeaderLit = Brush(headerLit);
        var lineBrush = Brush(line);
        LineBrush = lineBrush;
        Line = new ImmutablePen(lineBrush, 1);
        AccentBack = Brush(accentBack);
        AccentText = Brush(accentText);
        Selection = Brush(selection);
        SelectionText = selectionText is { } st ? Brush(st) : null;
        SelectionEdge = new ImmutablePen(Brush(edge), 2);
        Outline = outline ? SelectionEdge : null;
        CurrentEdge = currentEdge is { } ce ? new ImmutablePen(Brush(ce), 2) : null;
        Focus = focus is { } f ? Brush(f) : null;
        PressedFill = pressedFill is { } pf ? Brush(pf) : null;
        PressedEdge = pressedEdge is { } pe ? new ImmutablePen(Brush(pe), 1) : null;
    }

    public IBrush Background { get; }
    public IBrush Text { get; }
    /// <summary>Nulls and a column loading, in the cells.</summary>
    public IBrush Muted { get; }
    public IBrush Error { get; }
    public IBrush Header { get; }
    /// <summary>The headers' names, and the value bar's.</summary>
    public IBrush HeaderText { get; }
    /// <summary>The types under the names, the row numbers, the value bar's place.</summary>
    public IBrush HeaderMuted { get; }
    /// <summary>The header of a selected column and the number of a selected row.</summary>
    public IBrush HeaderLit { get; }
    public IBrush LineBrush { get; }
    public IPen Line { get; }
    /// <summary>A filtered column's header.</summary>
    public IBrush AccentBack { get; }
    public IBrush AccentText { get; }
    /// <summary>Under the selected cells.</summary>
    public IBrush Selection { get; }
    /// <summary>The selected cells' text; null keeps each text's own color, as in Excel.</summary>
    public IBrush? SelectionText { get; }
    /// <summary>Where the selection is, on the headers and row numbers.</summary>
    public IPen SelectionEdge { get; }
    /// <summary>Around the selection; null for none.</summary>
    public IPen? Outline { get; }
    /// <summary>Around the current cell; null for none (the outline marks the selection).</summary>
    public IPen? CurrentEdge { get; }
    /// <summary>The current row and column: a row followed across hundreds of columns (Excel's "Focus Cell"); null for none.</summary>
    public IBrush? Focus { get; }
    /// <summary>A filter button under the pointer or with its dropdown open: a fill, an edge, or both.</summary>
    public IBrush? PressedFill { get; }
    public IPen? PressedEdge { get; }

    private static ImmutableSolidColorBrush Brush(Color color) => new(color);

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}
