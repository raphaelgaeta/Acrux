using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Acrux;

/// <summary>
/// A grid that keeps nothing per row: it paints the cells on screen from the
/// scroll position and the column cache, every frame. The vertical scroll bar
/// counts rows, not pixels, so 5 million rows never become a 120-million-pixel
/// surface (Skia draws in 32-bit floats, which lose whole pixels up there).
/// The selection is a rectangle, from an anchor cell to the current cell, as
/// in the WinForms grid; the keys are the DataGridView's. Measured against
/// the WinForms grid and Avalonia's DataGrid in spike/AvaloniaGrid (branch
/// spike/avalonia-grid): the DataGrid makes an object for every row.
/// </summary>
public sealed class TableGrid : Grid
{
    /// <summary>
    /// The most cells one Ctrl+C copies, a guard until the owner decides the
    /// rule: the text of a whole 5-million-row column is ~60 MB, and the
    /// clipboard and whatever pastes it would choke.
    /// </summary>
    public const long CopyLimit = 1_000_000;

    private readonly ColumnCache _cache;
    private readonly double[] _width;  // each column's width, which the user can drag or fit
    private readonly double[] _left;   // x of each column's left edge; one more for the right end
    private readonly CellSurface _surface;
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal };
    // the columns load once the scrolling stops, as in the WinForms app: a
    // fast scroll would otherwise read every column it flew past
    private readonly DispatcherTimer _loadTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _dragging;
    private int[]? _rows;   // a filter's rows, as indexes into the file; null shows them all

    public TableGrid(ColumnCache cache)
    {
        _cache = cache;
        int count = cache.Fields.Count;
        _width = Enumerable.Range(0, count).Select(Cells.Width).ToArray();
        _left = new double[count + 1];
        for (int c = 0; c < count; c++)
            _left[c + 1] = _left[c] + _width[c];

        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions = new RowDefinitions("*,Auto");
        _surface = new CellSurface(this) { ClipToBounds = true, Focusable = true };
        _surface.ActualThemeVariantChanged += (_, _) => _surface.InvalidateVisual();   // light or dark, live
        Children.Add(_surface);
        SetColumn(_vertical, 1);
        Children.Add(_vertical);
        SetRow(_horizontal, 1);
        Children.Add(_horizontal);

        _vertical.ValueChanged += (_, _) => Scrolled();
        _horizontal.ValueChanged += (_, _) => Scrolled();
        _surface.SizeChanged += (_, _) => { UpdateScrollBars(); Scrolled(); };
        _surface.PointerWheelChanged += Wheel;
        _surface.PointerPressed += Pressed;
        _surface.PointerMoved += Moved;
        _surface.PointerReleased += (_, _) =>
        {
            _dragging = false;
            if (_resizing >= 0)
            {
                _resizing = -1;
                _ = EnsureVisibleAsync();   // a narrower column brings others on screen
            }
        };
        _surface.KeyDown += KeyPressed;
        cache.ColumnLoaded += (_, _, _) => _surface.InvalidateVisual();
        _loadTimer.Tick += (_, _) =>
        {
            _loadTimer.Stop();
            _ = EnsureVisibleAsync();
        };
    }

    // off screen (another file took its place), the grid asks for nothing more
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _loadTimer.Stop();
    }

    /// <summary>A line for the status bar: what a copy did, or why it didn't.</summary>
    public event Action<string>? Status;

    /// <summary>A header was clicked: the column and its rectangle, in the grid's coordinates.</summary>
    public event Action<int, Rect>? HeaderClicked;

    /// <summary>The columns with an active filter: their headers say so.</summary>
    public HashSet<int> FilteredColumns { get; } = [];

    /// <summary>The rows shown: all of the file's, or a filter's.</summary>
    public int RowCount => _rows?.Length ?? _cache.RowCount;

    /// <summary>The file's row behind a row of the grid.</summary>
    public int FileRow(int row) => _rows is { } rows ? rows[row] : row;

    /// <summary>
    /// Shows a filter's rows (null: all of them). Nothing in the grid is per
    /// row, so this is a swap of the index list, whatever the counts.
    /// </summary>
    public void SetRows(int[]? rows)
    {
        _rows = rows;
        Anchor = Current = (0, Current.Column);
        UpdateScrollBars();
        FirstRow = 0;
        _surface.InvalidateVisual();
    }

    public ColumnCache Cache => _cache;
    public double TotalWidth => _left[^1];
    public double ViewportWidth => _surface.Bounds.Width;
    public double Left(int column) => _left[column];
    public double ColumnWidth(int column) => _width[column];
    public int ColumnCount => _cache.Fields.Count;
    public int VisibleRowCount => Math.Max(1, (int)((_surface.Bounds.Height - Cells.HeaderHeight) / Cells.RowHeight));

    public (int Row, int Column) Anchor { get; private set; }
    public (int Row, int Column) Current { get; private set; }

    /// <summary>The selected rectangle, inclusive.</summary>
    public (int Top, int Bottom, int Left, int Right) Selection =>
        (Math.Min(Anchor.Row, Current.Row), Math.Max(Anchor.Row, Current.Row),
         Math.Min(Anchor.Column, Current.Column), Math.Max(Anchor.Column, Current.Column));

    public long SelectedCells
    {
        get
        {
            var s = Selection;
            return (long)(s.Bottom - s.Top + 1) * (s.Right - s.Left + 1);
        }
    }

    public int FirstRow
    {
        get => (int)_vertical.Value;
        set => _vertical.Value = Math.Clamp(value, 0, _vertical.Maximum);
    }

    public double OffsetX
    {
        get => _horizontal.Value;
        set => _horizontal.Value = Math.Clamp(value, 0, _horizontal.Maximum);
    }

    public void FocusGrid() => _surface.Focus();

    /// <summary>The columns that touch the screen, widened by <paramref name="margin"/> pixels each side.</summary>
    public List<int> VisibleColumns(double margin = 0)
    {
        double from = OffsetX - margin, to = OffsetX + ViewportWidth + margin;
        int found = Array.BinarySearch(_left, from);
        int first = Math.Max(0, found < 0 ? ~found - 1 : found);
        var columns = new List<int>();
        for (int c = first; c < ColumnCount && _left[c] < to; c++)
            if (_left[c + 1] > from) columns.Add(c);
        return columns;
    }

    /// <summary>The screen's columns first, then half a screen each side, as the app loads them.</summary>
    public Task EnsureVisibleAsync()
    {
        var visible = VisibleColumns();
        var around = VisibleColumns(ViewportWidth / 2).Except(visible);
        return _cache.EnsureAsync([.. visible, .. around]);
    }

    /// <summary>Makes a cell current; extends the selection to it, or starts a new one there.</summary>
    public void Select(int row, int column, bool extend = false)
    {
        if (RowCount == 0) return;
        Current = (Math.Clamp(row, 0, RowCount - 1), Math.Clamp(column, 0, ColumnCount - 1));
        if (!extend) Anchor = Current;
        BringIntoView(Current);
        _surface.InvalidateVisual();
    }

    public void SelectAll()
    {
        Anchor = (0, 0);
        Current = (Math.Max(0, RowCount - 1), ColumnCount - 1);
        _surface.InvalidateVisual();
    }

    /// <summary>
    /// Copies the selection as the WinForms grid does: the header names, then
    /// the rows, tab-separated; a null copies as empty. Loads the selection's
    /// columns first and refuses, with a reason, what would not fit.
    /// </summary>
    public async Task<bool> CopyAsync()
    {
        var s = Selection;
        long cells = SelectedCells;
        if (cells > CopyLimit)
        {
            Status?.Invoke($"Not copied: {cells:N0} cells selected, a copy takes up to {CopyLimit:N0}.");
            return false;
        }
        var columns = Enumerable.Range(s.Left, s.Right - s.Left + 1).ToList();
        if (!_cache.Fits(columns))
        {
            Status?.Invoke($"Not copied: the {columns.Count} columns don't fit in memory together.");
            return false;
        }
        await _cache.EnsureAsync(columns);

        var text = new StringBuilder();
        text.AppendJoin('\t', columns.Select(c => _cache.Fields[c].Name)).Append(Environment.NewLine);
        for (int row = s.Top; row <= s.Bottom; row++)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) text.Append('\t');
                text.Append(Cells.CopyText(_cache, columns[i], FileRow(row)));
            }
            text.Append(Environment.NewLine);
        }
        _ = EnsureVisibleAsync();   // the screen's columns are the pinned ones again

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return false;
        await clipboard.SetTextAsync(text.ToString());
        Status?.Invoke($"Copied {cells:N0} cells ({text.Length / 1e6:0.0} MB of text).");
        return true;
    }

    private void BringIntoView((int Row, int Column) cell)
    {
        int visible = VisibleRowCount;
        if (cell.Row < FirstRow) FirstRow = cell.Row;
        else if (cell.Row >= FirstRow + visible) FirstRow = cell.Row - visible + 1;
        if (_left[cell.Column] < OffsetX) OffsetX = _left[cell.Column];
        else if (_left[cell.Column + 1] > OffsetX + ViewportWidth) OffsetX = _left[cell.Column + 1] - ViewportWidth;
    }

    private void UpdateScrollBars()
    {
        int visible = VisibleRowCount;
        _vertical.Maximum = Math.Max(0, RowCount - visible);
        _vertical.ViewportSize = visible;
        _vertical.LargeChange = visible;
        _vertical.SmallChange = 1;
        _horizontal.Maximum = Math.Max(0, TotalWidth - ViewportWidth);
        _horizontal.ViewportSize = ViewportWidth;
        _horizontal.LargeChange = ViewportWidth;
        _horizontal.SmallChange = Cells.Width(1);
    }

    private void Scrolled()
    {
        _loadTimer.Stop();
        _loadTimer.Start();
        _surface.InvalidateVisual();
    }

    private (int Row, int Column) CellAt(Point point)
    {
        int row = FirstRow + (int)Math.Floor((point.Y - Cells.HeaderHeight) / Cells.RowHeight);
        int i = Array.BinarySearch(_left, OffsetX + point.X);
        int column = i < 0 ? ~i - 1 : i;
        return (Math.Clamp(row, 0, Math.Max(0, RowCount - 1)), Math.Clamp(column, 0, ColumnCount - 1));
    }

    private void Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
            OffsetX -= (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * Cells.Width(1);
        else
            FirstRow -= (int)Math.Round(e.Delta.Y * 3);
        e.Handled = true;
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(_surface);
        _surface.Focus();
        if (BorderAt(point) is int border and >= 0)
        {
            if (e.ClickCount == 2)
                FitColumn(border);
            else
            {
                _resizing = border;
                _resizeFromX = point.X;
                _resizeFromWidth = _width[border];
                e.Pointer.Capture(_surface);
            }
            e.Handled = true;
            return;
        }
        if (point.Y < Cells.HeaderHeight)
        {
            int header = CellAt(point).Column;
            HeaderClicked?.Invoke(header, new Rect(_left[header] - OffsetX, 0, _width[header], Cells.HeaderHeight));
            e.Handled = true;
            return;
        }
        if (RowCount == 0) return;
        var (row, column) = CellAt(point);
        Select(row, column, extend: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        _dragging = true;
        e.Pointer.Capture(_surface);
        e.Handled = true;
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        var at = e.GetPosition(_surface);
        if (_resizing >= 0)
        {
            SetColumnWidth(_resizing, _resizeFromWidth + at.X - _resizeFromX);
            return;
        }
        if (!_dragging || !e.GetCurrentPoint(_surface).Properties.IsLeftButtonPressed)
        {
            _surface.Cursor = BorderAt(at) >= 0 ? ResizeCursor : null;
            UpdateTip(at);
            return;
        }
        var point = e.GetPosition(_surface);
        // past an edge, the view follows the pointer a row or a column at a time
        if (point.Y > _surface.Bounds.Height) FirstRow++;
        else if (point.Y < Cells.HeaderHeight) FirstRow--;
        if (point.X > _surface.Bounds.Width) OffsetX += Cells.Width(1);
        else if (point.X < 0) OffsetX -= Cells.Width(1);
        var clamped = new Point(Math.Clamp(point.X, 0, _surface.Bounds.Width - 1),
            Math.Clamp(point.Y, Cells.HeaderHeight, _surface.Bounds.Height - 1));
        var (row, column) = CellAt(clamped);
        if ((row, column) != Current) Select(row, column, extend: true);
    }

    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
    private const double MinColumnWidth = 30, MaxFitWidth = 800, BorderGrip = 4;
    private int _resizing = -1;
    private double _resizeFromX, _resizeFromWidth;

    /// <summary>The column whose right border is under the point, in the header; -1 for none.</summary>
    private int BorderAt(Point point)
    {
        if (point.Y < 0 || point.Y >= Cells.HeaderHeight) return -1;
        double x = OffsetX + point.X;
        int found = Array.BinarySearch(_left, x);
        int column = found < 0 ? ~found - 1 : found;
        for (int c = Math.Max(0, column - 1); c <= Math.Min(ColumnCount - 1, column); c++)
            if (Math.Abs(_left[c + 1] - x) <= BorderGrip) return c;
        return -1;
    }

    public void SetColumnWidth(int column, double width)
    {
        _width[column] = Math.Max(MinColumnWidth, width);
        for (int c = column; c < ColumnCount; c++)
            _left[c + 1] = _left[c] + _width[c];
        UpdateScrollBars();
        _surface.InvalidateVisual();
    }

    /// <summary>
    /// Two clicks on a border: the column fits its header and the rows on
    /// screen. Only those: the DataGridView measures every row, which on 5
    /// million rows would freeze the window.
    /// </summary>
    public void FitColumn(int column)
    {
        double widest = Cells.Format(_cache.Fields[column].Name, Cells.Bold, Brushes.Black).Width + (FilteredColumns.Contains(column) ? 16 : 0);
        int last = Math.Min(RowCount, FirstRow + VisibleRowCount + 1);
        for (int row = FirstRow; row < last; row++)
        {
            var (text, kind) = Cells.Display(_cache, column, FileRow(row));
            var face = kind is Cells.Kind.Text or Cells.Kind.Number ? Cells.Face : Cells.Italic;
            widest = Math.Max(widest, Cells.Format(text, face, Brushes.Black).Width);
        }
        SetColumnWidth(column, Math.Min(MaxFitWidth, widest + 2 * Cells.Padding + 1));
        _ = EnsureVisibleAsync();
    }

    private (int Row, int Column)? _tipCell;

    /// <summary>A cut cell's tooltip shows its whole value, as the WinForms grid's does.</summary>
    private void UpdateTip(Point point)
    {
        if (point.Y < Cells.HeaderHeight || RowCount == 0)
        {
            _tipCell = null;
            ToolTip.SetTip(_surface, null);
            return;
        }
        var cell = CellAt(point);
        if (cell == _tipCell) return;
        _tipCell = cell;
        var (text, kind) = Cells.Display(_cache, cell.Column, FileRow(cell.Row));
        ToolTip.SetTip(_surface, Cells.IsCut(text, kind, _width[cell.Column]) ? Cells.CopyText(_cache, cell.Column, FileRow(cell.Row)) : null);
    }

    private async void KeyPressed(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var (row, column) = Current;
        int page = VisibleRowCount, lastRow = RowCount - 1, lastColumn = ColumnCount - 1;
        (int, int)? to = e.Key switch
        {
            Key.Up => ctrl ? (0, column) : (row - 1, column),
            Key.Down => ctrl ? (lastRow, column) : (row + 1, column),
            Key.Left => ctrl ? (row, 0) : (row, column - 1),
            Key.Right => ctrl ? (row, lastColumn) : (row, column + 1),
            Key.PageUp => (row - page, column),
            Key.PageDown => (row + page, column),
            Key.Home => ctrl ? (0, 0) : (row, 0),
            Key.End => ctrl ? (lastRow, lastColumn) : (row, lastColumn),
            _ => null,
        };
        if (to is var (r, c))
        {
            Select(r, c, extend: shift);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A)
        {
            SelectAll();
            e.Handled = true;
        }
        else if ((ctrl && e.Key == Key.C) || (ctrl && e.Key == Key.Insert))   // Ctrl+Shift+C included
        {
            e.Handled = true;
            await CopyAsync();
        }
    }

    private sealed class CellSurface(TableGrid owner) : Control
    {

        public override void Render(DrawingContext context)
        {
            var palette = Palette.For(ActualThemeVariant);
            var size = Bounds.Size;
            context.FillRectangle(palette.Background, new Rect(size));
            var cache = owner.Cache;
            int firstRow = owner.FirstRow;
            int rows = Math.Max(0, Math.Min((int)Math.Ceiling((size.Height - Cells.HeaderHeight) / Cells.RowHeight), owner.RowCount - firstRow));
            var selection = owner.Selection;
            var current = owner.Current;
            Rect? currentRect = null;

            foreach (int column in owner.VisibleColumns())
            {
                double x = owner.Left(column) - owner.OffsetX, width = owner.ColumnWidth(column);
                bool filtered = owner.FilteredColumns.Contains(column);
                var headerInk = filtered ? palette.AccentText : palette.Text;
                context.FillRectangle(filtered ? palette.AccentBack : palette.Header, new Rect(x, 0, width, Cells.HeaderHeight));
                DrawText(context, cache.Fields[column].Name, Cells.Bold, headerInk, x, 0, width - (filtered ? 16 : 0), Cells.HeaderHeight, right: false);
                if (filtered)   // drawn, never in the name: Ctrl+C copies the name; and color is never the only cue
                    DrawText(context, "▼", Cells.Face, headerInk, x + width - 20, 0, 18, Cells.HeaderHeight, right: false);
                bool right = Cells.IsNumeric(cache, column);
                bool columnSelected = column >= selection.Left && column <= selection.Right;
                for (int r = 0; r < rows; r++)
                {
                    int row = firstRow + r;
                    double y = Cells.HeaderHeight + r * Cells.RowHeight;
                    bool selected = columnSelected && row >= selection.Top && row <= selection.Bottom;
                    if (selected)
                        context.FillRectangle(palette.AccentBack, new Rect(x, y, width, Cells.RowHeight));
                    else if (row % 2 == 1)
                        context.FillRectangle(palette.AlternateRow, new Rect(x, y, width, Cells.RowHeight));
                    if (row == current.Row && column == current.Column)
                        currentRect = new Rect(x + 1, y + 1, width - 2, Cells.RowHeight - 2);
                    var (text, kind) = Cells.Display(cache, column, owner.FileRow(row));
                    DrawCell(context, palette, selected, text, kind, x, y, width, right);
                }
                context.DrawLine(palette.Line, new Point(x + width - 0.5, 0), new Point(x + width - 0.5, size.Height));
            }
            for (int r = 0; r <= rows; r++)
            {
                double y = Cells.HeaderHeight + r * Cells.RowHeight - 0.5;
                context.DrawLine(palette.Line, new Point(0, y), new Point(size.Width, y));
            }
            if (currentRect is { } rect)
                context.DrawRectangle(null, palette.Current, rect);
        }

        /// <summary>
        /// A cell by the app's GridPainting rules: null, "…" and "error" in
        /// muted (or red) italics; a number that doesn't fit becomes "###",
        /// since cut-off digits read as another number; a text that doesn't
        /// fit ends in "…".
        /// </summary>
        private static void DrawCell(DrawingContext context, Palette palette, bool selected, string text, Cells.Kind kind,
            double x, double y, double width, bool right)
        {
            var (face, brush) = kind switch
            {
                Cells.Kind.Null or Cells.Kind.Loading => (Cells.Italic, palette.Muted),
                Cells.Kind.Error => (Cells.Italic, palette.Error),
                _ => (Cells.Face, palette.Text),
            };
            if (selected) brush = palette.AccentText;   // the selection keeps its own text color, as in the app
            double room = width - 2 * Cells.Padding - 1;
            var formatted = Cells.Format(text, face, brush);
            if (formatted.Width > room)
            {
                if (kind == Cells.Kind.Number)
                    formatted = Cells.Format("###", face, brush);
                else
                {
                    formatted.MaxTextWidth = room;
                    formatted.MaxLineCount = 1;
                    formatted.Trimming = TextTrimming.CharacterEllipsis;
                }
            }
            Place(context, formatted, x, y, width, Cells.RowHeight, right);
        }

        private static void DrawText(DrawingContext context, string text, Typeface face, IBrush brush,
            double x, double y, double width, double height, bool right) =>
            Place(context, Cells.Format(text, face, brush), x, y, width, height, right);

        private static void Place(DrawingContext context, FormattedText formatted, double x, double y, double width, double height, bool right)
        {
            using (context.PushClip(new Rect(x, y, width - 1, height)))
                context.DrawText(formatted, new Point(right ? x + width - Cells.Padding - 1 - formatted.Width : x + Cells.Padding,
                    y + (height - formatted.Height) / 2));
        }
    }
}
