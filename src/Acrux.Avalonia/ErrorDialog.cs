using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Acrux;

/// <summary>
/// An error that stops what the user asked for, as the WinForms app's
/// MessageBox: the message selectable (to copy into a bug report) and OK.
/// </summary>
internal sealed class ErrorDialog : Window
{
    private ErrorDialog(string title, string message)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 600;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Acrux/Assets/acrux.ico")));
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 12,
            Children =
            {
                new ScrollViewer
                {
                    MaxHeight = 480,
                    Content = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                },
                ok,
            },
        };
        Opened += (_, _) => ok.Focus();
    }

    public static Task ShowAsync(Window owner, string title, string message) => new ErrorDialog(title, message).ShowDialog(owner);
}
