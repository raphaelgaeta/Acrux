using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Acrux;

/// <summary>
/// The app's window: the bar on top, the grid below (the file's, or a
/// script's result), and the C# terminal under it when open.
/// </summary>
internal sealed class MainWindow : Window
{
    // DataFrameProvider.OpenDialogFilter's extensions, and the installer's
    // "Open with" (installer/acrux.iss): change one list, change them all
    private static readonly string[] Extensions = ["*.parquet", "*.csv", "*.xlsx", "*.db", "*.sqlite", "*.sqlite3"];

    private readonly MemoryBudget _memory = new();
    private readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Button _open = new() { Content = "Open file", Margin = new Thickness(6, 4, 0, 4) };
    private readonly Button _columns = new() { Content = "Columns", IsEnabled = false, Margin = new Thickness(6, 4, 0, 4) };
    private readonly Button _clearFilters = new() { Content = "Clear all filters", IsEnabled = false, Margin = new Thickness(6, 4, 0, 4) };
    private readonly TextBlock _info = new() { Text = "No data loaded", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _memoryInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Opacity = 0.7 };
    private readonly Button _terminalButton = new() { Content = "C# terminal", IsEnabled = false, Margin = new Thickness(6, 4, 0, 4) };
    private readonly Button _restore = new() { Content = "Restore file", IsVisible = false, Margin = new Thickness(6, 4, 0, 4) };
    private readonly ContentControl _gridHost = new();
    private readonly TerminalPanel _terminal = new();
    private readonly GridSplitter _splitter = new() { ResizeDirection = GridResizeDirection.Rows, Height = 5, IsVisible = false };
    private readonly Grid _body = new() { RowDefinitions = new RowDefinitions("*,Auto,0") };
    // The header's filter drops down under the header. A click elsewhere closes it, and
    // goes on: a click on another header's button closes one dropdown and opens the next.
    private readonly Popup _filterPopup = new()
    {
        IsLightDismissEnabled = true,
        OverlayDismissEventPassThrough = true,
        Placement = PlacementMode.BottomEdgeAlignedLeft,
        WindowManagerAddShadowHint = true,
    };
    private (int Column, long At) _filterDismissed = (-1, 0);
    private Session? _session;
    private TableGrid? _grid;        // the file's: kept under a script's result, scroll and filters included
    private TableGrid? _resultGrid;  // a script's result, while one is shown

    public MainWindow(string? file)
    {
        Title = "Acrux";
        Width = 1280;
        Height = 800;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Acrux.Avalonia/Assets/acrux.ico")));

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _open, _columns, _clearFilters, _terminalButton, _restore, _info, _memoryInfo },
        };
        Grid.SetRow(_splitter, 1);
        Grid.SetRow(_terminal, 2);
        _body.Children.Add(_gridHost);
        _body.Children.Add(_splitter);
        _body.Children.Add(_terminal);
        _body.Children.Add(_filterPopup);   // in the tree, so the dropdown takes the window's light or dark
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_body);
        Content = root;

        _terminalButton.Click += (_, _) => ToggleTerminal();
        _terminal.ExecuteRequested += async (code, rowCap) => await RunChainAsync(code, rowCap);
        _terminal.UndoRequested += async () => await UndoStepAsync();
        _restore.Click += (_, _) => RestoreFile();

        _open.Click += async (_, _) => await ChooseFileAsync();
        _columns.Click += async (_, _) => await ChooseColumnsAsync();
        _clearFilters.Click += async (_, _) =>
        {
            _session?.ClearFilters();
            await ApplyFiltersAsync();
        };
        Opened += async (_, _) =>
        {
            if (file is not null) await LoadAsync(file);
        };
        Closed += (_, _) =>
        {
            _trimTimer.Stop();
            _session?.Dispose();   // frees the columns and deletes a converted copy
            _session = null;
        };

        // Every 5 s, as the WinForms app: the cap follows the free memory, so
        // when another program takes memory the cache lets go of the columns
        // off screen; then, on Linux, the C heap gives back what was freed
        // (NativeHeap: ~0.8 GB less at rest on the 700-column file).
        _trimTimer.Tick += (_, _) =>
        {
            _memory.Trim();
            NativeHeap.Trim();
            if (_session is { } session) UpdateMemory(session);
        };
        _trimTimer.Start();

        // a file dropped on the window opens, as from "Open file"
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = _open.IsEnabled && e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (_open.IsEnabled && e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath() is { } path)
                await LoadAsync(path);
        });
    }

    private async Task ChooseFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a Parquet, CSV, Excel or SQLite file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Data files") { Patterns = Extensions }, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await LoadAsync(path);
    }

    /// <summary>
    /// Opens a file, whatever its format: the content decides, not the
    /// extension. An xlsx with several sheets or an SQLite file with several
    /// tables asks which one first; the conversion to parquet comes after.
    /// </summary>
    private async Task LoadAsync(string path)
    {
        _open.IsEnabled = false;
        try
        {
            _info.Text = "Opening...";
            var format = await Task.Run(() => DataFrameProvider.DetectFormat(path));
            int table = 0;
            if (format is SourceFormat.Excel or SourceFormat.Sqlite)
            {
                var names = await Task.Run(() => DataFrameProvider.GetTableNames(path));
                if (names.Length > 1)
                {
                    table = await new TableChoice(names, format == SourceFormat.Excel ? "Select sheet" : "Select table").ChooseAsync(this);
                    if (table < 0)
                    {
                        _info.Text = "Load canceled";
                        return;
                    }
                }
            }
            _info.Text = format switch
            {
                SourceFormat.Csv => "Converting CSV to parquet...",
                SourceFormat.Excel => "Converting Excel to parquet...",
                SourceFormat.Sqlite => "Converting SQLite table to parquet...",
                _ => "Reading schema...",
            };
            var session = await Session.OpenAsync(path, _memory, table, new Progress<string>(message => _info.Text = message));
            var old = _session;
            _session = session;
            var grid = new TableGrid(session.File);
            grid.Status += message => _info.Text = $"{Info(session)}   ·   {message}";
            grid.HeaderClicked += async (column, header) => await HeaderFilterAsync(column, header);
            session.File.ColumnLoaded += (_, _, _) => UpdateMemory(session);
            _filterPopup.IsOpen = false;   // a dropped file arrives without a click to close it
            _gridHost.Content = grid;
            _grid = grid;
            _resultGrid = null;
            _columns.IsEnabled = true;
            _clearFilters.IsEnabled = false;
            _terminalButton.IsEnabled = true;
            _restore.IsVisible = false;
            old?.Dispose();   // the old grids are off screen: their columns, a script's result and a copy can go

            Title = $"{Path.GetFileName(path)} — Acrux";
            _info.Text = Info(session);
            UpdateMemory(session);
            grid.FocusGrid();
        }
        catch (Exception ex)
        {
            _info.Text = _session is { } shown ? Info(shown) : "No data loaded";
            await ErrorDialog.ShowAsync(this, "Error", $"Error opening {Path.GetFileName(path)}:\n\n{PolarsErrors.Describe(ex)}");
        }
        finally
        {
            _open.IsEnabled = true;
        }
    }

    private static string Info(Session session)
    {
        var file = session.File;
        int total = file.Fields.Count, shown = total - session.HiddenColumns.Count;
        string columns = shown == total ? $"{total:N0} columns" : $"{shown:N0} of {total:N0} columns";
        return session.Rows is null
            ? $"{file.RowCount:N0} rows × {columns}"
            : $"{session.RowCount:N0} of {file.RowCount:N0} rows × {columns} (filtered)";
    }

    /// <summary>
    /// A header click: the dropdown under the header at once, then the
    /// column's values (its own filter left out, as in Excel) when they come,
    /// then the rows again on OK.
    /// </summary>
    private async Task HeaderFilterAsync(int column, Rect header)
    {
        if (_session is not { } session || _grid is not { } grid) return;
        // the click that closed this column's dropdown reaches its header too: it was meant to close it
        if (_filterDismissed.Column == column && Environment.TickCount64 - _filterDismissed.At < 500) return;
        string name = session.File.Fields[column].Name;
        var dropdown = new FilterDropdown(name, session.Filters.GetValueOrDefault(name)) { MinWidth = Math.Max(300, header.Width) };
        using var reading = new CancellationTokenSource();
        void Closed(object? sender, EventArgs e)
        {
            if (_filterPopup.Child != dropdown) return;
            if (!dropdown.Done.IsCompleted) _filterDismissed = (column, Environment.TickCount64);
            dropdown.Cancel();
        }
        _filterPopup.Closed += Closed;
        _filterPopup.PlacementTarget = grid;
        _filterPopup.PlacementRect = header;
        _filterPopup.Child = dropdown;
        _filterPopup.IsOpen = true;
        grid.OpenFilterColumn = column;
        Dispatcher.UIThread.Post(dropdown.FocusSearch, DispatcherPriority.Loaded);   // once the dropdown is on screen
        try
        {
            var values = session.ValuesAsync(name, reading.Token);
            if (await Task.WhenAny(values, dropdown.Done) != values)
            {
                reading.Cancel();   // closed before the values came: they are no one's now
                return;
            }
            var (list, hasBlanks, capped) = await values;
            if (session != _session) return;   // another file came in meanwhile
            dropdown.SetValues(list, hasBlanks, capped);
            if (!await dropdown.Done || session != _session) return;
            _filterPopup.IsOpen = false;
            session.SetFilter(name, dropdown.Result);
            await ApplyFiltersAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_filterPopup.Child == dropdown) _filterPopup.IsOpen = false;
            _info.Text = Info(session);
            await ErrorDialog.ShowAsync(this, "Error", $"Error filtering column \"{name}\":\n\n{PolarsErrors.Describe(ex)}");
        }
        finally
        {
            _filterPopup.Closed -= Closed;
            // a click on another header may have opened its dropdown already: that one stays
            if (_filterPopup.Child == dropdown)
            {
                _filterPopup.IsOpen = false;
                _filterPopup.Child = null;
                grid.OpenFilterColumn = -1;
                grid.FocusGrid();
            }
        }
    }

    /// <summary>The rows again, from the file and every filter; the grid only swaps its list of rows.</summary>
    private async Task ApplyFiltersAsync()
    {
        if (_session is not { } session || _grid is not { } grid) return;
        _info.Text = "Applying filters...";
        var query = Stopwatch.StartNew();
        if (!await session.ApplyFiltersAsync() || session != _session) return;   // a newer filter or file took over
        query.Stop();
        var swap = Stopwatch.StartNew();
        grid.SetRows(session.Rows);
        grid.SetFiltered(Enumerable.Range(0, session.File.Fields.Count)
            .Where(c => session.Filters.ContainsKey(session.File.Fields[c].Name)));
        _clearFilters.IsEnabled = session.Filters.Count > 0;
        _info.Text = $"{Info(session)}  [query {query.ElapsedMilliseconds:N0} ms | grid {swap.ElapsedMilliseconds:N0} ms]";
        grid.Announce(session.Filters.Count > 0
            ? $"Filtered: {grid.RowCount:N0} of {session.File.RowCount:N0} rows"
            : $"No filter: all {grid.RowCount:N0} rows");
    }

    /// <summary>"Columns": which columns the grid shows. The filters stay, even on a hidden column.</summary>
    private async Task ChooseColumnsAsync()
    {
        if (_session is not { } session || _grid is not { } grid) return;
        var names = session.File.Fields.Select(f => f.Name).ToArray();
        var shown = await new ColumnChoice(names, session.HiddenColumns).ChooseAsync(this);
        if (shown is null || session != _session) return;
        var keep = shown.ToHashSet();
        session.HiddenColumns.Clear();
        session.HiddenColumns.UnionWith(names.Where(n => !keep.Contains(n)));
        grid.SetHidden(Enumerable.Range(0, names.Length).Where(c => !keep.Contains(names[c])).ToHashSet());
        _info.Text = Info(session);
        grid.FocusGrid();
    }

    private void ToggleTerminal()
    {
        bool open = !_splitter.IsVisible;
        _splitter.IsVisible = open;
        _body.RowDefinitions[2].Height = open ? new GridLength(300) : new GridLength(0);
        if (open) _terminal.FocusInput();
        else (_resultGrid ?? _grid)?.FocusGrid();
    }

    private static string ScriptInfo(Session session, ColumnCache result) =>
        $"[script, step {session.Chain.Count}] {result.RowCount:N0} rows × {result.Fields.Count:N0} columns";

    /// <summary>
    /// The terminal's chain again, from the file, with <paramref name="step"/>
    /// at the end (the replay, in Session.RunAsync); the result shows in a
    /// grid of its own, the file's waiting underneath.
    /// </summary>
    private async Task RunChainAsync(string? step, bool rowCap)
    {
        if (_session is not { } session)
        {
            _terminal.AppendError("Open a file before running scripts.");
            return;
        }
        _terminal.SetBusy(true);
        if (step is not null) _terminal.AppendCode(step);
        _info.Text = "Running script...";
        var clock = Stopwatch.StartNew();
        try
        {
            bool starting = session.Chain.Count == 0;
            var result = await session.RunAsync(step, rowCap);
            if (result is null || session != _session) return;   // a newer run or another file took over
            if (step is not null) _terminal.ClearInput();
            ShowResult(session, result);
            string inherited = starting && session.InheritedFilters > 0
                ? $" — inheriting {session.InheritedFilters} active filter(s)" : "";
            string capped = rowCap && result.RowCount == ScriptHost.RowCap
                ? $" — result capped at {ScriptHost.RowCap:N0} rows" : "";
            _terminal.AppendResult($"step {session.Chain.Count}: {result.RowCount:N0} rows × {result.Fields.Count:N0} columns "
                + $"in {clock.ElapsedMilliseconds:N0} ms{inherited}{capped}");
        }
        catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException ex)
        {
            _terminal.AppendError(ScriptHost.Describe(ex));
            _info.Text = session.Result is { } shown ? ScriptInfo(session, shown) : Info(session);
        }
        catch (Exception ex)
        {
            _terminal.AppendError(PolarsErrors.Describe(ex));
            _info.Text = session.Result is { } shown ? ScriptInfo(session, shown) : Info(session);
        }
        finally
        {
            _terminal.SetBusy(false);
            _terminal.FocusInput();
        }
    }

    /// <summary>
    /// A script's result on screen. The header filters are off until "Restore
    /// file", as in the WinForms app: the grid no longer mirrors the file.
    /// </summary>
    private void ShowResult(Session session, ColumnCache result)
    {
        _filterPopup.IsOpen = false;
        var grid = new TableGrid(result, filterButtons: false);   // header filters are off in script mode
        grid.Status += message => _info.Text = $"{ScriptInfo(session, result)}   ·   {message}";
        grid.HeaderClicked += (_, _) => _info.Text = "Header filters are disabled in script mode — use \"Restore file\"";
        result.ColumnLoaded += (_, _, _) => UpdateMemory(session);
        _gridHost.Content = grid;
        _resultGrid = grid;
        _restore.IsVisible = true;
        _columns.IsEnabled = false;
        _clearFilters.IsEnabled = false;
        _info.Text = ScriptInfo(session, result);
        UpdateMemory(session);
    }

    private async Task UndoStepAsync()
    {
        if (_session is not { } session || session.Chain.Count == 0) return;
        session.UndoStep();
        _terminal.AppendResult($"undo: back to step {session.Chain.Count}");
        if (session.Chain.Count == 0)
            RestoreFile();
        else
            await RunChainAsync(null, _terminal.Limit);
    }

    /// <summary>Back to the file, as it was: its grid kept its scroll, filters and hidden columns.</summary>
    private void RestoreFile()
    {
        if (_session is not { } session || _grid is not { } grid) return;
        session.Restore();
        _gridHost.Content = grid;
        _resultGrid = null;
        _restore.IsVisible = false;
        _columns.IsEnabled = true;
        _clearFilters.IsEnabled = session.Filters.Count > 0;
        _ = grid.EnsureVisibleAsync();   // the columns that yielded memory to the result come back
        _info.Text = Info(session);
        UpdateMemory(session);
        grid.FocusGrid();
    }

    /// <summary>How much of the file is in memory, against the cap: honest about the cost, as the WinForms app.</summary>
    private void UpdateMemory(Session session)
    {
        if (session != _session) return;
        var file = session.Result ?? session.File;   // what the screen shows
        string loading = file.LoadingCount > 0 ? $" · loading {file.LoadingCount:N0}" : "";
        _memoryInfo.Text = $"{file.LoadedCount:N0} of {file.Fields.Count:N0} columns in memory "
            + $"({FormatBytes(_memory.HeldBytes)} of {FormatBytes(_memory.Limit)}){loading}";
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20:N0} MB";
}
