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
    private readonly Button _open = new() { Content = "Open file", Margin = new Thickness(6, 4) };
    private readonly TextBlock _info = new() { Text = "No data loaded", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _memoryInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Opacity = 0.7 };
    private readonly ContentControl _gridHost = new();
    private Session? _session;

    public MainWindow(string? file)
    {
        Title = "Acrux";
        Width = 1280;
        Height = 800;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Acrux.Avalonia/Assets/acrux.ico")));

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Children = { _open, _info, _memoryInfo } };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_gridHost);
        Content = root;

        _open.Click += async (_, _) => await ChooseFileAsync();
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
    /// Opens a file, whatever its format (the content decides). An xlsx or an
    /// SQLite file opens its first sheet or table for now; the choice comes
    /// in the next phase.
    /// </summary>
    private async Task LoadAsync(string path)
    {
        _open.IsEnabled = false;
        try
        {
            _info.Text = "Opening...";
            var session = await Session.OpenAsync(path, _memory, progress: new Progress<string>(message => _info.Text = message));
            var old = _session;
            _session = session;
            var grid = new TableGrid(session.File);
            grid.Status += message => _info.Text = $"{Info(session)}   ·   {message}";
            session.File.ColumnLoaded += (_, _, _) => UpdateMemory(session);
            _gridHost.Content = grid;
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

    private static string Info(Session session) =>
        $"{session.File.RowCount:N0} rows × {session.File.Fields.Count:N0} columns";

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
