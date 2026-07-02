using System;
using System.Data;
using System.Drawing;
using System.Linq;
using PolarsLabel = Polars.CSharp.Label;
using FormsLabel = System.Windows.Forms.Label;
using System.Windows.Forms;
using Polars.CSharp;

using Apache.Arrow;
using Apache.Arrow.Ipc;
namespace PolarsGridViewer;
public static class DataFrameProvider
{
    public static async Task<DataFrame> GetDataFrameAsync(string parquetPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Arquivo .parquet não encontrado.", parquetPath);

        return await Task.Run(() => DataFrame.ReadParquet(parquetPath), cancellationToken);
    }
}

public partial class MainForm : Form
{
    private readonly DataGridView _grid;
    private readonly TextBox _txtFilter;
    private readonly Button _btnLoad;
    private readonly Button _btnClear;
    private readonly FormsLabel _lblInfo;

    private DataTable? _originalTable;
    private DataView? _dataView;

    

public MainForm()
{
    Text = "Visualizador Parquet ReadOnly";
    Width = 1280;
    Height = 760;
    StartPosition = FormStartPosition.CenterScreen;

    var topPanel = new Panel
    {
        Dock = DockStyle.Top,
        Height = 64,
        Padding = new Padding(10)
    };

    _btnLoad = new Button
    {
        Text = "Abrir arquivo .parquet",
        AutoSize = true,
        Left = 10,
        Top = 15
    };
    _btnLoad.Click += BtnLoad_Click;

    _txtFilter = new TextBox
    {
        Width = 320,
        Left = 155,
        Top = 17,
        PlaceholderText = "Digite um texto para filtrar..."
    };
    _txtFilter.TextChanged += TxtFilter_TextChanged;

    _btnClear = new Button
    {
        Text = "Limpar filtro",
        AutoSize = true,
        Left = 490,
        Top = 15
    };
    _btnClear.Click += BtnClear_Click;

    _lblInfo = new FormsLabel
    {
        AutoSize = true,
        Left = 630,
        Top = 20,
        Text = "Nenhum dado carregado"
    };

    topPanel.Controls.Add(_btnLoad);
    topPanel.Controls.Add(_txtFilter);
    topPanel.Controls.Add(_btnClear);
    topPanel.Controls.Add(_lblInfo);

    _grid = new DataGridView
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = true,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        MultiSelect = true,
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,

        // ── Otimizações ──────────────────────────────
        VirtualMode = true,                          // só renderiza células visíveis
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,      // ← CRÍTICO, evita recalculo constante
        RowHeadersVisible = false,                   // remove coluna de índice (custo de render)
        RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        AllowUserToResizeRows = false,               // evita recalculo de altura
    };

    // DoubleBuffered via reflection (propriedade protegida)
    typeof(DataGridView)
        .GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance)!
        .SetValue(_grid, true);

    // Altura fixa de linha — evita recalculo por linha
    _grid.RowTemplate.Height = 24;
    _grid.RowTemplate.Resizable = DataGridViewTriState.False;

    // Fonte
    _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10, FontStyle.Bold);
    _grid.DefaultCellStyle.Font = new Font("Segoe UI", 10);
    _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;

    // Fornece valor apenas para células visíveis
    _grid.CellValueNeeded += Grid_CellValueNeeded;

    Controls.Add(_grid);
    Controls.Add(topPanel);
}

private object[]?[] _columnCache = System.Array.Empty<object[]?>();  // colunas materializadas sob demanda
private int[]?      _filteredRows;      // índices das linhas filtradas (null = sem filtro)
private int         _rowCount;

private RecordBatch? _batch;
private void Grid_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
{
    if (_batch is null) return;

    // Materializa a coluna só na primeira vez que alguma célula dela fica visível
    var column = _columnCache[e.ColumnIndex]
        ??= PolarsTableAdapter.ExtractColumn(_batch.Column(e.ColumnIndex), _rowCount);

    int dataRow = _filteredRows is null ? e.RowIndex : _filteredRows[e.RowIndex];
    e.Value = column[dataRow];
}
private async void BtnLoad_Click(object? sender, EventArgs e)
{
    using var dialog = new OpenFileDialog
    {
        Title = "Selecione um arquivo Parquet",
        Filter = "Arquivos Parquet (*.parquet)|*.parquet|Todos os arquivos (*.*)|*.*"
    };

    if (dialog.ShowDialog(this) != DialogResult.OK)
        return;

    try
    {
        _btnLoad.Enabled = false;
        _lblInfo.Text = "Carregando...";

        // Leitura do parquet e conversão para Arrow fora da thread de UI
        var df_arrow = await Task.Run(async () =>
        {
            DataFrame df_ = await DataFrameProvider.GetDataFrameAsync(dialog.FileName);
            return df_.ToArrow();
        });

        _batch = df_arrow;
        _rowCount = df_arrow.Length;
        _filteredRows = null;

        // Cache vazio: cada coluna só é extraída quando aparece na tela
        _columnCache = new object[]?[df_arrow.ColumnCount];

        // Popula o grid
        _grid.SuspendLayout();
        _grid.RowCount = 0;
        _grid.Columns.Clear();

        var gridColumns = df_arrow.Schema.FieldsList
            .Select(field => (DataGridViewColumn)new DataGridViewTextBoxColumn
            {
                Name       = field.Name,
                HeaderText = field.Name,
                Width      = 120,
                SortMode   = DataGridViewColumnSortMode.Programmatic,
                FillWeight = 1   // padrão é 100; a soma não pode passar de 65535
            })
            .ToArray();

        _grid.Columns.AddRange(gridColumns);

        _grid.RowCount = _rowCount;   // ← não popula células, só registra total
        _grid.ResumeLayout();

        UpdateInfo();
    }
    catch (Exception ex)
    {
        MessageBox.Show(
            $"Erro ao carregar DataFrame:\n\n{ex.Message}",
            "Erro",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        _lblInfo.Text = "Falha ao carregar";
    }
    finally
    {
        _btnLoad.Enabled = true;
    }
}

    private void TxtFilter_TextChanged(object? sender, EventArgs e)
    {
        ApplyFilter(_txtFilter.Text);
    }

    private void BtnClear_Click(object? sender, EventArgs e)
    {
        _txtFilter.Text = string.Empty;
        ApplyFilter(string.Empty);
    }

    private void ApplyFilter(string text)
    {
        if (_dataView is null || _originalTable is null)
            return;

        if (string.IsNullOrWhiteSpace(text))
        {
            _dataView.RowFilter = string.Empty;
            UpdateInfo();
            return;
        }

        var escaped = EscapeLikeValue(text.Trim());

        var clauses = _originalTable.Columns
            .Cast<DataColumn>()
            .Select(c => $"CONVERT([{c.ColumnName}], 'System.String') LIKE '%{escaped}%'")
            .ToArray();

        _dataView.RowFilter = string.Join(" OR ", clauses);
        UpdateInfo();
    }

private void UpdateInfo()
{
    var total     = _rowCount;
    var exibindo  = _filteredRows?.Length ?? total;
    var colunas   = _batch?.ColumnCount ?? 0;

    _lblInfo.Text = _filteredRows is not null
        ? $"{exibindo:N0} de {total:N0} linhas × {colunas} colunas (filtrado)"
        : $"{total:N0} linhas × {colunas} colunas";
}

    private static string EscapeLikeValue(string value)
    {
        return value
            .Replace("'", "''")
            .Replace("[", "[[]")
            .Replace("%", "[%]")
            .Replace("*", "[*]");
    }
}

