using System;
using System.Windows.Forms;
namespace Acrux;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // light or dark as Windows is set (Windows 11; read once: switching
        // takes a restart). Before any window exists, so every one follows.
        Application.SetColorMode(SystemColorMode.System);

        // leftovers from previous runs killed before their cleanup
        DataFrameProvider.CleanStaleTempFiles();

        // A file opened from Explorer arrives as the first argument (the
        // installer registers "Acrux.exe" "%1"). A path that doesn't exist
        // still goes through: the load shows the error, not an empty window.
        Application.Run(new MainForm(args.FirstOrDefault()));

        // Guaranteed exit: the embedded native runtime (Polars/rayon) and
        // Roslyn may own threads the CLR would wait on past the message loop.
        // Nothing legitimate runs after the form closes (temp cleanup happens
        // in OnFormClosed), so end the process unconditionally.
        Environment.Exit(0);
    }
}
