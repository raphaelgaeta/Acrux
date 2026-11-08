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
dotnet run --project src/Acrux.Cli -- head arquivo.parquet -n 10   # CLI, roda no Linux
```

O CLI tem os comandos `schema`, `columns`, `head [-n N]`, `rows` e `type`.
Erros saem em uma linha com código 1 (`ACRUX_DEBUG=1` mostra a exceção
inteira); uso errado sai com código 2.

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
0.6.0 exige Arrow ≥ 23.0.0 — não fazer downgrade),
Microsoft.CodeAnalysis.CSharp.Scripting (terminal C#) e Microsoft.Data.Sqlite
(leitura de SQLite; traz o `e_sqlite3` nativo). Não há projeto de testes.

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
- **O Core roda no Linux** pelo `Acrux.Cli` (net10.0, nativo
  `Polars.NET.Native.linux-x64` 0.6.0), validado em degraus em 2026-11-07:
  1. o nativo carrega (`schema`, `columns`): **ok**;
  2. o caminho do grid (`head`: `GetDataFrameAsync` com `limit` → `ToArrow`
     → `PolarsTableAdapter.GetCellValue` célula a célula): **ok**. Achou
     tipos que o adaptador não conhecia, já corrigidos (ver Armadilhas);
  3. CSV cp1252 e vírgula decimal, xlsx, SQLite, temporários em
     `/tmp/Acrux`: **ok**;
  4. o terminal C# (`ScriptHost`/Roslyn) no Linux: **pendente**.

Próximos passos:

1. Degrau 4: um comando `query` no CLI que rode script pelo `ScriptHost`.
2. Com isso fechado, spike de grid virtualizada no Avalonia.

### Testar o app de janela no laptop (Wine)

O WinForms não roda nativo no Linux. No laptop, o app roda sob o Wine 11,
montado pela Yukidama em 2026-11-07; os fatos medidos estão em
`~/Yukidama/conhecimento/wine.md`. Configurar o laptop é trabalho da
Yukidama, não deste projeto.

- **Lançar:** pelo menu do Super+D (entrada "Acrux", em
  `~/.local/share/applications/acrux.desktop`) ou por `acrux-wine [arquivo]`
  (em `~/.local/bin`; prefixo em `~/.local/share/wineprefixes/acrux`). O
  argumento só abre o arquivo quando o `Main` aceitar um caminho, o que está
  pendente.
- **Abre a compilação mais nova**: a de mtime mais recente entre
  `~/Acrux-publish/Acrux.exe` e o publish sem `-o`
  (`src/Acrux.WinForms/bin/Release/net10.0-windows/win-x64/publish/`).
  `acrux-wine --qual` diz qual abriria, e por quê, sem abrir. `ACRUX_EXE`
  fixa outro executável.
- **Só o self-contained roda**: o prefixo não tem o .NET Desktop Runtime, e o
  lançador ignora o apphost leve do build framework-dependent, mesmo que seja
  mais novo. Refaça o publish a cada mudança. Um publish feito a partir de
  branch sem a `feat/brand` sai com o ícone padrão do WinForms.
- O ícone do menu é uma cópia local do SVG da `feat/brand`
  (`~/.local/share/icons/hicolor/scalable/apps/acrux.svg`). Ele não
  acompanha mudanças no SVG: repita o `git show` se a arte mudar. Por ser
  arte fora da MIT, não entra no espelho de dotfiles da Yukidama.
- O Polars (`native_shim.dll`) funciona sob o Wine: o parquet de teste
  carregou com os acentos certos.
- **Nunca desligue o `mscoree` para rodar .NET.** Com
  `WINEDLLOVERRIDES="mscoree=..."`, o runtime falha com "Could not load file
  or assembly System.Runtime.dll — módulo não encontrado". O lançador
  descarta overrides externos.
- **Prefixo novo exige o pacote `wine-common`** (já instalado). Sem ele, o
  `wineboot` entra em laço copiando `.winmd`: o `rundll32` fica a 99% de CPU e
  o processador chega a 100 °C, com ou sem tela.
- **Interromper um comando que chamou o wine não para o Wine.** Para parar:
  `WINEPREFIX=~/.local/share/wineprefixes/acrux wineserver -k`.
- **Esquisitices conhecidas:**
  - o seletor de colunas abre lado a lado no sway, em vez de flutuar;
  - a janela fica borrada na escala 1.5, porque passa pelo Xwayland;
  - o prefixo usa `Decorated=N`: sem isso, todo clique caía 26 px acima do
    alvo.
- Aparência e ícone ainda pedem Windows de verdade: o Wine valida
  funcionamento, não fidelidade visual.

## Arquitetura

Fluxo: `OpenFileDialog` (parquet, CSV, xlsx ou SQLite) → **o formato é
decidido pelo conteúdo, não pela extensão** (`DataFrameProvider.DetectFormat`
lê os bytes iniciais: `PAR1` = parquet, `SQLite format 3\0` = SQLite, ZIP com
`xl/workbook.xml` = xlsx, texto sem NUL ou com BOM UTF-16 = CSV; `.xls`
antigo, outros ZIPs e binários são recusados com mensagem clara) → xlsx e
SQLite têm "tabelas" a escolher (`GetTableNames`: abas via ZIP puro, lendo
`xl/workbook.xml`; tabelas e views de dados via `pragma_table_list`, sem
as virtuais e suas `shadow`; 2+ abrem o
`SheetSelectorForm`) → xlsx: a aba escolhida vira parquet temporário por
`DataFrame.ReadExcel` (motor calamine; materializa em RAM, limitado a ~1M
linhas pelo formato; **números chegam como double** — representação interna
do Excel, validado por probe) → SQLite: a tabela escolhida vira parquet
temporário por leitura tipada para Arrow + `FromArrow` + `WriteParquet`
(conexão só leitura, sem pool; materializa durante a conversão; ver
armadilha de tipagem abaixo) → CSV é convertido **uma única vez**
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
| `src/Acrux.Core/DataFrameProvider.cs` | Detecção de formato pelo conteúdo, conversão para parquet, leitura via LazyFrame |
| `src/Acrux.Core/FilterEngine.cs` | Filtros estilo Excel via Polars (distintos + máscara) |
| `src/Acrux.Core/PolarsTableAdapter.cs` | `GetCellValue`: leitura O(1) por célula do Arrow |
| `src/Acrux.Core/ScriptHost.cs` | Terminal C#: avaliação Roslyn de scripts Polars |
| `src/Acrux.WinForms/Program.cs` | Entrada do app: limpeza de temporários órfãos e `Environment.Exit(0)` |
| `src/Acrux.WinForms/MainForm.cs` | Grid virtual, carga do arquivo, costura dos filtros |
| `src/Acrux.WinForms/ColumnSelectorForm.cs` | Diálogo de seleção de colunas na abertura |
| `src/Acrux.WinForms/SheetSelectorForm.cs` | Diálogo de escolha da aba do xlsx ou da tabela do SQLite (2+) |
| `src/Acrux.WinForms/FilterPopupForm.cs` | Popup de filtro aberto pelo cabeçalho da coluna |
| `src/Acrux.WinForms/ScriptTerminalPanel.cs` | UI do terminal (painel inferior do MainForm) |
| `src/Acrux.Cli/Program.cs` | CLI: rota de leitura escolhida pelo formato detectado; `head` usa o caminho do grid |

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
- `PolarsSchema.ToDictionary()` **foi removido** → `ToFrozenDictionary()` para
  buscar por nome; **para listar colunas na ordem do arquivo, `ToList()`**
  (ver abaixo).
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
  (validado). Ainda assim não usar para filtrar em memória (princípio 1). O
  DataFrame **sobrevive ao `Dispose` do RecordBatch de origem**: validado
  com 20 rodadas de GC e realocação nativa, com somas e valores intactos.
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
- **Tipo de array sem `case` no `PolarsTableAdapter.GetCellValue` cai no
  `_ => array.ToString()`, e a célula mostra o *nome do tipo* do array.**
  Até 2026-11-07 isso acontecia com inteiros de 8/16 bits e sem sinal (o
  `u32` é o que o Polars devolve em `Count`/`Len`), decimal, binário/blob,
  lista, hora, duração e categórico (`DictionaryArray`). O degrau 2 do CLI
  achou, e a correção foi validada com um parquet de 20 tipos:
  - **inteiros:** o número;
  - **decimal:** `decimal`, ou o texto exato quando passa de 28 dígitos;
  - **binário:** `0x…`, até 32 bytes mais o tamanho total;
  - **lista:** `[1, 2]`, até 20 itens, cultura invariante, textos entre
    aspas;
  - **hora:** `HH:mm:ss.FFFFFFF` em texto (o `TimeOnly` padrão perde os
    segundos);
  - **duração:** `TimeSpan`;
  - **categórico:** o valor do dicionário;
  - **timestamp:** sem fuso é hora de parede, sem offset (antes parecia
    UTC); com fuso, convertido para o fuso da coluna.

  **Ordem dos `case`:** `StringArray` herda de `BinaryArray` (e o mesmo
  vale para View e Large), então texto vem antes de binário. Tipo novo:
  conferir com `acrux head` num parquet que o contenha.
- **O Polars 0.6.0 recusa timestamp com fuso em formato de offset**
  (`+05:30`), só aceita nome IANA (`America/Sao_Paulo`): dá panic já na
  leitura do parquet ("unable to parse time zone"). A exceção chega ao .NET
  e o processo não cai; contorno do próprio Polars:
  `POLARS_IGNORE_TIMEZONE_PARSE_ERROR=1`.
- `DataType.String` etc. seguem estáticos e **fora do XML doc** do pacote.
- Para validar APIs novas do pacote, use a skill `polars-probe`.
- **Leitura de banco via ADO.NET** (probe no Linux, 2026-11):
  `DataFrame.ReadDatabase(IDataReader, batchSize)` recebe um *leitor*, não
  um caminho, e lê tudo para Arrow (eager). **Não fecha o leitor**: o
  chamador faz `using`. `LazyFrame.ScanDatabase(Func<IDataReader>, …)` abre
  dois leitores (schema + dados) e fecha os dois, mas `SinkParquet` sobre ele
  **não faz streaming**: o motor novo dá panic "not yet implemented:
  AnonymousScan" no stderr e cai para escrita eager. A RAM é a mesma do
  `ReadDatabase`, e ainda há ruído. Para SQLite, nem o `ReadDatabase` serve:
  ele confia no tipo que o leitor informa (ver SQLite abaixo).
- **`PolarsSchema.ToFrozenDictionary()` não preserva a ordem das colunas**
  (validado: 40 colunas voltam embaralhadas). `ToList()` e `ToArrowSchema()`
  preservam. O `GetColumnNamesAsync` usou `ToFrozenDictionary().Keys` até
  2026-11-07, e por isso o seletor de colunas e o grid saíam embaralhados.
  Agora usa `ToList()`.

### SQLite (.db) — probe no Linux, 2026-11
- **`System.Data.SQLite` 2.0.4 não traz o SQLite nativo**: sozinho dá
  `DllNotFoundException: e_sqlite3`. Use `Microsoft.Data.Sqlite`, que traz o
  `e_sqlite3` para win/linux/osx via SQLitePCLRaw.
- **O leitor ADO.NET tipa cada coluna pela PRIMEIRA linha, e o SQLite aceita
  qualquer valor em qualquer coluna**: depois da 1ª linha, numa coluna
  INTEGER, `'abc'` vira 0 e `3.5` vira 3. Se a 1ª linha é NULL, vale o tipo
  declarado (INTEGER com textos → zeros) ou binary quando não há tipo
  declarado — **inclusive em `CAST(...)`**, que não tem tipo declarado. Tudo
  sem erro. Por isso o `ConvertSqlite` não usa `ReadDatabase`: ele decide o
  tipo pelas classes de armazenamento presentes na coluna
  (`group_concat(DISTINCT typeof(col))`, uma passada), lê com o getter
  daquele tipo para Arrow e entrega por `FromArrow`. Regras: só integer →
  i64; integer + real → f64; só blob → binary; qualquer mistura com texto →
  str; só NULL → tipo declarado. Medido: 300k linhas × 10 colunas em ~0,8 s.
- **O SQLite não tem tipo de data nem booleano**: data guardada em texto chega
  como str; BOOLEAN chega como i64 (0/1).
- **Detecção por conteúdo**: arquivo SQLite começa com `"SQLite format 3\0"`
  (16 bytes). A extensão `.db` não garante nada. Isso está no `DetectFormat`.
- **Índices de busca (FTS5) poluem a lista de tabelas**: num banco real (o
  `ofertas.db` do Corrente de Ofertas), a tabela virtual `busca` e as
  `shadow` dela (`busca_config`, `_data`, `_docsize`, `_idx`) vinham antes de
  `mensagens` em ordem alfabética, e a "primeira tabela" era o índice, com uma
  coluna só (`texto`). O `sqlite_master` não distingue esses casos; o
  `pragma_table_list` (SQLite 3.37+; o embutido é 3.53) marca o tipo como
  `virtual` ou `shadow`. Por isso o `GetSqliteTableNames` lista só
  `table`/`view` do schema `main`.
- **`ReadSqliteTablesAsync` devolve o banco inteiro como dicionário**
  (`OrderedDictionary<string, DataFrame>`: ordem garantida, tabelas antes das
  views, nomes sem diferenciar maiúsculas, como no SQLite). É para o CLI, que
  não pede `--table` ao operador; desenho do dono, 2026-11-07. Tudo vai para
  a RAM, o que serve para bancos de agente, mas não para bancos de GB. O app
  de janela continua no `EnsureParquetAsync`, tabela a tabela. O chamador
  descarta os DataFrames.
- **`ReadSqliteTablesToSingleDataFrameAsync` junta o banco num DataFrame só**
  (desenho do dono): é o equivalente do `pd.concat(dict)` do pandas, que lá
  gera um MultiIndex. A coluna `tableName` vem primeiro, e as colunas são
  unidas por `ConcatDiagonal` (nulo onde a tabela não tem a coluna). Tabela
  sem linhas entra só com as colunas. **A mesma coluna com tipos diferentes em
  tabelas diferentes derruba o concat** ("failed to vstack column"; validado),
  por isso ela é convertida antes para um tipo comum: Float64 se todos os
  lados são numéricos, senão String. É o que o CLI usa para `.db`
  (`LoadAsync`/`ColumnNamesAsync` em `Acrux.Cli/Program.cs`); os outros
  formatos seguem pelo parquet, e `columns`/`schema` leem só o cabeçalho.

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
- `gh` instalado e autenticado (conta `raphaelgaeta`, HTTPS): push e
  `gh pr create` funcionam direto do terminal.
- Fluxo de trabalho do dono do projeto: ele valida o desenho antes do código.
  Para features novas, apresente a arquitetura/avaliação primeiro e aguarde o
  aval antes de implementar.
