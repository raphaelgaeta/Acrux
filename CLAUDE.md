# Acrux

Um núcleo de leitura de dados tabulares (parquet, CSV, xlsx e SQLite) sobre o
Polars.NET, com duas fronteiras: o visualizador WinForms (`DataGridView` em
modo virtual) e o CLI. Otimizado para arquivos grandes (700 colunas, milhões
de linhas). O nome vem de α Crucis,
a estrela mais brilhante do Cruzeiro do Sul (o repositório já se chamou
ParquetGridViewer e o projeto, PolarsGridViewer).

## Build e execução

```
dotnet build                              # solução inteira (Acrux.sln), da raiz
dotnet run --project src/Acrux.WinForms
dotnet run --project src/Acrux.Cli -- head arquivo.parquet -n 10   # CLI, roda no Linux
```

O CLI tem os comandos `schema` (nome e tipo do Polars de cada coluna, uma
por linha), `columns`, `head [-n N]`, `rows [--where col=valor]...`, `type`,
`values <arquivo> <coluna> [--where col=valor]... [-n N]` (valores
distintos, ordenados; padrão: 100), `column <arquivo> <nome> [-n N]`
(padrão: 10 linhas; num `.db`, procura a coluna em todas as tabelas) e
`filter <arquivo> <coluna> --last <período>
[-n N]` (no espírito do `journalctl --since`: unidades do systemd, como
`30min`, `1h 30min`, `2 days`, `1M` = mês; a janela termina agora, então
linhas com data no futuro ficam de fora — numa coluna só de data, o dia de
hoje entra inteiro; da linha mais recente para a mais antiga; mostra as
linhas e quantas casaram; horários iguais saem na ordem do arquivo). Ele
lê só a coluna de tempo para achar as linhas e depois busca cada uma onde
ela está: a memória fica perto de 0,6 GB mesmo num parquet de 700 colunas,
ao custo de ~28 ms por linha mostrada nesse arquivo (~4 ms com 5 colunas).
O `--where col=valor` (desenho do dono, 2026-11-09) compara o **texto** da
coluna, o mesmo que o `values` imprime: a conversão do Polars para texto,
que não é o texto do `head` (`true` × `True`, `1e-9` × `1E-09`). Repetido, a
mesma coluna é OU e outra coluna é E; `col=` casa os vazios (nulo). Por ora
só no `rows` e no `values`, onde **todos** os filtros valem, inclusive o da
própria coluna (a regra do Excel é do popup). `head` e `column` esperam
medição no parquet de 700 colunas; `--since`/`--until`, no padrão do
journalctl, vêm depois. Medido nesse arquivo: `values` e `rows --where` em
~150 ms, com pico de `RssAnon` abaixo de 160 MB.
**Num `.db`, cada comando responde por tabela** (`schema`, `columns`,
`rows`, `head`, `values`, `column`, `filter`): somar ou unir tabelas
diferentes dá números sem sentido, e a mesma coluna pode ter tipos
diferentes em cada tabela. Os que citam colunas (`values`, `column`,
`filter`, `--where`) respondem só pelas tabelas que têm todas elas. O
`schema` e o `rows` de parquet leem só os metadados, sem os dados.
Erros saem em uma linha com código 1, sem o plano que o Polars anexa
(`PolarsErrors.Brief`; `ACRUX_DEBUG=1` mostra a exceção inteira); uso errado
sai com código 2. `acrux version` diz qual commit está
instalado (ver "Deploy de teste do CLI" abaixo).

Três projetos sob `src/`, agregados pelo `Acrux.sln` da raiz: `Acrux.Core`
(net10.0, sem UI — `ColumnCache`, `DataFrameProvider`, `FilterEngine`,
`MemoryBudget`, `PlanOrder`, `PolarsTableAdapter`, `ScriptHost`, `SystemMemory`,
`TextDates`, `TimeFilter`), `Acrux.WinForms` (net10.0-windows,
WinForms, `AssemblyName` mantido como `Acrux`) e `Acrux.Cli` (net10.0,
console); os dois últimos referenciam o Core. `dotnet build` na raiz basta, mas
**`dotnet run` e `dotnet publish` exigem apontar o projeto**: na raiz o `run`
não acha projeto ("Couldn't find a project to run") e o `publish` falha com
**NETSDK1099**, porque tenta aplicar single-file também no Acrux.Core, que é
biblioteca. O `Directory.Build.props` da raiz liga `EnableWindowsTargeting`:
baixa os reference assemblies do Windows e permite **compilar** contra a API do
Windows a partir do Linux — o binário resultante continua rodando só no
Windows.

Pacotes: Polars.NET 1.0.1 (+ Native.win-x64/linux-x64/linux-arm64, Linq, ML), Apache.Arrow
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

### Release

Pelo GitHub Actions (`.github/workflows/release.yml`, desenho aprovado em
2026-11-08):

1. Na `master`, crie e suba a tag: `git tag v0.1.0 && git push origin v0.1.0`.
2. O workflow compila e testa: no Linux, o smoke test e o CLI self-contained,
   duas vezes, cada uma na sua arquitetura: `ubuntu-latest` para o x64 e
   `ubuntu-24.04-arm` (runner ARM do GitHub, grátis em repositório público)
   para o arm64 (`acrux-cli-v0.1.0-linux-x64.tar.gz` e `…-linux-arm64.tar.gz`,
   com um link `acrux` dentro); no
   Windows, o instalador (`Acrux-v0.1.0-setup.exe`) e o app single-file
   (`Acrux-v0.1.0-win-x64.zip`); os dois levam a `LICENSE` e o `NOTICE` da
   arte.
3. Ele cria um release **rascunho**, com o `SHA256SUMS` e as notas (texto
   fixo de instalação + as geradas pelo GitHub). O dono confere e publica.

**O instalador** (`installer/acrux.iss`, Inno Setup 6.7.1 instalado na CI pelo
`choco`, com a versão fixa; desenho aprovado em 2026-11-08):

- **Por usuário, sem admin:** instala em `%LOCALAPPDATA%\Programs\Acrux`,
  com atalho no menu Iniciar, ícone na área de trabalho opcional e
  desinstalador.
- **Instala uma pasta, não o single-file:** é um publish self-contained
  normal, que abre mais rápido e não extrai nada em `%TEMP%\.net`.
- **Runtime do Visual C++ (ver Armadilhas):** se a `vcruntime140.dll` falta,
  o setup baixa o `vc_redist.x64.exe` do link oficial da Microsoft e o roda
  com `runas`. Só nesse momento o Windows pede admin. Baixar, em vez de
  embutir, evita redistribuir o pacote, que pela licença é restrito a quem
  tem Visual Studio. Se o download falhar ou o pedido de admin for recusado,
  o Acrux se instala mesmo assim, e o setup mostra o link.
- **"Abrir com" no Explorer** (desenho aprovado em 2026-11-10): o `[Registry]`
  do `.iss` cria o ProgID `Acrux.DataFile` (`"Acrux.exe" "%1"`) e o põe no
  `OpenWithProgids` de `.parquet`, `.csv`, `.xlsx`, `.db`, `.sqlite` e
  `.sqlite3`, as extensões do `OpenDialogFilter`: mudou uma lista, mude a
  outra. É por usuário (`HKA` = `HKCU` com `PrivilegesRequired=lowest`), e
  `ChangesAssociations=yes` avisa o Explorer na instalação e na remoção.
  - **Nenhum app se torna o padrão sozinho:** desde o Windows 8, só o
    usuário escolhe o padrão, na UI do sistema. Escrever o `UserChoice` não
    funciona, porque o driver UCPD.sys protege essa chave (documentação da
    Microsoft, *Windows app defaults platform*). Muito guia antigo ainda
    ensina isso.
  - **Só o `.parquet` ganha o valor padrão:** em geral, nenhum outro app o
    reivindica, então o duplo clique abre o Acrux. CSV, xlsx e `.db` têm dono
    (o Excel, por exemplo), e o Acrux só entra na lista. O valor padrão do
    `.parquet` fica na desinstalação, como a Microsoft recomenda: sem o
    ProgID, o Windows o ignora.
  - **O registro é do instalador, nunca do app:** o desinstalador apaga o que
    o instalador escreveu. Um app que se registrasse a cada abertura
    gravaria no registro de quem usa o zip, e deixaria um "Abrir com"
    apontando para um `.exe` apagado.
  - **Verbos extras** (um "Acrux: schema" no clique direito) cairiam no
    "Mostrar mais opções" do Windows 11. O menu principal exige
    `IExplorerCommand` numa DLL COM nativa, num pacote MSIX ou sparse, que
    precisa de assinatura. Por ora, só o "Abrir com".
- **`AppId` fixo** no `.iss`: o Windows reconhece a instalação por ele.
  Nunca mude.
- O `.iss` só compila no Windows (na CI). Para testar, baixe o artefato
  `windows` do PR com `gh run download`. O primeiro, de 70 MB, funcionou na
  VM limpa do Windows 11, onde o zip falhava por falta do runtime (teste do
  dono, 2026-11-08).

Em PR, o mesmo workflow só compila e testa, sem release. A versão vem da tag
(`-p:Version=0.1.0`); fora de release vale `0.0.0-dev`
(`Directory.Build.props`), e na CI de PR, `0.0.0-ci.N`. O `acrux version`
mostra a versão e o commit (`0.1.0 (b0bdb9a)`); num deploy local, o arquivo
`COMMIT` tem prioridade. **Sem assinatura ainda:** o Windows mostra o aviso do
SmartScreen. O pedido de assinatura grátis da SignPath Foundation foi enviado
em 2026-11-11, pela conta do dono (com 2FA). Os termos deles exigem a seção
"Code signing policy" na página inicial (o README) e o termo na página de
download (as notas do release, no `release.yml`): a frase de atribuição, os
papéis do time e a privacidade. **Mudou o time ou o que o app transmite?
Atualize a seção.** Pendências conhecidas do pedido: o ícone fora da MIT (os
termos pedem licença aprovada pela OSI em todos os componentes) e um aviso no
setup antes de mexer nas associações de arquivo. Store,
winget e RPM ficam para quando houver usuários. As Actions estão nas versões
conferidas em 2026-11-08 (checkout v7, setup-dotnet v6, upload-artifact v7,
download-artifact v8, todas em Node 24).

O `Acrux.WinForms.csproj` traz tuning intencional de GC para desktop
(workstation concurrent + DATAS via `GarbageCollectionAdaptationMode=1`) —
não reverter sem motivo.

O `README.md` (em inglês, voltado ao público do repositório) resume features e
arquitetura; este arquivo segue sendo a fonte de verdade técnica — ao mudar
comportamento visível, atualizar os dois.

**Os GIFs do README saem de execuções reais** (`docs/demo/`, desde
2026-11-11). Os de terminal (`cli.gif`, `messy_csv.gif`) são desenhados pelo
`terminal_gif.py` a partir de uma gravação (`cli.json`, `analyst.json`): o
comando, a saída e o tempo são de verdade, e só a digitação é encenada. Os
da janela são gravações do Windows re-temporizadas pelo `edit_gif.py`, que
marca cada trecho acelerado. O `messy_csv.gif` põe o pandas ao lado do
Acrux, num CSV como o Excel em português salva; desenho do dono. Ele só é
justo se três coisas continuarem valendo:
- a mesma regra de ritmo nos dois lados;
- a legenda dizendo o que foi encurtado (os tracebacks);
- o `docs/demo/README.md` dizendo onde o Acrux converte menos (datas com
  todos os dias até 12).

Mudou a saída do CLI ou a versão do pandas? Grave de novo e redesenhe; os
comandos estão no `docs/demo/README.md`. O desenho é determinístico: a mesma
gravação dá o mesmo GIF, byte a byte.

## Identidade visual

Logo de Kat Ribeiro (design), propriedade do dono do projeto e **fora da MIT**
(`assets/brand/NOTICE.md`). Paleta: `#8c5ea4` e `#acd7e7`. No repositório só
entra a arte em uso; o kit completo (`.ai`, `.eps`, JPGs, PDFs com contato dela)
fica fora.

**Acrux é o projeto; o urso do logo é o mascote, Ginan the Snooper.** Ginan é
o nome oficial (IAU, 2017) de ε Crucis, a "Intrometida" do Cruzeiro do Sul;
*snooper* é o xereta: o urso polar que veio bisbilhotar o céu do sul, e os
seus dados. Fora do README, porque não foi conferido na fonte primária: a
Wikipédia diz, citando o relatório de 2023 do grupo de nomes de estrelas da
IAU, que Ginan seria o nome wardaman da própria Acrux, e que o nome foi mantido
mesmo assim.

- **O ícone é recorte:** urso e estrela grande são transparentes e assumem a cor
  do fundo (claros no tema claro, escuros no escuro), como nos mockups da autora.
  Não preencher. "Ícone Preto" some no tema escuro; o ícone com nome só é legível
  a partir de ~48 px; a ilustração sem moldura se perde abaixo de 32 px.
  **Por isso o ícone pede fundo neutro:** numa barra de título roxa, o urso
  fica roxo sobre a metade roxa do degradê e some (visto pelo dono na VM,
  2026-11-10). A marca na barra é o próprio ícone.
- **`acrux.ico`:** gerado de `assets/brand/acrux-icon.svg`. A arte não é quadrada
  (1022,4×1009,25): vai com margem transparente, sem esticar. Quadros 16 a 64 px
  em BMP e 256 em PNG, pelo `Icon` do System.Drawing.
- **`ApplicationIcon` só marca o `.exe`.** Funciona em publish feito no Linux
  (quadros conferidos na tabela de recursos PE, sem NETSDK1074). Janela com barra
  de título precisa de `Icon = AppIcon.Value`; o `FilterPopupForm` é tool window e
  não mostra ícone.
- **Fonte Aller: nunca embutir nem commitar.** A licença gratuita limita a 25
  usuários e só permite redistribuir o `.zip` original. O README não aceita fonte
  própria; o nome aparece só como imagem, via logo. Azul no branco dá 1,54:1 de
  contraste, por isso o README usa roxo no tema claro e gradiente no escuro.

**Tema do app de janela** (`Theme.cs`, desenho aprovado em 2026-11-10): claro
ou escuro conforme o Windows, e a marca só onde ajuda a ler. É o "tema
mínimo": nada desenhado à mão além dos nulos, porque o que se gasta em
aparência no WinForms se perde na troca para o Avalonia.

- **Modo escuro:** `Application.SetColorMode(SystemColorMode.System)` no
  `Program`, antes de qualquer janela; não é mais experimental no .NET 10.
  Só existe no Windows 11 (o Wine fica no claro), é lido uma vez (trocar o
  tema do Windows pede reabrir o app) e não vale em alto contraste.
- **Nenhuma cor fixa** (`Color.White`, `Color.Black`): elas viram blocos no
  escuro. Use `SystemColors` ou o `Theme`.
- **Cada cor da marca no modo em que se lê** (WCAG; texto pede 4,5:1):
  - a barra de título fica a do sistema, e só a borda da janela é roxa
    (`FormBorderColor`; o Windows 10 ignora). O roxo dá 4,94:1 contra o
    branco e 3,30:1 contra `#202020`, acima do mínimo de 3:1 para o que não
    é texto. Uma barra roxa apagava o urso do ícone (ver "O ícone é recorte",
    acima);
  - destaque (seleção, cabeçalho filtrado): preto sobre azul no claro,
    13,6:1; branco sobre roxo no escuro, 4,94:1. Branco sobre azul daria
    1,54:1;
  - o cabeçalho filtrado mantém o " ▼": a cor não pode ser o único sinal.
- **Em alto contraste, a marca sai** (`SystemInformation.HighContrast`): o
  esquema do usuário vale mais.
- **Cinza e vermelho calculados, não do sistema:** o texto secundário
  (nulos, eco do terminal) fica a 60% entre o fundo e o texto, ~5,7:1 no
  claro e ~6,6:1 num fundo `#202020`. O erro é `Firebrick` no claro (6,7:1)
  e `#f48771` no escuro, porque o `Firebrick` daria 2,4:1.
- **O grid desenha, mas não muda o dado** (`GridPainting.cs`, desenho
  aprovado em 2026-11-10). O `CellPainting` muda só a pintura; a célula
  guarda o valor, e o Ctrl+C copia o dado, não o desenho. A dica de uma
  célula cortada mostra o valor inteiro. Quatro casos:
  - **nulo:** `null` cinza em itálico. O Ctrl+C copia vazio, e um texto vazio
    continua vazio;
  - **número que não cabe:** `###`, como no Excel. Cortado no fim, o número
    parecia outro (`922337203685…` para um valor de 19 dígitos). A medida usa
    a área e os flags do próprio `DataGridViewTextBoxCell` (código do
    .NET 10: menos borda e `Padding`, 2 px em cima e 1 embaixo, `SingleLine`,
    `NoPrefix`), então o grid nunca corta com reticências um número que a
    regra deixou passar;
  - **texto com quebra de linha:** uma linha só, com `↵` onde havia cada
    quebra. Antes aparecia colado (`linha 1linha 2`). O CLI troca por espaço;
    no grid, o símbolo mostra que havia uma quebra;
  - **coluna filtrada:** o `▼` é desenhado na borda direita do cabeçalho, que
    ganha `Padding` à direita para o nome não passar por baixo. O
    `HeaderText` é sempre o nome real: com o `▼` no texto, o Ctrl+C levava
    `bool ▼` para o dado colado.
- **Números à direita, pelo tipo Arrow.** Atenção: **no Arrow,
  `BooleanType` herda de `NumberType`**, e por isso o `IsNumeric` o exclui.
  Os decimais (`Decimal32Type` a `Decimal256Type`) não herdam de
  `NumberType`. Conferido no parquet de 20 tipos.
- **Barra do topo em `FlowLayoutPanel`**, não por pixel: texto maior (DPI,
  fonte) empurra o vizinho em vez de sobrepor, e botão escondido não deixa
  buraco.
- **Aparência se confere na VM do Windows 11**, nos dois modos. Os GIFs do
  README são anteriores ao tema.

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
- O `Acrux.Cli` escolhe o nativo do Polars **pelo RID do publish**
  (`dotnet publish -r linux-arm64` leva o `Polars.NET.Native.linux-arm64`)
  e, sem RID, **pela máquina que compila**, pelo sistema e pela arquitetura
  (`PolarsNativeRid` no `.csproj`; desde 2026-11-11, antes só o sistema). O
  publish para outra arquitetura sai do próprio laptop: o `linux-arm64`
  levou 6 s. O Polars.NET não tem `win-arm64`. As versões managed e nativa
  precisam ser idênticas: divergência dá segfault em runtime, não erro de
  build.
- **O CLI roda em ARM64** (2026-11-11): o smoke test inteiro passou numa
  Arduino UNO Q (Debian 13, 4 núcleos ARMv8.0, 1,7 GB) em 40 s, e num
  contêiner Ubuntu 24.04 arm64 pelo qemu. Na placa, com teto de 700 MB:
  comandos sobre um CSV de 2M linhas em ~4,6 s e ~160 MB (o laptop: 0,4 s),
  sobre um parquet de 1M × 21 de 0,5 a 1,6 s, até 74 MB. Ver a armadilha da
  glibc, abaixo.

Próximos passos:

1. Degrau 4: um comando `query` no CLI que rode script pelo `ScriptHost`.
2. Com isso fechado, spike de grid virtualizada no Avalonia.

### Testar o app de janela no Linux (Wine)

O WinForms não roda nativo no Linux, mas o publish self-contained roda sob o
Wine: com a 1.0.1, abrir, escolher colunas, filtro de cabeçalho e terminal C#
funcionaram (2026-11-08). O Wine valida funcionamento, não aparência: ícone,
visual e os GIFs do README se conferem no Windows. **Nunca desligue o
`mscoree`** (`WINEDLLOVERRIDES="mscoree=..."`): sem ele o runtime .NET falha
com "Could not load file or assembly System.Runtime.dll". A montagem do
laptop do mantenedor fica no `CLAUDE.local.md`, fora do git.

### Deploy de teste do CLI (opcional, por clone)

Quem liga o hook tem, a cada commit, o CLI publicado sozinho: o último commit
que compilou e passou no smoke test (desenho aprovado em 2026-11-08).

- **Hook:** `.githooks/post-commit` chama `scripts/deploy-cli.sh` em segundo
  plano (`setsid`), e o commit volta na hora. Hooks não vêm no clone: cada
  clone liga uma vez com `git config core.hooksPath .githooks`. Numa branch
  sem `.githooks/`, nada acontece. Merge sem conflito não dispara o hook
  (é `post-merge`, não `post-commit`): publique à mão.
  `ACRUX_NO_DEPLOY=1 git commit …` pula o deploy.
- **O script publica o commit, não o disco:** `git archive` do commit →
  `dotnet publish` Release linux-x64 framework-dependent (~3 s) →
  `~/.local/share/acrux-cli/<commit>/` → `tests/smoke/run.sh` no binário
  novo → só então troca o link `current`, e o anterior vira `previous`.
  Sobram só essas duas pastas (~170 MB cada; 135 MB são o
  `libnative_shim.so` do Polars). Uma rajada de commits (rebase) entra em
  fila numa trava (`flock`) e publica só o mais novo. Build ou teste que
  falha não troca nada: o `current` continua o anterior. **O `publish` roda
  com o descritor da trava fechado (`9>&-`)**: o `dotnet` deixa servidores
  de compilação vivos por minutos (`VBCSCompiler`, nós do MSBuild), e,
  herdando a trava, eles seguraram o deploy seguinte (2026-11-08). Deploy
  parado sem log novo: procure quem tem `acrux-cli/.lock` aberto em
  `/proc/*/fd`; `dotnet build-server shutdown` solta.
- **Log:** `~/.local/share/acrux-cli/deploy.log` (cortado para 500 linhas
  quando passa de 1.000). Uma falha não avisa na tela: **confira com
  `acrux version`** se o commit esperado está no ar.
- **Voltar atrás:** `cd ~/.local/share/acrux-cli && ln -sfn "$(readlink
  previous)" current`. Publicar um commit à mão:
  `scripts/deploy-cli.sh <commit>`.
- **Comando no PATH:** aponte um link para
  `~/.local/share/acrux-cli/current/Acrux.Cli`. **O `current` nunca pode
  ficar sem alvo** (sem ele, o comando some sem mensagem): o script troca o
  link por `mv`, de uma vez, e a limpeza poupa `current` e `previous`.
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

#### O app de Windows no mesmo deploy (opcional, por clone)

Desenho aprovado em 2026-11-08. Com `git config acrux.deployWindows true`,
o hook roda `scripts/deploy-windows.sh` depois do CLI:

- **Compila o commit do `current` do CLI**, o mais novo que passou no smoke
  test (o app de janela não tem teste automático: compilar é a conferência).
  Roda mesmo quando o deploy do CLI falha, porque segue o `current` dele.
  `scripts/deploy-windows.sh <commit>` compila outro à mão.
- **Publish igual ao do release** (win-x64, self-contained, single-file) em
  `~/.local/share/acrux-windows/<commit>/`, com `COMMIT.txt` dentro e
  `current`/`previous` como no CLI. ~5 s e ~300 MB por build.
- **Só recompila quando o app muda** (`Directory.Build.props`,
  `src/Acrux.Core`, `src/Acrux.WinForms`): um commit de documentação, do CLI
  ou de teste mantém o build e registra "builds the same app as …: kept".
- **Entrega:** se o clone tiver `git config acrux.windowsDeliver '<comando>'`,
  o `sh` roda o comando com a pasta do `current` em `$1` (por exemplo, uma
  cópia para uma VM do Windows). Código 0 é "entregue" e fica gravado em
  `delivered`; 3 é destino fora do ar; outro código é falha. Nos dois
  últimos casos, a entrega é tentada de novo no próximo deploy, e o
  `current` não muda.
- O log é o mesmo `deploy.log` do CLI, com as linhas marcadas `windows:`.

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
barato → `ColumnCache.OpenAsync` (schema Arrow e número de linhas, só
metadados) → grid virtual com **todas** as colunas → cada coluna é lida
quando chega à tela (ver "O cache de colunas", abaixo). O botão "Columns"
(`ColumnSelectorForm`) esconde e mostra colunas: a escondida fica no grid com
`Visible = false`, para o índice da coluna no grid ser sempre o do arquivo.

**O cache de colunas** (`ColumnCache`, desenho aprovado em 2026-11-10): o
grid abre o arquivo inteiro, de qualquer largura. Antes, abrir as 700
colunas × 5M pedia ~25 GB e derrubava o processo (ver "Memória", nas
Armadilhas).
- **Uma coluna por `Collect`**, inteira (todas as linhas), em paralelo:
  descartar uma coluna de um lote importado não devolve nada (ver
  Armadilhas), e 15 `Collect`s paralelos levaram 174 ms contra 299 de um só
  (VM de 8 núcleos).
- **Teto = (memória disponível + o que os caches guardam) ÷ 2** (direção do
  dono: "o quanto está disponível", na referência de um PC de 16 GB com o
  Chrome, ~8 GB livres). Um teto só (`MemoryBudget`) para o cache do
  arquivo e o do resultado de script; com um script na tela, as colunas do
  arquivo cedem primeiro. Os caches entram na conta para o teto não
  encolher enquanto eles crescem. "Disponível" é o do Gerenciador de Tarefas
  (`GlobalMemoryStatusEx`; `MemAvailable` no Linux, em `SystemMemory`), a
  RAM física, não o limite de commit. Sem teto fixo: máquina com folga
  guarda o arquivo inteiro.
- **Despeja antes de carregar**, a coluna usada há mais tempo primeiro, pelo
  tamanho esperado do que vai chegar (largura do tipo; texto, 16 B por
  linha até a 1ª coluna do tipo chegar, depois a média medida). O pico fica
  no teto mais uma tela em carga (~600 MB na VM). Despejando depois, o pico
  subia de 1,7 para 2,1 GB.
- **Fixadas:** as colunas do último `EnsureAsync` (a tela mais meia tela de
  cada lado) nunca saem; a tela sempre carrega, mesmo além do teto.
- **Reavaliado** antes de cada carga e a cada 5 s (`Trim`): se outro
  programa toma memória, o teto cai e o cache solta colunas fora da tela.
  Com uma carga em andamento, o teto não é recalculado: a memória de
  trabalho dela pareceria ocupada.
- **Uma thread só:** o cache é chamado da thread da UI; as cargas rodam no
  pool e voltam pelo contexto de sincronização para guardar a coluna e
  descartar as velhas. Assim, uma célula nunca lê um buffer no meio de um
  `Dispose`. Uma TUI ou o Avalonia precisam do mesmo contexto.
- **No grid:** a célula de coluna fora da memória mostra `…` cinza
  (`CellPlaceholder`, desenhado pelo `GridPainting`), ou `error` se a
  leitura falhou (a coluna não é tentada de novo). Cada falta reinicia um
  timer de 100 ms, e a carga só começa quando o grid para de rolar. A
  barra de status diz quantas colunas estão na memória, contra o teto.
- **Ctrl+C** (e Ctrl+Shift+C, Ctrl+Insert) carrega antes as colunas da
  seleção; se elas não cabem juntas no teto, avisa em vez de copiar pela
  metade.
- **O resultado de um script também vem coluna por coluna** (desenho
  aprovado em 2026-11-11, `ColumnCache.FromPlanAsync`): cada coluna é uma
  execução do plano com só ela selecionada (`plano.Select(coluna)`), e o
  Polars lê do arquivo só o que ela precisa; as linhas, por
  `Select(Len())`. Calculado inteiro, um filtro sobre as 700 colunas morria
  (ver Armadilhas). Quatro colunas por vez (`PlanConcurrency`): cada uma
  refaz o trabalho do plano, e o Polars já usa todos os núcleos em cada
  execução. Com 30 de uma vez, filtrar e ordenar passou de 8 GB.
  - **Só quando a ordem das linhas se repete** (`PlanOrder`, lendo o
    `Explain(true)`): cada coluna vem de uma execução diferente, e uma
    ordem que mudasse entre elas desalinharia as colunas sem erro nenhum.
    Por lista do que se mediu estável: leitura, filtro, `Select`,
    `WithColumns`, `Limit`/`Head`/`Tail`, ordenação, `GroupBy` com ordem
    mantida, `Explode`, `Unpivot`, um passo que termina em `Collect()`.
    `Unique` sem `maintainOrder`, join, `Shuffle`/`Sample`, nó
    desconhecido: o resultado é calculado inteiro, numa execução, se
    couber no teto contando com tantas linhas quanto o arquivo; se não, o
    terminal mostra os tamanhos e sugere o `Select` antes.
  - O plano é do cache e só é liberado depois da última carga em
    andamento: fechar o resultado no meio de uma carga não usa um handle
    liberado (conferido com 30 colunas em voo).
  - **Erro que só aparece ao calcular uma coluna** (um `Cast` que não
    converte, por exemplo) não impede o passo de entrar na cadeia: a
    contagem de linhas nem calcula essa coluna. A célula mostra `error`, e o
    terminal diz por quê, ao lado do "Undo step". Antes, com o `Collect`
    inteiro, o passo falhava no terminal e ficava fora da cadeia.
- Medido pelo `Acrux.Core`, no laptop, com teto de 3 GB e o processo
  limitado a 8 GB (2026-11-11), passando pelo `ScriptHost` como o app:
  `lf.Filter(Col("c001_i32") > Lit(0))` (1M × 700) abriu em 0,8 s (a
  compilação incluída) e rolou as 47 telas em ~100 ms cada, com o arquivo
  cedendo a memória; ordenar por coluna com milhões de empates, ~0,6 s por
  tela; filtrar e ordenar por texto, ~1,1 s, pico de 5,3 GB; `GroupBy`, 47
  ms. Alinhamento conferido pelo `row_id`: 240 mil células por script, nenhuma
  errada. `Unique` com as 700 colunas: a mensagem (25 GB contra 3 GB), sem
  executar.
- Medido pelo `Acrux.Core`, no laptop (2026-11-10): rolar as 700 colunas
  em telas de 15, com 8 de margem, deu ~100 ms por tela; com o teto de
  3,8 GB (metade de 7,6 livres), 96 colunas ficaram na memória, e voltar 9
  telas não leu nada. Células conferidas contra um `Collect` direto: 75
  milhões nas 15 primeiras colunas e os 20 tipos do `tipos.parquet`, sem
  diferença.

Ciclo de vida do processo: `Program.Main` termina com `Environment.Exit(0)`
(garantia contra threads estrangeiras — Polars/rayon, Roslyn — segurarem um
processo fantasma; relato de campo em 2026-07, não reproduzido em matriz de
10 cenários, belt preventivo). Temporários levam o PID no nome
(`_pid{N}_`) e `CleanStaleTempFiles()` varre órfãos de execuções mortas na
inicialização, pulando PIDs vivos (multi-instância seguro).

Fluxo de filtro: clique no cabeçalho → `FilterEngine.GetDistinctValuesAsync`
(dropdown limitado a `DistinctCap = 10.000`) → `FilterPopupForm` →
`GetVisibleRowsAsync` (máscara booleana varrida em C#) → `_filteredRows`.
**Toda consulta do `FilterEngine` recebe o filtro pronto, como `Expr?`**
(desde 2026-11-09): quem chama monta com `BuildPredicate` (valores por
coluna), `TimeFilter.Between` (tempo), ou os dois unidos por `&`. A
semântica Excel (a lista de uma coluna ignora o filtro dela mesma) é do
app: `BuildPredicate(_activeFilters, except: coluna)`. O CLI aplica todos.
Filtros concorrentes são cancelados via `_filterCts`.

Terminal C# (botão "C# terminal"): scripts Roslyn encadeados por **replay** —
`MainForm._scriptChain` guarda os textos dos passos; cada execução re-roda a
cadeia inteira sobre um `ScanParquet` fresco, com o passo N recebendo em `df`
o `LazyFrame` (plano lazy, não dados) do passo N-1. O passo 1 recebe o arquivo
**como exibido no grid**: colunas visíveis + filtros ativos aplicados como
"passo 0" implícito (`FilterEngine.BuildPredicate` público é reutilizado), o
filtro antes do `Select`, porque o filtro pode estar numa coluna escondida.
Esse estado visual é **congelado no 1º passo** (`_chainBaseColumns`/
`_chainBaseFilters`) — obrigatório, pois o replay re-executa tudo a cada
comando e a base não pode derivar; "Restore file"/novo arquivo zeram a base.
Os `_activeFilters` NÃO são mais limpos ao entrar em modo script: sobrevivem
como base da cadeia e são **reaplicados ao restaurar**. Nada é coletado:
a cadeia compõe um único plano lazy (`Limit(ScriptHost.RowCap)` opcional),
que o `ScriptHost.BuildChainAsync` devolve e o `ColumnCache.FromPlanAsync`
mostra coluna por coluna (ver "O cache de colunas"). Passo que
termina em `DataFrame` (ex.: `.Collect()`) volta ao lazy via `df.Lazy()`
(validado; os dados dele ficam materializados no plano durante o replay).
Um passo com erro não entra na cadeia. "Undo step" remove o último e
re-executa; "Restore file" zera a cadeia e volta ao arquivo: o cache do
arquivo continua vivo sob o resultado do script, sem colunas fixadas, e só
relê as colunas que cederam memória ao resultado. Imports do script:
`Polars.CSharp` + estáticos (`Col`/`Lit`). Em modo script (`_scriptMode`) os
filtros de cabeçalho ficam desativados — o grid não espelha mais o arquivo —
até "Restore file" (decisão V1; a V2 composável exigiria FilterEngine
aceitar fonte lazy genérica).

**O nome é `df`** (desenho do dono, 2026-11-11: é o nome que quem mexe com
dados usa). Até então era `lf`, que continua como apelido do mesmo plano
(`ScriptGlobals.lf => df`), para scripts antigos rodarem. O nome promete
dados, e o `df` é um plano: quem escreve `df.Height`, `df.Transpose()` ou
`df["a"]` recebe o erro do compilador e uma dica do `ScriptHost.Describe`,
de que o `df` é lazy e de como coletar só as colunas necessárias. A dica sai
quando o erro é CS1061 (membro que falta) ou CS0021 (indexador) sobre
`LazyFrame` e o `DataFrame` tem o membro, e o exemplo só continua com o
membro quando ele devolve uma tabela: um passo que termina num número ou
numa `Series` falha por si. **O Roslyn traduz as mensagens** (pt-BR num
Windows em português, conferido no laptop), mas não o nome do tipo; por isso
o `Describe` lê a mensagem na cultura invariante.

| Arquivo | Papel |
|---|---|
| `src/Acrux.Core/DataFrameProvider.cs` | Detecção de formato pelo conteúdo, conversão para parquet, leitura via LazyFrame |
| `src/Acrux.Core/FilterEngine.cs` | Filtros via Polars (distintos, máscara e contagem), todos recebendo o filtro como `Expr` |
| `src/Acrux.Core/ColumnCache.cs` | As colunas de uma tabela (o arquivo ou o resultado de um script), lidas quando chegam à tela |
| `src/Acrux.Core/MemoryBudget.cs` | O teto que os caches dividem: metade da memória livre, eles incluídos |
| `src/Acrux.Core/PlanOrder.cs` | Lê o plano do Polars e diz se as linhas saem na mesma ordem a cada execução |
| `src/Acrux.Core/SystemMemory.cs` | A memória física disponível agora, no Windows e no Linux |
| `src/Acrux.Core/PolarsTableAdapter.cs` | `GetCellValue`: leitura O(1) por célula do Arrow |
| `src/Acrux.Core/ScriptHost.cs` | Terminal C#: os scripts Polars pelo Roslyn, encadeados num plano lazy |
| `src/Acrux.Core/TextDates.cs` | Datas guardadas como texto (CSV, SQLite) viram data só quando não há ambiguidade |
| `src/Acrux.Core/PolarsErrors.cs` | Erros do Polars para gente: a mensagem sem o plano que ele anexa (CLI e app) |
| `src/Acrux.Core/TimeFilter.cs` | Filtro de tempo relativo: lê períodos no padrão do systemd, monta o predicado por tipo de coluna e acha as posições das N linhas mais recentes |
| `src/Acrux.WinForms/Program.cs` | Entrada do app: limpeza de temporários órfãos, o arquivo recebido como argumento (duplo clique, "Abrir com") e `Environment.Exit(0)` |
| `src/Acrux.WinForms/AppIcon.cs` | Ícone das janelas, lido do `.ico` embutido |
| `src/Acrux.WinForms/GridPainting.cs` | O que o grid desenha sem mudar o dado: nulo, `###`, `↵` e o `▼` do filtro |
| `src/Acrux.WinForms/Theme.cs` | Cores do app: a marca onde se lê, as do sistema no resto; modo claro e escuro |
| `src/Acrux.WinForms/MainForm.cs` | Grid virtual sobre o cache de colunas, carga do arquivo, costura dos filtros, Ctrl+C |
| `src/Acrux.WinForms/ColumnSelectorForm.cs` | O diálogo do botão "Columns": quais colunas o grid mostra |
| `src/Acrux.WinForms/SheetSelectorForm.cs` | Diálogo de escolha da aba do xlsx ou da tabela do SQLite (2+) |
| `src/Acrux.WinForms/FilterPopupForm.cs` | Popup de filtro aberto pelo cabeçalho da coluna |
| `src/Acrux.WinForms/ScriptTerminalPanel.cs` | UI do terminal (painel inferior do MainForm) |
| `src/Acrux.Cli/Program.cs` | CLI: rota de leitura escolhida pelo formato detectado; `head` usa o caminho do grid |

### Princípios de design (não violar)

1. **Zero materialização**: os dados vivem só em buffers Arrow: os do
   arquivo no `ColumnCache`, uma coluna por lote, até metade da memória
   livre; o resultado de um script também, num segundo `ColumnCache`
   (inteiro só quando a ordem pode mudar e ele cabe). Células são lidas por demanda
   em `CellValueNeeded` via `PolarsTableAdapter.GetCellValue`. NUNCA
   converter colunas inteiras em `object[]` — com milhões de linhas isso
   esgota a RAM e trava a máquina. E nunca carregar todas as colunas de uma
   vez: num arquivo largo, o processo cai sem exceção (ver Armadilhas).
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

### Memória (o cache de colunas, 2026-11-10)
- **Faltou memória, o processo morre sem exceção.** Abrir as 700 colunas ×
  5M no app (todas de uma vez) levou o `Acrux.exe` a 14,3 GB na VM (8 GB +
  7,7 GB de paginação), e o `native_shim.dll` encerrou o processo com
  `0xc0000409`: o Rust aborta quando uma alocação falha, e nenhum
  `try/catch` do .NET pega. Por isso o cache de colunas, e não um aviso.
- **Descartar uma coluna de um lote importado do Polars não devolve
  memória:** o `RecordBatch` do `ToArrow()` é liberado inteiro, no
  `Dispose` do lote. Para soltar colunas uma a uma, cada uma vem do seu
  `Collect` (probe no laptop).
- **O heap do Windows devolve a memória; o glibc do Linux, não.** Na VM,
  depois do `Dispose`, o processo voltou a ~12 MB. No laptop (32 threads),
  o glibc segurou ~1 GB a mais (uma arena por thread): com o teto de 1 GB,
  o pico de `RssAnon` foi 2,3 GB; com `MALLOC_ARENA_MAX=4`, 1,9 GB, e o
  `malloc_trim(0)` volta ao tamanho do cache. Uma tela de Linux (TUI,
  Avalonia) vai precisar dos dois.
- **Memória no Windows se mede pelos bytes privados**
  (`Process.PrivateMemorySize64`), o equivalente do `RssAnon`.
- **Filtrar ou ordenar com todas as colunas estoura, mesmo com `Limit` no
  fim** (2026-11-11, laptop, processo limitado a 8 GB). Com o arquivo aberto
  inteiro, a base do terminal são as 700 colunas: `lf.Filter(Col("c001_i32")
  > Lit(0))`, e até com `.Limit(1000)` no fim, morreu num `Collect` só; o
  `SinkParquet` em streaming também, com 8 ou com 2 threads. É a armadilha
  de "ordenar e cortar linhas largas" (abaixo): o Polars guarda as linhas
  que casam, com todas as colunas, antes de cortar. Por isso o resultado do
  script vem coluna por coluna. Um passo que termina em `.Collect()` sobre
  as 700 colunas continua perigoso, e o app não tem como interceptá-lo.
- **A ordem das linhas entre execuções está no `Explain(true)`.** O
  `GroupBy` do Polars.NET mantém a ordem por padrão
  (`AGGREGATE[maintain_order: true]`); o `Unique`, não
  (`UNIQUE[maintain_order: false]`), e deu uma ordem diferente em cada uma
  de 7 execuções. Filtro, ordenação com milhões de empates e `GroupBy`
  deram a mesma ordem em 9, sozinhos, ao lado de outras colunas e em
  paralelo. O `Sort` só mostra `maintain_order: true` quando ligado; sem
  ele, a ordem dos empates se repetiu nas medições, mas não é garantida
  pelo Polars.

### Polars.NET 1.0.1 (0.4.0 → 0.6.0 em 2026-07, por probe; → 1.0.1 em 2026-11-08)
- **No Windows, o nativo do Polars exige o runtime do Visual C++.** O
  `native_shim.dll` importa `VCRUNTIME140.dll` (a 0.6.0 importava também o
  `_1`), que vem do Visual C++ Redistributable e **não existe num Windows
  limpo**. Lá, abrir qualquer arquivo dá "Error loading DataFrame: Dll was
  not found." (VM do Windows 11, 2026-11-08). O Wine tem uma cópia própria, e
  quase todo PC já tem o redistributable instalado: por isso ninguém tinha
  visto. As outras importações são do Windows (o UCRT `api-ms-win-crt-*`
  existe desde o Windows 10), e o `e_sqlite3.dll` não depende disso. O
  instalador resolve; o zip pede o redistributable nas notas. A 1.1.0 ainda
  importa; relatado ao autor com a sugestão de compilar com o CRT estático
  (`+crt-static`; ErrorLSC/Polars.NET #37, 2026-11-10). Conferir as
  importações de um `.dll` novo: tabela de importação PE (o `objdump` do
  Fedora não lê PE).
- **O nativo de cada plataforma pede outra coisa da máquina** (1.0.1 e
  1.1.0, conferido em 2026-11-11):
  - **x64 (Windows e Linux): AVX2.** O autor compila com
    `target-cpu=x86-64-v3` (o `release.yml` dele), então CPU sem AVX2
    (Intel antes de 2013, AMD antes de 2015, alguns Celeron e Pentium) cai
    com instrução ilegal. No Windows em ARM, o emulador (Prism) só tem AVX2
    a partir do Windows 11 24H2;
  - **linux-x64: glibc 2.28**, porque sai de um contêiner
    `manylinux_2_28`;
  - **linux-arm64: glibc 2.39** (`pidfd_spawnp`, `__isoc23_strtol`), porque
    sai do `cross`, sem esse contêiner. No Debian 12 (e no Raspberry Pi OS
    Bookworm), a primeira chamada ao Polars falha com "GLIBC_2.38 not found"
    (contêiner arm64 pelo qemu), e o `acrux version`, que não chama o
    Polars, responde. Ficam de fora também Ubuntu 22.04, RHEL 9 e Amazon
    Linux 2023. Relatado com a sugestão do contêiner
    `manylinux_2_28_aarch64` no runner ARM, e pedido o `win-arm64`, que não
    existe (ErrorLSC/Polars.NET #40). O nativo arm64 não tem `target-cpu`:
    roda numa CPU ARMv8.0.

  Conferir num pacote novo: `readelf -V libnative_shim.so | grep -o
  'GLIBC_[0-9.]*' | sort -V | tail -1`.
- **A troca 0.6.0 → 1.0.1 foi medida antes e depois** (branch
  `refactor/polars-1.0.1`): o smoke test deu a mesma saída, e uma bateria de
  19 testes de estresse (CLI e caminhos da janela no parquet de 700 colunas ×
  5M, CSV de 2M, xlsx, SQLite) deu **os mesmos tempos (±12%) e de 18% a 74%
  menos memória**. Causa provável, não confirmada: a 1.0.0 tirou o mimalloc.
  Memória medida como pico de `RssAnon`, com o mesmo teto de 8 GB (o porquê
  está na armadilha de ordenar linhas largas, abaixo). O `filter` com
  janela larga continuou estourando na 1.0.1 com ordenar e cortar, streaming
  ou `TopK`: a correção em duas passadas vale para as duas versões.
- `PolarsSchema.ToDictionary()` **foi removido** → `ToFrozenDictionary()` para
  buscar por nome; **para listar colunas na ordem do arquivo, `ToList()`**
  (ver abaixo).
- **Ordenar e cortar linhas largas estoura a memória, mesmo em streaming**
  (700 colunas × 5M, 2026-11-08, cada teste num escopo `systemd-run --user
  --scope -p MemoryMax=8G -p MemorySwapMax=0`). Para mostrar as 10 linhas
  mais recentes de uma janela com 730 mil linhas:
  - `Filter` + `Sort` + `Limit`, `Collect(Engine.Streaming)` e `TopK`:
    **todos passaram de 8 GB**, porque guardam as linhas que casam, com as
    700 colunas, antes de cortar;
  - `WithRowIndex` + `IsIn` com as posições: decodifica as páginas de todas
    as colunas onde as linhas caem (2,6 GB com 10 linhas, morto com 1.000);
  - `Slice(posição, 1)` por linha: **o DataFrame de uma linha prende o bloco
    decodificado inteiro** (~13 MB por linha com 700 colunas) enquanto vive;
  - o que funciona: as posições pela coluna de tempo
    (`TimeFilter.NewestRows`, 0,1 s) e cada linha virando texto logo depois
    do `Slice`, com o DataFrame descartado (`PrintWindow` no CLI): 0,6 GB
    com 10 ou 100 linhas.

  **Meça pelo pico de `RssAnon`** (`/proc/<pid>/status`), não pelo RSS: o
  RSS conta as páginas do parquet mapeado (mmap), que o kernel descarta à
  vontade, e passou de 8 GB num teste que usava 0,7 GB de fato.
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
  falha com um float na linha 200.001 (probe, 2026-11-08), nem na 1.1.0
  (`ReadCsv` também). A causa está no shim: com o valor nulo, ele não chama
  `with_infer_schema_length`, e o Polars fica nas suas 100 linhas
  (ErrorLSC/Polars.NET #36, 2026-11-10). Estratégia do app (EnsureParquetAsync): amostra de 10k (~235 ms em
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
- `Expr.IsIn`: desde a 0.6.0 há overload direto `IsIn(IEnumerable<T>)`, e o
  `BuildPredicate` o usa desde 2026-11-09 (mesmas contagens que o antigo
  `IsIn(Lit(Series.From(...)).Implode())`, conferidas na 1.0.1).
- **A lista de valores (`values` e o dropdown do filtro) converte a coluna
  para texto antes de ordenar** (`Cast(DataType.String)`, 2026-11-09):
  números saem em ordem de texto (`10.5, 32.9, 7.25`), e binário que não é
  UTF-8, lista e duração **falham** ("invalid utf8", "cannot cast List
  type", "casting from Duration('ms') to String not supported"). Pendente:
  ordenar pelo tipo nativo e converter cada tipo para texto, validado por
  probe. O texto também difere do grid (fuso do arquivo, não o local; `true`
  × `True`), mas o filtro funciona: a lista e o `BuildPredicate` usam a mesma
  conversão.
- **O nulo ordena primeiro e ocupa uma vaga do `Limit`** da lista de
  valores: com `Limit(cap + 1)`, uma coluna com nulos e mais valores que o
  teto dizia que não fora cortada (achado em 2026-11-09). Por isso
  `Limit(cap + 2)`; o smoke test cobre (`values tipos.parquet i8 -n 1`).
- `WithRowIndex`/`ArgWhere` **agora existem** (índices como `UInt32Array`,
  validados) e poderiam substituir a varredura de máscara booleana no
  `FilterEngine` — mudança de desenho, requer aval do dono.
- `LazyFrame.Collect(Engine)`: enum `[Auto, InMemory, Streaming, Gpu]`.
  **`Engine.Gpu` no Windows é fallback silencioso para CPU** (validado em
  máquina com RTX 4070: nenhuma DLL CUDA carregada, tempos idênticos à CPU;
  nunca lança erro, então try/catch NÃO detecta). A engine GPU do Polars é
  o cudf-polars, camada Python sobre libcudf, Linux/WSL2 apenas — não existe
  no núcleo Rust que o Polars.NET embrulha. No código do polars-lazy 0.55.2,
  o `Engine::Gpu` só liga uma flag (`OptFlags::GPU`, que o Python usa para
  entregar o plano ao cudf-polars) e roda o motor em memória; igual na
  1.1.0 (ErrorLSC/Polars.NET #39, 2026-11-10). Benchmark no arquivo real
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
- Para validar APIs novas do pacote, use a skill `polars-probe`. Ela começa
  pelo `llms-full.txt` do autor (busca, nunca leitura inteira), que ensina a
  escrever, mas não cobre as armadilhas desta seção.
- **Erro de plano do Polars traz o plano inteiro** (coluna inexistente, tipo
  que não combina). A mensagem útil vem numa linha, seguida de uma linha em
  branco, `Resolved plan until failure:` e o plano, que pode listar todas as
  colunas selecionadas (visto no terminal C#, 2026-11-10). O
  `PolarsErrors.Brief` corta nesse marcador, e o app e o CLI mostram só a
  mensagem. Formato conferido na 1.0.1 com `select`, `filter` e `group_by`
  de coluna inexistente e com texto + número. Erro sem plano (um `Cast`
  impossível, por exemplo) sai igual.
- **Leitura de banco via ADO.NET** (probe no Linux, 2026-11):
  `DataFrame.ReadDatabase(IDataReader, batchSize)` recebe um *leitor*, não
  um caminho, e lê tudo para Arrow (eager). **Não fecha o leitor**: o
  chamador faz `using`. `LazyFrame.ScanDatabase(Func<IDataReader>, …)` abre
  dois leitores (schema + dados) e fecha os dois, mas `SinkParquet` sobre ele
  **não faz streaming**: o motor novo dá panic "not yet implemented:
  AnonymousScan" no stderr e cai para escrita eager. A RAM é a mesma do
  `ReadDatabase`, e ainda há ruído. Igual na 1.1.0 (ErrorLSC/Polars.NET
  #38, 2026-11-10). Para SQLite, nem o `ReadDatabase` serve:
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
- Colunas do grid: `Name` e `HeaderText` são sempre o nome real da coluna
  do parquet. O `ClipboardCopyMode` inclui o `HeaderText` no Ctrl+C, então
  nada de tela entra nele: o `▼` do filtro é desenhado (`GridPainting`).
- **`Rows.Clear()` lê a célula atual antes de apagar as linhas** (o
  `OnClearingRows` faz `CurrentCell = null`, e sair da célula pede o valor
  dela). Por isso a indireção (`_filteredRows`) só troca **depois** do
  `Rows.Clear()`: trocada antes, uma célula atual além do novo total lia
  fora da lista nova (IndexOutOfRange na pintura, X vermelho e a janela de
  erro do .NET). Achado pelo dono na VM, em 2026-11-10: filtrar o
  `tipos.parquet` com a célula atual na linha 3. Estava assim desde
  2026-09.
- **O grid copia por dois caminhos** (código do .NET 10). Ctrl+C e
  Ctrl+Insert são teclas de entrada (`IsInputKey`): passam pelo evento
  `KeyDown`, que pode parar a cópia com `e.Handled`. O Ctrl+Shift+C não é,
  e o `ProcessDialogKey` copia antes de qualquer `KeyDown`. Para carregar as
  colunas da seleção antes de copiar, o app marca o Ctrl+Shift+C como tecla
  de entrada no `PreviewKeyDown` e trata as três no `KeyDown`.
- **O Ctrl+A seleciona célula por célula** (`SelectAll` em `CellSelect`; o
  próprio código diz "Bonjour the scalability issues!") e desfaz o
  compartilhamento de cada linha: num arquivo de milhões de linhas, trava.
  Não tratado ainda.
- **Cor de fundo no cabeçalho exige `EnableHeadersVisualStyles = false`:**
  com os estilos visuais ligados, o cabeçalho ignora `BackColor`. Desligados,
  ele fica plano e segue o modo escuro.

## Convenções

- Código, comentários, strings de GUI e mensagens de commit em **inglês**
  (repo público).
  Comentários enxutos: explicar o *porquê*/a restrição, nunca narrar a linha.
- Trabalho **sempre em branch** com prefixo (`refactor/`, `feat/`, `docs/`) —
  **nunca commit direto em `master`**; a integração é por PR.
- PR com `gh pr create --body-file <arquivo>`. Merge com `--merge`, **nunca
  `--squash`** — os commits separados devem sobreviver no histórico
  (reafirmado pelo dono em 2026-11-08). O repositório apaga a branch sozinho
  ao mesclar.
- Movimentação de arquivo com `git mv`, para o histórico acompanhar o arquivo.
- Fluxo de trabalho do dono do projeto: ele valida o desenho antes do código.
  Para features novas, apresente a arquitetura/avaliação primeiro e aguarde o
  aval antes de implementar.
