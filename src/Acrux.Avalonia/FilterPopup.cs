using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;

namespace Acrux;

/// <summary>
/// The header's filter, as the WinForms FilterPopupForm: the column's distinct
/// values to check, a search that narrows the list, "(Blanks)" for nulls, and
/// a note when the list stops at FilterEngine.DistinctCap. Everything checked
/// is no filter. The list virtualizes, so 10,000 values cost a screenful.
/// </summary>
internal sealed class FilterPopup : Window
{
    private readonly Item[] _all;
    private readonly ListBox _list = new() { Height = 260 };
    private readonly TextBox _search = new() { PlaceholderText = "Search values..." };
    private readonly CheckBox _blanks = new() { Content = "(Blanks)" };
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _ok = new() { Content = "OK", IsDefault = true };
    private readonly bool _hasBlanks, _capped;

    public ColumnFilter? Result { get; private set; }

    public FilterPopup(string column, string[] values, bool hasBlanks, bool capped, ColumnFilter? current)
    {
        Title = $"Filter: {column}";
        Width = 300;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        _hasBlanks = hasBlanks;
        _capped = capped;
        _all = [.. values.Select(v => new Item(v, current is null || current.Values.Contains(v)))];
        foreach (var item in _all)
            item.PropertyChanged += (_, _) => UpdateStatus();

        _list.ItemTemplate = new FuncDataTemplate<Item>((_, _) =>
        {
            // the text in a TextBlock, not as the content itself: a string content is
            // read for an access key, and its first underscore vanished (ITEM_000003
            // showed as ITEM000003, c006_str12k as c006str12k)
            var text = new TextBlock();
            text.Bind(TextBlock.TextProperty, new ReflectionBinding(nameof(Item.Value)));
            var box = new CheckBox { Content = text };
            box.Bind(ToggleButton.IsCheckedProperty, new ReflectionBinding(nameof(Item.IsChecked)) { Mode = BindingMode.TwoWay });
            return box;
        });
        _search.TextChanged += (_, _) => Rebuild();
        _blanks.IsVisible = hasBlanks;
        _blanks.IsChecked = current?.IncludeBlanks ?? true;
        _blanks.IsCheckedChanged += (_, _) => UpdateStatus();

        var checkAll = new Button { Content = "Check all" };
        checkAll.Click += (_, _) => SetVisible(true);
        var uncheckAll = new Button { Content = "Uncheck all" };
        uncheckAll.Click += (_, _) => SetVisible(false);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        _ok.Click += (_, _) => Accept();

        Content = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 8,
            Children =
            {
                _search,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { checkAll, uncheckAll } },
                _list,
                _blanks,
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, _ok },
                },
            },
        };
        Rebuild();
    }

    private void Rebuild()
    {
        string search = _search.Text?.Trim() ?? "";
        _list.ItemsSource = search.Length == 0
            ? _all
            : _all.Where(i => i.Value.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        UpdateStatus();
    }

    /// <summary>Checks or unchecks only what the search shows.</summary>
    private void SetVisible(bool value)
    {
        foreach (var item in (IEnumerable<Item>)_list.ItemsSource!)
            item.IsChecked = value;
    }

    private void UpdateStatus()
    {
        int checkedCount = _all.Count(i => i.IsChecked);
        _status.Text = $"{checkedCount:N0} of {_all.Length:N0} values checked"
            + (_capped ? $"\nShowing the first {FilterEngine.DistinctCap:N0} values" : "");
        _ok.IsEnabled = checkedCount > 0 || (_hasBlanks && _blanks.IsChecked == true);
    }

    private void Accept()
    {
        var chosen = _all.Where(i => i.IsChecked).Select(i => i.Value).ToHashSet();
        bool blanks = _hasBlanks && _blanks.IsChecked == true;
        // everything checked is no filter; a capped list can't stand for "all values"
        bool all = chosen.Count == _all.Length && (!_hasBlanks || blanks);
        Result = all ? null : new ColumnFilter(chosen, blanks);
        Close(true);
    }

    private sealed class Item(string value, bool isChecked) : INotifyPropertyChanged
    {
        public string Value { get; } = value;

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
