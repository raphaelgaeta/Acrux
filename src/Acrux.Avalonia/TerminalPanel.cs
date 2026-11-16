using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Acrux;

/// <summary>
/// The C# terminal, as the WinForms ScriptTerminalPanel: the history of steps
/// and results above, the code below, Ctrl+Enter to run, the 1M-row limit and
/// "Undo step" at the side.
/// </summary>
internal sealed class TerminalPanel : Grid
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

    private readonly SelectableTextBlock _history = new()
    {
        FontFamily = Mono,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8, 6),
    };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input = new()
    {
        FontFamily = Mono,
        FontSize = 13,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Height = 76,
        PlaceholderText = "df.Filter(Col(\"value\") > 1000)   (Ctrl+Enter to run)",
    };
    private readonly Button _run = new() { Content = "Run (Ctrl+Enter)", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _limit = new() { Content = $"Limit to {ScriptHost.RowCap:N0} rows", IsChecked = true };
    private readonly Button _undo = new() { Content = "Undo step", HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>Run this code as the next step; the bool is the row limit.</summary>
    public event Action<string, bool>? ExecuteRequested;
    public event Action? UndoRequested;

    public TerminalPanel()
    {
        RowDefinitions = new RowDefinitions("*,Auto");
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _history.Inlines!.Add(_intro = new Run(
            "// C# Polars terminal — `df` is the previous step's result.\n" +
            "// On the first run, `df` is the file AS SHOWN in the grid:\n" +
            "// shown columns and active header filters included.\n" +
            "// `df` is lazy (a LazyFrame): nothing is read until the grid shows it.\n" +
            "// End with a LazyFrame or DataFrame expression. E.g.:\n" +
            "//   df.Filter(Col(\"value\") > 1000).Sort(\"value\", descending: true)\n\n"));
        _scroll = new ScrollViewer { Content = _history };
        SetColumnSpan(_scroll, 2);
        Children.Add(_scroll);

        SetRow(_input, 1);
        Children.Add(_input);
        var side = new StackPanel { Spacing = 6, Width = 200, Margin = new Thickness(8, 0, 6, 6), Children = { _run, _limit, _undo } };
        SetRow(side, 1);
        SetColumn(side, 1);
        Children.Add(side);

        // Tunnel, not the plain KeyDown: a TextBox that accepts returns takes the
        // Enter itself (a new line) and marks it handled, so a bubbling handler
        // never sees Ctrl+Enter.
        _input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                Execute();
            }
        }, RoutingStrategies.Tunnel);
        _run.Click += (_, _) => Execute();
        _undo.Click += (_, _) => UndoRequested?.Invoke();
    }

    public bool Limit => _limit.IsChecked == true;

    private Run _intro = null!;

    // the theme is known once the panel is in the window
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _intro.Foreground = Colors.Muted;
    }

    public void FocusInput() => _input.Focus();

    public void SetBusy(bool busy)
    {
        _run.IsEnabled = !busy;
        _undo.IsEnabled = !busy;
        _input.IsReadOnly = busy;
    }

    // the echo and the errors take the palette of the moment (light or dark) when written
    private Palette Colors => Palette.For(ActualThemeVariant);
    public void AppendCode(string code) => Append($"> {code.Replace("\n", "\n  ")}\n", Colors.Muted);
    public void AppendResult(string text) => Append(text + "\n\n", null);
    public void AppendError(string text) => Append(text + "\n\n", Colors.Error);

    private void Execute()
    {
        string code = _input.Text?.Trim() ?? "";
        if (code.Length == 0 || !_run.IsEnabled) return;
        ExecuteRequested?.Invoke(code, Limit);
    }

    /// <summary>The step ran: its code leaves the input box.</summary>
    public void ClearInput() => _input.Text = "";

    private void Append(string text, IBrush? brush)
    {
        var run = new Run(text);
        if (brush is not null) run.Foreground = brush;
        _history.Inlines!.Add(run);
        _scroll.ScrollToEnd();
    }
}
