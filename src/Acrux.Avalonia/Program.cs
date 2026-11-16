using Avalonia;

namespace Acrux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // first of all: glibc makes an arena when a thread first allocates
        NativeHeap.LimitArenas();
        // leftovers from previous runs killed before their cleanup
        DataFrameProvider.CleanStaleTempFiles();
        // A file opened from the file manager arrives as the first argument. A
        // path that doesn't exist still goes through: the load shows the error.
        App.FileToOpen = args.FirstOrDefault();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        // Guaranteed exit, as in the WinForms app: Polars' threads (rayon) and
        // Roslyn's may hold the process after the last window closes.
        Environment.Exit(0);
    }

    // Avalonia's designer looks for this method by name
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
