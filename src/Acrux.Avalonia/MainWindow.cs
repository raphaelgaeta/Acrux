using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

namespace Acrux;

/// <summary>The app's window: the bar on top, the file's grid below.</summary>
internal sealed class MainWindow : Window
{
    // DataFrameProvider.OpenDialogFilter's extensions, and the installer's
    // "Open with" (installer/acrux.iss): change one list, change them all
    private static readonly string[] Extensions = ["*.parquet", "*.csv", "*.xlsx", "*.db", "*.sqlite", "*.sqlite3"];

    private readonly MemoryBudget _memory = new();
    private readonly Button _open = new() { Content = "Open file", Margin = new Thickness(6, 4, 0, 4) };
    private readonly Button _columns = new() { Content = "Columns", IsEnabled = false, Margin = new Thickness(6, 4, 0, 4) };
    private readonly Button _clearFilters = new() { Content = "Clear all filters", IsEnabled = false, Margin = new Thickness(6, 4, 0, 4) };
    private readonly TextBlock _info = new() { Text = "No data loaded", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _memoryInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Opacity = 0.7 };
    private readonly ContentControl _gridHost = new();
    private Session? _session;
    private TableGrid? _grid;

    public MainWindow(string? file)
    {
        Title = "Acrux";
        Width = 1280;
        Height = 800;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Acrux.Avalonia/Assets/acrux.ico")));

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Children = { _open, _columns, _clearFilters, _info, _memoryInfo } };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_gridHost);
        Content = root;

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
            _session?.Dispose();   // frees the columns and deletes a converted copy
            _session = null;
        };
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
            _gridHost.Content = grid;
            _grid = grid;
            _columns.IsEnabled = true;
            _clearFilters.IsEnabled = false;
            old?.Dispose();   // the old grid is off screen: its columns and its copy can go

            Title = $"{Path.GetFileName(path)} — Acrux";
            _info.Text = Info(session);
            UpdateMemory(session);
            grid.FocusGrid();
        }
        catch (Exception ex)
        {
            _info.Text = $"Error opening {Path.GetFileName(path)}: {PolarsErrors.Brief(ex)}";
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
    /// A header click: the column's values (its own filter left out, as in
    /// Excel), the popup under the header, then the rows again.
    /// </summary>
    private async Task HeaderFilterAsync(int column, Rect header)
    {
        if (_session is not { } session || _grid is not { } grid) return;
        string name = session.File.Fields[column].Name;
        try
        {
            _info.Text = $"Reading values of \"{name}\"...";
            var (values, hasBlanks, capped) = await session.ValuesAsync(name);
            if (session != _session) return;   // another file came in meanwhile
            var popup = new FilterPopup(name, values, hasBlanks, capped, session.Filters.GetValueOrDefault(name))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = grid.PointToScreen(header.BottomLeft),
            };
            _info.Text = Info(session);
            if (!await popup.ShowDialog<bool>(this)) return;
            session.SetFilter(name, popup.Result);
            await ApplyFiltersAsync();
        }
        catch (Exception ex)
        {
            _info.Text = $"Error filtering \"{name}\": {PolarsErrors.Brief(ex)}";
        }
        finally
        {
            grid.FocusGrid();
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
        grid.FilteredColumns.Clear();
        for (int c = 0; c < session.File.Fields.Count; c++)
            if (session.Filters.ContainsKey(session.File.Fields[c].Name))
                grid.FilteredColumns.Add(c);
        _clearFilters.IsEnabled = session.Filters.Count > 0;
        _info.Text = $"{Info(session)}  [query {query.ElapsedMilliseconds:N0} ms | grid {swap.ElapsedMilliseconds:N0} ms]";
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

    /// <summary>How much of the file is in memory, against the cap: honest about the cost, as the WinForms app.</summary>
    private void UpdateMemory(Session session)
    {
        if (session != _session) return;
        var file = session.File;
        string loading = file.LoadingCount > 0 ? $" · loading {file.LoadingCount:N0}" : "";
        _memoryInfo.Text = $"{file.LoadedCount:N0} of {file.Fields.Count:N0} columns in memory "
            + $"({FormatBytes(_memory.HeldBytes)} of {FormatBytes(_memory.Limit)}){loading}";
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20:N0} MB";
}
