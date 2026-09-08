# ADR 003 — Superfícies e ponto de entrada único

- **Data:** 2026-09-08
- **Status:** aprovado (aguardando implementação)
- **Escopo:** estrutura de projetos e distribuição. Não muda o Core.

## Contexto

O Acrux vai ter mais de uma superfície: a GUI WinForms que já existe, um CLI,
provavelmente um TUI e possivelmente um REPL sobre o `ScriptHost`.

A restrição que decide o desenho: **a GUI é WinForms, logo Windows-only, e
CLI/TUI são multiplataforma.** Não existe um binário único que sirva aos dois
mundos — o que existe é um binário por plataforma, com o mesmo nome e a mesma
interface de linha de comando.

O comportamento desejado:

```
acrux arquivo.parquet      → GUI no Windows, TUI no Linux
acrux schema arquivo       → imprime e sai
acrux head arquivo -n 20   → imprime e sai
acrux tui arquivo          → força TUI
acrux repl arquivo         → REPL (ScriptHost)
```

O caso sem subcomando é o que importa: torna a associação de arquivo trivial no
Windows (duplo clique passa o caminho como argumento) e dá o comportamento
esperado no Linux sem a pessoa aprender nada.

## Achados da investigação

Todos verificados em probe isolado, compilando no Fedora com
`EnableWindowsTargeting`.

### O executável de entrada não pode ser `net10.0` puro

Um executável `net10.0` que referencia um projeto `net10.0-windows` **não
compila**:

```
error NU1201: Project libwin is not compatible with net10.0.
Project libwin supports: net10.0-windows7.0
```

Isso corrige a estrutura alvo: `src/Acrux/` não é um executável neutro que
carrega a UI de Windows condicionalmente — é um executável
**multi-target**, `<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>`,
com a `ProjectReference` para a UI de Windows condicionada ao TFM:

```xml
<ItemGroup Condition="'$(TargetFramework)' == 'net10.0-windows'">
  <ProjectReference Include="../Acrux.WinForms/Acrux.WinForms.csproj" />
</ItemGroup>
```

A alternativa — manter a entrada neutra e carregar a UI por reflexão em tempo
de execução — foi descartada: além de quebrar o publish single-file, o processo
precisaria do framework `Microsoft.WindowsDesktop.App`, que um app `net10.0`
não referencia.

### WinForms como biblioteca funciona, com uma pegadinha

`ApplicationConfiguration.Initialize()` é **gerado por source generator apenas
para `OutputType=WindowsApplication`**. Numa biblioteca o build falha com
`error WFO0001: Only projects with 'OutputType=WindowsApplication' supported`.

A correção é chamar as três coisas em que ele expande:

```csharp
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.SetHighDpiMode(HighDpiMode.SystemAware);
```

Com isso a biblioteca `net10.0-windows` com `UseWindowsForms` compila normal.

### O `FrameworkReference` flui pela `ProjectReference`

O executável **não** precisa declarar `UseWindowsForms`. O
`runtimeconfig.json` do TFM de Windows sai com os dois frameworks:

```json
"frameworks": [
  { "name": "Microsoft.NETCore.App",      "version": "10.0.0" },
  { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" }
]
```

E o TFM neutro sai só com `Microsoft.NETCore.App` — ou seja, o binário de Linux
não carrega peso de desktop.

### `[STAThread]` e ciclo de vida

O TFM `net10.0-windows` define o símbolo `WINDOWS`, então o atributo entra por
compilação condicional no `Main` único:

```csharp
#if WINDOWS
    [STAThread]
#endif
    private static int Main(string[] args)
```

O `Application.Run` sai do `Program.cs` da UI e vira uma chamada de biblioteca
(`Shell.Run(path)`), invocada pelo despacho. Duas coisas que hoje moram no
`Program.cs` do `Acrux.WinForms` mudam de dono e **melhoram** com isso:

- `DataFrameProvider.CleanStaleTempFiles()` passa para a entrada, antes do
  despacho. Hoje só a GUI varre temporários órfãos, mas o CLI também cria
  temporários ao abrir CSV/xlsx — a varredura passa a valer para todas as
  superfícies.
- `Environment.Exit(0)` (a garantia contra threads de Polars/rayon e Roslyn
  segurarem processo fantasma) passa a ser responsabilidade da entrada, que é
  quem sabe se a superfície terminou.

### Despacho por subcomando e publish single-file

Compatíveis. O publish single-file self-contained funciona nos dois TFMs, e o
binário Linux foi **executado**: despacho por subcomando correto, incluindo o
caso sem subcomando.

Uma armadilha nova: `dotnet publish` sem `-f` num projeto multi-target falha
com **NETSDK1129** (`The 'Publish' target is not supported without specifying a
target framework`). É irmão do NETSDK1099 já documentado — publicar passa a
exigir `-f net10.0-windows` ou `-f net10.0`.

## Alternativas consideradas

### 1. Binário único com despacho por subcomando (escolhida)

Um executável `acrux` por plataforma, superfícies como bibliotecas.

### 2. Um pacote com vários binários (`acrux` + `acrux-gui`)

Distribuir junto, mas com dois executáveis: um console e um WinExe.

**Rejeitada:** é a solução para o problema errado. O único ganho real seria
contornar o comportamento de console do Windows (ver Riscos), e ela cobra por
isso a associação de arquivo apontando para um binário diferente do que a
pessoa digita, mais dois nomes para documentar e versionar.

### 3. Pacotes separados por superfície (`acrux-cli`, `acrux-gui`)

**Rejeitada:** fragmenta a identidade do projeto e empurra para o usuário uma
decisão que é detalhe de implementação — no Linux não existe GUI, então
"escolher entre CLI e GUI" é uma pergunta sem resposta na metade das
plataformas. Também duplica release, changelog e instruções de instalação.

### 4. GUI que instala um atalho de CLI

A GUI é o produto e, ao instalar, registra um `acrux` no PATH.

**Rejeitada:** inverte a dependência. Faz o CLI e o TUI dependerem de um
instalador de GUI que não existe no Linux, onde justamente eles são a única
superfície.

## Decisão

**Alternativa 1.** Um executável `acrux` como ponto de entrada único, com
despacho por subcomando, e as UIs como bibliotecas que ele carrega. Sem
subcomando, a superfície padrão é a da plataforma: GUI no Windows, TUI no
Linux.

## Consequências na estrutura de projetos

```
src/
  Acrux.Core/          lógica (net10.0)
  Acrux/               executável: parsing de argumento e despacho
                       (net10.0 + net10.0-windows, AssemblyName = acrux)
  Acrux.WinForms/      biblioteca de UI (net10.0-windows), referenciada só
                       pelo TFM de Windows
  Acrux.Tui/           biblioteca de UI multiplataforma (futuro)
```

Mudanças concretas:

- O `Acrux.Cli` deixa de ser superfície irmã e **vira o ponto de entrada**:
  `src/Acrux.Cli` → `src/Acrux` (com `git mv`, para o histórico acompanhar).
- O `Acrux.WinForms` deixa de ser `WinExe` e vira `Library`. Seu `Program.cs`
  desaparece; o `AssemblyName = Acrux` que ele carrega hoje passa para o
  executável de entrada.
- O `OutputType` do executável é condicional por TFM: `WinExe` no
  `net10.0-windows`, `Exe` no resto (verificado que compila).

## Implicação de distribuição

**Um nome, uma instalação por plataforma.** O usuário instala `acrux` e tem
todas as superfícies que a plataforma dele suporta. Não existe `acrux.cli` nem
`acrux.gui` para escolher, nem release separado por superfície: o artefato do
Windows traz GUI + CLI + TUI + REPL; o de Linux traz CLI + TUI + REPL. A
diferença entre eles é consequência da plataforma, não uma decisão empurrada
para quem instala.

## Riscos

### 1. Console no Windows: `WinExe` versus `Exe`

**É o primeiro da lista de propósito:** é o único risco que atinge as duas
superfícies ao mesmo tempo, e o único que degrada a experiência de quem usa o
programa — os outros dois custam trabalho de quem o mantém.

No Windows os dois modos têm defeitos opostos, e o binário único precisa dos
dois comportamentos. `WinExe` não recebe console anexado, então a saída de
`acrux schema` não aparece no `cmd` sem `AttachConsole(ATTACH_PARENT_PROCESS)`;
`Exe` mostra uma janela de console atrás da GUI no duplo clique.

As duas falhas são concretas e nenhuma é cosmética:

- Se `acrux schema` não imprimir no `cmd`, **o CLI no Windows está quebrado** —
  o subcomando roda, termina com sucesso e não mostra nada.
- Se a janela de console aparecer atrás da GUI no duplo clique, **a
  experiência piora em relação ao que existe hoje**: o `Acrux.exe` atual é
  `WinExe` e abre limpo.

**Mitigação prevista:** `WinExe` no TFM de Windows mais `AttachConsole` via
P/Invoke nos subcomandos de console. É a razão de o `OutputType` ser
condicional por TFM.

**Verificar o `AttachConsole` é pré-requisito para considerar este desenho
fechado**, e a verificação **só pode ser feita numa máquina Windows** — não há
como exercitá-la compilando no Linux. Até lá o desenho está aprovado com uma
pendência conhecida, não validado. Se a mitigação não se sustentar, a
alternativa 2 (dois binários) volta à mesa.

### 2. Multi-target vira obrigação, não escolha

A entrada precisa dos dois TFMs (NU1201). Isso significa build e publish
duplicados no CI, e `dotnet publish` passando a exigir `-f` (NETSDK1129).
Custo aceito: é a única forma de um executável carregar a UI de Windows.

### 3. Fronteira de superfície precisa ficar no Core

Se lógica vazar para a `Acrux.WinForms` — como o
`DataFrameProvider.CleanStaleTempFiles()` que hoje só a GUI chama — o TUI
nasce sem ela. O ponto de entrada único ajuda, porque tudo que é comum a todas
as superfícies tem agora um lugar óbvio para morar.

## Explicitamente não verificado

- **O comportamento de console no Windows não foi testado**: toda a
  investigação rodou no Fedora, compilando com `EnableWindowsTargeting`. Que
  `WinExe` + `AttachConsole(ATTACH_PARENT_PROCESS)` dê a saída esperada no
  `cmd` e no PowerShell é comportamento documentado do Windows, **não medido
  aqui**. Precisa de uma passada numa máquina Windows antes de a implementação
  fechar.
- A execução do binário foi feita **apenas no TFM de Linux**. O binário de
  Windows compila e publica em single-file, mas não foi executado.
