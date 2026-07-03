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
    private readonly Button _btnTerminal;
    private readonly Button _btnRestore;
    private readonly FormsLabel _lblInfo;
    private readonly SplitContainer _split;
    private readonly ScriptTerminalPanel _terminal;

    private string? _parquetPath;
    private string? _tempParquetPath;   // parquet convertido de CSV (apagar ao trocar/fechar)
    private string[] _selectedColumns = [];
    private bool _scriptMode;
    private readonly List<string> _scriptChain = new();
    private readonly Dictionary<string, ColumnFilter> _activeFilters = new();
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _scriptCts;

    

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

    _btnTerminal = new Button
    {
        Text = "Terminal C#",
        AutoSize = true,
        Left = 340,
        Top = 15,
        Enabled = false
    };
    _btnTerminal.Click += (_, _) => ToggleTerminal();

    _btnRestore = new Button
    {
        Text = "Restaurar arquivo",
        AutoSize = true,
        Left = 460,
        Top = 15,
        Visible = false
    };
    _btnRestore.Click += BtnRestore_Click;

    _lblInfo = new FormsLabel
    {
        AutoSize = true,
        Left = 620,
        Top = 20,
        Text = "Nenhum dado carregado"
    };

    topPanel.Controls.Add(_btnLoad);
    topPanel.Controls.Add(_btnClearFilters);
    topPanel.Controls.Add(_btnTerminal);
    topPanel.Controls.Add(_btnRestore);
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

    _terminal = new ScriptTerminalPanel();
    _terminal.ExecuteRequested += Terminal_ExecuteRequested;
    _terminal.UndoRequested += Terminal_UndoRequested;

    _split = new SplitContainer
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
        Panel2Collapsed = true,
        SplitterWidth = 6
    };
    _split.Panel1.Controls.Add(_grid);
    _split.Panel2.Controls.Add(_terminal);

    Controls.Add(_split);
    Controls.Add(topPanel);
}

private void ToggleTerminal()
{
    _split.Panel2Collapsed = !_split.Panel2Collapsed;
    if (!_split.Panel2Collapsed)
    {
        try { _split.SplitterDistance = Math.Max(120, _split.Height * 60 / 100); }
        catch (InvalidOperationException) { /* janela pequena demais, fica no default */ }
        _terminal.FocusInput();
    }
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
        Title = "Selecione um arquivo Parquet ou CSV",
        Filter = DataFrameProvider.OpenDialogFilter
    };

    if (dialog.ShowDialog(this) != DialogResult.OK)
        return;

    var isCsv = dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
    string sourcePath = dialog.FileName;
    bool sourceIsTemp = false;
    try
    {
        _btnLoad.Enabled = false;

        // CSV: conversão única para parquet temporário; o app opera sobre parquet
        _lblInfo.Text = isCsv ? "Convertendo CSV para parquet..." : "Lendo schema...";
        (sourcePath, sourceIsTemp) = await DataFrameProvider.EnsureParquetAsync(dialog.FileName);

        // Só metadados: rápido mesmo em arquivos grandes
        var columnNames = await DataFrameProvider.GetColumnNamesAsync(sourcePath);

        string[] selectedColumns;
        using (var selector = new ColumnSelectorForm(columnNames))
        {
            if (selector.ShowDialog(this) != DialogResult.OK)
            {
                if (sourceIsTemp) TryDeleteTemp(sourcePath);
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
            using DataFrame df_ = await DataFrameProvider.GetDataFrameAsync(sourcePath, selectedColumns);
            return df_.ToArrow();
        });

        // Novo arquivo: filtros e modo script anteriores não se aplicam
        _filterCts?.Cancel();
        _scriptCts?.Cancel();
        _activeFilters.Clear();
        _scriptChain.Clear();

        // sucesso: o temp anterior (se houver) pode ir embora
        if (_tempParquetPath is not null)
            TryDeleteTemp(_tempParquetPath);
        _tempParquetPath = sourceIsTemp ? sourcePath : null;

        _parquetPath = sourcePath;
        _selectedColumns = selectedColumns;
        _btnClearFilters.Enabled = false;
        _scriptMode = false;
        _btnRestore.Visible = false;
        _btnTerminal.Enabled = true;

        DisplayBatch(df_arrow);
        UpdateInfo();
    }
    catch (Exception ex)
    {
        // falha: não deixa órfão o temp recém-convertido (se não virou o ativo)
        if (sourceIsTemp && !ReferenceEquals(sourcePath, _tempParquetPath) && sourcePath != _tempParquetPath)
            TryDeleteTemp(sourcePath);
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

private static void TryDeleteTemp(string path)
{
    try { File.Delete(path); }
    catch (IOException) { /* ainda em uso; fica para a limpeza do SO */ }
    catch (UnauthorizedAccessException) { }
}

protected override void OnFormClosed(FormClosedEventArgs e)
{
    if (_tempParquetPath is not null)
        TryDeleteTemp(_tempParquetPath);
    base.OnFormClosed(e);
}

    /// <summary>
    /// Troca o conteúdo do grid pelo batch dado (posse transferida para o form).
    /// Caminho único de exibição: carga do arquivo, restauração e scripts.
    /// </summary>
    private void DisplayBatch(RecordBatch batch)
    {
        // Esvazia o grid antes de descartar o batch anterior
        // (Rows.Clear é O(1); RowCount = 0 removeria linha a linha)
        _grid.Rows.Clear();
        _grid.Columns.Clear();
        _batch?.Dispose();

        _batch = batch;
        _rowCount = batch.Length;
        _filteredRows = null;

        _grid.SuspendLayout();

        var gridColumns = batch.Schema.FieldsList
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

        if (_rowCount > 0)
            _grid.RowCount = _rowCount;   // ← não popula células, só registra total
        _grid.ResumeLayout();
    }

    private async void Terminal_ExecuteRequested(string code, bool applyRowCap)
    {
        if (_parquetPath is null)
        {
            _terminal.AppendError("Abra um arquivo .parquet antes de executar scripts.");
            return;
        }

        _terminal.AppendCode(code);
        await RunChainAsync(appendStep: code, applyRowCap);
    }

    private async void Terminal_UndoRequested()
    {
        if (_scriptChain.Count == 0) return;

        _scriptChain.RemoveAt(_scriptChain.Count - 1);

        if (_scriptChain.Count == 0)
        {
            _terminal.AppendResult("cadeia vazia — restaurando o arquivo original");
            await RestoreFileAsync();
            return;
        }

        await RunChainAsync(appendStep: null, _terminal.LimitEnabled);
    }

    /// <summary>
    /// Replay da cadeia de scripts (mais um passo candidato, se houver) sobre
    /// um scan fresco do arquivo. O passo só entra na cadeia se a execução
    /// inteira der certo — um erro deixa a cadeia como estava.
    /// </summary>
    private async Task RunChainAsync(string? appendStep, bool applyRowCap)
    {
        if (_parquetPath is null) return;

        _scriptCts?.Cancel();
        var cts = _scriptCts = new CancellationTokenSource();

        _terminal.SetBusy(true);
        _lblInfo.Text = "Executando script...";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var steps = new List<string>(_scriptChain);
            if (appendStep is not null)
                steps.Add(appendStep);

            var batch = await ScriptHost.RunChainAsync(steps, _parquetPath, applyRowCap, cts.Token);
            if (cts.Token.IsCancellationRequested)
            {
                batch.Dispose();
                return;
            }

            if (appendStep is not null)
                _scriptChain.Add(appendStep);

            DisplayBatch(batch);
            EnterScriptMode();

            var capped = applyRowCap && batch.Length == ScriptHost.RowCap
                ? $" — resultado limitado a {ScriptHost.RowCap:N0} linhas"
                : "";
            _terminal.AppendResult(
                $"passo {_scriptChain.Count}: {batch.Length:N0} linhas × {batch.ColumnCount} colunas em {sw.ElapsedMilliseconds:N0} ms{capped}");
            UpdateInfo();
        }
        catch (OperationCanceledException)
        {
            // execução obsoleta, descartada
        }
        catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException ex)
        {
            _terminal.AppendError(string.Join(Environment.NewLine, ex.Diagnostics));
            UpdateInfo();
        }
        catch (Exception ex)
        {
            _terminal.AppendError(ex.Message);
            UpdateInfo();
        }
        finally
        {
            _terminal.SetBusy(false);
            _terminal.FocusInput();
        }
    }

    /// <summary>
    /// Grid mostrando resultado de script: os filtros de cabeçalho perdem a
    /// premissa de que o grid espelha o arquivo, então ficam desativados até
    /// "Restaurar arquivo" (decisão de desenho V1).
    /// </summary>
    private void EnterScriptMode()
    {
        _scriptMode = true;
        _filterCts?.Cancel();
        _activeFilters.Clear();
        _btnClearFilters.Enabled = false;
        _btnRestore.Visible = true;
    }

    private async void BtnRestore_Click(object? sender, EventArgs e)
    {
        _scriptChain.Clear();
        await RestoreFileAsync();
    }

    private async Task RestoreFileAsync()
    {
        if (_parquetPath is null) return;

        try
        {
            _btnRestore.Enabled = false;
            _lblInfo.Text = "Recarregando arquivo...";

            var path = _parquetPath;
            var columns = _selectedColumns;
            var batch = await Task.Run(async () =>
            {
                using var df = await DataFrameProvider.GetDataFrameAsync(path, columns);
                return df.ToArrow();
            });

            DisplayBatch(batch);
            _scriptMode = false;
            _btnRestore.Visible = false;
            UpdateInfo();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Erro ao restaurar o arquivo:\n\n{ex.Message}",
                "Erro",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _lblInfo.Text = "Falha ao restaurar";
        }
        finally
        {
            _btnRestore.Enabled = true;
        }
    }

    private async void Grid_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (_batch is null || _parquetPath is null ||
            e.Button != MouseButtons.Left || e.ColumnIndex < 0)
            return;

        if (_scriptMode)
        {
            _lblInfo.Text = "Filtros de cabeçalho desativados no modo script — use \"Restaurar arquivo\"";
            return;
        }

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
        if (_parquetPath is null || _scriptMode) return;

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

    _lblInfo.Text = _scriptMode
        ? $"[script, passo {_scriptChain.Count}] {total:N0} linhas × {colunas} colunas"
        : _filteredRows is not null
            ? $"{exibindo:N0} de {total:N0} linhas × {colunas} colunas (filtrado)"
            : $"{total:N0} linhas × {colunas} colunas";
}

}

