using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
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
///
/// What it draws for reading (design approved 2026-11-15, after comparing
/// with Excel): the row numbers on the left; a light selection whose text keeps
/// its color, with an outline, and its rows and columns lit on the headers; a
/// band on the current row and column; each column's type under its name; the
/// decimal separators lined up; each column as wide as its header, then as its
/// rows when it loads; and, above, the current cell whole (<see cref="ValueBar"/>).
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
    private readonly bool[] _hidden;   // "Columns": no width, skipped by keys, clicks and copies
    private readonly bool[] _sized;    // the user set the width: nothing else changes it
    private readonly bool[] _fitted;   // fit to its rows once, when it loaded
    private readonly HashSet<int> _filtered = [];
    private readonly HashSet<int> _toFit = [];
    private bool _fitPosted;
    private readonly double _frameWidth;   // the row numbers, left of the cells
    private readonly bool _filterButtons;  // the filter's button on each header: the file's grid has them, a script's result doesn't
    private int _hoverButton = -1;         // the header whose button is under the pointer
    private readonly CellSurface _surface;
    private readonly ValueBar _valueBar = new();
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal };
    // the columns load once the scrolling stops, as in the WinForms app: a
    // fast scroll would otherwise read every column it flew past
    private readonly DispatcherTimer _loadTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _dragging;
    private int[]? _rows;   // a filter's rows, as indexes into the file; null shows them all
    // one column's cells on screen, and how far each number moves to line up its decimals
    private (string Text, Cells.Kind Kind)[] _cells = [];
    private double[] _shifts = [];

    public TableGrid(ColumnCache cache, bool filterButtons = true)
    {
        _cache = cache;
        _filterButtons = filterButtons;
        int count = cache.Fields.Count;
        _width = new double[count];
        _left = new double[count + 1];
        _hidden = new bool[count];
        _sized = new bool[count];
        _fitted = new bool[count];
        for (int c = 0; c < count; c++)   // not Relayout: the surface it repaints doesn't exist yet
        {
            _width[c] = Math.Clamp(HeaderWidth(c), Cells.MinFit, Cells.MaxAutoFit);
            _left[c + 1] = _left[c] + _width[c];
        }
        _frameWidth = Math.Ceiling(Cells.Format(cache.RowCount.ToString("N0", CultureInfo.CurrentCulture),
            Cells.Bold, Brushes.Black, Cells.FontSize - 1, tabular: true).Width) + 2 * Cells.Padding + 4;

        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions = new RowDefinitions("Auto,*,Auto");
        SetColumnSpan(_valueBar, 2);
        Children.Add(_valueBar);
        _surface = new CellSurface(this) { ClipToBounds = true, Focusable = true };
        _surface.ActualThemeVariantChanged += (_, _) => _surface.InvalidateVisual();   // light or dark, live
        SetRow(_surface, 1);
        Children.Add(_surface);
        SetRow(_vertical, 1);
        SetColumn(_vertical, 1);
        Children.Add(_vertical);
        SetRow(_horizontal, 2);
        Children.Add(_horizontal);

        _vertical.ValueChanged += (_, _) => Scrolled();
        _horizontal.ValueChanged += (_, _) => Scrolled();
        _surface.SizeChanged += (_, _) =>
        {
            UpdateScrollBars();
            Scrolled();
            // a result computed whole arrives loaded: it fits once there are rows on screen to fit
            for (int c = 0; c < ColumnCount; c++)
                if (_cache.TryGet(c) is not null) QueueFit(c);
        };
        _surface.PointerWheelChanged += Wheel;
        _surface.PointerPressed += Pressed;
        _surface.PointerMoved += Moved;
        _surface.PointerExited += (_, _) => SetHoverButton(-1);
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
        cache.ColumnLoaded += (_, column, error) =>
        {
            _surface.InvalidateVisual();
            if (column == Current.Column) UpdateValueBar();
            if (error is null) QueueFit(column);
        };
        _loadTimer.Tick += (_, _) =>
        {
            _loadTimer.Stop();
            _ = EnsureVisibleAsync();
        };
        UpdateValueBar();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HighContrast.Changed += Repaint;
    }

    // off screen (another file took its place), the grid asks for nothing more
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _loadTimer.Stop();
        HighContrast.Changed -= Repaint;   // a static event would keep a closed file's grid alive
    }

    // high contrast went on or off: the colors come from Palette.For on the next frame
    private void Repaint()
    {
        _surface.InvalidateVisual();
        _valueBar.Repaint();
    }

    /// <summary>A line for the status bar: what a copy did, or why it didn't.</summary>
    public event Action<string>? Status;

    /// <summary>A header was clicked: the column and its rectangle, in the grid's coordinates.</summary>
    public event Action<int, Rect>? HeaderClicked;

    /// <summary>The columns with an active filter: their headers say so.</summary>
    public IReadOnlySet<int> FilteredColumns => _filtered;

    /// <summary>Marks the filtered columns: their buttons show a funnel on the accent.</summary>
    public void SetFiltered(IEnumerable<int> columns)
    {
        _filtered.Clear();
        _filtered.UnionWith(columns);
        _surface.InvalidateVisual();
    }

    /// <summary>The column whose filter dropdown is open (-1 for none): its button stays pressed.</summary>
    public int OpenFilterColumn
    {
        get => _openFilter;
        set
        {
            _openFilter = value;
            _surface.InvalidateVisual();
        }
    }

    private int _openFilter = -1;

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
        UpdateValueBar();
        _surface.InvalidateVisual();
    }

    public ColumnCache Cache => _cache;
    public double TotalWidth => _left[^1];
    /// <summary>The width the cells have: the surface's, less the row numbers.</summary>
    public double ViewportWidth => Math.Max(0, _surface.Bounds.Width - _frameWidth);
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

    // a column's left edge on the surface, past the row numbers
    private double ScreenX(int column) => _left[column] - OffsetX + _frameWidth;

    /// <summary>The columns that touch the screen, widened by <paramref name="margin"/> pixels each side.</summary>
    public List<int> VisibleColumns(double margin = 0)
    {
        double from = OffsetX - margin, to = OffsetX + ViewportWidth + margin;
        int found = Array.BinarySearch(_left, from);
        int first = Math.Max(0, found < 0 ? ~found - 1 : found);
        var columns = new List<int>();
        for (int c = first; c < ColumnCount && _left[c] < to; c++)
            if (!_hidden[c] && _left[c + 1] > from) columns.Add(c);
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
        Current = (Math.Clamp(row, 0, RowCount - 1), NearestShown(column, column >= Current.Column ? +1 : -1));
        if (!extend) Anchor = Current;
        BringIntoView(Current);
        UpdateValueBar();
        _surface.InvalidateVisual();
    }

    public void SelectAll()
    {
        Anchor = (0, NearestShown(0, +1));
        Current = (Math.Max(0, RowCount - 1), NearestShown(ColumnCount - 1, -1));
        UpdateValueBar();
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
        var columns = Enumerable.Range(s.Left, s.Right - s.Left + 1).Where(c => !_hidden[c]).ToList();
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

    /// <summary>The current cell in the value bar, whole.</summary>
    private void UpdateValueBar()
    {
        if (RowCount == 0 || ColumnCount == 0)
        {
            _valueBar.Show("", "", "no rows", note: true);
            return;
        }
        var (row, column) = Current;
        int fileRow = FileRow(row);
        string place = $"row {fileRow + 1:N0} · {_cache.TypeNames[column]}";
        var (text, kind) = Cells.Display(_cache, column, fileRow);
        (string value, bool note) = kind switch
        {
            Cells.Kind.Loading => ("loading…", true),
            Cells.Kind.Error => (PolarsErrors.Describe(_cache.Failure(column)!), true),
            Cells.Kind.Null => ("null", true),
            _ when text.Length == 0 => ("empty text", true),
            _ => (Cells.CopyText(_cache, column, fileRow), false),
        };
        _valueBar.Show(_cache.Fields[column].Name, place, value, note);
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
        _horizontal.SmallChange = Cells.ScrollStep;
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
        return (Math.Clamp(row, 0, Math.Max(0, RowCount - 1)), ColumnAtX(OffsetX + point.X - _frameWidth));
    }

    private void Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
            OffsetX -= (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * Cells.ScrollStep;
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
            if (point.X >= _frameWidth)   // the corner above the row numbers does nothing
            {
                int header = CellAt(point).Column;
                // in the grid's coordinates: the cells sit under the value bar
                HeaderClicked?.Invoke(header, new Rect(ScreenX(header), 0, _width[header], Cells.HeaderHeight)
                    .Translate(_surface.Bounds.Position));
            }
            e.Handled = true;
            return;
        }
        if (RowCount == 0) return;
        var (row, column) = CellAt(point);
        if (point.X < _frameWidth) column = Current.Column;   // a row number: that row, in the same column
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
            SetHoverButton(ButtonAt(at));
            UpdateTip(at);
            return;
        }
        var point = e.GetPosition(_surface);
        // past an edge, the view follows the pointer a row or a column at a time
        if (point.Y > _surface.Bounds.Height) FirstRow++;
        else if (point.Y < Cells.HeaderHeight) FirstRow--;
        if (point.X > _surface.Bounds.Width) OffsetX += Cells.ScrollStep;
        else if (point.X < _frameWidth) OffsetX -= Cells.ScrollStep;
        var clamped = new Point(Math.Clamp(point.X, _frameWidth, _surface.Bounds.Width - 1),
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
        if (point.Y < 0 || point.Y >= Cells.HeaderHeight || point.X < _frameWidth) return -1;
        double x = OffsetX + point.X - _frameWidth;
        int column = ColumnAtX(x);
        for (int c = Math.Max(0, column - 1); c <= Math.Min(ColumnCount - 1, column); c++)
            if (!_hidden[c] && Math.Abs(_left[c + 1] - x) <= BorderGrip) return c;
        return -1;
    }

    // A header's filter button, at its right, clear of the border that resizes:
    // centered on the header's height, as Excel's AutoFilter arrows.
    private static Rect ButtonRect(double x, double width) =>
        new(x + width - Cells.ButtonRoom, (Cells.HeaderHeight - 20) / 2, 20, 20);

    /// <summary>The header whose filter button is under the point; -1 for none.</summary>
    private int ButtonAt(Point point)
    {
        if (!_filterButtons || point.Y < 0 || point.Y >= Cells.HeaderHeight || point.X < _frameWidth) return -1;
        int column = CellAt(point).Column;
        return ButtonRect(ScreenX(column), _width[column]).Contains(point) ? column : -1;
    }

    private void SetHoverButton(int column)
    {
        if (column == _hoverButton) return;
        _hoverButton = column;
        _surface.InvalidateVisual();
    }

    /// <summary>A width the user chose, by dragging or fitting: nothing changes it afterwards.</summary>
    public void SetColumnWidth(int column, double width)
    {
        _sized[column] = true;
        _width[column] = Math.Max(MinColumnWidth, width);
        Relayout(column);
    }

    /// <summary>
    /// Hides these columns and shows the rest. A hidden column keeps its index
    /// (a column's index is the file's) and takes no room.
    /// </summary>
    public void SetHidden(IReadOnlySet<int> hidden)
    {
        for (int c = 0; c < ColumnCount; c++)
            _hidden[c] = hidden.Contains(c);
        Anchor = Current = (Current.Row, NearestShown(Current.Column, +1));
        Relayout(0);
        UpdateValueBar();
        _ = EnsureVisibleAsync();
    }

    public bool IsHidden(int column) => _hidden[column];

    // the left edges from this column on, a hidden column taking no room
    private void Relayout(int from)
    {
        for (int c = from; c < ColumnCount; c++)
            _left[c + 1] = _left[c] + (_hidden[c] ? 0 : _width[c]);
        UpdateScrollBars();
        _surface.InvalidateVisual();
    }

    /// <summary>The column itself if shown; else the next shown one that way, or the other way.</summary>
    private int NearestShown(int column, int step)
    {
        column = Math.Clamp(column, 0, ColumnCount - 1);
        for (int c = column; c >= 0 && c < ColumnCount; c += step)
            if (!_hidden[c]) return c;
        for (int c = column; c >= 0 && c < ColumnCount; c -= step)
            if (!_hidden[c]) return c;
        return column;   // all hidden: "Columns" never allows it
    }

    // the shown column whose span holds x: the last left edge at or before x
    // (hidden columns share their edge with the next one, so take the last)
    private int ColumnAtX(double x)
    {
        int low = 0, high = ColumnCount;   // the first edge past x, in _left[1..]
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_left[mid + 1] > x) high = mid; else low = mid + 1;
        }
        return NearestShown(Math.Min(low, ColumnCount - 1), +1);
    }

    private double HeaderWidth(int column) =>
        Cells.HeaderWidth(_cache.Fields[column].Name, _cache.TypeNames[column], _filterButtons);

    /// <summary>
    /// A column's cells on rows [<paramref name="first"/>, first + count), as
    /// drawn, into _cells; and into _shifts, how far each number moves left so
    /// the decimal separators line up (0 for the rest).
    /// </summary>
    private void ReadColumn(int column, int first, int count)
    {
        if (_cells.Length < count)
        {
            _cells = new (string, Cells.Kind)[count];
            _shifts = new double[count];
        }
        bool align = Cells.IsFractional(_cache, column);
        int most = 0;
        for (int r = 0; r < count; r++)
        {
            _cells[r] = Cells.Display(_cache, column, FileRow(first + r));
            if (align && _cells[r].Kind == Cells.Kind.Number) most = Math.Max(most, Cells.Decimals(_cells[r].Text));
        }
        for (int r = 0; r < count; r++)
            _shifts[r] = most > 0 && _cells[r].Kind == Cells.Kind.Number ? Cells.DecimalShift(_cells[r].Text, most) : 0;
    }

    /// <summary>What a column asks for: its header and the rows on screen, the decimals' room included.</summary>
    private double ContentWidth(int column)
    {
        double widest = HeaderWidth(column);
        int first = FirstRow, count = Math.Max(0, Math.Min(RowCount - first, VisibleRowCount + 1));
        ReadColumn(column, first, count);
        for (int r = 0; r < count; r++)
            widest = Math.Max(widest, Cells.Format(_cells[r].Text, _cells[r].Kind, Brushes.Black).Width + _shifts[r] + 2 * Cells.Padding + 1);
        return widest;
    }

    /// <summary>
    /// Two clicks on a border: the column fits its header and the rows on
    /// screen. Only those: the DataGridView measures every row, which on 5
    /// million rows would freeze the window.
    /// </summary>
    public void FitColumn(int column)
    {
        SetColumnWidth(column, Math.Min(MaxFitWidth, ContentWidth(column)));
        _ = EnsureVisibleAsync();
    }

    // A column that loads fits its rows on screen, once, unless the user sized it.
    // The columns that arrive together fit together: one relayout, not one each.
    private void QueueFit(int column)
    {
        if (_fitted[column] || _sized[column]) return;
        _toFit.Add(column);
        if (_fitPosted) return;
        _fitPosted = true;
        Dispatcher.UIThread.Post(FitLoaded, DispatcherPriority.Background);
    }

    private void FitLoaded()
    {
        _fitPosted = false;
        if (_surface.Bounds.Height <= Cells.HeaderHeight) return;   // no rows to fit yet: SizeChanged queues them again
        // the leftmost column on screen stays where it is, though columns before it change width
        var shown = VisibleColumns();
        int anchor = shown.Count > 0 ? shown[0] : 0;
        double at = _left[anchor] - OffsetX;
        int from = ColumnCount;
        foreach (int column in _toFit)
        {
            if (_sized[column] || _cache.TryGet(column) is null) continue;   // sized, or gave its memory back meanwhile
            _fitted[column] = true;
            double width = Math.Clamp(ContentWidth(column), Cells.MinFit, Cells.MaxAutoFit);
            if (width <= _width[column]) continue;   // the header's width already holds it: nothing moves
            _width[column] = width;
            from = Math.Min(from, column);
        }
        _toFit.Clear();
        if (from == ColumnCount) return;
        Relayout(from);
        OffsetX = _left[anchor] - at;
        Scrolled();   // narrower or wider, other columns may come on screen: load them when it settles
    }

    private (int Row, int Column)? _tipCell;

    /// <summary>A cut cell's tooltip shows its whole value, as the WinForms grid's does.</summary>
    private void UpdateTip(Point point)
    {
        if (point.Y < Cells.HeaderHeight || point.X < _frameWidth || RowCount == 0)
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
        int page = VisibleRowCount, lastRow = RowCount - 1;
        int firstColumn = NearestShown(0, +1), lastColumn = NearestShown(ColumnCount - 1, -1);
        (int, int)? to = e.Key switch
        {
            Key.Up => ctrl ? (0, column) : (row - 1, column),
            Key.Down => ctrl ? (lastRow, column) : (row + 1, column),
            Key.Left => ctrl ? (row, firstColumn) : (row, Math.Max(firstColumn, column - 1)),
            Key.Right => ctrl ? (row, lastColumn) : (row, Math.Min(lastColumn, column + 1)),
            Key.PageUp => (row - page, column),
            Key.PageDown => (row + page, column),
            Key.Home => ctrl ? (0, firstColumn) : (row, firstColumn),
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
        private const double NameTop = 4, NameHeight = 22, TypeTop = 26, TypeHeight = 17;   // the header's two lines

        public override void Render(DrawingContext context)
        {
            var palette = Palette.For(ActualThemeVariant);
            var size = Bounds.Size;
            double frame = owner._frameWidth, top = Cells.HeaderHeight, height = Cells.RowHeight;
            context.FillRectangle(palette.Background, new Rect(size));
            var cache = owner.Cache;
            int firstRow = owner.FirstRow;
            int rows = Math.Max(0, Math.Min((int)Math.Ceiling((size.Height - top) / height), owner.RowCount - firstRow));
            var s = owner.Selection;
            var current = owner.Current;
            bool any = owner.RowCount > 0;
            double right = Math.Min(size.Width, owner.ScreenX(owner.ColumnCount));   // where the last column ends

            // the current row's band, under everything on it
            if (palette.Focus is { } focus && any && current.Row >= firstRow && current.Row < firstRow + rows)
                context.FillRectangle(focus, new Rect(frame, top + (current.Row - firstRow) * height, Math.Max(0, right - frame), height));

            foreach (int column in owner.VisibleColumns())
            {
                double x = owner.ScreenX(column), width = owner.ColumnWidth(column);
                bool selectedColumn = any && column >= s.Left && column <= s.Right;
                if (palette.Focus is { } band && any && column == current.Column)   // the current column's band
                    context.FillRectangle(band, new Rect(x, top, width, rows * height));
                owner.ReadColumn(column, firstRow, rows);
                bool numeric = Cells.IsNumeric(cache, column);
                for (int r = 0; r < rows; r++)
                {
                    int row = firstRow + r;
                    double y = top + r * height;
                    bool isCurrent = row == current.Row && column == current.Column;
                    bool selected = !isCurrent && selectedColumn && row >= s.Top && row <= s.Bottom;
                    if (isCurrent)   // the cell you're on stays clear, as in Excel
                        context.FillRectangle(palette.Background, new Rect(x, y, width, height));
                    else if (selected)
                        context.FillRectangle(palette.Selection, new Rect(x, y, width, height));
                    var (text, kind) = owner._cells[r];
                    DrawCell(context, palette, text, kind, x, y, width, numeric, owner._shifts[r], selected);
                }
                DrawHeader(context, palette, column, x, width, filtered: owner._filtered.Contains(column), lit: selectedColumn,
                    pressed: column == owner._hoverButton || column == owner._openFilter);
                context.DrawLine(palette.Line, new Point(x + width - 0.5, 0), new Point(x + width - 0.5, size.Height));
            }

            // the row numbers, over whatever column slid under them
            context.FillRectangle(palette.Header, new Rect(0, 0, frame, size.Height));
            for (int r = 0; r < rows; r++)
            {
                int row = firstRow + r;
                bool lit = any && row >= s.Top && row <= s.Bottom;
                double y = top + r * height;
                if (lit) context.FillRectangle(palette.HeaderLit, new Rect(0, y, frame, height));
                var number = Cells.Format((owner.FileRow(row) + 1).ToString("N0", CultureInfo.CurrentCulture),
                    lit ? Cells.Bold : Cells.Face, lit ? palette.HeaderText : palette.HeaderMuted, Cells.FontSize - 1, tabular: true);
                Place(context, number, 0, y, frame, height, right: true);
            }
            context.DrawLine(palette.Line, new Point(frame - 0.5, 0), new Point(frame - 0.5, size.Height));
            for (int r = 0; r <= rows; r++)
            {
                double y = top + r * height - 0.5;
                context.DrawLine(palette.Line, new Point(0, y), new Point(size.Width, y));
            }
            if (!any) return;

            // where the selection is: under its column headers, beside its row numbers, and around it.
            // Rows far off screen would be millions of pixels away, past what Skia's floats keep:
            // the edges are clamped to just outside the view.
            double x1 = owner.ScreenX(s.Left), x2 = owner.ScreenX(s.Right + 1);
            double y1 = top + (double)(s.Top - firstRow) * height, y2 = top + (double)(s.Bottom + 1 - firstRow) * height;
            x1 = Math.Max(x1, frame - 4); x2 = Math.Min(x2, size.Width + 4);
            y1 = Math.Max(y1, top - 4); y2 = Math.Min(y2, size.Height + 4);
            if (x1 >= x2 || y1 >= y2) return;
            if (x2 > frame) context.DrawLine(palette.SelectionEdge, new Point(Math.Max(x1, frame), top - 1), new Point(x2, top - 1));
            if (y2 > top) context.DrawLine(palette.SelectionEdge, new Point(frame - 1, Math.Max(y1, top)), new Point(frame - 1, y2));
            using (context.PushClip(new Rect(frame, top, Math.Max(0, size.Width - frame), Math.Max(0, size.Height - top))))
            {
                if (palette.Outline is { } outline)
                    context.DrawRectangle(null, outline, new Rect(x1 + 1, y1 + 1, Math.Max(0, x2 - x1 - 2), Math.Max(0, y2 - y1 - 2)));
                if (palette.CurrentEdge is { } edge && current.Row >= firstRow && current.Row < firstRow + rows)
                    context.DrawRectangle(null, edge, new Rect(owner.ScreenX(current.Column) + 1, top + (current.Row - firstRow) * height + 1,
                        Math.Max(0, owner.ColumnWidth(current.Column) - 2), height - 2));
            }
        }

        /// <summary>
        /// A header: the column's name, its Polars type under it, and the
        /// filter's button, drawn, never in the name (Ctrl+C copies the name).
        /// The button is a gray chevron; on a filtered column, a funnel on the
        /// accent: a shape of its own, since color is never the only cue.
        /// </summary>
        private void DrawHeader(DrawingContext context, Palette palette, int column, double x, double width,
            bool filtered, bool lit, bool pressed)
        {
            var cache = owner.Cache;
            context.FillRectangle(filtered ? palette.AccentBack : lit ? palette.HeaderLit : palette.Header, new Rect(x, 0, width, Cells.HeaderHeight));
            var ink = filtered ? palette.AccentText : palette.HeaderText;
            double room = width - (owner._filterButtons ? Cells.ButtonRoom - Cells.Padding : 0);
            Place(context, Fit(Cells.Format(cache.Fields[column].Name, Cells.Bold, ink), room), x, NameTop, room, NameHeight, right: false);
            Place(context, Fit(Cells.Format(cache.TypeNames[column], Cells.Face, filtered ? ink : palette.HeaderMuted, Cells.TypeSize), room),
                x, TypeTop, room, TypeHeight, right: false);
            if (!owner._filterButtons) return;

            var button = ButtonRect(x, width);
            using (context.PushClip(new Rect(x, 0, Math.Max(0, width - 1), Cells.HeaderHeight)))
            {
                if (pressed)   // under the pointer, or its dropdown open
                    context.DrawRectangle(filtered ? null : palette.PressedFill, filtered ? new Pen(ink, 1) : palette.PressedEdge, button.Deflate(0.5), 3, 3);
                var center = button.Center;
                var shape = new StreamGeometry();
                using (var path = shape.Open())
                {
                    if (filtered)
                    {
                        path.BeginFigure(center + new Vector(-5, -4), isFilled: true);
                        path.LineTo(center + new Vector(5, -4));
                        path.LineTo(center + new Vector(1, 1));
                        path.LineTo(center + new Vector(1, 5));
                        path.LineTo(center + new Vector(-1, 4));
                        path.LineTo(center + new Vector(-1, 1));
                        path.EndFigure(true);
                    }
                    else
                    {
                        path.BeginFigure(center + new Vector(-4, -2), isFilled: false);
                        path.LineTo(center + new Vector(0, 2));
                        path.LineTo(center + new Vector(4, -2));
                        path.EndFigure(false);
                    }
                }
                if (filtered)
                    context.DrawGeometry(ink, null, shape);
                else
                    context.DrawGeometry(null, new Pen(pressed ? palette.HeaderText : palette.HeaderMuted, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), shape);
            }
        }

        /// <summary>
        /// A cell by the app's GridPainting rules: null, "…" and "error" in
        /// muted (or red) italics; a number that doesn't fit becomes "###",
        /// since cut-off digits read as another number; a text that doesn't
        /// fit ends in "…". A selected cell keeps its text's color, but in high
        /// contrast, where the selection is the system's Highlight and has a text color of its own.
        /// </summary>
        private static void DrawCell(DrawingContext context, Palette palette, string text, Cells.Kind kind,
            double x, double y, double width, bool right, double shift, bool selected)
        {
            var brush = kind switch
            {
                Cells.Kind.Null or Cells.Kind.Loading => palette.Muted,
                Cells.Kind.Error => palette.Error,
                _ => palette.Text,
            };
            if (selected && palette.SelectionText is { } selectionText) brush = selectionText;
            double room = width - 2 * Cells.Padding - 1 - shift;
            var formatted = Cells.Format(text, kind, brush);
            if (formatted.Width > room && formatted.Width <= room + shift)
                shift = 0;   // it fits, only not lined up (the column was fit to another screen): "###" would hide a number that fits
            else if (formatted.Width > room)
            {
                if (kind == Cells.Kind.Number)
                {
                    formatted = Cells.Format("###", kind, brush);
                    shift = 0;
                }
                else
                    formatted = Fit(formatted, width);
            }
            Place(context, formatted, x, y, width, Cells.RowHeight, right, shift);
        }

        // a text cut, ending in "…", to what a space this wide holds inside its padding
        private static FormattedText Fit(FormattedText formatted, double width)
        {
            double room = width - 2 * Cells.Padding - 1;
            if (formatted.Width <= room) return formatted;
            formatted.MaxTextWidth = Math.Max(1, room);
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            return formatted;
        }

        private static void Place(DrawingContext context, FormattedText formatted, double x, double y, double width, double height,
            bool right, double shift = 0)
        {
            using (context.PushClip(new Rect(x, y, width - 1, height)))
                context.DrawText(formatted, new Point(right ? x + width - Cells.Padding - 1 - shift - formatted.Width : x + Cells.Padding,
                    y + (height - formatted.Height) / 2));
        }
    }
}
