using System.Drawing;
using System.Windows.Forms;

namespace PolarsGridViewer;

/// <summary>
/// Painel do terminal C# Polars (dock inferior do MainForm). Só UI: o
/// MainForm assina <see cref="ExecuteRequested"/> e devolve o resultado
/// via <see cref="AppendResult"/>/<see cref="AppendError"/>.
/// </summary>
public sealed class ScriptTerminalPanel : Panel
{
    private readonly RichTextBox _history;
    private readonly TextBox _input;
    private readonly Button _btnRun;
    private readonly CheckBox _chkLimit;

    /// <summary>(código, aplicar limite de linhas)</summary>
    public event Action<string, bool>? ExecuteRequested;

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
            Text = "// Terminal C# Polars — `lf` é o LazyFrame do arquivo aberto.\n" +
                   "// Termine com uma expressão LazyFrame ou DataFrame. Ex.:\n" +
                   "//   lf.Filter(Col(\"valor\") > 1000).Sort(\"valor\", descending: true)\n\n"
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

        _btnRun = new Button { Text = "Executar (Ctrl+Enter)", AutoSize = true, Dock = DockStyle.Top };
        _btnRun.Click += (_, _) => RequestExecute();

        _chkLimit = new CheckBox
        {
            Text = $"Limitar a {ScriptHost.RowCap:N0} linhas",
            Checked = true,
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 6, 0, 0)
        };

        var side = new Panel { Dock = DockStyle.Right, Width = 175, Padding = new Padding(8, 0, 0, 0) };
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
