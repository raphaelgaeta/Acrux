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
namespace PolarsGridViewer;

public partial class MainForm : Form
{
    private readonly DataGridView _grid;
    private readonly Button _btnLoad;
    private readonly Button _btnClearFilters;
    private readonly Button _btnTerminal;
    private readonly Button _btnRestore;
    private readonly FormsLabel _lblInfo;
    private readonly SplitContainer _split;
    private readonly ScriptTerminalPanel _terminal;

    private string? _parquetPath;
    private string? _tempParquetPath;   // parquet converted from CSV (delete on switch/close)
    private string[] _selectedColumns = [];
    private bool _scriptMode;
    private readonly List<string> _scriptChain = new();
    private readonly Dictionary<string, ColumnFilter> _activeFilters = new();
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _scriptCts;



public MainForm()
{
    Text = "Parquet Grid Viewer";
    Width = 1280;
    Height = 760;
    StartPosition = FormStartPosition.CenterScreen;

    var topPanel = new Panel
    {
        Dock = DockStyle.Top,
        Height = 64,
        Padding = new Padding(10)
    };

    _btnLoad = new Button
    {
        Text = "Open file",
        AutoSize = true,
        Left = 10,
        Top = 15
    };
    _btnLoad.Click += BtnLoad_Click;

    _btnClearFilters = new Button
    {
        Text = "Clear all filters",
        AutoSize = true,
        Left = 165,
        Top = 15,
        Enabled = false
    };
    _btnClearFilters.Click += BtnClearFilters_Click;

    _btnTerminal = new Button
    {
        Text = "C# terminal",
        AutoSize = true,
        Left = 340,
        Top = 15,
        Enabled = false
    };
    _btnTerminal.Click += (_, _) => ToggleTerminal();

    _btnRestore = new Button
    {
        Text = "Restore file",
        AutoSize = true,
        Left = 460,
        Top = 15,
        Visible = false
    };
    _btnRestore.Click += BtnRestore_Click;

    _lblInfo = new FormsLabel
    {
        AutoSize = true,
        Left = 620,
        Top = 20,
        Text = "No data loaded"
    };

    topPanel.Controls.Add(_btnLoad);
    topPanel.Controls.Add(_btnClearFilters);
    topPanel.Controls.Add(_btnTerminal);
    topPanel.Controls.Add(_btnRestore);
    topPanel.Controls.Add(_lblInfo);

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
        BackgroundColor = Color.White,
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

    // supplies values only for visible cells
    _grid.CellValueNeeded += Grid_CellValueNeeded;

    // header click opens the Excel-style column filter
    _grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;

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

private RecordBatch? _batch;
private void Grid_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
{
    if (_batch is null) return;

    // reads the cell straight from the Arrow buffer (O(1), no column materialization)
    int dataRow = _filteredRows is null ? e.RowIndex : _filteredRows[e.RowIndex];
    e.Value = PolarsTableAdapter.GetCellValue(_batch.Column(e.ColumnIndex), dataRow);
}
private async void BtnLoad_Click(object? sender, EventArgs e)
{
    using var dialog = new OpenFileDialog
    {
        Title = "Select a Parquet or CSV file",
        Filter = DataFrameProvider.OpenDialogFilter
    };

    if (dialog.ShowDialog(this) != DialogResult.OK)
        return;

    var isCsv = dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
    string sourcePath = dialog.FileName;
    bool sourceIsTemp = false;
    try
    {
        _btnLoad.Enabled = false;

        // CSV: one-time conversion to a temp parquet; the app operates on parquet
        _lblInfo.Text = isCsv ? "Converting CSV to parquet..." : "Reading schema...";
        var progress = new Progress<string>(msg => _lblInfo.Text = msg);
        (sourcePath, sourceIsTemp) = await DataFrameProvider.EnsureParquetAsync(dialog.FileName, progress);

        // metadata only: fast even on huge files
        var columnNames = await DataFrameProvider.GetColumnNamesAsync(sourcePath);

        string[] selectedColumns;
        using (var selector = new ColumnSelectorForm(columnNames))
        {
            if (selector.ShowDialog(this) != DialogResult.OK)
            {
                if (sourceIsTemp) TryDeleteTemp(sourcePath);
                _lblInfo.Text = "Load canceled";
                return;
            }
            selectedColumns = selector.SelectedColumns;
        }

        _lblInfo.Text = "Loading...";

        // Collects only the chosen columns (projection pushdown) and converts
        // to Arrow off the UI thread. The Polars DataFrame is discarded right
        // after the conversion so we never hold two copies.
        var df_arrow = await Task.Run(async () =>
        {
            using DataFrame df_ = await DataFrameProvider.GetDataFrameAsync(sourcePath, selectedColumns);
            return df_.ToArrow();
        });

        // new file: previous filters and script mode no longer apply
        _filterCts?.Cancel();
        _scriptCts?.Cancel();
        _activeFilters.Clear();
        _scriptChain.Clear();

        // success: the previous temp (if any) can go
        if (_tempParquetPath is not null)
            TryDeleteTemp(_tempParquetPath);
        _tempParquetPath = sourceIsTemp ? sourcePath : null;

        _parquetPath = sourcePath;
        _selectedColumns = selectedColumns;
        _btnClearFilters.Enabled = false;
        _scriptMode = false;
        _btnRestore.Visible = false;
        _btnTerminal.Enabled = true;

        DisplayBatch(df_arrow);
        UpdateInfo();
    }
    catch (Exception ex)
    {
        // failure: don't orphan the freshly converted temp (unless it became the active one)
        if (sourceIsTemp && !ReferenceEquals(sourcePath, _tempParquetPath) && sourcePath != _tempParquetPath)
            TryDeleteTemp(sourcePath);
        MessageBox.Show(
            $"Error loading DataFrame:\n\n{ex.Message}",
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
    if (_tempParquetPath is not null)
        TryDeleteTemp(_tempParquetPath);
    base.OnFormClosed(e);
}

    /// <summary>
    /// Swaps the grid content for the given batch (ownership transfers to the
    /// form). Single display path: file load, restore and scripts.
    /// </summary>
    private void DisplayBatch(RecordBatch batch)
    {
        // Empty the grid before disposing the previous batch
        // (Rows.Clear is O(1); RowCount = 0 would remove row by row)
        _grid.Rows.Clear();
        _grid.Columns.Clear();
        _batch?.Dispose();

        _batch = batch;
        _rowCount = batch.Length;
        _filteredRows = null;

        _grid.SuspendLayout();

        var gridColumns = batch.Schema.FieldsList
            .Select(field => (DataGridViewColumn)new DataGridViewTextBoxColumn
            {
                Name       = field.Name,
                HeaderText = field.Name,
                Width      = 120,
                SortMode   = DataGridViewColumnSortMode.Programmatic,
                FillWeight = 1   // default is 100 and the sum caps at 65535
            })
            .ToArray();

        _grid.Columns.AddRange(gridColumns);

        if (_rowCount > 0)
            _grid.RowCount = _rowCount;   // registers the total; no cells are populated
        _grid.ResumeLayout();
    }

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

            var batch = await ScriptHost.RunChainAsync(steps, _parquetPath, applyRowCap, cts.Token);
            if (cts.Token.IsCancellationRequested)
            {
                batch.Dispose();
                return;
            }

            if (appendStep is not null)
                _scriptChain.Add(appendStep);

            DisplayBatch(batch);
            EnterScriptMode();

            var capped = applyRowCap && batch.Length == ScriptHost.RowCap
                ? $" — result capped at {ScriptHost.RowCap:N0} rows"
                : "";
            _terminal.AppendResult(
                $"step {_scriptChain.Count}: {batch.Length:N0} rows × {batch.ColumnCount} columns in {sw.ElapsedMilliseconds:N0} ms{capped}");
            UpdateInfo();
        }
        catch (OperationCanceledException)
        {
            // stale run, discarded
        }
        catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException ex)
        {
            _terminal.AppendError(string.Join(Environment.NewLine, ex.Diagnostics));
            UpdateInfo();
        }
        catch (Exception ex)
        {
            _terminal.AppendError(ex.Message);
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
    /// (V1 design decision).
    /// </summary>
    private void EnterScriptMode()
    {
        _scriptMode = true;
        _filterCts?.Cancel();
        _activeFilters.Clear();
        _btnClearFilters.Enabled = false;
        _btnRestore.Visible = true;
    }

    private async void BtnRestore_Click(object? sender, EventArgs e)
    {
        _scriptChain.Clear();
        await RestoreFileAsync();
    }

    private async Task RestoreFileAsync()
    {
        if (_parquetPath is null) return;

        try
        {
            _btnRestore.Enabled = false;
            _lblInfo.Text = "Reloading file...";

            var path = _parquetPath;
            var columns = _selectedColumns;
            var batch = await Task.Run(async () =>
            {
                using var df = await DataFrameProvider.GetDataFrameAsync(path, columns);
                return df.ToArrow();
            });

            DisplayBatch(batch);
            _scriptMode = false;
            _btnRestore.Visible = false;
            UpdateInfo();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error restoring the file:\n\n{ex.Message}",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _lblInfo.Text = "Restore failed";
        }
        finally
        {
            _btnRestore.Enabled = true;
        }
    }

    private async void Grid_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (_batch is null || _parquetPath is null ||
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

            var (values, hasBlanks, capped) = await FilterEngine.GetDistinctValuesAsync(
                _parquetPath, column, _activeFilters);

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
                $"Error filtering column \"{column}\":\n\n{ex.Message}",
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
            var rows = await FilterEngine.GetVisibleRowsAsync(_parquetPath, _activeFilters, cts.Token);
            swQuery.Stop();
            if (cts.Token.IsCancellationRequested) return;   // another interaction took over

            var swGrid = System.Diagnostics.Stopwatch.StartNew();
            _filteredRows = rows;

            // Shrinking RowCount removes rows one by one (minutes on millions
            // of rows); Rows.Clear() is an O(1) reset and re-adding is bulk.
            _grid.Rows.Clear();
            int newCount = rows?.Length ?? _rowCount;
            if (newCount > 0)
                _grid.RowCount = newCount;
            swGrid.Stop();

            UpdateHeaderIndicators();
            _btnClearFilters.Enabled = _activeFilters.Count > 0;
            UpdateInfo();
            _lblInfo.Text += $"  [query {swQuery.ElapsedMilliseconds} ms | grid {swGrid.ElapsedMilliseconds} ms]";
        }
        catch (OperationCanceledException)
        {
            // stale query, discarded
        }
    }

    /// <summary>▼ suffix on the headers of columns with an active filter.</summary>
    private void UpdateHeaderIndicators()
    {
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            var wanted = _activeFilters.ContainsKey(col.Name) ? col.Name + " ▼" : col.Name;
            if (col.HeaderText != wanted)
                col.HeaderText = wanted;
        }
    }

private void UpdateInfo()
{
    var total   = _rowCount;
    var showing = _filteredRows?.Length ?? total;
    var columns = _batch?.ColumnCount ?? 0;

    _lblInfo.Text = _scriptMode
        ? $"[script, step {_scriptChain.Count}] {total:N0} rows × {columns} columns"
        : _filteredRows is not null
            ? $"{showing:N0} of {total:N0} rows × {columns} columns (filtered)"
            : $"{total:N0} rows × {columns} columns";
}

}
