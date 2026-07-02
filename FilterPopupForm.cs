using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PolarsGridViewer;

/// <summary>
/// Popup de filtro de coluna estilo Excel: busca, lista de valores com
/// checkbox, "(Vazias)" para nulls e limpar filtro. Aberto sob o cabeçalho.
/// </summary>
public sealed class FilterPopupForm : Form
{
    private readonly TextBox _txtSearch;
    private readonly CheckedListBox _list;
    private readonly CheckBox _chkBlanks;
    private readonly Button _btnCheckAll;
    private readonly Button _btnUncheckAll;
    private readonly Button _btnOk;
    private readonly Button _btnCancel;
    private readonly Button _btnClearFilter;
    private readonly Label _lblStatus;

    private readonly string[] _allValues;
    private readonly HashSet<string> _checked;
    private readonly bool _hasBlanks;
    private readonly bool _capped;
    private bool _rebuilding;

    /// <summary>
    /// Filtro resultante após OK: null significa "sem filtro nesta coluna"
    /// (tudo marcado ou filtro limpo).
    /// </summary>
    public ColumnFilter? ResultFilter { get; private set; }

    public FilterPopupForm(string column, string[] values, bool hasBlanks, bool capped, ColumnFilter? current)
    {
        _allValues = values;
        _hasBlanks = hasBlanks;
        _capped = capped;

        // Sem filtro ativo = tudo marcado (comportamento Excel)
        _checked = current is null
            ? new HashSet<string>(values)
            : new HashSet<string>(values.Where(current.SelectedValues.Contains));

        Text = $"Filtrar: {column}";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;   // posicionado pelo chamador
        ShowInTaskbar = false;
        Width = 340;
        Height = 460;
        Padding = new Padding(8);

        _txtSearch = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Buscar valor..."
        };
        _txtSearch.TextChanged += (_, _) => RebuildList();

        var topButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 4)
        };
        _btnCheckAll = new Button { Text = "Marcar todas", AutoSize = true };
        _btnCheckAll.Click += (_, _) => SetVisibleChecked(true);
        _btnUncheckAll = new Button { Text = "Desmarcar todas", AutoSize = true };
        _btnUncheckAll.Click += (_, _) => SetVisibleChecked(false);
        topButtons.Controls.Add(_btnCheckAll);
        topButtons.Controls.Add(_btnUncheckAll);

        _list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false
        };
        _list.ItemCheck += List_ItemCheck;

        _chkBlanks = new CheckBox
        {
            Dock = DockStyle.Bottom,
            Text = "(Vazias)",
            Checked = current?.IncludeBlanks ?? true,
            Visible = hasBlanks,
            Height = 24
        };
        _chkBlanks.CheckedChanged += (_, _) => UpdateStatus();

        _lblStatus = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 34,
            TextAlign = ContentAlignment.MiddleLeft
        };

        var bottomButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 0)
        };
        _btnOk = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        _btnOk.Click += BtnOk_Click;
        _btnCancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        _btnClearFilter = new Button
        {
            Text = "Limpar filtro",
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Enabled = current is not null
        };
        _btnClearFilter.Click += (_, _) => ResultFilter = null;
        bottomButtons.Controls.Add(_btnOk);
        bottomButtons.Controls.Add(_btnCancel);
        bottomButtons.Controls.Add(_btnClearFilter);

        Controls.Add(_list);
        Controls.Add(topButtons);
        Controls.Add(_txtSearch);
        Controls.Add(_chkBlanks);
        Controls.Add(_lblStatus);
        Controls.Add(bottomButtons);

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        RebuildList();
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        // Tudo marcado = coluna sem filtro. Com a lista cortada no cap não
        // dá para representar "todos os valores", então também vira sem filtro.
        bool allChecked = _checked.Count == _allValues.Length &&
                          (!_hasBlanks || _chkBlanks.Checked);

        ResultFilter = allChecked
            ? null
            : new ColumnFilter
            {
                SelectedValues = new HashSet<string>(_checked),
                IncludeBlanks = _hasBlanks && _chkBlanks.Checked
            };
    }

    private void List_ItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_rebuilding) return;

        var value = (string)_list.Items[e.Index];
        if (e.NewValue == CheckState.Checked)
            _checked.Add(value);
        else
            _checked.Remove(value);

        // ItemCheck dispara antes da mudança ser aplicada
        BeginInvoke(UpdateStatus);
    }

    private void RebuildList()
    {
        _rebuilding = true;
        _list.BeginUpdate();
        _list.Items.Clear();

        var filter = _txtSearch.Text.Trim();
        foreach (var value in _allValues)
        {
            if (filter.Length > 0 &&
                !value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            _list.Items.Add(value, _checked.Contains(value));
        }

        _list.EndUpdate();
        _rebuilding = false;
        UpdateStatus();
    }

    /// <summary>Marca/desmarca apenas os itens visíveis (respeitando a busca).</summary>
    private void SetVisibleChecked(bool value)
    {
        _rebuilding = true;
        _list.BeginUpdate();
        for (int i = 0; i < _list.Items.Count; i++)
        {
            var item = (string)_list.Items[i];
            _list.SetItemChecked(i, value);
            if (value) _checked.Add(item);
            else _checked.Remove(item);
        }
        _list.EndUpdate();
        _rebuilding = false;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var text = $"{_checked.Count:N0} de {_allValues.Length:N0} valores marcados";
        if (_capped)
            text += $"\nMostrando os primeiros {FilterEngine.DistinctCap:N0} valores";
        _lblStatus.Text = text;

        _btnOk.Enabled = _checked.Count > 0 || (_hasBlanks && _chkBlanks.Checked);
    }
}
