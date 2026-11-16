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
/// </summary>
public sealed class Palette
{
    private static readonly Color Purple = Color.Parse("#8c5ea4"), Blue = Color.Parse("#acd7e7");

    public static readonly Palette Light = new(Colors.White, Colors.Black, Color.Parse("#f0eff3"), Color.Parse("#dcdcdc"),
        accentBack: Blue, accentText: Colors.Black, error: Color.Parse("#b22222"), current: Purple);

    public static readonly Palette Dark = new(Color.Parse("#202020"), Colors.White, Color.Parse("#2b2b2b"), Color.Parse("#3a3a3a"),
        accentBack: Purple, accentText: Colors.White, error: Color.Parse("#f48771"), current: Blue);

    public static Palette For(ThemeVariant? variant) => variant == ThemeVariant.Dark ? Dark : Light;

    private Palette(Color background, Color text, Color header, Color line, Color accentBack, Color accentText, Color error, Color current)
    {
        Background = Brush(background);
        AlternateRow = Brush(Blend(background, text, 0.04));   // barely there: enough to follow a row across 700 columns
        Text = Brush(text);
        Muted = Brush(Blend(background, text, 0.6));
        Error = Brush(error);
        Header = Brush(header);
        Line = new ImmutablePen(Brush(line), 1);
        AccentBack = Brush(accentBack);
        AccentText = Brush(accentText);
        Current = new ImmutablePen(Brush(current), 2);
    }

    public IBrush Background { get; }
    public IBrush AlternateRow { get; }
    public IBrush Text { get; }
    public IBrush Muted { get; }
    public IBrush Error { get; }
    public IBrush Header { get; }
    public IPen Line { get; }
    /// <summary>The selection and a filtered column's header.</summary>
    public IBrush AccentBack { get; }
    public IBrush AccentText { get; }
    /// <summary>The current cell's border.</summary>
    public IPen Current { get; }

    private static ImmutableSolidColorBrush Brush(Color color) => new(color);

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}
