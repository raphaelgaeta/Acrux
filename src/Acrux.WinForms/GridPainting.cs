using System.Drawing;
using System.Windows.Forms;

namespace Acrux;

/// <summary>
/// Where the grid draws something other than the cell's text. Only the
/// painting changes: each cell keeps its value, so Ctrl+C copies the data, and
/// the ToolTip of a cut-off cell shows it whole.
/// - null: a muted, italic "null", apart from an empty string;
/// - a number too wide for its column: "###", as in Excel. Cut-off digits
///   read as another number (922337203685… for a 19-digit value);
/// - text with line breaks: one line, a "↵" where each break was;
/// - a filtered column: a "▼" drawn at the header's right edge. The header
///   text stays the column's real name, so a copy doesn't carry the glyph.
/// </summary>
internal sealed class GridPainting
{
    private const string TooWide = "###";
    private const string LineBreak = " ↵ ";
    private const string FilterGlyph = "▼";

    private readonly Func<string, bool> _isFiltered;
    private readonly Font _nullFont;
    private readonly Padding _glyphRoom;

    public GridPainting(DataGridView grid, Func<string, bool> isFiltered)
    {
        _isFiltered = isFiltered;
        _nullFont = new Font(grid.DefaultCellStyle.Font ?? grid.Font, FontStyle.Italic);
        var headerFont = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;
        _glyphRoom = new Padding(0, 0, TextRenderer.MeasureText(FilterGlyph, headerFont).Width + 4, 0);
        grid.CellPainting += Paint;
    }

    /// <summary>Leaves room at the header's right edge for the filter's "▼".</summary>
    public void MarkFiltered(DataGridViewColumn column, bool filtered) =>
        column.HeaderCell.Style.Padding = filtered ? _glyphRoom : Padding.Empty;

    private void Paint(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (sender is not DataGridView grid || e.Graphics is null || e.CellStyle is null || e.ColumnIndex < 0)
            return;

        if (e.RowIndex < 0)
        {
            if (_isFiltered(grid.Columns[e.ColumnIndex].Name))
                PaintFilterGlyph(e);
            return;
        }

        switch (e.Value)
        {
            case DBNull:
                DrawText(e, "null", _nullFont, Theme.Muted);
                break;
            case string text when text.AsSpan().IndexOfAny('\r', '\n') >= 0:
                DrawText(e, text.ReplaceLineEndings(LineBreak), e.CellStyle.Font, e.CellStyle.ForeColor);
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong
                or Half or float or double or decimal when !Fits(e):
                DrawText(e, TooWide, e.CellStyle.Font, e.CellStyle.ForeColor);
                break;
        }
    }

    private void PaintFilterGlyph(DataGridViewCellPaintingEventArgs e)
    {
        e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
        var b = e.CellBounds;
        var glyph = new Rectangle(b.Right - 1 - _glyphRoom.Right, b.Y, _glyphRoom.Right - 2, b.Height - 1);
        TextRenderer.DrawText(e.Graphics!, FilterGlyph, e.CellStyle!.Font, glyph, e.CellStyle.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        e.Handled = true;
    }

    private static bool Fits(DataGridViewCellPaintingEventArgs e)
    {
        if (e.FormattedValue is not string text)
            return true;
        var (area, flags) = TextArea(e);
        var size = TextRenderer.MeasureText(e.Graphics!, text, e.CellStyle!.Font, new Size(int.MaxValue, area.Height), flags);
        return size.Width <= area.Width;
    }

    // Everything but the text from the grid (background, selection, borders,
    // focus), then the text in the grid's own place; the selection keeps its
    // text color.
    private static void DrawText(DataGridViewCellPaintingEventArgs e, string text, Font? font, Color color)
    {
        e.Paint(e.ClipBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
        bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
        var (area, flags) = TextArea(e);
        TextRenderer.DrawText(e.Graphics!, text, font, area,
            selected ? e.CellStyle!.SelectionForeColor : color, flags | TextFormatFlags.EndEllipsis);
        e.Handled = true;
    }

    // The rectangle and flags DataGridViewTextBoxCell draws its text with
    // (.NET 10 source): the cell less its right and bottom border, the style's
    // padding, and 2 px top and 1 px bottom margin; one line, no prefix.
    private static (Rectangle Area, TextFormatFlags Flags) TextArea(DataGridViewCellPaintingEventArgs e)
    {
        var b = e.CellBounds;
        var pad = e.CellStyle!.Padding;
        var area = new Rectangle(b.X + pad.Left, b.Y + pad.Top + 2,
            b.Width - 1 - pad.Horizontal, b.Height - 1 - pad.Vertical - 3);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping
            | (e.CellStyle.Alignment == DataGridViewContentAlignment.MiddleRight ? TextFormatFlags.Right : TextFormatFlags.Left);
        return (area, flags);
    }
}
