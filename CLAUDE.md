# Acrux

Um núcleo de leitura de dados tabulares (parquet, CSV, xlsx e SQLite) sobre o
Polars.NET, com duas fronteiras: o app de janela no Avalonia (uma grade
desenhada, no Windows e no Linux) e o CLI. Otimizado para arquivos grandes
(700 colunas, milhões de linhas). O nome vem de α Crucis,
a estrela mais brilhante do Cruzeiro do Sul (o repositório já se chamou
ParquetGridViewer e o projeto, PolarsGridViewer).

## Build e execução

```
dotnet build                              # solução inteira (Acrux.sln), da raiz
dotnet run --project src/Acrux.Avalonia -- arquivo.parquet         # o app de janela (Windows e Linux)
dotnet run --project src/Acrux.Cli -- head arquivo.parquet -n 10   # CLI
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
`MemoryBudget`, `NativeHeap`, `PlanOrder`, `PolarsTableAdapter`, `ScriptHost`,
`Session`, `SystemMemory`, `TextDates`, `TimeFilter`), `Acrux.Avalonia`
(net10.0, o app de janela, com `AssemblyName` `Acrux`: o executável é o
`Acrux.exe` no Windows e o `Acrux` no Linux; ver "O app no Avalonia") e
`Acrux.Cli` (net10.0, console); os dois últimos referenciam o Core. O app
WinForms (`src/Acrux.WinForms`, só Windows) saiu na troca, em 2026-11-16: o
histórico do git o guarda. `dotnet build` na raiz basta, mas
**`dotnet run` e `dotnet publish` exigem apontar o projeto**: na raiz o `run`
não acha projeto ("Couldn't find a project to run") e o `publish` falha com
**NETSDK1099**, porque tenta aplicar single-file também no Acrux.Core, que é
biblioteca.

Pacotes: Polars.NET 1.0.1 (+ Native.win-x64/linux-x64/linux-arm64, Linq), Apache.Arrow
23 (a 1.0.1 exige Arrow ≥ 23.0.0 — não fazer downgrade),
Microsoft.CodeAnalysis.CSharp.Scripting (terminal C#), Microsoft.Data.Sqlite
(leitura de SQLite; traz o `e_sqlite3` nativo), Avalonia 12.1.6 (+ Desktop,
Themes.Fluent) e Tmds.DBus.Protocol 0.94.1 (o cliente de D-Bus do próprio
Avalonia, para o alto contraste do Linux; ver "O app no Avalonia"). Não há projeto de testes; o
CLI tem um smoke test em `tests/smoke` (ver "Deploy de teste do CLI").

Publicação — sempre com o caminho do `.csproj` (ver NETSDK1099 acima):

```
dotnet publish src/Acrux.Avalonia/Acrux.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish src/Acrux.Avalonia/Acrux.Avalonia.csproj -c Release -r linux-x64 --self-contained true
```

**Nunca publicar com `PublishTrimmed=true`:** o scripting do Roslyn (o
terminal C#) precisa dos metadados que o trimming tira. (O WinForms nem
permitia: erro NETSDK1175.) O publish traz os símbolos nativos do Skia
(`libSkiaSharp.pdb` e `libHarfBuzzSharp.pdb`, ~105 MB): o instalador e o
`.tar.gz` os deixam de fora, e o zip leva só o `Acrux.exe`. Single-file self-contained funciona,
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
   com um link `acrux` dentro), e, só no x64, o app de janela
   (`Acrux-v0.1.0-linux-x64.tar.gz`, ver "O pacote do Linux"), testado numa
   instalação descartável; no Windows, o instalador (`Acrux-v0.1.0-setup.exe`)
   e o app single-file (`Acrux-v0.1.0-win-x64.zip`). Dos mesmos arquivos do
   Linux saem os `.deb` (`acrux_0.1.0_amd64.deb`, `…_arm64.deb` e
   `acrux-desktop_0.1.0_amd64.deb`), instalados e testados em contêineres do
   Debian e do Ubuntu (ver "Os .deb"). Todos os pacotes levam a
   `LICENSE` e os avisos de terceiros (abaixo); os do app, também o `NOTICE`
   da arte. O app no arm64 fica de fora: não foi
   testado, e o nativo arm64 do Polars pede glibc 2.39, o que deixa de fora
   a maioria dos desktops ARM.
3. Ele cria um release **rascunho**, com o `SHA256SUMS` e as notas (texto
   fixo de instalação + as geradas pelo GitHub). O dono confere e publica.
4. Publicado o release, o dono roda o **Rebuild** do pacote `acrux` no Copr
   (ver "O RPM pelo Copr", abaixo). Por enquanto é à mão: o `Makefile` pega o
   último release publicado, então basta o clique. O rascunho não conta,
   então rodar antes de publicar remonta o release anterior.

**Os avisos de terceiros** (`scripts/third_party_notices.py`, desenho aprovado
em 2026-11-17). Antes, os pacotes levavam só a `LICENSE` do Acrux, sem o texto
das licenças do que vai dentro deles: o runtime do .NET, o Avalonia, o Skia,
o Arrow, o SQLite, o Roslyn e os ~500 crates Rust do nativo do Polars. A CI
roda o script depois de cada `dotnet publish` em pasta, sem rede:
- **o que entra:** o que o `*.deps.json` do publish lista, ou seja, só o que
  foi para a pasta (o app do Linux não leva os nativos do Windows). Os
  pacotes vêm do cache do NuGet que o restore encheu. O zip single-file não
  tem `.deps.json`, por isso o job do Windows publica a pasta do setup
  primeiro e o zip copia os avisos dela;
- **dois arquivos:** o `THIRD-PARTY-NOTICES.txt` (pacote, versão, licença e
  copyright, depois os textos, cada texto igual uma vez só) e o
  `THIRD-PARTY-NOTICES-polars.html`, a lista de crates que o autor do
  Polars.NET gera com o `cargo-about`, igual nas três plataformas;
- **os textos:** o arquivo de licença ou de avisos do próprio pacote, como
  está (o RTF do Roslyn vira texto); se o pacote só declara a licença, o
  texto padrão (`installer/third-party/MIT.txt` e `Apache-2.0.txt`, do
  `spdx/license-list-data`) com o copyright do `.nuspec`. Os pacotes do
  Polars.NET não trazem a licença do wrapper: ela está em
  `installer/third-party/Polars.NET-LICENSE.txt`, copiada do GitHub do autor;
- **a trava:** o build falha se um pacote tem licença fora do `ALLOWED`, se
  não tem texto de licença nem copyright, ou se um crate do Polars tem
  licença fora da lista. O `ALLOWED` são as licenças que o Fedora aceita para
  código (status `allowed` no fedora-license-data), porque o Copr só aceita
  material sob a lista do Fedora. A exceção é o `tiny-keccak` (CC0-1.0), que
  o próprio Fedora empacota pela regra de antes de 2022. **A CI parou numa
  dependência nova?** Leia a licença dela. Se o Fedora a aceita, acrescente
  o id ao `ALLOWED`, ou o pacote ao `OVERRIDES`; se não aceita, a dependência
  não entra;
- `--spdx` imprime as licenças de terceiros numa expressão SPDX, para o campo
  `License:` do RPM;
- os avisos do Skia e do HarfBuzz trazem o texto da LGPL, do `libmicrohttpd`
  (que oferece a opção da licença eCos), entre o material de terceiros que a
  Microsoft lista para o Skia. Eles vão como a Microsoft os escreveu.

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
- **Atualizar por cima limpa a versão anterior** (`[InstallDelete]`,
  2026-11-16): o Inno só sobrescreve, e uma DLL que a versão nova não traz
  ficaria na pasta. Antes de copiar, o setup apaga as DLLs da raiz e as 13
  pastas de recursos do .NET (`cs` a `zh-Hant`). Os dois `.exe` e os `.json`
  têm o mesmo nome em toda versão, e o `unins000.*` é o desinstalador.
  Medido comparando os dois publishes no laptop: o 0.3.0 por cima do 0.2.1
  deixaria 85 DLLs do WinForms (42 MB), todas nesses lugares. Não foi
  testado na VM (decisão do dono): a instalação do v0.3.0 lá foi numa pasta
  nova. Mudou o que o publish põe fora da raiz? Mude a lista.
- O `.iss` só compila no Windows (na CI). Para testar, baixe o artefato
  `windows` do PR com `gh run download`. O primeiro, de 70 MB, funcionou na
  VM limpa do Windows 11, onde o zip falhava por falta do runtime (teste do
  dono, 2026-11-08).

**O pacote do Linux** (`installer/linux/`, desenho aprovado em 2026-11-15:
`.tar.gz` com `install.sh`; o RPM está mais abaixo). É o
equivalente do setup por usuário do Windows, sem root:
- **onde:** o app em `~/.local/share/acrux` (o `XDG_DATA_HOME`), o
  `acrux.desktop` (menu e "Abrir com" para parquet, CSV, xlsx e SQLite: os
  tipos MIME das extensões do `.iss`, os mesmos do `.desktop` do RPM e do
  `.deb`; mudou uma lista, mude as outras), o
  ícone SVG da marca e o `acrux-parquet.xml`, que declara o
  `application/vnd.apache.parquet` (IANA, 2024; o `shared-mime-info` do
  Fedora 44 não o conhece) pela extensão e pela marca `PAR1`;
- **os padrões:** um app em `~/.local/share/applications` passa à frente dos
  do sistema, e sem padrão escrito o Acrux tomaria o duplo clique de todo
  tipo que lista. Por isso o `install.sh` anota quem abre cada tipo antes
  (`xdg-mime query default`), grava esses apps como padrão depois, e o Acrux
  só fica com o parquet sem dono. **O LibreOffice Calc 26.2 declara o
  parquet** (conferido no `.desktop` dele): onde ele está instalado, o
  parquet continua com ele, e o script imprime o `xdg-mime default` para
  trocar. O SQLite, que ninguém abria, fica com o Acrux;
- **`uninstall.sh`** tira tudo, menos os padrões gravados no
  `mimeapps.list` (sem o `.desktop`, o desktop os ignora), e apaga o
  `${TMPDIR:-/tmp}/Acrux` se nenhum Acrux estiver aberto;
- **teste:** com o `XDG_DATA_HOME` e o `XDG_CONFIG_HOME` numa pasta
  temporária, nunca no `~` do laptop, onde o `acrux.desktop` é da Yukidama.
  Conferido: o `desktop-file-validate`, o GIO reconhecendo o parquet com e
  sem extensão, a remoção limpa. A CI repete isso a cada PR.

**O RPM pelo Copr** (`installer/rpm/acrux.spec` e `.copr/Makefile`, desenho
aprovado em 2026-11-17). O Copr é o serviço de pacotes da comunidade do
Fedora: quem usa Fedora instala com `dnf copr enable` e `dnf install`.
- **O projeto:** `raphaelgaeta/Acrux`
  (copr.fedorainfracloud.org/coprs/raphaelgaeta/Acrux), criado pelo dono em
  2026-11-17. Chroots: `fedora-43`, `-44`, `-45` e `-rawhide`, em `x86_64` e
  `aarch64`, com "Follow Fedora branching" ligado (o Fedora novo entra
  sozinho). O resto ficou no padrão: sem rede no build, sem AppStream, sem
  `fedora-review`. Ficaram de fora: o `fedora-eln`, o EPEL e os derivados do
  RHEL (no RHEL 9, a glibc 2.34 não roda o CLI arm64; não testados) e o
  `ppc64le`, o `s390x` e o `riscv64` (sem nativo do Polars). O pacote
  `acrux` é SCM: o clone do GitHub, committish vazio (a `master`), o spec
  `installer/rpm/acrux.spec` e o método `make srpm`. O primeiro build que
  passou nos oito chroots foi o 11346404 (2026-11-18, v0.3.1), e a
  instalação pelo `dnf copr enable` foi conferida num contêiner Fedora 44 x86_64
  (os dois pacotes, smoke test e app no Xvfb) e num arm64 pelo qemu (o CLI,
  smoke test);
- **Reempacota o release, não recompila:** o RPM leva os `.tar.gz` do
  release, os mesmos arquivos que a CI testou e o `SHA256SUMS` confere. O
  Copr não exige as regras de empacotamento do Fedora (binário pronto pode),
  mas exige que todo o material esteja sob licença da lista do Fedora (ver
  "Os avisos de terceiros" e a licença do logo, em "Identidade visual").
  Compilar no Copr com o .NET do Fedora (`dotnet-sdk-10.0`) daria um pacote
  menor, mas seria um terceiro build que ninguém testou, e o NuGet pediria
  rede no build;
- **dois pacotes de um `.spec`:** o `acrux` (o CLI, em x86_64 e aarch64:
  todo Fedora desde o 40 tem a glibc 2.39 que o nativo arm64 do Polars pede)
  em `/usr/lib64/acrux-cli`, com `/usr/bin/acrux`; e o `acrux-desktop` (o
  app, só x86_64, como no release) em `/usr/lib64/acrux`, com
  `/usr/bin/acrux-desktop`, o `acrux.desktop`, o ícone e o tipo parquet. O
  `.desktop` tem o mesmo nome do `install.sh`: a instalação por usuário em
  `~/.local` passa à frente e o menu não mostra dois. Os dois pacotes são
  self-contained, sem depender do .NET do Fedora;
- **o que o `.spec` precisa dizer à mão:** nada de símbolos de depuração
  nem de `strip` (binários prontos), e sem os links de build-id, que
  colidiriam entre o CLI e o app, porque os dois levam as mesmas bibliotecas
  nativas. As bibliotecas embutidas não são anunciadas ao sistema, e o RPM
  não pede o `liblttng-ust` (o rastreamento opcional do .NET). O que o
  `rpm` não vê, porque é carregado em tempo de execução, entra como
  `Requires`: o `libicu` do .NET e as bibliotecas do X11 que o Avalonia
  carrega;
- **o `License:`** lista as licenças de terceiros que o
  `third_party_notices.py --spdx` imprime, mais a MIT do Acrux e, no app, a
  CC BY-ND 4.0 do logo. A CI confere (`--check-spec`) e para se divergir;
- **a versão:** o `Makefile` do Copr usa o `VERSION=` dado, a tag em que o
  Copr compilou, ou, compilando a `master`, o último release **publicado**
  no GitHub (rascunho não conta). Ele baixa os três `.tar.gz` do release,
  confere pelo `SHA256SUMS` e monta o pacote fonte, de ~281 MB. **A versão
  vai dentro do `.spec` do pacote fonte** (um `%global acrux_version` no
  topo de uma cópia): o Copr importa esse `.spec` e roda o `rpmbuild` de
  novo, sem o `--define` do `Makefile`. O primeiro build no Copr
  (2026-11-18) falhou nos oito chroots por isso, e o teste no laptop não
  pegou porque passava o `--define` também no rebuild. Mudou só o
  empacotamento, sem release novo? Suba o `Release:` do `.spec` (`2%{?dist}`);
- **testado em 2026-11-17** com o v0.3.1, no laptop: o pacote fonte pelo
  `Makefile`; os RPMs pelo `rpmbuild --rebuild` (o `%check` roda o CLI, o
  nativo do Polars incluído); e, num contêiner Fedora 44 limpo, o `dnf
  install` dos dois RPMs, o smoke test inteiro contra o `/usr/bin/acrux` e o
  app aberto 10 s num Xvfb, sem erro. O aarch64 foi montado com
  `--target aarch64 --nocheck` (o `%check` não roda binário arm64 no x86_64)
  e testado num contêiner Fedora 44 arm64 pelo qemu: smoke test inteiro em
  2,5 min;
- **para testar de novo, como o Copr faz:** `make -f .copr/Makefile srpm
  outdir=<pasta> VERSION=X.Y.Z`, depois `rpmbuild --rebuild --define
  "_topdir <pasta>" <src.rpm>`, **sem `--define` da versão** (com ele, o
  teste esconde um `.spec` que não se sustenta sozinho), e o contêiner
  (`podman run --rm -v <RPMS>:/rpms:ro,Z registry.fedoraproject.org/fedora:44`,
  com `dnf install /rpms/*.rpm sqlite tzdata diffutils`). Nunca instale o RPM
  no laptop: o `~/.local` e o menu dele são da Yukidama.

**Os .deb** (`installer/deb/build-deb.sh` e `test-deb.sh`, desenho aprovado
em 2026-11-18). O Debian oficial não serve: a política dele exige que tudo
possa ser modificado (a CC BY-ND do logo, embutido no app, iria para o
non-free) e compila tudo do código-fonte, sem rede, com dependências que já
estejam no Debian, e o .NET não está lá (conferido no packages.debian.org;
o Ubuntu tem o `dotnet10`). Por isso, a mesma ideia do RPM, sem
repositório:
- **o que sai:** o `acrux` (o CLI, amd64 e arm64) em `/usr/lib/acrux-cli`,
  com `/usr/bin/acrux`; o `acrux-desktop` (o app, só amd64) em
  `/usr/lib/acrux`, com `/usr/bin/acrux-desktop`, o `acrux.desktop`, o ícone
  e o tipo parquet. As licenças em `/usr/share/doc/<pacote>/`, com o
  `copyright` que o Debian exige. Comprimidos com xz: o `dpkg-deb` do Ubuntu
  usa zstd, que o Debian 11 não abre. Os gatilhos do `dpkg`
  (`shared-mime-info`, `desktop-file-utils`, o cache de ícones) atualizam o
  sistema sozinhos, sem scripts de instalação;
- **o `Depends`:** a glibc que o nativo do Polars pede (2.28 no amd64, 2.39
  no arm64: no Debian 12 arm64, o `apt` recusa com "acrux : Depends: libc6
  (>= 2.39) but 2.36-9 is to be installed", em vez do erro do Polars na
  primeira consulta); o ICU do .NET, cujo nome muda a cada versão do
  sistema (`libicu70` no Ubuntu 22.04, 72 no Debian 12, 74 no Ubuntu 24.04,
  76 no Debian 13), numa lista de alternativas de 70 a 80 (sistema novo com
  ICU mais novo: acrescente o nome); e, no app, o fontconfig e as
  bibliotecas do X11 que o Avalonia carrega em tempo de execução;
- **a versão:** a do release; a de CI (`0.0.0-ci.42`) vira `0.0.0~ci.42`,
  porque com o hífen o `dpkg` leria `ci.42` como revisão;
- **testado na CI, a cada PR:** o job `deb` instala os `.deb` com o `apt`
  em contêineres limpos do Debian 12 e 13 e do Ubuntu 22.04 e 24.04 (amd64,
  com o smoke test e o app aberto 10 s num Xvfb) e do Debian 13 e do Ubuntu
  24.04 (arm64, smoke test), e confere que o Debian 12 arm64 recusa. O
  release só sai com todos verdes. O mesmo teste roda no laptop:
  `podman run --rm -v .:/repo:ro,Z -v <debs>:/debs:ro,Z debian:13
  /repo/installer/deb/test-deb.sh /debs` (conferido em 2026-11-18 com o
  v0.3.1, nos seis sistemas, o arm64 pelo qemu);
- **atualizar:** não há repositório, então o `apt upgrade` não acha versão
  nova; a pessoa instala os arquivos do release seguinte. Um repositório
  APT (o OBS, build.opensuse.org, é o equivalente do Copr para o Debian e o
  Ubuntu) fica para quando alguém pedir.

Em PR, o mesmo workflow só compila e testa, sem release. A versão vem da tag
(`-p:Version=0.1.0`); fora de release vale `0.0.0-dev`
(`Directory.Build.props`), e na CI de PR, `0.0.0-ci.N`. O `acrux version`
mostra a versão e o commit (`0.1.0 (b0bdb9a)`); num deploy local, o arquivo
`COMMIT` tem prioridade. **Sem assinatura:** o Windows mostra o aviso do
SmartScreen. Os caminhos, conferidos em 2026-11-12:
- **SignPath Foundation (grátis): recusada em 2026-11-12.** O programa pede um
  projeto já conhecido (estrelas, contribuidores, artigos, discussões fora do
  GitHub); dá para pedir de novo quando houver esses sinais. Os termos deles
  também pedem licença aprovada pela OSI em todos os componentes, e o ícone
  está sob a CC BY-ND 4.0, que não é uma licença da OSI;
- **Azure Artifact Signing:** pessoa física só nos EUA e no Canadá (Microsoft
  Learn), então não serve;
- **Microsoft Store com MSIX:** a conta individual é grátis, e a Microsoft
  re-assina o pacote: quem instala pela Store não vê o aviso. Os downloads do
  GitHub continuariam sem assinatura;
- **certificado OV:** US$ 150–300 por ano, leva o nome civil do dono em cada
  arquivo e ainda mostra o aviso até ganhar reputação (desde 2024, o EV
  também).

**Mudou o que o app transmite? Atualize a seção "Privacy" do README.** Winget
fica para quando houver usuários. As Actions estão nas versões
conferidas em 2026-11-08 (checkout v7, setup-dotnet v6, upload-artifact v7,
download-artifact v8, todas em Node 24).

O `Acrux.Avalonia.csproj` traz tuning intencional de GC para desktop
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
marca cada trecho acelerado; a espera do próprio app nunca é acelerada.
Trecho cortado também leva selo (`edit_gif.py`, `a-b:cut`). O
`open_grid.gif` do topo é a gravação do dono na VM (build `b8e1d23`,
2026-11-12): abrir o 5M × 700, a contagem das linhas em tempo real e um
filtro de cabeçalho. Os 16 s em que o painel de Widgets do Windows ficou
aberto (manchetes de notícia) saíram com selo. **Gravação de tela para o
README: sem Widgets, notificação ou outro app na área gravada**, e a ação
no canto esquerdo da janela, que é o que cabe legível em 900 px. **O
`open_grid.gif` e o `script_terminal.gif` ainda são do app WinForms**: a
regravação com o app no Avalonia é da fase 6, e a legenda do README diz que
a gravação é de antes da troca.
O `messy_csv.gif` põe o pandas ao lado do
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

Logo de Kat Ribeiro (design), propriedade do dono do projeto, **sob a CC BY-ND
4.0, fora da MIT** (`assets/brand/NOTICE.md`; decisão do dono em 2026-11-17):
qualquer um pode redistribuir sem alterar, com crédito. Converter o formato ou
o tamanho (os PNG de um desktop, por exemplo) a própria licença permite, e não
conta como alteração. A licença não dá direito de marca: um fork usa outro
nome e outro ícone. **Por que essa licença:** o Copr só aceita material sob
licença da lista do Fedora, onde a CC BY-ND 4.0 é `allowed-content`
(fedora-license-data, conferido em 2026-11-17). O "todos os direitos
reservados" de antes barrava o RPM, e o ícone vai dentro do próprio app (o
`acrux.ico` no `Acrux.dll`). Paleta: `#8c5ea4` e `#acd7e7`. No repositório só
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
- **`acrux.ico`** (`src/Acrux.Avalonia/Assets/`): gerado de `assets/brand/acrux-icon.svg`. A arte não é quadrada
  (1022,4×1009,25): vai com margem transparente, sem esticar. Quadros 16 a 64 px
  em BMP e 256 em PNG, pelo `Icon` do System.Drawing.
- **`ApplicationIcon` só marca o `.exe`.** Funciona em publish feito no Linux
  (quadros conferidos na tabela de recursos PE, sem NETSDK1074). Janela com barra
  de título precisa do ícone também, pelo `AvaloniaResource`:
  `new WindowIcon(AssetLoader.Open(new Uri("avares://Acrux/Assets/acrux.ico")))`.
  O `avares://` leva o nome do assembly (`Acrux`): mudou o `AssemblyName`,
  mude as URIs. No Linux, o ícone do menu é o SVG que o `install.sh` instala.
- **Fonte Aller: nunca embutir nem commitar.** A licença gratuita limita a 25
  usuários e só permite redistribuir o `.zip` original. O README não aceita fonte
  própria; o nome aparece só como imagem, via logo. Azul no branco dá 1,54:1 de
  contraste, por isso o README usa roxo no tema claro e gradiente no escuro.

**Tema do app de janela** (desenho aprovado em 2026-11-10 para o WinForms,
levado ao Avalonia em 2026-11-15; `Grid/Palette.cs`, `Grid/Cells.cs`): claro
ou escuro conforme o sistema, e a marca só onde ajuda a ler.

- **Claro ou escuro:** `RequestedThemeVariant = ThemeVariant.Default` no
  `App`: o do Windows, ou o `color-scheme` do portal no Linux. A grade se
  repinta ao vivo (`ActualThemeVariantChanged`).
- **Nenhuma cor fixa fora da `Palette`:** a grade, a barra de valor e o
  texto do terminal pegam as cores dela, pelo modo.
- **Cada cor da marca no modo em que se lê** (WCAG; texto pede 4,5:1):
  - destaque (cabeçalho filtrado): preto sobre azul no claro, 13,6:1;
    branco sobre roxo no escuro, 4,94:1. Branco sobre azul daria 1,54:1;
  - a seleção e as faixas são a cor da marca misturada ao fundo (ver "A
    leitura da grade"), e o filtro tem um funil: a cor nunca é o único sinal;
  - **a barra de título é a do sistema:** numa barra roxa, o urso do ícone
    sumia (ver "O ícone é recorte", acima).
- **Em alto contraste, a marca sai** (ver "Alto contraste", em "O app no
  Avalonia"): o esquema do usuário vale mais.
- **Cinza e vermelho calculados, não do sistema:** o texto secundário
  (nulos, eco do terminal) fica a 60% entre o fundo e o texto, ~5,7:1 no
  claro e ~6,6:1 num fundo `#202020`. O erro é `Firebrick` no claro (6,7:1)
  e `#f48771` no escuro, porque o `Firebrick` daria 2,4:1.
- **A grade desenha, mas não muda o dado** (`Cells.Display` contra
  `Cells.CopyText`): o Ctrl+C, a barra de valor e a dica de uma célula
  cortada levam o dado, não o desenho. Os casos:
  - **nulo:** `null` cinza em itálico. O Ctrl+C copia vazio, e um texto vazio
    continua vazio;
  - **número que não cabe:** `###`, como no Excel. Cortado no fim, o número
    parecia outro (`922337203685…` para um valor de 19 dígitos);
  - **texto com quebra de linha:** uma linha só, com `↵` onde havia cada
    quebra. O CLI troca por espaço; na grade, o símbolo mostra que havia uma
    quebra;
  - **o filtro é desenhado, nunca escrito no nome:** com um `▼` no texto do
    cabeçalho, o Ctrl+C do WinForms levava `bool ▼` para o dado colado.
- **Números à direita, pelo tipo Arrow.** Atenção: **no Arrow,
  `BooleanType` herda de `NumberType`**, e por isso o `IsNumeric` o exclui.
  Os decimais (`Decimal32Type` a `Decimal256Type`) não herdam de
  `NumberType` e entram à parte (o porte para o Avalonia os tinha deixado à
  esquerda, até 2026-11-15). Conferido no parquet de 20 tipos.
- **Aparência se confere na VM do Windows 11**, nos dois modos e num tema de
  contraste (ligue-o pelas Configurações: o atalho não chega à VM).

## O app no Avalonia (Windows e Linux)

O app de janela era WinForms, só Windows, até 2026-11-16. A troca para o
Avalonia passou pelos degraus abaixo; o WinForms saiu do repositório (o
histórico do git o guarda).

- **O Core roda no Linux** pelo `Acrux.Cli` (net10.0), validado em degraus
  em 2026-11-07:
  1. o nativo carrega (`schema`, `columns`): **ok**;
  2. o caminho do grid (`head`: `GetDataFrameAsync` com `limit` → `ToArrow`
     → `PolarsTableAdapter.GetCellValue` célula a célula): **ok**. Achou
     tipos que o adaptador não conhecia, já corrigidos (ver Armadilhas);
  3. CSV cp1252 e vírgula decimal, xlsx, SQLite, temporários em
     `/tmp/Acrux`: **ok**;
  4. o terminal C# (`ScriptHost`/Roslyn) no Linux: **ok** (2026-11-15, no
     spike do Avalonia: a cadeia, o resultado coluna por coluna e a dica do
     `ScriptHost.Describe`).
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

- **Por que uma grade desenhada:** o `DataGridView` do WinForms custava
  tempo por linha e por coluna ao montar as linhas (47 s de janela branca no
  notebook de 2017, ~9 s na VM depois do PR #24), e o indexador `Rows[i]`
  varria a lista de linhas compartilhadas (1.745 ms de grade para filtrar
  até 950 linhas, até o `AddCopies` do PR #26).
- **O spike do Avalonia** (branch `spike/avalonia-grid`, `spike/AvaloniaGrid`,
  2026-11-15) mediu duas grades sobre o `ColumnCache`, no laptop e na VM. O
  `DataGrid` aberto do Avalonia percorre a lista duas vezes e cria um objeto
  para cada uma das 5M linhas (a mesma armadilha do `DataGridView`): 5,6 s
  para abrir na VM, 5,9 s para selecionar 300 linhas. A grade desenhada não
  guarda nada por linha: abriu em 1,0 s na VM (o WinForms, ~9 s), filtrou
  para 950 linhas com 8 ms de grade (1.745 ms) e selecionou 300 linhas em
  9 ms. O levantamento de alternativas (Uno, Photino + AG Grid, Dear ImGui,
  Terminal.Gui, GTK4, MAUI) e os números estão no README do spike.

**O app** (desenho aprovado em 2026-11-15): `src/Acrux.Avalonia`. Ficou ao
lado do WinForms até fazer tudo o que ele fazia; na troca (2026-11-16), o
instalador e a release passaram a apontar para ele, o executável ficou com o
nome `Acrux`, e o WinForms saiu. O branch `spike/avalonia-grid` fica no
GitHub, fora da `master`, com o levantamento e os números do spike.
- **A lógica sem tela fica no Core** (`Session`): o arquivo aberto, a cópia
  convertida (apagada no `Dispose`) e, nas próximas fases, os filtros e a
  cadeia de scripts. O Avalonia e uma futura interface de terminal (Terminal.Gui
  ou Consolonia) usam a mesma sessão, chamada da thread da interface, como o
  `ColumnCache`.
- **A grade é a desenhada do spike** (`Grid/TableGrid.cs`): pinta só as
  células da tela; a barra vertical conta linhas, não pixels (o Skia desenha
  em float de 32 bits, que perde pixels inteiros acima de ~16 milhões); as
  teclas do `DataGridView`; as regras de desenho (`Grid/Cells.cs`, ver
  "Tema do app de janela"); as cores (`Grid/Palette.cs`); as colunas
  carregam 100 ms depois que a rolagem para.
- **A leitura da grade** (desenho aprovado em 2026-11-15, depois de comparar
  com o Excel, numa página com cada proposta ligada e desligada). A grade
  copiava as linhas do Excel, mas não o conforto: a seleção era o bloco cheio
  do Windows clássico, sem número de linha, com texto de 13 px e todas as
  colunas com 120 px. Agora:
  - **seleção calma:** tinta clara da cor da marca sob o intervalo, o texto
    mantém a cor, contorno de 2 px na outra cor da marca, e a célula atual
    fica sem preenchimento;
  - **moldura:** os números de linha à esquerda (a linha do arquivo, de 1 em
    diante; com filtro, pulam como no Excel), e os cabeçalhos e números da
    seleção acesos, com a borda da cor do contorno;
  - **faixa** na linha e na coluna atuais (o "Focus Cell" do Excel 365);
  - **texto de 15 px** (o 11 pt do Excel), linhas de 26 px, cabeçalho de
    46 px com o **tipo do Polars sob o nome** (`ColumnCache.TypeNames`, o
    mesmo texto do `acrux schema`);
  - **barra de valor** (`Grid/ValueBar.cs`): a célula atual inteira, como a
    barra de fórmulas, com altura fixa de duas linhas, porque uma barra que
    crescesse com o valor moveria as linhas sob o ponteiro;
  - **largura pelo conteúdo:** o cabeçalho dá a largura inicial; quando a
    coluna carrega, ela se ajusta às linhas da tela, uma vez, com teto de
    320 px, e só cresce. As colunas que chegam juntas se ajustam juntas, e a
    coluna mais à esquerda da tela fica parada. Largura que o usuário escolheu
    não muda mais;
  - **decimais alinhados** nas linhas da tela, sem acrescentar zeros. Se uma
    tela com mais casas empurraria um número para fora de uma coluna que cabe
    nele, ele fica sem alinhar: o `###` é só para o que não cabe;
  - **algarismos de mesma largura** (`tnum`): o Segoe UI e a Noto Sans já os
    têm; a Adwaita Sans, fonte padrão do GNOME, não (medido);
  - **sem listras:** a faixa da linha atual faz o papel delas;
  - **datas sem a meia-noite:** uma coluna `date` chegava como `DateTime` e
    mostrava `11/23/2017 12:00:00 AM` em toda linha. Agora a grade, a barra
    de valor e o Ctrl+C levam só o dia, no formato curto do sistema
    (`11/23/2017`, `23/11/2017`), como o Excel. Vinha do WinForms;
  - **o filtro é um dropdown** (desenho aprovado em 2026-11-15): um botão em
    cada cabeçalho, sempre visível, como as setas do AutoFiltro do Excel (num
    arquivo de 700 colunas, quem abre pela primeira vez descobre o filtro sem
    procurar). Sem filtro, um ▾ cinza; com filtro, um funil sobre a cor de
    destaque: a forma muda, não só a cor. O botão fica 4 px antes da borda,
    que é a alça de redimensionar. Clicar no nome também abre o filtro, até
    a ordenação precisar desse clique. O filtro (`FilterDropdown`) abre na
    hora, preso ao cabeçalho (um `Popup` da janela), lendo os valores, e se
    preenche quando eles chegam; Esc ou um clique fora fecha sem aplicar, e
    Enter ou OK aplica. O clique fora passa adiante: no botão de outra coluna,
    fecha um e abre o outro; no botão da mesma coluna, só fecha. O resultado
    de um script fica sem botão, porque ali os filtros de cabeçalho estão
    desligados.

  Todo texto mantém 4,5:1 sobre as tintas, o cinza incluído (4,5 a 4,7:1 na
  seleção); o contorno, 4:1 ou mais. **No Windows, o Avalonia já desenha o
  texto com suavização por subpixel**, como o ClearType do Excel: as franjas
  coloridas aparecem numa foto da VM. Não há o que ajustar na nitidez. Os decimais (`Decimal32Type` a
  `Decimal256Type`) também vão à direita, como no WinForms: o porte os tinha
  deixado à esquerda. O tempo de desenho da janela não mudou (13 ms antes e
  depois, no arquivo de 700 colunas, pelo harness).
- **Fases**, cada uma com PR, medição no Linux e teste na VM (#27 a #33,
  mesclados em 2026-11-16; o #32 trouxe a leitura da grade, o alto contraste
  e o leitor de tela, e o #33, a troca, que saiu no release v0.3.0):
  1. esqueleto: janela, abrir arquivo (qualquer formato; xlsx e SQLite pela
     primeira aba ou tabela), grade, barra com a memória, tema do sistema,
     ícone, arquivo por argumento, limpeza de temporários e
     `Environment.Exit(0)` (**feita**, PR #27);
  2. a escolha de aba ou tabela do xlsx e do SQLite (`TableChoice`, como o
     `SheetSelectorForm`: tabelas antes das views; "Cancel" não abre nada)
     (**feita**, PR #28);
  3. o filtro do cabeçalho, "Clear all filters" e o seletor de colunas
     (**feita**, PR #29): os filtros, as linhas e as colunas escondidas na
     `Session`; a grade só troca a lista de linhas, e a coluna escondida fica
     no seu índice, sem largura;
  4. o terminal C# (**feita**, PR #30): a cadeia, a
     base congelada no 1º passo, o resultado e "Undo step"/"Restore file" na
     `Session`; o resultado numa grade própria, a do arquivo esperando por
     baixo com rolagem, filtros e colunas; os filtros de cabeçalho desligados
     no modo script, como no WinForms;
  5. acabamento (**feita**, PR #31): o `Trim` da
     memória a cada 5 s; no Linux, as arenas do glibc limitadas a 4 no início
     do `Main` (`NativeHeap.LimitArenas`, o `mallopt` do `MALLOC_ARENA_MAX`) e
     o `malloc_trim(0)` no mesmo ciclo de 5 s; as mensagens de erro numa
     janela, como o `MessageBox`; soltar um arquivo na janela;
  6. entrega (**feita**, PR #33): o `win-x64` e o `linux-x64`
     na CI, o instalador apontando para o app, o pacote do Linux (ver "O
     pacote do Linux", em Release) e o WinForms fora. Faltam os GIFs
     regravados com o app novo.
- **No Avalonia, o texto de uma `CheckBox` (ou de um botão) perde o primeiro
  `_`:** um `string` como conteúdo é lido como tecla de atalho, e
  `ITEM_000003` aparecia `ITEM000003` (achado por imagem na fase 3; o popup
  do spike também tinha). Nome de coluna e valor vão num `TextBlock`.
- **Alto contraste** (`HighContrast.cs`, paleta aprovada em 2026-11-15 na
  página "Paletas da grade"): a marca sai da grade e entra o esquema do
  usuário, como no WinForms.
  - **Windows:** o Avalonia diz quando o tema de contraste está ligado
    (`ContrastPreference`, pelo `GetHighContrast` do Win32), e as cores vêm do
    `GetSysColor`, como o `SystemColors` do WinForms: uma cor que o usuário
    editou também vale. Os padrões dos quatro temas (Aquatic, Desert, Dusk,
    Night sky) foram lidos dos `.theme` da VM.
  - **Linux:** não há cores do sistema para ler, então a paleta é fixa, sem
    matiz: preto no branco ou branco no preto, pelo `color-scheme`, e a
    seleção inverte. Quem diz se está ligado é a chave `contrast` do portal
    (`org.freedesktop.appearance`), que o Avalonia 12 não lê (lê só
    `color-scheme` e `accent-color`). O app a lê pelo D-Bus, com o
    `Tmds.DBus.Protocol` que o próprio Avalonia usa (mesma versão), e a lê de
    novo a cada `SettingChanged`. Neste laptop o portal é o gtk e responde
    `contrast = 0`.
  - **Muda com o app aberto:** no Windows, a grade passou do Aquatic para as
    cores da marca na hora, ao escolher "Nenhum" (teste do dono na VM,
    2026-11-15; o Avalonia avisa pelo `ColorValuesChanged`). No WinForms,
    trocar o tema pede reabrir. No Linux, a troca ao vivo não foi testada:
    ligar o contraste mexe na configuração do desktop, que é da Yukidama.
  - **O atalho Alt esquerdo + Shift esquerdo + Print Screen não chega à VM:**
    o Linux fica com o Print Screen. Na VM, use Configurações > Acessibilidade
    > Temas de contraste.
  - **As escolhas da página:** nulo na cor do texto, em itálico (a Microsoft
    reserva o GrayText para o que está desabilitado); linhas em GrayText;
    nenhuma cor misturada, porque nenhum tema de contraste tem (sem faixa da
    célula atual e sem cabeçalho aceso, só a borda); cabeçalho, números de
    linha e barra de valor nas cores de botão, porque o cabeçalho é clicável.
    A seleção é o Highlight com o texto dele, sem o contorno; a célula atual
    fica limpa, com borda de 2 px na cor do texto.
  - **O tema Fluent não tem alto contraste:** botões, diálogos, o terminal e
    o dropdown do filtro ficam com as cores dele. Por isso o texto que o app
    põe sobre eles (o eco e os erros do terminal, o fundo do dropdown) usa a
    paleta da marca (`Palette.Chrome`): na do sistema, podia cair sobre um
    fundo que ele não escolheu.
- **Leitor de tela** (`Grid/Announcer.cs`, desenho aprovado em 2026-11-15).
  O Avalonia 12 não tem padrão de tabela para leitor de tela (dos dez
  *providers* de automação, nenhum é de grade), e a grade não tem um elemento
  por célula. Por isso ela fala por uma *live region*, a ideia do `aria-live`:
  um elemento invisível cujo nome o leitor diz quando muda, onde quer que esteja
  o foco. No Windows o Avalonia transforma essa mudança no `LiveRegionChanged`
  da UI Automation (Narrator, NVDA); no Linux, manda a mudança de nome pelo
  AT-SPI (Orca).
  - **o foco na grade** diz "data grid" e o tamanho ("5,000,000 rows × 700
    columns", mais "filtered from …" com filtro), e a ajuda, as teclas;
  - **cada movimento** diz a célula, interrompendo a anterior: coluna, linha
    e o valor inteiro (o número por trás do `###`, "null", "empty text",
    "loading…", que vira o valor quando a coluna chega). Texto longo para em
    200 caracteres, dizendo quantos faltaram. Com mais de uma célula, também
    o tamanho da seleção;
  - **depois do que está sendo dito:** a cópia (a mesma linha da barra de
    status) e o resultado do filtro ("Filtered: 950 of 5,000,000 rows");
  - **Alt+↓** abre o filtro da coluna atual, como no Excel; o dropdown se
    chama "Filter: coluna";
  - a mesma frase duas vezes é dita duas vezes: um espaço de largura zero
    alterna no fim, porque um nome que não muda não gera evento.

  **Sem leitor de tela, nada disso custa:** sem um cliente de acessibilidade
  ouvindo, o AT-SPI não manda os eventos (conferido com `dbus-monitor` no
  barramento de acessibilidade). Por isso também não dá para conferir os
  anúncios sem o Orca ou o Narrator: o teste é do dono. **Com o Narrator, na
  VM, funcionou** (dono, 2026-11-16); o Orca não foi testado. Na VM, ligue o
  Narrator pelas Configurações: o Ctrl+Win+Enter fica com o Sway. Numa das 15 rodadas
  desse teste pelo harness, o processo caiu com uma exceção não tratada
  (SIGABRT), cuja mensagem se perdeu; não se repetiu em 14 rodadas. Falar
  passou a não poder derrubar o app (`try` no `Say`).
- **Lacunas conhecidas:** a grade fala pelo anunciador, sem a navegação de
  tabela do leitor de tela; o tema Fluent não segue o alto contraste (acima);
  e o Ctrl+C recusa mais de 1 milhão de células, uma trava provisória até o
  dono decidir a regra.

### Testar o app de janela no Linux

O app roda nativo: `dotnet run --project src/Acrux.Avalonia -- arquivo`, ou
o pacote instalado pelo `install.sh`. (O app WinForms rodava sob o Wine; com
ele fora, o Wine não serve mais a este projeto.) Aparência e ícone ainda se
conferem também na VM do Windows.

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

Desenho aprovado em 2026-11-08; publica o app do Avalonia desde a troca. Com `git config acrux.deployWindows true`,
o hook roda `scripts/deploy-windows.sh` depois do CLI:

- **Compila o commit do `current` do CLI**, o mais novo que passou no smoke
  test (o app de janela não tem teste automático: compilar é a conferência).
  Roda mesmo quando o deploy do CLI falha, porque segue o `current` dele.
  `scripts/deploy-windows.sh <commit>` compila outro à mão.
- **Publish igual ao do release** (win-x64, self-contained, single-file) em
  `~/.local/share/acrux-windows/<commit>/`, com `COMMIT.txt` dentro e
  `current`/`previous` como no CLI. ~5 s e ~300 MB por build.
- **Só recompila quando o app muda** (`Directory.Build.props`,
  `src/Acrux.Core`, `src/Acrux.Avalonia`): um commit de documentação, do CLI
  ou de teste mantém o build e registra "builds the same app as …: kept".
- **Entrega:** se o clone tiver `git config acrux.windowsDeliver '<comando>'`,
  o `sh` roda o comando com a pasta do `current` em `$1` (por exemplo, uma
  cópia para uma VM do Windows). Código 0 é "entregue" e fica gravado em
  `delivered`; 3 é destino fora do ar; outro código é falha. Nos dois
  últimos casos, a entrega é tentada de novo no próximo deploy, e o
  `current` não muda.
- O log é o mesmo `deploy.log` do CLI, com as linhas marcadas `windows:`.

### Benchmark (`bench/`, desde 2026-11-12)

O Acrux ao lado do pandas, do Polars e do DuckDB (pacote Python e CLI), nos
mesmos arquivos e na mesma máquina, com toda resposta conferida contra as
outras. O resultado e o método estão em `docs/benchmarks.md`; os comandos, em
`bench/README.md`. Desenho do dono: o laptop limitado ao que um PC comum tem
(4 threads com `taskset`, 8 GB sem swap, leitura a 500 MB/s por
`IOReadBandwidthMax`, cache do arquivo descartado antes de cada execução),
para não "roubar" com 32 threads e 31 GB. **Conferido num notebook de
verdade** em 2026-11-12 (i7-7500U, 8 GB, SATA, Windows 10, Defender ligado;
`bench/windows/`): as primeiras linhas e a primeira tela confirmaram a
emulação; as 10 mais recentes não (2,2 s contra 1,2 s do DuckDB, onde a
emulação dava 0,13 s de diferença). A busca linha a linha é trabalho de
CPU, e a emulação não limita a velocidade de cada núcleo. **A primeira tela
do benchmark são os dados, lidos pelo Core, não a janela:** no notebook, a
janela do app WinForms ficou 47 s branca enquanto o `DataGridView` montava
as linhas (~9 s na VM depois do PR #24). A grade do Avalonia não monta
linhas: o protótipo abriu o arquivo em ~1 s na VM. O notebook não foi medido
com ela, e o texto público diz isso. No Windows: o
PowerShell 5.1 grava o JSON com BOM (`utf-8-sig`); o pico é o
`PeakPagedMemorySize64` (o commit) amostrado; e uma rodada só é fria para
quem lê o arquivo primeiro depois do boot.
- **Os arquivos são gerados com semente fixa** (`make_stress_parquet.py`,
  `make_messy_files.py`), com o SHA-256 no `bench/README.md`: o original dos
  GIFs não tem gerador. Ficam fora do repositório (9 GB).
- **Cada ferramenta no seu melhor uso, mais o que se digita primeiro.**
  Quando uma estoura, as variantes de especialista entram também (`top_k`,
  streaming, laço por row group). Ler o CSV "certo" é com o formato da data
  escrito: o `try_parse_dates` do Polars levou 26 dos 28 s.
- **O CLI do DuckDB muda de comportamento com `AI_AGENT` no ambiente**
  ("agent mode"), e um agente rodando o benchmark tem essa variável: o
  `bench.py` a tira.
- **O que o benchmark achou contra o Acrux** (detalhes em
  `docs/benchmarks.md`):
  - num arquivo alto (10 colunas × 100M linhas), a primeira tela do app
    pede de 10 a 12 GB (3 rodadas por teto: 1 de 3 coube em 10 GB, 3 de 3
    em 12). Os dados ocupam ~7,2 GB em qualquer ferramenta; o resto é a
    memória de trabalho das colunas carregando ao mesmo tempo (cada uma
    custa ~o próprio tamanho enquanto carrega). Uma de cada vez pede
    ~8,0 GB: coube em 1 de 3 rodadas com 8 GB, ainda ~0,8 GB acima do
    Polars e do DuckDB. Perto do limite, uma rodada só engana: meça 3 por
    teto. A próxima mudança do `ColumnCache` é limitar os **bytes** em voo,
    não o número de colunas (no arquivo largo, todas de uma vez é o mais
    rápido), e para PCs com menos memória que as colunas, carregar faixas de
    linhas;
  - no mesmo arquivo, o agrupar pelo Core usa 2,3 GB (o Polars 2.1 do
    Python, 330 MB). O `filter --last` usava 5,9 GB e caiu para 1,2 GB com
    o motor de streaming (PR #20; o DuckDB, ~0,2 GB).
- **`bench/results/` guarda os JSON de cada rodada,** com a máquina e as
  versões, e as tabelas do documento saem deles (`summary.py`,
  `screen_memory.py`). Mudou o Acrux? Rode de novo e compare.

## Arquitetura

Fluxo: o seletor de arquivos (`StorageProvider`), o argumento da linha de
comando ou um arquivo solto na janela (parquet, CSV, xlsx ou SQLite) → **o formato é
decidido pelo conteúdo, não pela extensão** (`DataFrameProvider.DetectFormat`
lê os bytes iniciais: `PAR1` = parquet, `SQLite format 3\0` = SQLite, ZIP com
`xl/workbook.xml` = xlsx, texto sem NUL ou com BOM UTF-16 = CSV; `.xls`
antigo, outros ZIPs e binários são recusados com mensagem clara) → xlsx e
SQLite têm "tabelas" a escolher (`GetTableNames`: abas via ZIP puro, lendo
`xl/workbook.xml`; tabelas e views de dados via `pragma_table_list`, sem
as virtuais e suas `shadow`; 2+ abrem o
`TableChoice`) → xlsx: a aba escolhida vira parquet temporário por
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
`%TEMP%\Acrux` ou `/tmp/Acrux`, apagado na troca/fechamento pela `Session`) —
o app opera **somente sobre parquet** daí em diante, preservando o re-scan
barato → `ColumnCache.OpenAsync` (schema Arrow e número de linhas, só
metadados) → a grade desenhada com **todas** as colunas, sem nada por linha
→ cada coluna é lida quando chega à tela (ver "O cache de colunas",
abaixo). O botão "Columns" (`ColumnChoice`) esconde e mostra colunas: a
escondida fica na grade no seu índice, sem largura, para o índice da coluna
na grade ser sempre o do arquivo.

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
- **Na grade:** a célula de coluna fora da memória mostra `…` cinza
  (`Cells.Kind.Loading`), ou `error` se a leitura falhou (a coluna não é
  tentada de novo; a barra de valor mostra o porquê). Cada rolagem reinicia
  um timer de 100 ms, e a carga só começa quando a grade para de rolar. A
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

Fluxo de filtro: o botão do cabeçalho (ou Alt+↓) → `Session.ValuesAsync` →
`FilterEngine.GetDistinctValuesAsync` (lista limitada a `DistinctCap =
10.000`) → `FilterDropdown` → `Session.SetFilter` e `ApplyFiltersAsync` →
`GetVisibleRowsAsync` (máscara booleana varrida em C#) → `Session.Rows`, que
a grade só troca (`TableGrid.SetRows`).
**Toda consulta do `FilterEngine` recebe o filtro pronto, como `Expr?`**
(desde 2026-11-09): quem chama monta com `BuildPredicate` (valores por
coluna), `TimeFilter.Between` (tempo), ou os dois unidos por `&`. A
semântica Excel (a lista de uma coluna ignora o filtro dela mesma) é do
app: `BuildPredicate(filtros, except: coluna)`, no `Session.ValuesAsync`. O
CLI aplica todos. Um filtro novo cancela o que ainda estava calculando
(`ApplyFiltersAsync` devolve `false` para o cancelado).

Terminal C# (botão "C# terminal"): scripts Roslyn encadeados por **replay** —
`Session.Chain` guarda os textos dos passos; cada execução (`RunAsync`) re-roda a
cadeia inteira sobre um `ScanParquet` fresco, com o passo N recebendo em `df`
o `LazyFrame` (plano lazy, não dados) do passo N-1. O passo 1 recebe o arquivo
**como exibido na grade**: colunas visíveis + filtros ativos aplicados como
"passo 0" implícito (`FilterEngine.BuildPredicate` público é reutilizado), o
filtro antes do `Select`, porque o filtro pode estar numa coluna escondida.
Esse estado visual é **congelado no 1º passo** (na `Session`) —
obrigatório, pois o replay re-executa tudo a cada comando e a base não pode
derivar; "Restore file"/novo arquivo zeram a base. Os filtros NÃO são limpos
ao entrar em modo script: sobrevivem como base da cadeia e voltam ao
restaurar. Nada é coletado:
a cadeia compõe um único plano lazy (`Limit(ScriptHost.RowCap)` opcional),
que o `ScriptHost.BuildChainAsync` devolve e o `ColumnCache.FromPlanAsync`
mostra coluna por coluna (ver "O cache de colunas"). Passo que
termina em `DataFrame` (ex.: `.Collect()`) volta ao lazy via `df.Lazy()`
(validado; os dados dele ficam materializados no plano durante o replay).
Um passo com erro não entra na cadeia. "Undo step" remove o último e
re-executa; "Restore file" zera a cadeia e volta ao arquivo: o cache do
arquivo continua vivo sob o resultado do script, sem colunas fixadas, e só
relê as colunas que cederam memória ao resultado. Imports do script:
`Polars.CSharp` + estáticos (`Col`/`Lit`). Em modo script os filtros de
cabeçalho ficam desativados — a grade do resultado nem tem o botão, porque
não espelha mais o arquivo — até "Restore file" (decisão V1; a V2 composável
exigiria FilterEngine aceitar fonte lazy genérica). A grade do arquivo
espera por baixo, com rolagem, filtros e colunas.

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
| `src/Acrux.Core/Session.cs` | O arquivo aberto, sem tela: a cópia em parquet (apagada no fim), o cache de colunas, os filtros com as linhas que mantêm, as colunas escondidas e a cadeia do terminal com o resultado |
| `src/Acrux.Core/NativeHeap.cs` | O heap C do Linux: as arenas do glibc limitadas e o `malloc_trim`, que devolve ao sistema a memória que o Polars liberou |
| `src/Acrux.Avalonia/HighContrast.cs` | O alto contraste do sistema: o tema e as cores no Windows, a chave `contrast` do portal no Linux |
| `src/Acrux.Avalonia/ErrorDialog.cs` | O erro que interrompe o pedido, numa janela, com a mensagem selecionável |
| `src/Acrux.Avalonia/TerminalPanel.cs` | O terminal C#: o histórico, o código, Ctrl+Enter (ouvido no tunnel: a caixa de várias linhas trata o Enter), o limite de 1M linhas e "Undo step" |
| `src/Acrux.Avalonia/Program.cs` | Entrada do app: as arenas do glibc, a limpeza de temporários órfãos, o arquivo recebido como argumento (duplo clique, "Abrir com") e `Environment.Exit(0)` |
| `src/Acrux.Avalonia/MainWindow.cs` | A janela do app: abrir arquivo, a barra com a memória, a grade, o terminal, a costura dos filtros |
| `src/Acrux.Avalonia/FilterDropdown.cs` | O filtro do cabeçalho, como dropdown: os valores da coluna, a busca, "(Blanks)", o aviso de lista cortada |
| `src/Acrux.Avalonia/ColumnChoice.cs` | O diálogo do botão "Columns": quais colunas a grade mostra |
| `src/Acrux.Avalonia/TableChoice.cs` | Qual aba do xlsx ou tabela do SQLite abrir, quando há mais de uma |
| `src/Acrux.Avalonia/Grid/TableGrid.cs` | A grade desenhada: nada por linha, rolagem em linhas, teclas do `DataGridView`, seleção calma, números de linha, faixa da célula atual, largura pelo conteúdo, Ctrl+C |
| `src/Acrux.Avalonia/Grid/Announcer.cs` | O que o leitor de tela diz da grade: a célula atual, a seleção, a cópia, o filtro |
| `src/Acrux.Avalonia/Grid/ValueBar.cs` | A célula atual inteira acima da grade, como a barra de fórmulas: coluna, linha, tipo e valor |
| `src/Acrux.Avalonia/Grid/Cells.cs` | O texto de cada célula sem mudar o dado (`null`, `###`, `↵`, datas só com o dia), os tamanhos, o alinhamento dos decimais e o que o Ctrl+C copia |
| `src/Acrux.Avalonia/Grid/Palette.cs` | As cores da grade no claro e no escuro (a marca onde se lê) e, no alto contraste, as do sistema |
| `installer/acrux.iss` | O instalador do Windows (Inno Setup), por usuário, com o "Abrir com" |
| `installer/linux/install.sh` | A instalação por usuário no Linux, com o `uninstall.sh`, o `.desktop` e o tipo parquet (`acrux-parquet.xml`) |
| `installer/rpm/acrux.spec` | O RPM do Copr: reempacota o release em `acrux` (CLI) e `acrux-desktop` (app); o `.copr/Makefile` baixa o release e monta o pacote fonte |
| `installer/deb/build-deb.sh` | Os `.deb` do Debian e do Ubuntu, dos mesmos arquivos do release; o `test-deb.sh` os instala e testa num contêiner |
| `src/Acrux.Cli/Program.cs` | CLI: rota de leitura escolhida pelo formato detectado; `head` usa o caminho da grade |

### Princípios de design (não violar)

1. **Zero materialização**: os dados vivem só em buffers Arrow: os do
   arquivo no `ColumnCache`, uma coluna por lote, até metade da memória
   livre; o resultado de um script também, num segundo `ColumnCache`
   (inteiro só quando a ordem pode mudar e ele cabe). Células são lidas por demanda,
   só as da tela, no `Render` da grade, via `PolarsTableAdapter.GetCellValue`. NUNCA
   converter colunas inteiras em `object[]` — com milhões de linhas isso
   esgota a RAM e trava a máquina. E nunca carregar todas as colunas de uma
   vez: num arquivo largo, o processo cai sem exceção (ver Armadilhas).
2. **Indireção de índices**: filtros (e futura ordenação) produzem `int[]`
   de índices (`Session.Rows`), nunca cópias dos dados. Recalculados sempre
   a partir do original + conjunto de filtros ativos (`Session.Filters`),
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
  `malloc_trim(0)` volta ao tamanho do cache. O app no Avalonia usa os dois
  (`NativeHeap`, 2026-11-15): no arquivo de 700 colunas, teto de 3 GB e 60
  telas roladas, o pico de `RssAnon` caiu de 4,3 para 3,9 GB, e o app parado,
  de 4,2 para 3,4 GB.
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
    (`TimeFilter.NewestRows`, 0,1 s, no motor de streaming desde
    2026-11-12) e cada linha virando texto logo depois
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
  Limit` — por isso o `GetDistinctValuesAsync` usa `Collect(Engine.
  Streaming)`. **O dado novo veio do benchmark num arquivo alto** (10
  colunas × 100M linhas, 2026-11-12): sobre poucas colunas e muitas linhas,
  o motor em memória materializa cada expressão intermediária para todas
  as linhas. As posições do `filter --last` (`NewestRows`) caíram de
  5,5 GB e 2,8 s para 1,2 GB e 0,44 s com `Streaming`, e a contagem do
  mesmo comando foi junto. O comando inteiro, de 3,0 s e 6,0 GB para 0,9 s
  e 1,2 GB no "PC comum" do `bench/`; no arquivo largo, de 313 para 94 MB.
  Agrupar também cairia (2,3 GB → 169 MB, por probe), mas o resultado do
  terminal passa pelo `PlanOrder`, medido com o motor em memória: revalidar
  a ordem antes de trocar ali. Escolher o motor por consulta, medindo; nada
  de seletor para o usuário.
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
