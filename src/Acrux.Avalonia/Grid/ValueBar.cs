using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Acrux;

/// <summary>
/// The current cell whole, as Excel's formula bar (design approved
/// 2026-11-15): on the left the column, the row and the type; on the right the
/// value, with its line breaks, the whole of a long text and the number behind
/// a "###". Its height is fixed, two lines and a scroll: a bar that grew with
/// the value would move the rows under the pointer.
/// </summary>
internal sealed class ValueBar : Border
{
    private const double LineSize = Cells.FontSize - 1;

    private readonly Border _where = new() { Width = 230, BorderThickness = new Thickness(0, 0, 1, 0) };
    private readonly TextBlock _column = new() { FontWeight = FontWeight.SemiBold, FontSize = LineSize, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _place = new() { FontSize = Cells.TypeSize, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly SelectableTextBlock _value = new() { FontSize = LineSize, TextWrapping = TextWrapping.Wrap };
    private bool _note;

    public ValueBar()
    {
        Height = 2 * Math.Ceiling(LineSize * 1.34) + 14;
        BorderThickness = new Thickness(0, 0, 0, 1);
        _where.Child = new StackPanel
        {
            Margin = new Thickness(10, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _column, _place },
        };
        DockPanel.SetDock(_where, Dock.Left);
        Child = new DockPanel
        {
            Children =
            {
                _where,
                new ScrollViewer
                {
                    Margin = new Thickness(10, 6, 4, 6),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = _value,
                },
            },
        };
        ActualThemeVariantChanged += (_, _) => Repaint();
        Repaint();
    }

    /// <summary>
    /// A cell: <paramref name="note"/> marks a value that isn't the data
    /// itself (null, an empty text, a column loading or failed), drawn as the
    /// grid draws a null.
    /// </summary>
    public void Show(string column, string place, string value, bool note)
    {
        _column.Text = column;
        _place.Text = place;
        _value.Text = value;
        _note = note;
        Repaint();
    }

    /// <summary>The colors again: light or dark, high contrast on or off.</summary>
    public void Repaint()
    {
        var palette = Palette.For(ActualThemeVariant);
        Background = palette.Header;
        BorderBrush = palette.LineBrush;
        _where.BorderBrush = palette.LineBrush;
        _column.Foreground = palette.HeaderText;
        _place.Foreground = palette.HeaderMuted;
        _value.Foreground = _note ? palette.HeaderMuted : palette.HeaderText;
        _value.FontStyle = _note ? FontStyle.Italic : FontStyle.Normal;
    }
}
