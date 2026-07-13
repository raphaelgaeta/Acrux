using System;
using System.Windows.Forms;

namespace Acrux;

/// <summary>Minimal picker shown when an .xlsx has more than one sheet.</summary>
public sealed class SheetSelectorForm : Form
{
    private readonly ListBox _list;

    public int SelectedSheetIndex => _list.SelectedIndex;

    public SheetSelectorForm(string[] sheetNames)
    {
        Text = "Select sheet";
        Width = 340;
        Height = 380;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(10);

        _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        _list.Items.AddRange(sheetNames);
        _list.SelectedIndex = 0;
        _list.DoubleClick += (_, _) => DialogResult = DialogResult.OK;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0)
        };
        var ok = new Button { Text = "Open", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(_list);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}
