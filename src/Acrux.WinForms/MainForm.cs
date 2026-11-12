using System;
using System.Data;
using System.Drawing;
using System.Linq;
using PolarsLabel = Polars.CSharp.Label;
using FormsLabel = System.Windows.Forms.Label;
using System.Windows.Forms;
using Polars.CSharp;

using Apache.Arrow;
using Apache.Arrow.Ipc;
using ArrowTypes = Apache.Arrow.Types;
using Field = Apache.Arrow.Field;
namespace Acrux;

public partial class MainForm : Form
{
    private readonly DataGridView _grid;
    private readonly Button _btnLoad;
    private readonly Button _btnColumns;
    private readonly Button _btnClearFilters;
    private readonly Button _btnTerminal;
    private readonly Button _btnRestore;
    private readonly FormsLabel _lblInfo;
    private readonly FormsLabel _lblMemory;
    private readonly SplitContainer _split;
    private readonly ScriptTerminalPanel _terminal;

    private string? _parquetPath;
    private string? _tempParquetPath;   // parquet converted from CSV (delete on switch/close)
    // the memory the caches share: half of what is free, them included
    private readonly MemoryBudget _memory = new();
    // the file, column by column (null before a file opens); it stays alive
    // under a script result, unpinned, so "Restore file" rereads only the
    // columns that gave their memory to the result
    private ColumnCache? _cache;
    // a script result on screen, also column by column; null when the grid shows the file
    private ColumnCache? _result;
    // hidden by the "Columns" dialog: they stay in the grid with Visible =
    // false, so a grid column's index is always the file column's
    private readonly HashSet<string> _hiddenColumns = new();
    // restarted by every cell whose column isn't in memory: the columns load
    // once the grid stops moving
    private readonly System.Windows.Forms.Timer _loadTimer = new() { Interval = 100 };
    // reads the free memory again: the cache gives some back when others take it
    private readonly System.Windows.Forms.Timer _trimTimer = new() { Interval = 5000 };
    private bool _showingColumnError;
    private bool _scriptMode;
    private readonly List<string> _scriptChain = new();
    // grid state frozen when the chain starts: the replay re-runs every step,
    // so the base must not drift while the chain is alive
    private string[]? _chainBaseColumns;
    private Dictionary<string, ColumnFilter>? _chainBaseFilters;
    private readonly Dictionary<string, ColumnFilter> _activeFilters = new();
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _scriptCts;
    // the grid's rows, in blocks when there are many (see GridRows)
    private readonly GridRows _rows;
    // builds running, and what the buttons were before the first: they wait for the rows
    private int _rowBuilds;
    private (bool Load, bool Columns, bool ClearFilters, bool Restore) _enabledBeforeRows;


public MainForm(string? initialFile = null)
{
    Text = "Acrux";
    Icon = AppIcon.Value;
    Theme.Apply(this);
    Width = 1280;
    Height = 760;
    StartPosition = FormStartPosition.CenterScreen;

    // laid out by flow, not by pixel: wider text (DPI, fonts) pushes the
    // next control instead of overlapping it, and a hidden button leaves no gap
    var topPanel = new FlowLayoutPanel
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        Padding = new Padding(10)
    };

    _btnLoad = new Button
    {
        Text = "Open file",
        AutoSize = true
    };
    _btnLoad.Click += BtnLoad_Click;

    _btnColumns = new Button
    {
        Text = "Columns",
        AutoSize = true,
        Enabled = false
    };
    _btnColumns.Click += BtnColumns_Click;

    _btnClearFilters = new Button
    {
        Text = "Clear all filters",
        AutoSize = true,
        Enabled = false
    };
    _btnClearFilters.Click += BtnClearFilters_Click;

    _btnTerminal = new Button
    {
        Text = "C# terminal",
        AutoSize = true,
        Enabled = false
    };
    _btnTerminal.Click += (_, _) => ToggleTerminal();

    _btnRestore = new Button
    {
        Text = "Restore file",
        AutoSize = true,
        Visible = false
    };
    _btnRestore.Click += BtnRestore_Click;

    _lblInfo = new FormsLabel
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,   // no Top: centered on the buttons' row
        Margin = new Padding(12, 0, 3, 0),
        Text = "No data loaded"
    };

    _lblMemory = new FormsLabel
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(12, 0, 3, 0),
        ForeColor = Theme.Muted,
        Visible = false
    };

    topPanel.Controls.Add(_btnLoad);
    topPanel.Controls.Add(_btnColumns);
    topPanel.Controls.Add(_btnClearFilters);
    topPanel.Controls.Add(_btnTerminal);
    topPanel.Controls.Add(_btnRestore);
    topPanel.Controls.Add(_lblInfo);
    topPanel.Controls.Add(_lblMemory);

    _grid = new DataGridView
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = true,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        MultiSelect = true,
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
        BorderStyle = BorderStyle.None,

        // ── virtual-mode tuning ──────────────────────────
        VirtualMode = true,                          // only visible cells are rendered
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,      // CRITICAL: avoids constant re-measuring
        RowHeadersVisible = false,                   // index column costs render time
        RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        AllowUserToResizeRows = false,               // avoids per-row height recalc
    };

    // DoubleBuffered via reflection (protected property)
    typeof(DataGridView)
        .GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance)!
        .SetValue(_grid, true);

    // fixed row height — avoids per-row measuring
    _grid.RowTemplate.Height = 24;
    _grid.RowTemplate.Resizable = DataGridViewTriState.False;

    _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10, FontStyle.Bold);
    _grid.DefaultCellStyle.Font = new Font("Segoe UI", 10);
    _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
    Theme.StyleGrid(_grid);
    _painting = new GridPainting(_grid, column => _activeFilters.ContainsKey(column));
    _rows = new GridRows(_grid);

    // supplies values only for visible cells
    _grid.CellValueNeeded += Grid_CellValueNeeded;

    // header click opens the Excel-style column filter
    _grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;

    // the copy keys: columns not in memory load before the copy. The grid
    // takes Ctrl+C and Ctrl+Insert as input keys, which reach KeyDown, but
    // copies Ctrl+Shift+C as a dialog key, before KeyDown: made input too
    _grid.PreviewKeyDown += (_, e) =>
    {
        if (e.KeyCode == Keys.C && e.Modifiers == (Keys.Control | Keys.Shift))
            e.IsInputKey = true;
    };
    _grid.KeyDown += Grid_KeyDown;

    _loadTimer.Tick += LoadTimer_Tick;
    _trimTimer.Tick += (_, _) =>
    {
        _memory.Trim();
        UpdateMemoryInfo();
    };
    _trimTimer.Start();

    _terminal = new ScriptTerminalPanel();
    _terminal.ExecuteRequested += Terminal_ExecuteRequested;
    _terminal.UndoRequested += Terminal_UndoRequested;

    _split = new SplitContainer
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
        Panel2Collapsed = true,
        SplitterWidth = 6
    };
    _split.Panel1.Controls.Add(_grid);
    _split.Panel2.Controls.Add(_terminal);

    Controls.Add(_split);
    Controls.Add(topPanel);

    // opened from Explorer: load once the window is on screen, so a dialog
    // (the sheet or table picker) has its owner
    if (initialFile is not null)
        Shown += async (_, _) => await LoadFileAsync(initialFile);
}

private void ToggleTerminal()
{
    _split.Panel2Collapsed = !_split.Panel2Collapsed;
    if (!_split.Panel2Collapsed)
    {
        try { _split.SplitterDistance = Math.Max(120, _split.Height * 60 / 100); }
        catch (InvalidOperationException) { /* window too small, keep the default */ }
        _terminal.FocusInput();
    }
}

private int[]?      _filteredRows;      // filtered row indices (null = no filter)
private int         _rowCount;

// the table on screen: the script result, or else the file
private ColumnCache? OnScreen => _result ?? _cache;

private void Grid_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
{
    // reads the cell straight from the Arrow buffer (O(1), no column materialization)
    if (OnScreen is not { } cache) return;
    int dataRow = _filteredRows is null ? e.RowIndex : _filteredRows[e.RowIndex];

    if (cache.TryGet(e.ColumnIndex) is { } column)
        e.Value = PolarsTableAdapter.GetCellValue(column, dataRow);
    else if (cache.Failure(e.ColumnIndex) is not null)
        e.Value = CellPlaceholder.Failed;
    else
    {
        e.Value = CellPlaceholder.Loading;
        _loadTimer.Stop();
        _loadTimer.Start();
    }
}

// nulls, numbers too wide, line breaks and the filter's "▼": drawn, never in the data
private readonly GridPainting _painting;

// Integers and floats share NumberType, and so does BooleanType (True/False
// stays left); Arrow keeps the decimals apart. Checked on the 20-type fixture.
private static bool IsNumeric(ArrowTypes.IArrowType type) => type
    is (ArrowTypes.NumberType and not ArrowTypes.BooleanType)
    or ArrowTypes.Decimal32Type or ArrowTypes.Decimal64Type
    or ArrowTypes.Decimal128Type or ArrowTypes.Decimal256Type;

private async void BtnLoad_Click(object? sender, EventArgs e)
{
    using var dialog = new OpenFileDialog
    {
        Title = "Select a Parquet, CSV or Excel file",
        Filter = DataFrameProvider.OpenDialogFilter
    };

    if (dialog.ShowDialog(this) == DialogResult.OK)
        await LoadFileAsync(dialog.FileName);
}

private async Task LoadFileAsync(string fileName)
{
    string sourcePath = fileName;
    bool sourceIsTemp = false;
    ColumnCache? cache = null;
    try
    {
        _btnLoad.Enabled = false;

        // the content decides the format, not the extension
        var format = await Task.Run(() => DataFrameProvider.DetectFormat(fileName));

        // xlsx with several sheets / SQLite with several tables: ask which one before converting
        int tableIndex = 0;
        if (format is SourceFormat.Excel or SourceFormat.Sqlite)
        {
            var tableNames = await Task.Run(() => DataFrameProvider.GetTableNames(fileName));
            if (tableNames.Length > 1)
            {
                using var picker = new SheetSelectorForm(tableNames,
                    format == SourceFormat.Excel ? "Select sheet" : "Select table");
                if (picker.ShowDialog(this) != DialogResult.OK)
                {
                    _lblInfo.Text = "Load canceled";
                    return;
                }
                tableIndex = picker.SelectedSheetIndex;
            }
        }

        // CSV/xlsx/SQLite: one-time conversion to a temp parquet; the app operates on parquet
        _lblInfo.Text = format switch
        {
            SourceFormat.Csv => "Converting CSV to parquet...",
            SourceFormat.Excel => "Converting Excel to parquet...",
            SourceFormat.Sqlite => "Converting SQLite table to parquet...",
            _ => "Reading schema...",
        };
        var progress = new Progress<string>(msg => _lblInfo.Text = msg);
        (sourcePath, sourceIsTemp) = await DataFrameProvider.EnsureParquetAsync(fileName, progress, tableIndex);

        // metadata only, fast on any file: the schema and the row count. The
        // columns load as the grid reaches them.
        _lblInfo.Text = "Reading schema...";
        cache = await ColumnCache.OpenAsync(sourcePath, _memory);

        // the grid empties while the old file is still its source
        ClearGrid();
        CloseResult();
        _filteredRows = null;
        CloseCache();
        _cache = cache;
        cache = null;   // the form owns it now
        _cache.ColumnLoaded += Cache_ColumnLoaded;
        _hiddenColumns.Clear();

        // new file: previous filters and script mode no longer apply
        _filterCts?.Cancel();
        _scriptCts?.Cancel();
        _activeFilters.Clear();
        _scriptChain.Clear();
        _chainBaseColumns = null;
        _chainBaseFilters = null;

        // success: the previous temp (if any) can go
        if (_tempParquetPath is not null)
            TryDeleteTemp(_tempParquetPath);
        _tempParquetPath = sourceIsTemp ? sourcePath : null;

        _parquetPath = sourcePath;
        _btnClearFilters.Enabled = false;
        _scriptMode = false;
        _btnRestore.Visible = false;
        _btnTerminal.Enabled = true;
        _btnColumns.Enabled = true;

        if (await ShowFileAsync())
            UpdateInfo();
    }
    catch (Exception ex)
    {
        cache?.Dispose();
        // failure: don't orphan the freshly converted temp (unless it became the active one)
        if (sourceIsTemp && !ReferenceEquals(sourcePath, _tempParquetPath) && sourcePath != _tempParquetPath)
            TryDeleteTemp(sourcePath);
        MessageBox.Show(
            $"Error loading DataFrame:\n\n{PolarsErrors.Describe(ex)}",
            "Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        _lblInfo.Text = "Load failed";
    }
    finally
    {
        _btnLoad.Enabled = true;
    }
}

private static void TryDeleteTemp(string path)
{
    try { File.Delete(path); }
    catch (IOException) { /* still in use; left for OS cleanup */ }
    catch (UnauthorizedAccessException) { }
}

protected override void OnFormClosed(FormClosedEventArgs e)
{
    _rows.Abandon();
    CloseResult();
    CloseCache();
    if (_tempParquetPath is not null)
        TryDeleteTemp(_tempParquetPath);
    base.OnFormClosed(e);
}

    /// <summary>
    /// Empties the grid before its source changes: Rows.Clear() reads the
    /// current cell on its way out, so the old source must still be in place.
    /// (Rows.Clear is O(1); RowCount = 0 would remove row by row.)
    /// </summary>
    private void ClearGrid()
    {
        _rows.Abandon();
        _grid.Rows.Clear();
        _grid.Columns.Clear();
    }

    /// <summary>
    /// The grid over the file: every column of the schema (the hidden ones
    /// with Visible = false), the rows through the current indirection. No
    /// cell is read here; the columns load as the grid reaches them. Without
    /// <paramref name="rows"/>, the caller sets them (the filters coming back).
    /// </summary>
    /// <returns>false when something else took the grid while the rows went in</returns>
    private Task<bool> ShowFileAsync(bool rows = true)
    {
        AddColumns(_cache!.Fields, _hiddenColumns);
        UpdateHeaderIndicators();
        _rowCount = _cache.RowCount;
        return rows ? SetRowCountAsync(_filteredRows?.Length ?? _rowCount) : Task.FromResult(true);
    }

    /// <summary>
    /// Shows a script result (ownership transfers to the form). The file's
    /// cache stays alive underneath, unpinned: its columns give way first when
    /// the result needs memory, and "Restore file" reads only what left.
    /// </summary>
    private Task<bool> ShowResultAsync(ColumnCache result)
    {
        ClearGrid();
        CloseResult();
        _result = result;
        _result.ColumnLoaded += Cache_ColumnLoaded;
        _rowCount = result.RowCount;
        _filteredRows = null;
        _cache?.Unpin();

        AddColumns(result.Fields, hidden: null);
        return SetRowCountAsync(_rowCount);
    }

    /// <summary>
    /// The rows of the empty grid. With many, they go in blocks (see
    /// <see cref="GridRows"/>), with the count in the status bar; meanwhile
    /// the first screen's columns load, and the buttons that would replace
    /// the grid wait.
    /// </summary>
    /// <returns>false when something else took the grid meanwhile</returns>
    private async Task<bool> SetRowCountAsync(int count)
    {
        if (count < GridRows.BlocksFrom)
            return await _rows.SetAsync(count);

        _ = LoadColumnsAsync(FirstScreenColumns());
        if (_rowBuilds++ == 0)
        {
            _enabledBeforeRows = (_btnLoad.Enabled, _btnColumns.Enabled, _btnClearFilters.Enabled, _btnRestore.Enabled);
            _btnLoad.Enabled = _btnColumns.Enabled = _btnClearFilters.Enabled = _btnRestore.Enabled = false;
            _terminal.SetBusy(true);
        }
        try
        {
            return await _rows.SetAsync(count, (done, total) =>
            {
                _lblInfo.Text = $"Preparing the grid: {done:N0} of {total:N0} rows...";
                // painted now: Windows paints only when the queue is empty,
                // and between blocks it may never be
                _lblInfo.Update();
            });
        }
        finally
        {
            if (--_rowBuilds == 0)
            {
                (_btnLoad.Enabled, _btnColumns.Enabled, _btnClearFilters.Enabled, _btnRestore.Enabled) = _enabledBeforeRows;
                _terminal.SetBusy(false);
            }
        }
    }

    /// <summary>
    /// The columns the first screen will show, and half a screen more, by
    /// their widths: the grid can't say, because its columns hide while the
    /// rows go in. Before them, so the data arrives with the rows.
    /// </summary>
    private List<int> FirstScreenColumns()
    {
        const DataGridViewElementStates visible = DataGridViewElementStates.Visible;
        var columns = new List<int>();
        int width = _grid.ClientSize.Width * 3 / 2;
        int first = _grid.FirstDisplayedScrollingColumnIndex;
        var column = first >= 0 ? _grid.Columns[first] : _grid.Columns.GetFirstColumn(visible);
        for (int used = 0; column is not null && used < width;
             column = _grid.Columns.GetNextColumn(column, visible, DataGridViewElementStates.None))
        {
            columns.Add(column.Index);
            used += column.Width;
        }
        return columns;
    }

    private void AddColumns(IReadOnlyList<Field> fields, IReadOnlySet<string>? hidden)
    {
        _grid.SuspendLayout();
        _grid.Columns.AddRange(fields
            .Select(field => (DataGridViewColumn)new DataGridViewTextBoxColumn
            {
                Name       = field.Name,
                HeaderText = field.Name,
                Width      = 120,
                SortMode   = DataGridViewColumnSortMode.Programmatic,
                FillWeight = 1,   // default is 100 and the sum caps at 65535
                Visible    = hidden is null || !hidden.Contains(field.Name),
                // numbers to the right, as in a spreadsheet: magnitudes line up
                DefaultCellStyle = { Alignment = IsNumeric(field.DataType)
                    ? DataGridViewContentAlignment.MiddleRight
                    : DataGridViewContentAlignment.MiddleLeft }
            })
            .ToArray());
        _grid.ResumeLayout();
    }

    // in-flight loads finish on their own and drop what they bring
    private void CloseCache()
    {
        if (_cache is null) return;
        _cache.ColumnLoaded -= Cache_ColumnLoaded;
        _cache.Dispose();
        _cache = null;
    }

    private void CloseResult()
    {
        if (_result is null) return;
        _result.ColumnLoaded -= Cache_ColumnLoaded;
        _result.Dispose();
        _result = null;
    }

    private async void LoadTimer_Tick(object? sender, EventArgs e)
    {
        _loadTimer.Stop();
        await LoadColumnsAsync(ColumnsAroundView());
    }

    private async Task LoadColumnsAsync(List<int> columns)
    {
        if (OnScreen is not { } cache || columns.Count == 0) return;

        try
        {
            var loading = cache.EnsureAsync(columns);
            UpdateMemoryInfo();
            await loading;
            UpdateMemoryInfo();
        }
        catch (Exception ex)
        {
            _lblInfo.Text = $"Loading columns failed: {PolarsErrors.Brief(ex)}";
        }
    }

    /// <summary>
    /// The columns on screen first, then half a screen past each edge, so a
    /// short scroll finds them loaded. In display order: a dragged column
    /// counts where it shows.
    /// </summary>
    private List<int> ColumnsAroundView()
    {
        var columns = new List<int>();
        int first = _grid.FirstDisplayedScrollingColumnIndex;
        if (first < 0) return columns;

        const DataGridViewElementStates visible = DataGridViewElementStates.Visible;
        const DataGridViewElementStates none = DataGridViewElementStates.None;
        int shown = Math.Max(1, _grid.DisplayedColumnCount(includePartialColumns: true));
        int margin = (shown + 1) / 2;

        DataGridViewColumn? column = _grid.Columns[first];
        for (int i = 0; i < shown + margin && column is not null; i++)
        {
            columns.Add(column.Index);
            column = _grid.Columns.GetNextColumn(column, visible, none);
        }
        column = _grid.Columns.GetPreviousColumn(_grid.Columns[first], visible, none);
        for (int i = 0; i < margin && column is not null; i++)
        {
            columns.Add(column.Index);
            column = _grid.Columns.GetPreviousColumn(column, visible, none);
        }
        return columns;
    }

    private void Cache_ColumnLoaded(ColumnCache cache, int column, Exception? error)
    {
        UpdateMemoryInfo();
        if (cache != OnScreen) return;   // the file's column, arriving under a script result
        _grid.InvalidateColumn(column);
        if (error is null) return;

        // a script's column fails when it is computed (a Cast that can't
        // convert, say), after the step joined the chain: said where the
        // script was typed, next to "Undo step"
        if (cache == _result)
        {
            _terminal.AppendError($"column \"{cache.Fields[column].Name}\": {PolarsErrors.Describe(error)}");
            return;
        }
        if (_showingColumnError) return;

        // one dialog at a time: a file gone bad fails every column at once,
        // and each failed cell says "error"
        _showingColumnError = true;
        try
        {
            MessageBox.Show(
                $"Error reading column \"{cache.Fields[column].Name}\":\n\n{PolarsErrors.Describe(error)}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _showingColumnError = false;
        }
    }

    private async void Grid_KeyDown(object? sender, KeyEventArgs e)
    {
        // the grid's own copy keys: Ctrl+C, Ctrl+Shift+C and Ctrl+Insert
        bool copy = (e.KeyCode == Keys.C && e.Modifiers is Keys.Control or (Keys.Control | Keys.Shift))
            || (e.KeyCode == Keys.Insert && e.Modifiers == Keys.Control);
        if (!copy || OnScreen is not { } cache)
            return;

        e.Handled = true;
        e.SuppressKeyPress = true;
        await CopySelectionAsync(cache);
    }

    /// <summary>
    /// Ctrl+C over columns not in memory: loads them first, so the copy never
    /// carries a "…". A selection too big for the budget is refused rather
    /// than copied in part.
    /// </summary>
    private async Task CopySelectionAsync(ColumnCache cache)
    {
        try
        {
            while (true)
            {
                var columns = SelectedColumns();
                if (columns.Count == 0) return;

                int failed = columns.FindIndex(c => cache.Failure(c) is not null);
                if (failed >= 0)
                {
                    MessageBox.Show(
                        $"Column \"{cache.Fields[columns[failed]].Name}\" could not be read, so the selection was not copied.",
                        "Copy",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                int missing = columns.Count(c => cache.TryGet(c) is null);
                if (missing == 0) break;

                if (!cache.Fits(columns))
                {
                    MessageBox.Show(
                        $"The selection spans {columns.Count:N0} columns, about {FormatBytes(cache.SizeOf(columns))} in memory, "
                        + $"and the cache can hold {FormatBytes(cache.Memory.Limit)} now (half of the free memory).\n\n"
                        + "Select fewer columns to copy.",
                        "Copy",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                _lblInfo.Text = $"Loading {missing:N0} column(s) to copy...";
                UseWaitCursor = true;
                try
                {
                    await cache.EnsureAsync([.. columns, .. ColumnsAroundView().Except(columns)]);
                }
                finally
                {
                    UseWaitCursor = false;
                }
                UpdateInfo();
                if (cache != OnScreen) return;   // another file or script, or the file restored, took over
                // the selection may have changed meanwhile: check it again
            }

            var data = _grid.GetClipboardContent();
            if (data is not null)
                Clipboard.SetDataObject(data);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error copying:\n\n{PolarsErrors.Describe(ex)}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // the columns with a selected cell, in file order
    private List<int> SelectedColumns()
    {
        var columns = new SortedSet<int>();
        foreach (DataGridViewCell cell in _grid.SelectedCells)
            columns.Add(cell.ColumnIndex);
        return [.. columns];
    }

    private async void BtnColumns_Click(object? sender, EventArgs e)
    {
        if (_cache is not { } cache || _result is not null) return;

        var names = cache.Fields.Select(f => f.Name).ToArray();
        using var selector = new ColumnSelectorForm(names, _hiddenColumns);
        if (selector.ShowDialog(this) != DialogResult.OK) return;

        var shown = selector.SelectedColumns.ToHashSet();
        ClearGrid();
        _hiddenColumns.Clear();
        _hiddenColumns.UnionWith(names.Where(n => !shown.Contains(n)));
        if (await ShowFileAsync())
            UpdateInfo();
    }

    // the columns the grid shows, in file order; null when none is hidden (no Select)
    private string[]? VisibleColumnNames() =>
        _cache is null || _hiddenColumns.Count == 0
            ? null
            : _cache.Fields.Select(f => f.Name).Where(n => !_hiddenColumns.Contains(n)).ToArray();

    private async void Terminal_ExecuteRequested(string code, bool applyRowCap)
    {
        if (_parquetPath is null)
        {
            _terminal.AppendError("Open a file before running scripts.");
            return;
        }

        _terminal.AppendCode(code);
        await RunChainAsync(appendStep: code, applyRowCap);
    }

    private async void Terminal_UndoRequested()
    {
        if (_scriptChain.Count == 0) return;

        _scriptChain.RemoveAt(_scriptChain.Count - 1);

        if (_scriptChain.Count == 0)
        {
            _terminal.AppendResult("empty chain — restoring the original file");
            await RestoreFileAsync();
            return;
        }

        await RunChainAsync(appendStep: null, _terminal.LimitEnabled);
    }

    /// <summary>
    /// Replays the script chain (plus a candidate step, if any) over a fresh
    /// scan of the file. The step only joins the chain if the whole run
    /// succeeds — an error leaves the chain untouched.
    /// </summary>
    private async Task RunChainAsync(string? appendStep, bool applyRowCap)
    {
        if (_parquetPath is null) return;

        _scriptCts?.Cancel();
        var cts = _scriptCts = new CancellationTokenSource();

        _terminal.SetBusy(true);
        _lblInfo.Text = "Running script...";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var steps = new List<string>(_scriptChain);
            if (appendStep is not null)
                steps.Add(appendStep);

            // chain start: freeze the grid's visual state as the chain base
            var startingChain = _scriptChain.Count == 0;
            var baseColumns = startingChain ? VisibleColumnNames() : _chainBaseColumns;
            var baseFilters = startingChain
                ? new Dictionary<string, ColumnFilter>(_activeFilters)
                : _chainBaseFilters;

            var plan = await ScriptHost.BuildChainAsync(
                steps, _parquetPath, baseColumns, baseFilters, applyRowCap, cts.Token);
            // counts the rows; the columns are computed as the grid reaches them
            var result = await ColumnCache.FromPlanAsync(plan, _memory, _cache?.RowCount ?? 0);
            if (cts.Token.IsCancellationRequested)
            {
                result.Dispose();
                return;
            }

            if (appendStep is not null)
            {
                _scriptChain.Add(appendStep);
                if (startingChain)
                {
                    _chainBaseColumns = baseColumns;
                    _chainBaseFilters = baseFilters;
                }
            }

            // script mode first: the rows take a while, and a header filter
            // must not land on the result meanwhile
            EnterScriptMode();
            if (!await ShowResultAsync(result))
                return;

            var inherited = startingChain && baseFilters is { Count: > 0 }
                ? $" — inheriting {baseFilters.Count} active filter(s)"
                : "";
            var capped = applyRowCap && result.RowCount == ScriptHost.RowCap
                ? $" — result capped at {ScriptHost.RowCap:N0} rows"
                : "";
            _terminal.AppendResult(
                $"step {_scriptChain.Count}: {result.RowCount:N0} rows × {result.Fields.Count:N0} columns in {sw.ElapsedMilliseconds:N0} ms{inherited}{capped}");
            UpdateInfo();
        }
        catch (OperationCanceledException)
        {
            // stale run, discarded
        }
        catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException ex)
        {
            _terminal.AppendError(ScriptHost.Describe(ex));
            UpdateInfo();
        }
        catch (Exception ex)
        {
            _terminal.AppendError(PolarsErrors.Describe(ex));
            UpdateInfo();
        }
        finally
        {
            _terminal.SetBusy(false);
            _terminal.FocusInput();
        }
    }

    /// <summary>
    /// Grid showing a script result: header filters lose the premise that the
    /// grid mirrors the file, so they stay disabled until "Restore file"
    /// (V1 design decision). Active filters are NOT cleared — they live on as
    /// the chain's frozen base and come back when the file is restored.
    /// </summary>
    private void EnterScriptMode()
    {
        _scriptMode = true;
        _filterCts?.Cancel();
        _btnClearFilters.Enabled = false;
        _btnColumns.Enabled = false;
        _btnRestore.Visible = true;
    }

    private async void BtnRestore_Click(object? sender, EventArgs e)
    {
        _scriptChain.Clear();
        await RestoreFileAsync();
    }

    private async Task RestoreFileAsync()
    {
        if (_cache is null) return;

        try
        {
            // the file's cache stayed alive under the script: what is still
            // in memory shows at once, the rest loads as the grid reaches it
            ClearGrid();
            CloseResult();
            _filteredRows = null;
            _scriptMode = false;
            _btnRestore.Visible = false;
            _btnColumns.Enabled = true;
            _chainBaseColumns = null;
            _chainBaseFilters = null;

            // the header filters that were active before the script come back,
            // and they set the rows: all of them first would be wasted
            bool filtered = _activeFilters.Count > 0;
            if (!await ShowFileAsync(rows: !filtered))
                return;
            UpdateInfo();
            if (filtered)
                await ReapplyFiltersAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error restoring the file:\n\n{PolarsErrors.Describe(ex)}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _lblInfo.Text = "Restore failed";
        }
    }

    private async void Grid_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (_parquetPath is null ||
            e.Button != MouseButtons.Left || e.ColumnIndex < 0)
            return;

        if (_scriptMode)
        {
            _lblInfo.Text = "Header filters are disabled in script mode — use \"Restore file\"";
            return;
        }

        var column = _grid.Columns[e.ColumnIndex].Name;   // Name is always the real column name

        try
        {
            _lblInfo.Text = $"Reading values of \"{column}\"...";

            // Excel: the column's own filter doesn't narrow its list
            var (values, hasBlanks, capped) = await FilterEngine.GetDistinctValuesAsync(
                _parquetPath, column, FilterEngine.BuildPredicate(_activeFilters, except: column));

            using var popup = new FilterPopupForm(
                column, values, hasBlanks, capped,
                _activeFilters.GetValueOrDefault(column));

            // position the popup right under the clicked header
            var headerRect = _grid.GetCellDisplayRectangle(e.ColumnIndex, -1, false);
            var screenPos = _grid.PointToScreen(new Point(headerRect.Left, headerRect.Bottom));
            var screen = Screen.FromControl(this).WorkingArea;
            screenPos.X = Math.Min(screenPos.X, screen.Right - popup.Width);
            screenPos.Y = Math.Min(screenPos.Y, screen.Bottom - popup.Height);
            popup.Location = screenPos;

            if (popup.ShowDialog(this) != DialogResult.OK)
            {
                UpdateInfo();
                return;
            }

            if (popup.ResultFilter is null)
                _activeFilters.Remove(column);
            else
                _activeFilters[column] = popup.ResultFilter;

            await ReapplyFiltersAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error filtering column \"{column}\":\n\n{PolarsErrors.Describe(ex)}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            UpdateInfo();
        }
    }

    private async void BtnClearFilters_Click(object? sender, EventArgs e)
    {
        if (_activeFilters.Count == 0) return;
        _activeFilters.Clear();
        await ReapplyFiltersAsync();
    }

    /// <summary>
    /// Recomputes the visible indices from the active filter set (always
    /// against the original data) and updates the grid via row indirection.
    /// </summary>
    private async Task ReapplyFiltersAsync()
    {
        if (_parquetPath is null || _scriptMode) return;

        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();

        _lblInfo.Text = "Applying filters...";
        try
        {
            var swQuery = System.Diagnostics.Stopwatch.StartNew();
            var rows = await FilterEngine.GetVisibleRowsAsync(
                _parquetPath, FilterEngine.BuildPredicate(_activeFilters), cts.Token);
            swQuery.Stop();
            if (cts.Token.IsCancellationRequested) return;   // another interaction took over

            var swGrid = System.Diagnostics.Stopwatch.StartNew();

            // Shrinking RowCount removes rows one by one (minutes on millions
            // of rows); Rows.Clear() is an O(1) reset and re-adding is bulk.
            // Clear BEFORE swapping the indices: Rows.Clear() first leaves the
            // current cell (CurrentCell = null) and reads its value, so while
            // the old rows exist, _filteredRows must still be the old array.
            // Swapped first, a current cell past the new count read past its
            // end (IndexOutOfRange in a paint: red X, crash dialog).
            _grid.Rows.Clear();
            _filteredRows = rows;
            UpdateHeaderIndicators();
            _btnClearFilters.Enabled = _activeFilters.Count > 0;
            if (!await SetRowCountAsync(rows?.Length ?? _rowCount))
                return;
            swGrid.Stop();

            UpdateInfo();
            _lblInfo.Text += $"  [query {swQuery.ElapsedMilliseconds} ms | grid {swGrid.ElapsedMilliseconds} ms]";
        }
        catch (OperationCanceledException)
        {
            // stale query, discarded
        }
    }

    /// <summary>
    /// Marks the headers of columns with an active filter: the accent color and
    /// a drawn "▼". The header text stays the real name, so Ctrl+C copies it.
    /// </summary>
    private void UpdateHeaderIndicators()
    {
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            bool filtered = _activeFilters.ContainsKey(col.Name);
            _painting.MarkFiltered(col, filtered);
            Theme.MarkFiltered(col, filtered);
        }
    }

private void UpdateInfo()
{
    var total   = _rowCount;
    var showing = _filteredRows?.Length ?? total;
    var columns = _result is not null ? $"{_result.Fields.Count:N0} columns"
        : _cache is null ? "0 columns"
        : _hiddenColumns.Count > 0 ? $"{_cache.Fields.Count - _hiddenColumns.Count:N0} of {_cache.Fields.Count:N0} columns"
        : $"{_cache.Fields.Count:N0} columns";

    _lblInfo.Text = _scriptMode
        ? $"[script, step {_scriptChain.Count}] {total:N0} rows × {columns}"
        : _filteredRows is not null
            ? $"{showing:N0} of {total:N0} rows × {columns} (filtered)"
            : $"{total:N0} rows × {columns}";
    UpdateMemoryInfo();
}

// How much of the table on screen is in memory, and what the caches hold
// against the budget (half of what is free, them included): honest about
// the cost, and the demo
private void UpdateMemoryInfo()
{
    var cache = OnScreen;
    _lblMemory.Visible = cache is not null;
    if (cache is null) return;

    var loading = cache.LoadingCount > 0 ? $" · loading {cache.LoadingCount:N0}" : "";
    _lblMemory.Text = $"{cache.LoadedCount:N0} of {cache.Fields.Count:N0} columns in memory "
        + $"({FormatBytes(_memory.HeldBytes)} of {FormatBytes(_memory.Limit)}){loading}";
}

private static string FormatBytes(long bytes) =>
    bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20:N0} MB";

}
