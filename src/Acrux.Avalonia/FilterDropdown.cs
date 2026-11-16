using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Acrux;

/// <summary>
/// The header's filter, as the WinForms FilterPopupForm: the column's distinct
/// values to check, a search that narrows the list, "(Blanks)" for nulls, and
/// a note when the list stops at FilterEngine.DistinctCap. Everything checked
/// is no filter. The list virtualizes, so 10,000 values cost a screenful.
///
/// A dropdown under the header, as Excel's (design approved 2026-11-15): no
/// title bar; it opens at once, reading the values, and fills when they come;
/// Esc or a click elsewhere closes it without applying, Enter or OK applies.
/// The window that hosts it (a Popup) is the caller's: this is its content.
/// </summary>
internal sealed class FilterDropdown : Border
{
    private Item[] _all = [];
    private readonly ListBox _list = new() { Height = 260, IsEnabled = false };
    private readonly TextBox _search = new() { PlaceholderText = "Search values...", IsEnabled = false };
    private readonly CheckBox _blanks = new() { Content = "(Blanks)", IsVisible = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _checkAll = new() { Content = "Check all", IsEnabled = false };
    private readonly Button _uncheckAll = new() { Content = "Uncheck all", IsEnabled = false };
    private readonly Button _ok = new() { Content = "OK", IsEnabled = false };
    private readonly ColumnFilter? _current;
    private readonly TaskCompletionSource<bool> _done = new();
    private bool _hasBlanks, _capped;

    /// <summary>The filter chosen on OK; null is no filter (everything checked).</summary>
    public ColumnFilter? Result { get; private set; }

    /// <summary>True on OK; false when it closed without applying.</summary>
    public Task<bool> Done => _done.Task;

    public FilterDropdown(string column, ColumnFilter? current)
    {
        _current = current;
        Avalonia.Automation.AutomationProperties.SetName(this, $"Filter: {column}");   // what a screen reader says as it opens
        Focusable = true;   // while the values are read, Esc needs something with focus to reach
        BorderThickness = new Thickness(1);
        _status.Text = $"Reading the values of \"{column}\"...";

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
        _blanks.IsCheckedChanged += (_, _) => UpdateStatus();
        _checkAll.Click += (_, _) => SetVisible(true);
        _uncheckAll.Click += (_, _) => SetVisible(false);
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Cancel();
        _ok.Click += (_, _) => Accept();
        // Enter and Esc by hand: IsDefault and IsCancel answer to a window's keys, and this is no window
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
            else if (e.Key == Key.Enter && _ok.IsEnabled) { Accept(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Child = new StackPanel
        {
            Margin = new Thickness(10),
            Spacing = 8,
            Children =
            {
                _search,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _checkAll, _uncheckAll } },
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
        ActualThemeVariantChanged += (_, _) => Paint();
        Paint();
    }

    /// <summary>The column's values, once read: the list fills and the buttons wake up.</summary>
    public void SetValues(string[] values, bool hasBlanks, bool capped)
    {
        _hasBlanks = hasBlanks;
        _capped = capped;
        _all = [.. values.Select(v => new Item(v, _current is null || _current.Values.Contains(v)))];
        foreach (var item in _all)
            item.PropertyChanged += (_, _) => UpdateStatus();
        _blanks.IsVisible = hasBlanks;
        _blanks.IsChecked = _current?.IncludeBlanks ?? true;
        _list.IsEnabled = _search.IsEnabled = _checkAll.IsEnabled = _uncheckAll.IsEnabled = true;
        Rebuild();
        _search.Focus();
    }

    /// <summary>Puts the typing in the search box, as soon as the dropdown shows.</summary>
    public void FocusSearch()
    {
        if (_search.IsEnabled) _search.Focus();
        else Focus();   // still reading: Esc must reach the dropdown all the same
    }

    /// <summary>Closes without applying: Esc, Cancel, or the host closing it (a click elsewhere).</summary>
    public void Cancel() => _done.TrySetResult(false);

    private void Paint()
    {
        var palette = Palette.Chrome(ActualThemeVariant);   // its controls are Fluent's, which has no high contrast
        Background = palette.Background;
        BorderBrush = palette.LineBrush;
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
        _done.TrySetResult(true);
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
