# PolarsGridViewer

Visualizador WinForms de arquivos .parquet: Polars.NET como motor de dados,
`DataGridView` em modo virtual como apresentação. Otimizado para arquivos
grandes (655+ colunas, milhões de linhas).

## Build e execução

```
dotnet build PolarsGridViewer.csproj
dotnet run --project PolarsGridViewer.csproj
```

.NET 8 (net8.0-windows), WinForms. Pacotes: Polars.NET 0.4.0 (+ Native.win-x64,
Linq) e Apache.Arrow.

## Arquitetura

Fluxo: `OpenFileDialog` → `DataFrameProvider.GetColumnNamesAsync` (só schema,
via `LazyFrame.ScanParquet`) → `ColumnSelectorForm` (usuário escolhe colunas) →
`GetDataFrameAsync` (`Select` + `Collect`, projection pushdown) → `ToArrow()` →
grid virtual.

| Arquivo | Papel |
|---|---|
| `MainForm.cs` | Grid virtual, carga do arquivo, costura dos filtros |
| `DataFrameProvider.cs` | Leitura do parquet via LazyFrame |
| `ColumnSelectorForm.cs` | Diálogo de seleção de colunas na abertura |
| `FilterEngine.cs` | Filtros estilo Excel via Polars (distintos + máscara) |
| `FilterPopupForm.cs` | Popup de filtro aberto pelo cabeçalho da coluna |
| `PolarsTableAdapter.cs` | `GetCellValue`: leitura O(1) por célula do Arrow |

### Princípios de design (não violar)

1. **Zero materialização**: os dados vivem só no `RecordBatch` Arrow
   (`_batch`). Células são lidas por demanda em `CellValueNeeded` via
   `PolarsTableAdapter.GetCellValue`. NUNCA converter colunas inteiras em
   `object[]` — com milhões de linhas isso esgota a RAM e trava a máquina.
2. **Indireção de índices**: filtros (e futura ordenação) produzem `int[]`
   de índices (`_filteredRows`), nunca cópias dos dados. Recalculados sempre
   a partir do original + conjunto de filtros ativos (`_activeFilters`),
   nunca encadeados sobre resultado anterior.
3. **Consultas de filtro re-escaneiam o arquivo** via `LazyFrame.ScanParquet`
   (projection pushdown; dezenas de ms em 5M de linhas). Não tentar filtrar
   "em memória" — ver armadilha do FromArrow abaixo.
4. O `DataFrame` do Polars é descartado logo após `ToArrow()` (a posse dos
   buffers é transferida; verificado por teste).

## Armadilhas conhecidas (custaram debugging real)

### Polars.NET 0.4.0
- `DataFrame.FromArrow(RecordBatch)` **derruba o processo** (crash nativo
  `AccessViolationException`). Não usar.
- `Expr.IsIn` exige `IsIn(Lit(Series.From("w", valores)).Implode())` — sem
  `.Implode()` há aviso de deprecação do is_in.
- Strings chegam como `StringViewArray` (não `StringArray`) no Arrow.
- `DataType.String` etc. são propriedades estáticas que **não aparecem no
  XML doc** do pacote; não há conversão implícita de `DataTypeKind`.
- Não existem `WithRowIndex`/`ArgWhere` nesta versão: para obter índices de
  linhas, colete uma coluna de máscara booleana e varra em C#.
- Para validar APIs novas do pacote, use a skill `polars-probe`.

### DataGridView (modo virtual)
- **Diminuir `RowCount` remove linhas uma a uma** (minutos com milhões de
  linhas). Caminho rápido: `Rows.Clear()` (O(1)) e então setar o novo valor.
- `FillWeight` padrão é 100 com soma máxima 65535 → mais de ~655 colunas
  estoura. Toda coluna criada recebe `FillWeight = 1`.
- Colunas do grid: `Name` é sempre o nome real da coluna do parquet;
  `HeaderText` pode ganhar sufixo " ▼" quando há filtro ativo.

## Convenções

- Comunicação com o usuário em **português (PT-BR)**; código e mensagens de
  commit em inglês.
- Remote do git chama-se `ParquetGridViewer` (não `origin`); trabalho e push
  na branch `master`: `git push ParquetGridViewer master`.
- `BuildSecret.cs` (na raiz, gitignored) contém credenciais de uma integração
  SharePoint removida — nunca commitar nem deletar sem confirmação.
- Fluxo de trabalho do dono do projeto: ele valida o desenho antes do código.
  Para features novas, apresente a arquitetura/avaliação primeiro e aguarde o
  aval antes de implementar.
