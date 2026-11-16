using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Acrux;

internal sealed class App : Application
{
    /// <summary>The file given on the command line, opened once the window is up.</summary>
    public static string? FileToOpen { get; set; }

    public override void Initialize()
    {
        // light or dark as the system is set: Windows' setting, or the
        // desktop portal's on Linux; the grid's colors follow (Palette)
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        HighContrast.Start(this);   // before the window: its first frame already in the system's colors
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(FileToOpen);
        base.OnFrameworkInitializationCompleted();
    }
}
