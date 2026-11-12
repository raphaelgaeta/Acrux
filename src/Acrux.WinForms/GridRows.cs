using System.Diagnostics;
using System.Windows.Forms;

namespace Acrux;

/// <summary>
/// Gives a virtual-mode grid its rows without freezing the window.
/// <para>
/// <c>RowCount = n</c> costs time per row and per column in WinForms (code of
/// .NET 10): each new row goes through
/// <c>DataGridViewRowCollection.AddCopiesPrivate</c> →
/// <c>OnAddedRow_PostNotification</c> → <c>AdjustExpandingColumns</c>, which
/// walks the visible columns even with <c>AutoSizeColumnsMode.None</c>; a
/// hidden column still costs a scan (<c>GetFirstColumn</c>). 5 million rows ×
/// 700 columns took 47 s on a 2017 notebook, with the window white, and no
/// grid or column property changed it.
/// </para>
/// <para>
/// So, from <see cref="BlocksFrom"/> rows on, the rows go in with the columns
/// hidden (half the cost) and in blocks of about <see cref="BlockTarget"/>,
/// with the window answering between them. The grid must be empty
/// (<c>Rows.Clear()</c>): shrinking <c>RowCount</c> removes rows one by one.
/// </para>
/// </summary>
internal sealed class GridRows(DataGridView grid)
{
    /// <summary>Below this, one <c>RowCount</c> is quick enough: under a second for 700 columns on a 2017 notebook.</summary>
    public const int BlocksFrom = 100_000;

    // sized by the time each block took: a slow PC gets smaller blocks
    private const int FirstBlock = 50_000;
    private const int SmallestBlock = 10_000;

    /// <summary>
    /// How long each block runs before the window answers again. Measured,
    /// 5M rows × 700 columns: 0.5 s costs ~4% over a single <c>RowCount</c>,
    /// 0.25 s ~30%.
    /// </summary>
    public TimeSpan BlockTarget { get; init; } = TimeSpan.FromMilliseconds(500);

    private int _generation;
    // the columns this build hid, shown again when it ends; null when none is running
    private List<DataGridViewColumn>? _hidden;
    private int _firstColumn = -1;

    public bool Building => _hidden is not null;

    /// <summary>
    /// The grid loses its columns (<c>Columns.Clear()</c>) or the window
    /// closes: a build in progress stops at its next block and shows nothing.
    /// </summary>
    public void Abandon()
    {
        _generation++;
        _hidden = null;
        _firstColumn = -1;
    }

    /// <summary>
    /// Sets the empty grid's row count; <paramref name="progress"/> hears the
    /// rows so far after each block.
    /// </summary>
    /// <returns>
    /// false when another call or <see cref="Abandon"/> took over: the caller
    /// leaves the screen to whoever did.
    /// </returns>
    public async Task<bool> SetAsync(int count, Action<int, int>? progress = null)
    {
        int generation = ++_generation;
        Debug.Assert(grid.RowCount == 0, "the grid must be empty: shrinking RowCount is row by row");
        if (count <= 0 || grid.Columns.Count == 0)
        {
            ShowHidden();
            return true;
        }
        if (count < BlocksFrom)
        {
            ShowHidden();
            grid.RowCount = count;
            return true;
        }

        // a build that took over from another keeps its list: those columns
        // are hidden now, and they are the ones to show
        if (_hidden is null)
        {
            _firstColumn = grid.FirstDisplayedScrollingColumnIndex;
            _hidden = [];
            grid.SuspendLayout();
            foreach (DataGridViewColumn column in grid.Columns)
                if (column.Visible)
                {
                    column.Visible = false;
                    _hidden.Add(column);
                }
            grid.ResumeLayout();
        }

        int block = FirstBlock;
        while (true)
        {
            long start = Stopwatch.GetTimestamp();
            grid.RowCount = (int)Math.Min(count, (long)grid.RowCount + block);
            double took = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            // at most 4× bigger at a time: a block that ran too fast to time
            // doesn't turn the rest into one long freeze
            double ratio = Math.Min(4, BlockTarget.TotalMilliseconds / Math.Max(took, 1));
            block = (int)Math.Clamp(block * ratio, SmallestBlock, count);
            progress?.Invoke(grid.RowCount, count);
            if (grid.RowCount >= count) break;

            // not Task.Yield: the continuation would be a posted message, which
            // Windows hands out before input and paint, and the window would
            // stay frozen
            await Task.Delay(1);
            if (generation != _generation || grid.IsDisposed) return false;
        }

        ShowHidden();
        return true;
    }

    private void ShowHidden()
    {
        if (_hidden is null) return;
        grid.SuspendLayout();
        foreach (var column in _hidden)
            if (column.DataGridView == grid)
                column.Visible = true;
        grid.ResumeLayout();

        // where the view was: a filter over column 400 keeps column 400 on screen
        if (_firstColumn >= 0 && _firstColumn < grid.Columns.Count && grid.Columns[_firstColumn].Visible)
        {
            try { grid.FirstDisplayedScrollingColumnIndex = _firstColumn; }
            catch (InvalidOperationException) { /* frozen column; the view starts at the left */ }
        }
        _hidden = null;
        _firstColumn = -1;
    }
}
