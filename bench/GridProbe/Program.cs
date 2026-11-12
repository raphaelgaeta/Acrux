// Times the desktop app's grid setup: a grid with MainForm's properties gets
// its columns and rows, the way the app did before GridRows ("before": one
// RowCount, columns visible) and the way it does now ("after": GridRows).
// A thread posts a message every 10 ms and times how long each waits for the
// window: how long a click would wait. (A WinForms timer can't tell: Windows
// hands out WM_TIMER only when the queue is empty.)
//
// Usage: GridProbe.exe [rows] [columns] [block ms]   (5000000 700 500)
// Appends one line per run to gridprobe.log, next to the executable.
using System.Diagnostics;
using Acrux;

int rows = args.Length > 0 ? int.Parse(args[0]) : 5_000_000;
int columns = args.Length > 1 ? int.Parse(args[1]) : 700;
var blockTarget = TimeSpan.FromMilliseconds(args.Length > 2 ? double.Parse(args[2]) : 500);
string log = Path.Combine(AppContext.BaseDirectory, "gridprobe.log");

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
var form = new Form { Width = 1366, Height = 768, Text = "GridProbe" };

// the longest a posted message waited for the window
double longestWait = 0;
void StartKnocking() => new Thread(() =>
{
    while (!form.IsDisposed)
    {
        long sent = Stopwatch.GetTimestamp();
        try { form.BeginInvoke(() => longestWait = Math.Max(longestWait, Stopwatch.GetElapsedTime(sent).TotalMilliseconds)); }
        catch (InvalidOperationException) { return; }   // the window closed
        Thread.Sleep(10);
    }
}) { IsBackground = true }.Start();

// MainForm's grid, property for property
DataGridView MakeGrid()
{
    var grid = new DataGridView
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = true,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        MultiSelect = true,
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
        BorderStyle = BorderStyle.None,
        VirtualMode = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        RowHeadersVisible = false,
        RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        AllowUserToResizeRows = false,
        BackgroundColor = SystemColors.Window,
        EnableHeadersVisualStyles = false,
    };
    typeof(DataGridView)
        .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .SetValue(grid, true);
    grid.RowTemplate.Height = 24;
    grid.RowTemplate.Resizable = DataGridViewTriState.False;
    grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10, FontStyle.Bold);
    grid.DefaultCellStyle.Font = new Font("Segoe UI", 10);
    grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
    grid.CellValueNeeded += (_, e) => e.Value = "…";

    grid.SuspendLayout();
    grid.Columns.AddRange(Enumerable.Range(0, columns)
        .Select(i => (DataGridViewColumn)new DataGridViewTextBoxColumn
        {
            Name = $"c{i:000}",
            HeaderText = $"c{i:000}",
            Width = 120,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            FillWeight = 1,
            DefaultCellStyle = { Alignment = i % 2 == 0
                ? DataGridViewContentAlignment.MiddleRight
                : DataGridViewContentAlignment.MiddleLeft },
        })
        .ToArray());
    grid.ResumeLayout();
    return grid;
}

async Task Run(string scenario)
{
    var grid = MakeGrid();
    form.Controls.Add(grid);
    await Task.Delay(200);   // the window settles
    int blocks = 0;
    longestWait = 0;
    var clock = Stopwatch.StartNew();
    if (scenario == "before")
        grid.RowCount = rows;
    else
        await new GridRows(grid) { BlockTarget = blockTarget }.SetAsync(rows, (_, _) => blocks++);
    grid.Refresh();
    clock.Stop();
    await Task.Delay(50);   // the messages posted during the paint arrive
    string how = scenario == "before" ? "one RowCount" : $"{blocks} blocks of ~{blockTarget.TotalMilliseconds:0} ms";
    File.AppendAllText(log,
        $"{DateTime.Now:s} {scenario,-6} {rows,11:N0} rows × {columns,4} columns: {clock.Elapsed.TotalSeconds,6:0.00} s, "
        + $"a message waited up to {longestWait / 1000,6:0.00} s ({how}){Environment.NewLine}");
    form.Controls.Remove(grid);
    grid.Dispose();
}

form.Shown += async (_, _) =>
{
    StartKnocking();
    await Run("before");
    await Run("after");
    form.Close();
};
Application.Run(form);
