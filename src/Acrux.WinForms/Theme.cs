using System.Drawing;
using System.Windows.Forms;

namespace Acrux;

/// <summary>
/// The app's few colors in one place: the brand on the window border and where
/// it helps read the grid, the system's colors everywhere else, so both light
/// and dark mode (Application.SetColorMode in Program) hold. High contrast
/// turns the brand off: the user's own scheme wins. Contrast ratios are WCAG:
/// 4.5:1 is the floor for text, 3:1 for the rest.
/// </summary>
internal static class Theme
{
    public static readonly Color Purple = ColorTranslator.FromHtml("#8c5ea4");
    public static readonly Color Blue = ColorTranslator.FromHtml("#acd7e7");

    private static bool Branded => !SystemInformation.HighContrast;
    private static bool Dark => Application.IsDarkModeEnabled;

    // Each brand color in the mode where it reads: black on blue 13.6:1,
    // white on purple 4.9:1 (white on blue would be 1.5:1)
    private static Color AccentBack => Dark ? Purple : Blue;
    private static Color AccentText => Dark ? Color.White : Color.Black;

    /// <summary>
    /// Secondary text (nulls, the terminal's echo): 60% of the way from the
    /// background to the text, so it is ~5.7:1 on white and ~6.6:1 on #202020
    /// whatever gray the system picks for GrayText.
    /// </summary>
    public static Color Muted => Blend(SystemColors.Window, SystemColors.WindowText, 0.6);

    /// <summary>Errors: Firebrick on light (6.7:1) is 2.4:1 on dark, hence a lighter red there (6.6:1).</summary>
    public static Color Error => Dark ? ColorTranslator.FromHtml("#f48771") : Color.Firebrick;

    /// <summary>
    /// A purple window border (3:1 or more against light and dark, the floor
    /// for non-text). The title bar stays the system's: the icon's bear and
    /// star are cutouts that show the bar's color (white on light, black on
    /// dark), and on a purple bar the bear vanished into the icon's purple
    /// half. Windows 11 only; older systems ignore it.
    /// </summary>
    public static void Apply(Form form)
    {
        if (!Branded)
            return;
        form.FormBorderColor = Purple;
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = SystemColors.Window;
        // flat headers: they take a back color (the filtered column's) and follow dark mode
        grid.EnableHeadersVisualStyles = false;
        if (!Branded)
            return;
        grid.DefaultCellStyle.SelectionBackColor = AccentBack;
        grid.DefaultCellStyle.SelectionForeColor = AccentText;
        // barely there in either mode: enough to follow a row across a wide file
        grid.AlternatingRowsDefaultCellStyle.BackColor = Blend(SystemColors.Window, SystemColors.WindowText, 0.04);
    }

    /// <summary>
    /// A filtered column's header in the accent color. The " ▼" in its text
    /// stays: color can't be the only cue.
    /// </summary>
    public static void MarkFiltered(DataGridViewColumn column, bool filtered)
    {
        if (!Branded)
            return;
        column.HeaderCell.Style.BackColor = filtered ? AccentBack : Color.Empty;
        column.HeaderCell.Style.ForeColor = filtered ? AccentText : Color.Empty;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));
}
