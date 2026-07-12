# Acrux

Visualizador WinForms de arquivos .parquet e .csv: Polars.NET como motor de
dados, `DataGridView` em modo virtual como apresentação. Otimizado para
arquivos grandes (655+ colunas, milhões de linhas). O nome vem de α Crucis,
a estrela mais brilhante do Cruzeiro do Sul (o repositório já se chamou
ParquetGridViewer e o projeto, PolarsGridViewer).

## Build e execução

```
dotnet build Acrux.csproj
dotnet run --project Acrux.csproj
```

.NET 10 (net10.0-windows), WinForms. Pacotes: Polars.NET 0.6.0 (+ Native.win-x64,
Linq, ML), Apache.Arrow 23 (a 0.6.0 exige Arrow ≥ 23.0.0 — não fazer downgrade)
e Microsoft.CodeAnalysis.CSharp.Scripting (terminal C#). Não há projeto de testes.

Publicação: WinForms **não suporta trimming** — nunca publicar com
`PublishTrimmed=true` (erro NETSDK1175). Single-file self-contained funciona,
mas o terminal C# **exige** `IncludeAllContentForSelfExtract=true` (já setado
no csproj — não remover): a API de scripting do Roslyn resolve referências,
inclusive o corlib internamente, via `Assembly.Location`, vazio em single-file
puro (dotnet/roslyn#50719). Sintoma: todo script falha com "type initializer
for ScriptHost"/`NotSupportedException` de referência de metadados. Não há
contorno por código (referências via `TryGetRawMetadata` resolvem as
explícitas, mas não a do corlib interno) — validado por harness em 2026-07.

O csproj traz tuning intencional de GC para desktop (workstation concurrent +
DATAS via `GarbageCollectionAdaptationMode=1`) — não reverter sem motivo.

O `README.md` (em inglês, voltado ao público do repositório) resume features e
arquitetura; este arquivo segue sendo a fonte de verdade técnica — ao mudar
comportamento visível, atualizar os dois.

## Arquitetura

Fluxo: `OpenFileDialog` (parquet ou CSV) → CSV é convertido **uma única vez**
para parquet temporário (`DataFrameProvider.EnsureParquetAsync`: valida UTF-8
por amostra e transcodifica cp1252/UTF-16 se preciso — ver armadilha abaixo —,
detecção de separador na 1ª linha, `decimalComma` quando `;`, `ScanCsv` →
`SinkParquet` streaming; temp em `%TEMP%\Acrux`, apagado na
troca/fechamento) —
o app opera **somente sobre parquet** daí em diante, preservando o re-scan
barato → `GetColumnNamesAsync` (só schema) → `ColumnSelectorForm` (usuário
escolhe colunas) → `GetDataFrameAsync` (`Select` + `Collect`, projection
pushdown) → `ToArrow()` → grid virtual.

Fluxo de filtro: clique no cabeçalho → `FilterEngine.GetDistinctValuesAsync`
(semântica Excel: aplica os filtros das *outras* colunas antes de coletar os
distintos; dropdown limitado a `DistinctCap = 10.000`) → `FilterPopupForm` →
`GetVisibleRowsAsync` (máscara booleana varrida em C#) → `_filteredRows`.
Filtros concorrentes são cancelados via `_filterCts`.

Terminal C# (botão "C# terminal"): scripts Roslyn encadeados por **replay** —
`MainForm._scriptChain` guarda os textos dos passos; cada execução re-roda a
cadeia inteira sobre um `ScanParquet` fresco, com o passo N recebendo em `lf`
o `LazyFrame` (plano lazy, não dados) do passo N-1. O passo 1 recebe o arquivo
**como exibido no grid**: seleção de colunas + filtros ativos aplicados como
"passo 0" implícito (`FilterEngine.BuildPredicate` público é reutilizado).
Esse estado visual é **congelado no 1º passo** (`_chainBaseColumns`/
`_chainBaseFilters`) — obrigatório, pois o replay re-executa tudo a cada
comando e a base não pode derivar; "Restore file"/novo arquivo zeram a base.
Os `_activeFilters` NÃO são mais limpos ao entrar em modo script: sobrevivem
como base da cadeia e são **reaplicados ao restaurar**. Nada é coletado no meio:
a cadeia compõe um único plano, coletado só no fim (`Limit(ScriptHost.RowCap)`
opcional) → `ToArrow` → `DisplayBatch` (caminho único de exibição). Passo que
termina em `DataFrame` (ex.: `.Collect()`) volta ao lazy via `df.Lazy()`
(validado; os dados dele ficam materializados no plano durante o replay).
Um passo com erro não entra na cadeia. "Undo step" remove o último e
re-executa; "Restore file" zera a cadeia. Imports do script:
`Polars.CSharp` + estáticos (`Col`/`Lit`). Em modo script (`_scriptMode`) os
filtros de cabeçalho ficam desativados — o grid não espelha mais o arquivo —
até "Restore file" (decisão V1; a V2 composável exigiria FilterEngine
aceitar fonte lazy genérica).

| Arquivo | Papel |
|---|---|
| `MainForm.cs` | Grid virtual, carga do arquivo, costura dos filtros |
| `DataFrameProvider.cs` | Leitura do parquet via LazyFrame |
| `ColumnSelectorForm.cs` | Diálogo de seleção de colunas na abertura |
| `FilterEngine.cs` | Filtros estilo Excel via Polars (distintos + máscara) |
| `FilterPopupForm.cs` | Popup de filtro aberto pelo cabeçalho da coluna |
| `PolarsTableAdapter.cs` | `GetCellValue`: leitura O(1) por célula do Arrow |
| `ScriptHost.cs` | Terminal C#: avaliação Roslyn de scripts Polars |
| `ScriptTerminalPanel.cs` | UI do terminal (painel inferior do MainForm) |

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
   "em memória": mesmo com `FromArrow` funcionando na 0.6.0, isso criaria uma
   segunda cópia dos dados em RAM, violando o princípio 1.
4. O `DataFrame` do Polars é descartado logo após `ToArrow()` (a posse dos
   buffers é transferida; verificado por teste).

## Armadilhas conhecidas (custaram debugging real)

### Polars.NET 0.6.0 (upgrade da 0.4.0 validado por probe em 2026-07)
- `PolarsSchema.ToDictionary()` **foi removido** → usar `ToFrozenDictionary()`.
- **Inferência de schema do CSV é por amostra** (default 100 linhas): int que
  vira float depois da amostra → "could not parse `8.1` as dtype `i64`" no
  sink. `inferSchemaLength: null` NÃO significa "arquivo todo" (cai no
  default, diferente do Python) e `ulong.MaxValue` dá "capacity overflow"
  nativo. Estratégia do app (EnsureParquetAsync): amostra de 10k (~235 ms em
  104 MB) + retry com 1M (~15 s) só quando falha. `decimalComma` errado NÃO
  dá erro — a coluna vira string silenciosamente; por isso a convenção
  decimal é farejada nos dados e, no retry, o valor ofensor da mensagem de
  erro pode invertê-la. O sink que falha imprime ruído no stderr
  ("Attempting fallback to Eager Write") e deixa parquet vazio — apagar.
- **O leitor CSV do Polars exige UTF-8 estrito**: CSV "ANSI" do Excel
  (Windows-1252, o formato do "Salvar como CSV" sem UTF-8) falha com
  "invalid utf-8 sequence". O enum `CsvEncoding` só tem `UTF8` e `LossyUTF8`,
  e o lossy **troca acentos por �** (validado) — não usar. A solução é a
  transcodificação streaming em `EnsureParquetAsync` (cp1252 sem BOM,
  UTF-16 via BOM). No net10.0 o `CodePagesEncodingProvider` já vem no
  framework — o pacote `System.Text.Encoding.CodePages` é desnecessário
  (aviso NU1510 se adicionado).
- **`Collect()` consome o handle do `LazyFrame`** (o 2º parâmetro bool do
  `Collect(Engine, bool)` permite reuso): acessar `Schema` ou qualquer membro
  do LazyFrame após o `Collect` dá `PolarsException` "Handle is invalid".
  Ler o `Schema` antes de coletar.
- `DataFrame.FromArrow(RecordBatch)` crashava na 0.4.0; na 0.6.0 **funciona**
  (validado). Ainda assim não usar para filtrar em memória (princípio 1).
- `Expr.IsIn`: o estilo antigo `IsIn(Lit(Series.From(...)).Implode())`
  continua funcionando; a 0.6.0 tem overload direto `IsIn(IEnumerable<T>)`
  (validado, mesma semântica) — simplificação disponível para o futuro.
- `WithRowIndex`/`ArgWhere` **agora existem** (índices como `UInt32Array`,
  validados) e poderiam substituir a varredura de máscara booleana no
  `FilterEngine` — mudança de desenho, requer aval do dono.
- `LazyFrame.Collect(Engine)`: enum `[Auto, InMemory, Streaming, Gpu]`.
  **`Engine.Gpu` no Windows é fallback silencioso para CPU** (validado em
  máquina com RTX 4070: nenhuma DLL CUDA carregada, tempos idênticos à CPU;
  nunca lança erro, então try/catch NÃO detecta). A engine GPU do Polars é
  o cudf-polars, camada Python sobre libcudf, Linux/WSL2 apenas — não existe
  no núcleo Rust que o Polars.NET embrulha. Benchmark no arquivo real
  (700col×5M, cache quente): `Auto`/`InMemory`/`Streaming` empatam em carga
  com projeção, máscara de filtro e groupby (diferenças = ruído de dezenas
  de ms); a única vitória consistente é `Streaming` ~2× em `Unique+Sort+
  Limit` — por isso só `GetDistinctValuesAsync` usa `Collect(Engine.
  Streaming)`. Não criar seletor de engine sem novo dado que o justifique.
- Strings continuam chegando como `StringViewArray` (não `StringArray`).
- `DataType.String` etc. seguem estáticos e **fora do XML doc** do pacote.
- Para validar APIs novas do pacote, use a skill `polars-probe`.

### DataGridView (modo virtual)
- **Diminuir `RowCount` remove linhas uma a uma** (minutos com milhões de
  linhas). Caminho rápido: `Rows.Clear()` (O(1)) e então setar o novo valor.
- `FillWeight` padrão é 100 com soma máxima 65535 → mais de ~655 colunas
  estoura. Toda coluna criada recebe `FillWeight = 1`.
- Colunas do grid: `Name` é sempre o nome real da coluna do parquet;
  `HeaderText` pode ganhar sufixo " ▼" quando há filtro ativo.

## Convenções

- Comunicação com o usuário em **português (PT-BR)**; código, comentários,
  strings de GUI e mensagens de commit em **inglês** (repo público).
  Comentários enxutos: explicar o *porquê*/a restrição, nunca narrar a linha.
- Trabalho e push na branch `master`. Confira o nome do remote com
  `git remote -v` antes do push (neste clone é `origin`; já se chamou
  `ParquetGridViewer` em outro checkout).
- Fluxo de trabalho do dono do projeto: ele valida o desenho antes do código.
  Para features novas, apresente a arquitetura/avaliação primeiro e aguarde o
  aval antes de implementar.
