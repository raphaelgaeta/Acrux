using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PolarsGridViewer;

/// <summary>
/// Dialog shown on open: pick which columns get collected and displayed.
/// Only the selected columns are read from disk (projection pushdown).
/// </summary>
public sealed class ColumnSelectorForm : Form
{
    private readonly CheckedListBox _list;
    private readonly TextBox _txtSearch;
    private readonly Button _btnCheckAll;
    private readonly Button _btnUncheckAll;
    private readonly Button _btnOk;
    private readonly Button _btnCancel;
    private readonly Label _lblCount;

    private readonly string[] _allColumns;
    private readonly HashSet<string> _checked;
    private bool _rebuilding;

    /// <summary>Checked columns, in the file's original order.</summary>
    public string[] SelectedColumns =>
        _allColumns.Where(c => _checked.Contains(c)).ToArray();

    public ColumnSelectorForm(string[] columns)
    {
        _allColumns = columns;
        _checked = new HashSet<string>(columns);   // all checked by default

        Text = "Select columns";
        Width = 420;
        Height = 560;
        MinimumSize = new Size(320, 400);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        _txtSearch = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Search columns..."
        };
        _txtSearch.TextChanged += (_, _) => RebuildList();

        _list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false
        };
        _list.ItemCheck += List_ItemCheck;

        var topButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 4)
        };
        _btnCheckAll = new Button { Text = "Check all", AutoSize = true };
        _btnCheckAll.Click += (_, _) => SetVisibleChecked(true);
        _btnUncheckAll = new Button { Text = "Uncheck all", AutoSize = true };
        _btnUncheckAll.Click += (_, _) => SetVisibleChecked(false);
        topButtons.Controls.Add(_btnCheckAll);
        topButtons.Controls.Add(_btnUncheckAll);

        _lblCount = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft
        };

        var bottomButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 4)
        };
        _btnOk = new Button { Text = "Load", AutoSize = true, DialogResult = DialogResult.OK };
        _btnCancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        bottomButtons.Controls.Add(_btnOk);
        bottomButtons.Controls.Add(_btnCancel);

        Controls.Add(_list);
        Controls.Add(topButtons);
        Controls.Add(_txtSearch);
        Controls.Add(_lblCount);
        Controls.Add(bottomButtons);

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        Padding = new Padding(10);

        RebuildList();
    }

    private void List_ItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_rebuilding) return;

        var name = (string)_list.Items[e.Index];
        if (e.NewValue == CheckState.Checked)
            _checked.Add(name);
        else
            _checked.Remove(name);

        // ItemCheck fires before the change is applied
        BeginInvoke(UpdateCount);
    }

    private void RebuildList()
    {
        _rebuilding = true;
        _list.BeginUpdate();
        _list.Items.Clear();

        var filter = _txtSearch.Text.Trim();
        foreach (var col in _allColumns)
        {
            if (filter.Length > 0 &&
                !col.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            _list.Items.Add(col, _checked.Contains(col));
        }

        _list.EndUpdate();
        _rebuilding = false;
        UpdateCount();
    }

    /// <summary>Checks/unchecks only the visible (search-matched) items.</summary>
    private void SetVisibleChecked(bool value)
    {
        _rebuilding = true;
        _list.BeginUpdate();
        for (int i = 0; i < _list.Items.Count; i++)
        {
            var name = (string)_list.Items[i];
            _list.SetItemChecked(i, value);
            if (value) _checked.Add(name);
            else _checked.Remove(name);
        }
        _list.EndUpdate();
        _rebuilding = false;
        UpdateCount();
    }

    private void UpdateCount()
    {
        _lblCount.Text = $"{_checked.Count:N0} of {_allColumns.Length:N0} columns selected";
        _btnOk.Enabled = _checked.Count > 0;
    }
}
