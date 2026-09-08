# Acrux

Visualizador WinForms de arquivos .parquet, .csv e .xlsx: Polars.NET como motor
de dados, `DataGridView` em modo virtual como apresentação. Otimizado para
arquivos grandes (655+ colunas, milhões de linhas). O nome vem de α Crucis,
a estrela mais brilhante do Cruzeiro do Sul (o repositório já se chamou
ParquetGridViewer e o projeto, PolarsGridViewer).

## Build e execução

```
dotnet build                              # solução inteira (Acrux.sln), da raiz
dotnet run --project src/Acrux.WinForms
```

Dois projetos sob `src/`, agregados pelo `Acrux.sln` da raiz: `Acrux.Core`
(net10.0, sem UI — `DataFrameProvider`, `FilterEngine`, `PolarsTableAdapter`,
`ScriptHost`) e `Acrux.WinForms` (net10.0-windows, WinForms, `AssemblyName`
mantido como `Acrux`, referencia o Core). `dotnet build` na raiz basta, mas
**`dotnet run` e `dotnet publish` exigem apontar o projeto**: na raiz o `run`
não acha projeto ("Couldn't find a project to run") e o `publish` falha com
**NETSDK1099**, porque tenta aplicar single-file também no Acrux.Core, que é
biblioteca. O `Directory.Build.props` da raiz liga `EnableWindowsTargeting`:
baixa os reference assemblies do Windows e permite **compilar** contra a API do
Windows a partir do Linux — o binário resultante continua rodando só no
Windows.

Pacotes: Polars.NET 0.6.0 (+ Native.win-x64, Linq, ML), Apache.Arrow 23 (a
0.6.0 exige Arrow ≥ 23.0.0 — não fazer downgrade) e
Microsoft.CodeAnalysis.CSharp.Scripting (terminal C#). Não há projeto de testes.

Publicação — sempre com o caminho do `.csproj` (ver NETSDK1099 acima):

```
dotnet publish src/Acrux.WinForms/Acrux.WinForms.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

WinForms **não suporta trimming** — nunca publicar com
`PublishTrimmed=true` (erro NETSDK1175). Single-file self-contained funciona,
mas o terminal C# **exige** `IncludeAllContentForSelfExtract=true` (já setado
no csproj — não remover): a API de scripting do Roslyn resolve referências,
inclusive o corlib internamente, via `Assembly.Location`, vazio em single-file
puro (dotnet/roslyn#50719). Sintoma: todo script falha com "type initializer
for ScriptHost"/`NotSupportedException` de referência de metadados. Não há
contorno por código (referências via `TryGetRawMetadata` resolvem as
explícitas, mas não a do corlib interno) — validado por harness em 2026-07.

O `Acrux.WinForms.csproj` traz tuning intencional de GC para desktop
(workstation concurrent + DATAS via `GarbageCollectionAdaptationMode=1`) —
não reverter sem motivo.

O `README.md` (em inglês, voltado ao público do repositório) resume features e
arquitetura; este arquivo segue sendo a fonte de verdade técnica — ao mudar
comportamento visível, atualizar os dois.

## Porte multiplataforma (em andamento)

Onde estamos:

- `Acrux.Core` já é net10.0 puro, sem dependência de WinForms; só o
  `Acrux.WinForms` é net10.0-windows.
- Os dois projetos **compilam** no Linux (`EnableWindowsTargeting`).
- Nada foi **executado** no Linux ainda — nem o Core, nem o nativo do Polars.
  O único pacote nativo referenciado é `Polars.NET.Native.win-x64`.

Próximos passos:

1. `Acrux.Cli` (console net10.0) para validar o carregamento do nativo do
   Polars no Fedora.
2. Com isso fechado, spike de grid virtualizada no Avalonia.

## Arquitetura

Fluxo: `OpenFileDialog` (parquet, CSV ou xlsx) → xlsx: abas enumeradas via
ZIP puro (`GetExcelSheetNames` lê `xl/workbook.xml`; 2+ abas abrem o
`SheetSelectorForm`) e a aba escolhida vira parquet temporário por
`DataFrame.ReadExcel` (motor calamine; materializa em RAM, limitado a ~1M
linhas pelo formato; **números chegam como double** — representação interna
do Excel, validado por probe) → CSV é convertido **uma única vez**
para parquet temporário (`DataFrameProvider.EnsureParquetAsync`: valida UTF-8
por amostra e transcodifica cp1252/UTF-16 se preciso — ver armadilha abaixo —,
detecção de separador na 1ª linha, `decimalComma` quando `;`, `ScanCsv` →
`SinkParquet` streaming; temp em `%TEMP%\Acrux`, apagado na
troca/fechamento) —
o app opera **somente sobre parquet** daí em diante, preservando o re-scan
barato → `GetColumnNamesAsync` (só schema) → `ColumnSelectorForm` (usuário
escolhe colunas) → `GetDataFrameAsync` (`Select` + `Collect`, projection
pushdown) → `ToArrow()` → grid virtual.

Ciclo de vida do processo: `Program.Main` termina com `Environment.Exit(0)`
(garantia contra threads estrangeiras — Polars/rayon, Roslyn — segurarem um
processo fantasma; relato de campo em 2026-07, não reproduzido em matriz de
10 cenários, belt preventivo). Temporários levam o PID no nome
(`_pid{N}_`) e `CleanStaleTempFiles()` varre órfãos de execuções mortas na
inicialização, pulando PIDs vivos (multi-instância seguro).

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
| `src/Acrux.Core/DataFrameProvider.cs` | Leitura do parquet via LazyFrame |
| `src/Acrux.Core/FilterEngine.cs` | Filtros estilo Excel via Polars (distintos + máscara) |
| `src/Acrux.Core/PolarsTableAdapter.cs` | `GetCellValue`: leitura O(1) por célula do Arrow |
| `src/Acrux.Core/ScriptHost.cs` | Terminal C#: avaliação Roslyn de scripts Polars |
| `src/Acrux.WinForms/Program.cs` | Entrada do app: limpeza de temporários órfãos e `Environment.Exit(0)` |
| `src/Acrux.WinForms/MainForm.cs` | Grid virtual, carga do arquivo, costura dos filtros |
| `src/Acrux.WinForms/ColumnSelectorForm.cs` | Diálogo de seleção de colunas na abertura |
| `src/Acrux.WinForms/SheetSelectorForm.cs` | Diálogo de escolha da aba do xlsx (2+ abas) |
| `src/Acrux.WinForms/FilterPopupForm.cs` | Popup de filtro aberto pelo cabeçalho da coluna |
| `src/Acrux.WinForms/ScriptTerminalPanel.cs` | UI do terminal (painel inferior do MainForm) |

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

### Datasets multi-arquivo (probe em 2026-09, Polars.NET 0.6.0)
- `ScanParquet` aceita **um único caminho `string`** — não há sobrecarga para
  lista de arquivos. Glob (`dir/*.parquet`) e diretório funcionam; **diretório
  recursa, glob simples não** (use `dir/**/*.parquet` para recursivo).
- **Ler `lf.Schema` sobre glob lê o rodapé de TODOS os arquivos**: ~24 ms por
  arquivo, linear (100 → 2,4 s; 1.000 → 24,4 s; 6.946 → 221 s). Mitigação
  obrigatória: ler o schema de **um** shard (~46 ms) e repassá-lo no parâmetro
  `schema:` — os mesmos 1.000 arquivos caem para **17,9 ms**.
- **Divergência de schema não é detectada na abertura.** O schema exposto é o
  do primeiro arquivo; o erro só estoura no `Collect`, depois de o usuário já
  ter escolhido colunas. Planejar a mensagem de erro para esse momento.
- `allowMissingColumns: true` resolve **coluna faltando** (preenche null).
  **Coluna extra não tem escape na 0.6.0**: falha mesmo com esse flag e mesmo
  projetando só as colunas conhecidas (o erro ocorre ao abrir o arquivo, antes
  da projeção). O nativo sugere `extra_columns='ignore'`, **não exposto** pela
  API C# — checar se a 0.7.0 expõe. Tipo divergente na mesma coluna também
  falha, sem unificação por supertipo.
- `LazyFrame.Concat(frames, ConcatType.Diagonal)` **unifica** schemas (união de
  colunas, null onde falta); `Vertical` exige schemas idênticos e `Horizontal`
  dá erro de coluna duplicada. Diagonal custa N handles nativos e perde o
  ganho do `schema:` — ver `docs/adr/001-datasets-multi-arquivo.md`.
- **`Len()` retorna `u32`** (teto de 4,29 bilhões de linhas). A contagem usa só
  metadados, mas toca um arquivo por vez: 6.946 shards frios = 113,8 s.

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
- Idioma dos documentos: `README.md` em **inglês** — é a vitrine pública do
  repositório; `CLAUDE.md` e `docs/` em **português**, são documentação
  interna.
- Comentário explica o **porquê**, nunca o **quê**. O modelo é o comentário do
  `Acrux.WinForms.csproj` sobre `IncludeAllContentForSelfExtract`: ele não
  descreve o que a propriedade faz — diz por que ela existe e aponta a causa
  raiz (`dotnet/roslyn#50719`). Comentário que narra a linha é ruído.
- Assuma **leitor de C# intermediário**: prefira código explícito a construção
  idiomática compacta. Ao usar algo avançado (`Span`, async streams, `unsafe`,
  LINQ denso), um comentário explica o que motivou a escolha.
- Decisões estruturais viram **ADR** em `docs/adr/`, numerados e curtos:
  contexto, alternativas consideradas, escolha e justificativa.
- Trabalho e push na branch `master`. Confira o nome do remote com
  `git remote -v` antes do push (neste clone é `origin`; já se chamou
  `ParquetGridViewer` em outro checkout).
- `gh` instalado e autenticado (conta `raphaelgaeta`, HTTPS): push e
  `gh pr create` funcionam direto do terminal.
- Fluxo de trabalho do dono do projeto: ele valida o desenho antes do código.
  Antes de implementar qualquer coisa não trivial, apresente o desenho (ou o
  ADR) e **espere aprovação** — não comece pelo código.
