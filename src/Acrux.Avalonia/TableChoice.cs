using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform;

namespace Acrux;

/// <summary>
/// Which sheet of an xlsx, or which table of an SQLite file, to open, when
/// there is more than one: the WinForms app's SheetSelectorForm. Two clicks
/// or Enter open the chosen one; Esc cancels.
/// </summary>
internal sealed class TableChoice : Window
{
    private readonly ListBox _list = new();

    public TableChoice(string[] names, string title)
    {
        Title = title;
        Width = 340;
        Height = 380;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Acrux/Assets/acrux.ico")));

        _list.ItemsSource = names;
        _list.SelectedIndex = 0;
        _list.DoubleTapped += (_, _) => Close(_list.SelectedIndex);
        var open = new Button { Content = "Open", IsDefault = true };
        open.Click += (_, _) => Close(_list.SelectedIndex);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(-1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { cancel, open },
        };
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
        Opened += (_, _) => _list.Focus();
    }

    /// <summary>The chosen index, or -1 when canceled (the window closed by any means).</summary>
    public async Task<int> ChooseAsync(Window owner) => await ShowDialog<int?>(owner) ?? -1;
}
