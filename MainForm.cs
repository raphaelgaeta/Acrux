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

public partial class MainForm : Form
{
    private readonly DataGridView _grid;
    private readonly Button _btnLoad;
    private readonly Button _btnClearFilters;
    private readonly FormsLabel _lblInfo;

    private string? _parquetPath;
    private readonly Dictionary<string, ColumnFilter> _activeFilters = new();
    private CancellationTokenSource? _filterCts;

    

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

    _btnClearFilters = new Button
    {
        Text = "Limpar todos os filtros",
        AutoSize = true,
        Left = 165,
        Top = 15,
        Enabled = false
    };
    _btnClearFilters.Click += BtnClearFilters_Click;

    _lblInfo = new FormsLabel
    {
        AutoSize = true,
        Left = 340,
        Top = 20,
        Text = "Nenhum dado carregado"
    };

    topPanel.Controls.Add(_btnLoad);
    topPanel.Controls.Add(_btnClearFilters);
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

    // Clique no cabeçalho abre o filtro da coluna (estilo Excel)
    _grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;

    Controls.Add(_grid);
    Controls.Add(topPanel);
}

private int[]?      _filteredRows;      // índices das linhas filtradas (null = sem filtro)
private int         _rowCount;

private RecordBatch? _batch;
private void Grid_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
{
    if (_batch is null) return;

    // Lê a célula direto do buffer Arrow (O(1), sem materializar a coluna)
    int dataRow = _filteredRows is null ? e.RowIndex : _filteredRows[e.RowIndex];
    e.Value = PolarsTableAdapter.GetCellValue(_batch.Column(e.ColumnIndex), dataRow);
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
        _lblInfo.Text = "Lendo schema...";

        // Só metadados: rápido mesmo em arquivos grandes
        var columnNames = await DataFrameProvider.GetColumnNamesAsync(dialog.FileName);

        string[] selectedColumns;
        using (var selector = new ColumnSelectorForm(columnNames))
        {
            if (selector.ShowDialog(this) != DialogResult.OK)
            {
                _lblInfo.Text = "Carregamento cancelado";
                return;
            }
            selectedColumns = selector.SelectedColumns;
        }

        _lblInfo.Text = "Carregando...";

        // Coleta apenas as colunas escolhidas (projection pushdown) e
        // converte para Arrow fora da thread de UI. O DataFrame do Polars
        // é descartado após a conversão para não manter duas cópias.
        var df_arrow = await Task.Run(async () =>
        {
            using DataFrame df_ = await DataFrameProvider.GetDataFrameAsync(dialog.FileName, selectedColumns);
            return df_.ToArrow();
        });

        // Esvazia o grid antes de descartar o batch anterior
        // (Rows.Clear é O(1); RowCount = 0 removeria linha a linha)
        _grid.Rows.Clear();
        _grid.Columns.Clear();
        _batch?.Dispose();

        _batch = df_arrow;
        _rowCount = df_arrow.Length;
        _filteredRows = null;

        // Novo arquivo: filtros anteriores não se aplicam
        _filterCts?.Cancel();
        _activeFilters.Clear();
        _parquetPath = dialog.FileName;
        _btnClearFilters.Enabled = false;

        // Popula o grid
        _grid.SuspendLayout();

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

    private async void Grid_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (_batch is null || _parquetPath is null ||
            e.Button != MouseButtons.Left || e.ColumnIndex < 0)
            return;

        var column = _grid.Columns[e.ColumnIndex].Name;   // Name é sempre o nome real da coluna

        try
        {
            _lblInfo.Text = $"Lendo valores de \"{column}\"...";

            var (values, hasBlanks, capped) = await FilterEngine.GetDistinctValuesAsync(
                _parquetPath, column, _activeFilters);

            using var popup = new FilterPopupForm(
                column, values, hasBlanks, capped,
                _activeFilters.GetValueOrDefault(column));

            // Posiciona o popup logo abaixo do cabeçalho clicado
            var headerRect = _grid.GetCellDisplayRectangle(e.ColumnIndex, -1, false);
            var screenPos = _grid.PointToScreen(new Point(headerRect.Left, headerRect.Bottom));
            var screen = Screen.FromControl(this).WorkingArea;
            screenPos.X = Math.Min(screenPos.X, screen.Right - popup.Width);
            screenPos.Y = Math.Min(screenPos.Y, screen.Bottom - popup.Height);
            popup.Location = screenPos;

            if (popup.ShowDialog(this) != DialogResult.OK)
            {
                UpdateInfo();
                return;
            }

            if (popup.ResultFilter is null)
                _activeFilters.Remove(column);
            else
                _activeFilters[column] = popup.ResultFilter;

            await ReapplyFiltersAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Erro ao filtrar coluna \"{column}\":\n\n{ex.Message}",
                "Erro",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            UpdateInfo();
        }
    }

    private async void BtnClearFilters_Click(object? sender, EventArgs e)
    {
        if (_activeFilters.Count == 0) return;
        _activeFilters.Clear();
        await ReapplyFiltersAsync();
    }

    /// <summary>
    /// Recalcula os índices visíveis a partir dos filtros ativos (sempre
    /// sobre o dado original) e atualiza o grid via indireção de linhas.
    /// </summary>
    private async Task ReapplyFiltersAsync()
    {
        if (_parquetPath is null) return;

        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();

        _lblInfo.Text = "Aplicando filtros...";
        try
        {
            var swQuery = System.Diagnostics.Stopwatch.StartNew();
            var rows = await FilterEngine.GetVisibleRowsAsync(_parquetPath, _activeFilters, cts.Token);
            swQuery.Stop();
            if (cts.Token.IsCancellationRequested) return;   // outra interação assumiu

            var swGrid = System.Diagnostics.Stopwatch.StartNew();
            _filteredRows = rows;

            // Diminuir RowCount remove linha a linha (lentíssimo com milhões
            // de linhas); Rows.Clear() é um reset O(1) e recriar é em bloco.
            _grid.Rows.Clear();
            int newCount = rows?.Length ?? _rowCount;
            if (newCount > 0)
                _grid.RowCount = newCount;
            swGrid.Stop();

            UpdateHeaderIndicators();
            _btnClearFilters.Enabled = _activeFilters.Count > 0;
            UpdateInfo();
            _lblInfo.Text += $"  [consulta {swQuery.ElapsedMilliseconds} ms | grid {swGrid.ElapsedMilliseconds} ms]";
        }
        catch (OperationCanceledException)
        {
            // consulta obsoleta, descartada
        }
    }

    /// <summary>Sufixo ▼ no cabeçalho das colunas com filtro ativo.</summary>
    private void UpdateHeaderIndicators()
    {
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            var wanted = _activeFilters.ContainsKey(col.Name) ? col.Name + " ▼" : col.Name;
            if (col.HeaderText != wanted)
                col.HeaderText = wanted;
        }
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

}

