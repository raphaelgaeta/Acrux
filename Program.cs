using System;
using System.Windows.Forms;
namespace Acrux;
internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // leftovers from previous runs killed before their cleanup
        DataFrameProvider.CleanStaleTempFiles();

        Application.Run(new MainForm());

        // Guaranteed exit: the embedded native runtime (Polars/rayon) and
        // Roslyn may own threads the CLR would wait on past the message loop.
        // Nothing legitimate runs after the form closes (temp cleanup happens
        // in OnFormClosed), so end the process unconditionally.
        Environment.Exit(0);
    }
}
