using System.Drawing;
using System.Windows.Forms;

namespace Acrux;

/// <summary>
/// C# Polars terminal panel (docked at the bottom of MainForm). UI only:
/// MainForm subscribes to <see cref="ExecuteRequested"/> and reports back
/// through <see cref="AppendResult"/>/<see cref="AppendError"/>.
/// </summary>
public sealed class ScriptTerminalPanel : Panel
{
    private readonly RichTextBox _history;
    private readonly TextBox _input;
    private readonly Button _btnRun;
    private readonly Button _btnUndo;
    private readonly CheckBox _chkLimit;

    /// <summary>(code, apply row cap)</summary>
    public event Action<string, bool>? ExecuteRequested;

    /// <summary>Undo the last chain step.</summary>
    public event Action? UndoRequested;

    /// <summary>Current state of the row-cap checkbox.</summary>
    public bool LimitEnabled => _chkLimit.Checked;

    public ScriptTerminalPanel()
    {
        Dock = DockStyle.Fill;
        var mono = new Font("Consolas", 10);

        _history = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.White,
            Font = mono,
            BorderStyle = BorderStyle.None,
            Text = "// C# Polars terminal — `lf` is the previous step's result\n" +
                   "// (on the first run, the LazyFrame of the open file).\n" +
                   "// End with a LazyFrame or DataFrame expression. E.g.:\n" +
                   "//   lf.Filter(Col(\"value\") > 1000).Sort(\"value\", descending: true)\n\n"
        };

        _input = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Font = mono
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                RequestExecute();
            }
        };

        _btnRun = new Button { Text = "Run (Ctrl+Enter)", AutoSize = true, Dock = DockStyle.Top };
        _btnRun.Click += (_, _) => RequestExecute();

        _chkLimit = new CheckBox
        {
            Text = $"Limit to {ScriptHost.RowCap:N0} rows",
            Checked = true,
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 6, 0, 0)
        };

        _btnUndo = new Button { Text = "Undo step", AutoSize = true, Dock = DockStyle.Top };
        _btnUndo.Click += (_, _) => UndoRequested?.Invoke();

        var side = new Panel { Dock = DockStyle.Right, Width = 175, Padding = new Padding(8, 0, 0, 0) };
        side.Controls.Add(_btnUndo);
        side.Controls.Add(_chkLimit);
        side.Controls.Add(_btnRun);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 88, Padding = new Padding(0, 6, 0, 0) };
        bottom.Controls.Add(_input);
        bottom.Controls.Add(side);

        Controls.Add(_history);
        Controls.Add(bottom);
    }

    public void FocusInput() => _input.Focus();

    public void SetBusy(bool busy)
    {
        _btnRun.Enabled = !busy;
        _btnUndo.Enabled = !busy;
        _input.Enabled = !busy;
    }

    public void AppendCode(string code)
    {
        var prefixed = "> " + code.ReplaceLineEndings("\n").Replace("\n", "\n> ");
        Append(prefixed + "\n", Color.Gray);
    }

    public void AppendResult(string text) => Append(text + "\n\n", Color.Black);

    public void AppendError(string text) => Append(text + "\n\n", Color.Firebrick);

    private void Append(string text, Color color)
    {
        _history.SelectionStart = _history.TextLength;
        _history.SelectionLength = 0;
        _history.SelectionColor = color;
        _history.AppendText(text);
        _history.ScrollToCaret();
    }

    private void RequestExecute()
    {
        var code = _input.Text.Trim();
        if (code.Length == 0) return;
        ExecuteRequested?.Invoke(code, _chkLimit.Checked);
    }
}
