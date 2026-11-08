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

O CLI tem os comandos `schema` (nome e tipo do Polars de cada coluna, uma
por linha), `columns`, `head [-n N]`, `rows`, `type`,
`column <arquivo> <nome> [-n N]` (padrão: 10 linhas; num `.db`, procura a
coluna em todas as tabelas) e `filter <arquivo> <coluna> --last <período>
[-n N]` (no espírito do `journalctl --since`: unidades do systemd, como
`30min`, `1h 30min`, `2 days`, `1M` = mês; a janela termina agora, então
linhas com data no futuro ficam de fora — numa coluna só de data, o dia de
hoje entra inteiro; da linha mais recente para a mais antiga; mostra as
linhas e quantas casaram).
**Num `.db`, cada comando responde por tabela** (`schema`, `columns`,
`rows`, `head`, `column`, `filter`): somar ou unir tabelas diferentes dá
números sem sentido, e a mesma coluna pode ter tipos diferentes em cada
tabela. O `schema` e o `rows` de parquet leem só os metadados, sem os dados.
Erros saem em uma linha com código 1 (`ACRUX_DEBUG=1` mostra a exceção
inteira); uso errado sai com código 2. `acrux version` diz qual commit está
instalado (ver "Deploy de teste do CLI" abaixo).

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

Pacotes: Polars.NET 1.0.1 (+ Native.win-x64/linux-x64, Linq, ML), Apache.Arrow
23 (a 1.0.1 exige Arrow ≥ 23.0.0 — não fazer downgrade),
Microsoft.CodeAnalysis.CSharp.Scripting (terminal C#) e Microsoft.Data.Sqlite
(leitura de SQLite; traz o `e_sqlite3` nativo). Não há projeto de testes; o
CLI tem um smoke test em `tests/smoke` (ver "Deploy de teste do CLI").

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
- Os três projetos **compilam** no Linux (`EnableWindowsTargeting`).
- **O Core roda no Linux** pelo `Acrux.Cli` (net10.0), validado em degraus
  em 2026-11-07:
  1. o nativo carrega (`schema`, `columns`): **ok**;
  2. o caminho do grid (`head`: `GetDataFrameAsync` com `limit` → `ToArrow`
     → `PolarsTableAdapter.GetCellValue` célula a célula): **ok**. Achou
     tipos que o adaptador não conhecia, já corrigidos (ver Armadilhas);
  3. CSV cp1252 e vírgula decimal, xlsx, SQLite, temporários em
     `/tmp/Acrux`: **ok**;
  4. o terminal C# (`ScriptHost`/Roslyn) no Linux: **pendente**.
- O `Acrux.Cli` escolhe o nativo do Polars **pela máquina que compila**:
  `Polars.NET.Native.win-x64` no Windows, `.linux-x64` no Linux
  (`$([MSBuild]::IsOSPlatform(...))`, vindo do protótipo de 2026-09-08). A
  condição olha a máquina, não o RID do publish: publish para outro sistema
  exige escolha explícita. As versões managed e nativa precisam ser
  idênticas: divergência dá segfault em runtime, não erro de build.

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
  carregou com os acentos certos (0.6.0). Com a **1.0.1**, o dono repetiu o
  roteiro dos GIFs no parquet de 700 colunas × 5M em 2026-11-08: abrir,
  escolher colunas, filtro de cabeçalho e terminal C# (Roslyn). Tudo
  funcionou.
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
  funcionamento, não fidelidade visual. **Os GIFs do README também**: a
  interface sob o Wine fica estranha demais para gravar (decisão do dono,
  2026-11-08). Regravar no Windows quando precisar.

### Deploy de teste do CLI no laptop

Cada commit publica o CLI sozinho, e o comando `acrux` passa a ser o último
commit que compilou e passou no smoke test (desenho aprovado em 2026-11-08).

- **Hook:** `.githooks/post-commit` chama `scripts/deploy-cli.sh` em segundo
  plano (`setsid`), e o commit volta na hora. Hooks não vêm no clone: cada
  clone liga uma vez com `git config core.hooksPath .githooks` (este já
  está ligado). Numa branch sem `.githooks/`, nada acontece.
  `ACRUX_NO_DEPLOY=1 git commit …` pula o deploy.
- **O script publica o commit, não o disco:** `git archive` do commit →
  `dotnet publish` Release linux-x64 framework-dependent (~3 s) →
  `~/.local/share/acrux-cli/<commit>/` → `tests/smoke/run.sh` no binário
  novo → só então troca o link `current`, e o anterior vira `previous`.
  Sobram só essas duas pastas (~170 MB cada; 135 MB são o
  `libnative_shim.so` do Polars). Uma rajada de commits (rebase) entra em
  fila numa trava (`flock`) e publica só o mais novo. Build ou teste que
  falha não troca nada: o `current` continua o anterior.
- **Log:** `~/.local/share/acrux-cli/deploy.log` (cortado para 500 linhas
  quando passa de 1.000). Uma falha não avisa na tela: **confira com
  `acrux version`** se o commit esperado está no ar.
- **Voltar atrás:** `cd ~/.local/share/acrux-cli && ln -sfn "$(readlink
  previous)" current`. Publicar um commit à mão:
  `scripts/deploy-cli.sh <commit>`.
- **Comando no PATH:** `~/.local/bin/acrux` é um symlink para
  `~/.local/share/acrux-cli/current/Acrux.Cli`, criado pela Yukidama em
  2026-11-08 (a pasta `~/.local/bin` é dela). Ele fica fora do espelho de
  dotfiles, na categoria `[acrux]` do `dotfiles/.ignorar`. Este projeto
  cuida de `~/.local/share/acrux-cli`, e **o `current` nunca pode ficar sem
  alvo**: sem ele, `acrux` vira "comando não encontrado", sem mensagem. O
  script garante isso de duas formas: troca o link por `mv`, de uma vez, e a
  limpeza poupa `current` e `previous`.
- **Smoke test** (`tests/smoke/run.sh <binário> [--update]`): roda todos os
  comandos sobre fixtures pequenos e compara a saída, com os códigos de
  saída, com `expected.txt`. São CSV com `;` e vírgula decimal, CSV
  americano (`mm/dd` com evidência e uma coluna ambígua), cp1252 gerado na
  hora, parquet com 20 tipos, xlsx com duas abas e um `.db` montado de
  `banco.sql` (tipos misturados, NULL na 1ª linha, nome com espaço, view,
  tabela vazia, data com fuso e uma data no futuro), além de arquivos com
  extensão trocada e os erros. Fixa `TZ=America/Sao_Paulo` e mascara o
  horário do cabeçalho do `filter` e a pasta temporária. **Mudou a saída de
  propósito? `--update` e revise o `git diff` do `expected.txt` antes do
  commit**: ele é o que o teste toma como certo. Os binários (`tipos.parquet`,
  `planilha.xlsx`) saem de `fixtures/make_binary.py` (precisa de pyarrow); o
  `.gitignore` libera `tests/smoke/fixtures/*`. Nada de dado real ali: o
  repositório é público.

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
`SinkParquet` streaming com `tryParseDates` **desligado**, e depois
`TextDates.ConvertParquet` reconhece as datas em texto; temp em
`%TEMP%\Acrux`, apagado na troca/fechamento) —
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
| `src/Acrux.Core/TextDates.cs` | Datas guardadas como texto (CSV, SQLite) viram data só quando não há ambiguidade |
| `src/Acrux.Core/TimeFilter.cs` | Filtro de tempo relativo: lê períodos no padrão do systemd e monta o predicado por tipo de coluna |
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
   "em memória": mesmo com `FromArrow` funcionando desde a 0.6.0, isso criaria uma
   segunda cópia dos dados em RAM, violando o princípio 1.
4. O `DataFrame` do Polars é descartado logo após `ToArrow()` (a posse dos
   buffers é transferida; verificado por teste).

## Armadilhas conhecidas (custaram debugging real)

### Polars.NET 1.0.1 (0.4.0 → 0.6.0 em 2026-07, por probe; → 1.0.1 em 2026-11-08)
- **A troca 0.6.0 → 1.0.1 foi medida antes e depois** (branch
  `refactor/polars-1.0.1`): o smoke test deu a mesma saída, e uma bateria de
  19 testes de estresse (CLI e caminhos da janela no parquet de 700 colunas ×
  5M, CSV de 2M, xlsx, SQLite) deu **os mesmos tempos (±12%) e de 18% a 74%
  menos memória**. Causa provável, não confirmada: a 1.0.0 tirou o mimalloc.
  Memória medida como pico de `RssAnon`: o RSS conta as páginas do parquet
  mapeado (mmap), que o kernel descarta à vontade, e passa de 8 GB num teste
  que usa 0,7 GB de fato. Cada teste rodou num escopo
  `systemd-run --user --scope -p MemoryMax=8G -p MemorySwapMax=0`, que mata
  o processo em vez de deixar o laptop entrar em swap.
- `PolarsSchema.ToDictionary()` **foi removido** → `ToFrozenDictionary()` para
  buscar por nome; **para listar colunas na ordem do arquivo, `ToList()`**
  (ver abaixo).
- **Comparar datas: pelo instante, não pelo literal.** Uma coluna com fuso
  (`datetime[μs, UTC]`) contra um `Lit(DateTime)` sem fuso dá erro no
  Polars. O `TimeFilter.Between` compara `Col(c).Dt.Timestamp(TimeUnit.
  Microseconds)` (microssegundos desde 1970) com dois `Lit(long)`, início e
  fim, calculados conforme o tipo: com fuso, o instante real; sem fuso, a
  hora de parede local (como o grid mostra); data, a meia-noite do dia. Cada
  lado usa o seu próprio `Col(...)`, porque a 0.6.0 consumia a `Expr` ao
  combiná-la (a 1.0.1 não consome mais). Validado contra contagem independente em Python nos três
  tipos, com linhas no passado e no futuro. `TimeSpan.TryParse`
  não lê `"1 hour"`: o `TimeFilter.ParseSpan` lê as unidades do systemd.
- **A inferência de datas do Polars sempre lê `nn/nn/aaaa` como dia/mês**
  (`ScanCsv(tryParseDates: true)` e `Str.ToDatetime` sem formato; testado
  em 2026-11-07). Num CSV americano em que todos os dias eram até 12,
  `01/02/2026` (2 de janeiro) virou 1º de fevereiro **em silêncio**. Por isso
  o `tryParseDates` está desligado e o `TextDates` decide pela evidência da
  coluna inteira:
  - ISO 8601 converte sempre;
  - `dd/mm` × `mm/dd` só quando algum valor tem primeiro ou segundo número
    acima de 12; tudo até 12 fica texto;
  - tudo ou nada: um valor que não converte (`31/02`) mantém a coluna como
    texto.

  São três passadas para não pesar: as primeiras 1.000 linhas descartam
  colunas, depois a coluna inteira dá a evidência, e por fim se verifica que
  não surgiram nulos. Medido: 1 milhão de linhas em ~0,5 s, sem custo de
  memória além do que o Polars já usa (~660 MB de pico com 32 threads).
  Também: com fuso nos dados, o `Str.ToDatetime` sem formato **é recusado
  pelo próprio Polars**; o `TextDates` sempre passa formato explícito.
- **Inferência de schema do CSV é por amostra** (default 100 linhas): int que
  vira float depois da amostra → "could not parse `8.1` as dtype `i64`" no
  sink. `inferSchemaLength: null` NÃO significa "arquivo todo" (cai no
  default, diferente do Python) e `ulong.MaxValue` dá "capacity overflow"
  nativo. **Continua assim na 1.0.1**, apesar de a nota da 0.7.0 prometer
  varrer o arquivo todo quando o parâmetro falta: `ScanCsv` sem ele ainda
  falha com um float na linha 200.001 (probe, 2026-11-08). Estratégia do app (EnsureParquetAsync): amostra de 10k (~235 ms em
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
- **Na 0.6.0, `Collect()` consumia o handle do `LazyFrame`** (acessar
  `Schema` depois dava "Handle is invalid") e combinar uma `Expr` a
  consumia. **Na 1.0.1, os dois são reutilizáveis** (probe, 2026-11-08:
  `Schema` e um 2º `Collect` depois do 1º; a mesma `Expr` em dois filtros e
  combinada consigo mesma, contagens iguais). O código ainda monta
  `LazyFrame` e `Expr` novos a cada consulta: não é mais preciso, mas também
  não custa.
- `DataFrame.FromArrow(RecordBatch)` crashava na 0.4.0; desde a 0.6.0 **funciona**
  (validado). Ainda assim não usar para filtrar em memória (princípio 1). O
  DataFrame **sobrevive ao `Dispose` do RecordBatch de origem**: validado
  com 20 rodadas de GC e realocação nativa, com somas e valores intactos.
- `Expr.IsIn`: o estilo antigo `IsIn(Lit(Series.From(...)).Implode())`
  continua funcionando; desde a 0.6.0 há overload direto `IsIn(IEnumerable<T>)`
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
    UTC); com fuso, mostrado no **fuso local de quem vê**, com o offset
    (decisão do dono, 2026-11-07). Cuidado ao mexer: um `?:` entre
    `DateTime` e `DateTimeOffset` converte os dois lados para
    `DateTimeOffset` e carimba o offset local na hora de parede. Use `if`.

  **Ordem dos `case`:** `StringArray` herda de `BinaryArray` (e o mesmo
  vale para View e Large), então texto vem antes de binário. Tipo novo:
  conferir com `acrux head` num parquet que o contenha.
- **O Polars recusa timestamp com fuso em formato de offset** (0.6.0 e 1.0.1)
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
  como str e passa pelo `TextDates` (o `ofertas.db` real: `data` ISO com
  fuso virou timestamp); BOOLEAN chega como i64 (0/1).
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
- **Juntar o banco num DataFrame só não serve** (o equivalente do
  `pd.concat(dict)`, com uma coluna `tableName`; existiu como
  `ReadSqliteTablesToSingleDataFrameAsync` e saiu em 2026-11-08). Somar ou
  unir tabelas diferentes dava números sem sentido, e a mesma coluna com
  tipos diferentes em duas tabelas **derruba o `ConcatDiagonal`** ("failed
  to vstack column"; validado). O contorno era converter para um tipo comum,
  e isso escondia o tipo real. Hoje tudo responde por tabela.

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
- Trabalho **sempre em branch** com prefixo (`refactor/`, `feat/`, `docs/`) —
  **nunca commit direto em `master`**; a integração é por PR.
- PR com `gh pr create --body-file <arquivo>`: a máquina é headless, `--web`
  não funciona. Merge com `--merge`, **nunca `--squash`** — os commits
  separados devem sobreviver no histórico.
- Movimentação de arquivo com `git mv`, para o histórico acompanhar o arquivo.
- Confira o nome do remote com `git remote -v` antes do push (neste clone é
  `origin`; já se chamou `ParquetGridViewer` em outro checkout).
- `gh` instalado e autenticado (conta `raphaelgaeta`, HTTPS): push e
  `gh pr create` funcionam direto do terminal.
- Fluxo de trabalho do dono do projeto: ele valida o desenho antes do código.
  Para features novas, apresente a arquitetura/avaliação primeiro e aguarde o
  aval antes de implementar.
