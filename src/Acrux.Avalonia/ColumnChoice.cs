using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform;

namespace Acrux;

/// <summary>
/// The "Columns" dialog, as the WinForms app's ColumnSelectorForm: which of
/// the file's columns the grid shows. A hidden column is never read; a shown
/// one loads when it reaches the screen. The search narrows the list, and
/// "Check all"/"Uncheck all" act on what it shows. The list virtualizes, so
/// 700 columns cost a screenful.
/// </summary>
internal sealed class ColumnChoice : Window
{
    private readonly Item[] _all;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search columns..." };
    private readonly TextBlock _count = new();
    private readonly Button _show = new() { Content = "Show", IsDefault = true };

    public ColumnChoice(IReadOnlyList<string> columns, IReadOnlySet<string> hidden)
    {
        Title = "Columns";
        Width = 420;
        Height = 560;
        MinWidth = 320;
        MinHeight = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Acrux.Avalonia/Assets/acrux.ico")));
        _all = [.. columns.Select(c => new Item(c, !hidden.Contains(c)))];
        foreach (var item in _all)
            item.PropertyChanged += (_, _) => UpdateCount();

        _list.ItemTemplate = new FuncDataTemplate<Item>((_, _) =>
        {
            // the text in a TextBlock, not as the content itself: a string content is
            // read for an access key, and its first underscore vanished (ITEM_000003
            // showed as ITEM000003, c006_str12k as c006str12k)
            var text = new TextBlock();
            text.Bind(TextBlock.TextProperty, new ReflectionBinding(nameof(Item.Name)));
            var box = new CheckBox { Content = text };
            box.Bind(ToggleButton.IsCheckedProperty, new ReflectionBinding(nameof(Item.IsChecked)) { Mode = BindingMode.TwoWay });
            return box;
        });
        _search.TextChanged += (_, _) => Rebuild();
        var checkAll = new Button { Content = "Check all" };
        checkAll.Click += (_, _) => SetVisible(true);
        var uncheckAll = new Button { Content = "Uncheck all" };
        uncheckAll.Click += (_, _) => SetVisible(false);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        _show.Click += (_, _) => Close(_all.Where(i => i.IsChecked).Select(i => i.Name).ToArray());

        var top = new StackPanel
        {
            Spacing = 6,
            Children = { _search, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { checkAll, uncheckAll } } },
        };
        var bottom = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                _count,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, _show },
                },
            },
        };
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;
        Rebuild();
        Opened += (_, _) => _search.Focus();
    }

    /// <summary>The columns to show, in the file's order; null when canceled.</summary>
    public Task<string[]?> ChooseAsync(Window owner) => ShowDialog<string[]?>(owner);

    private void Rebuild()
    {
        string search = _search.Text?.Trim() ?? "";
        _list.ItemsSource = search.Length == 0
            ? _all
            : _all.Where(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        UpdateCount();
    }

    private void SetVisible(bool value)
    {
        foreach (var item in (IEnumerable<Item>)_list.ItemsSource!)
            item.IsChecked = value;
    }

    private void UpdateCount()
    {
        int shown = _all.Count(i => i.IsChecked);
        _count.Text = $"{shown:N0} of {_all.Length:N0} columns shown";
        _show.IsEnabled = shown > 0;   // a grid with no column has nothing to show
    }

    private sealed class Item(string name, bool isChecked) : INotifyPropertyChanged
    {
        public string Name { get; } = name;

        public bool IsChecked
        {
            get => isChecked;
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
