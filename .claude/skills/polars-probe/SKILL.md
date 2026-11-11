---
name: polars-probe
description: Validate Polars.NET 1.0.1 APIs in an isolated console before wiring them into the app, after looking them up (grep, never a full read) in the author's llms-full.txt. Use when adding any new Polars/Arrow call (expressions, IO, casts) or when the XML docs are ambiguous or silent about an API.
---

# Polars API Probe

O XML doc do Polars.NET (0.4.0 a 1.1.0) é incompleto (membros estáticos
ausentes) e APIs já crasharam nativamente (`DataFrame.FromArrow` na 0.4.0).
Nunca ligue uma API nova direto no app WinForms — valide antes num console
isolado, porque um crash nativo derruba o processo sem stack .NET
aproveitável. O ponto de partida é a referência do autor: ela diz como se
escreve; o probe diz se funciona e quanto custa.

## Fluxo

0. **Referência do autor** — o `llms-full.txt`
   (<https://errorlsc.github.io/Polars.NET/llms-full.txt>; o índice é o
   `llms.txt`), que o autor do Polars.NET recomendou para poupar probes
   (issue #13 do Acrux, 2026-11-10). Tem ~167 KB e ~4 mil linhas: **nunca
   leia inteiro.** Guarde em cache e busque:
   ```bash
   f=${XDG_CACHE_HOME:-$HOME/.cache}/acrux-dev/polars-llms-full.txt
   mkdir -p "${f%/*}"
   if [ -f "$f" ]; then z=(-z "$f"); else z=(); fi   # só baixa se mudou (304)
   curl -fsSL -R "${z[@]}" -o "$f" https://errorlsc.github.io/Polars.NET/llms-full.txt
   grep -n -i -C4 '<Metodo>' "$f" | head -80
   grep -n '^# Chapter:' "$f"   # capítulos; leia um só com sed -n 'A,Bp'
   ```
   - **Não tem versão:** acompanha o último release (a 1.1.0 em
     2026-11-10), e o Acrux usa a versão fixada nos `.csproj`. Uma API que
     aparece lá pode não existir na versão em uso: confirme a assinatura no
     XML doc (passo 1) ou por reflexão (passo 3).
   - **Cobre API, idiomas e conceitos, não as armadilhas.** Em 2026-11-10,
     nenhuma das armadilhas do `CLAUDE.md` aparecia lá (`inferSchemaLength`,
     `Engine.Gpu`, `DataType.String`, o runtime do VC++: zero ocorrências).
     Com o exemplo dele em mãos, o probe fica menor: exercite só o
     comportamento que importa ao Acrux (passo 4).

1. **Descoberta estática** — procure a assinatura no XML doc do pacote:
   ```bash
   grep -o 'M:Polars.CSharp.<Tipo>.<Metodo>[^"(]*' \
     ~/.nuget/packages/polars.net*/1.0.1/lib/net8.0/*.xml | sort -u
   ```
   (O XML principal mudou de pacote: na 0.6.0 era o do `Polars.NET.Core`,
   na 1.0.1 é o `Polars.CSharp.xml` do `polars.net` — o glob acha os dois.
   Métodos sem parâmetros não têm parênteses no XML doc; não exija `(...)`
   no padrão.) Se não aparecer, o membro ainda pode existir: descubra por
   reflexão (passo 3) — foi assim que `DataType.String` foi encontrado.

2. **Console de teste** — crie (ou reutilize) um projeto console no
   scratchpad da sessão com este csproj:
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <OutputType>Exe</OutputType>
       <TargetFramework>net10.0</TargetFramework>
       <Nullable>enable</Nullable>
       <ImplicitUsings>enable</ImplicitUsings>
     </PropertyGroup>
     <ItemGroup>
       <PackageReference Include="Apache.Arrow" Version="23.0.0" />
       <PackageReference Include="Polars.NET" Version="1.0.1" />
       <!-- o nativo do sistema onde o probe roda: linux-x64 no laptop -->
       <PackageReference Include="Polars.NET.Native.linux-x64" Version="1.0.1" />
     </ItemGroup>
   </Project>
   ```
   Gere um parquet sintético pequeno com `DataFrame.FromSeries(...)` +
   `WriteParquet`, exercite a API alvo e imprima um resultado verificável
   (soma, índices esperados, contagem). Rode com `dotnet run -c Release`.
   Para testar o código do próprio Acrux, troque o `Polars.NET` por um
   `<ProjectReference>` para `src/Acrux.Core/Acrux.Core.csproj`.
   **Memória:** meça o pico de `RssAnon` (em `/proc/<pid>/status`), não o
   RSS, que conta o parquet mapeado; e rode o que pode estourar dentro de
   `systemd-run --user --scope -p MemoryMax=8G -p MemorySwapMax=0`.

3. **Reflexão quando o XML cala** — dentro do console:
   ```csharp
   foreach (var m in typeof(Polars.CSharp.DataType)
       .GetMembers(BindingFlags.Public | BindingFlags.Static))
       Console.WriteLine($"{m.MemberType}: {m}");
   ```
   (Reflexão via PowerShell no dll falha — faça dentro do console que já
   referencia os pacotes.)

4. **Asserte semântica, não só compilação** — ex.: para máscaras de filtro,
   confira os índices exatos esperados; para ownership de memória
   (`ToArrow`/`Dispose`), leia os dados após `GC.Collect()` e compare
   checksum.

5. Só depois de o probe passar, escreva o código no app. Se descobrir um
   fato novo relevante (crash, API ausente, comportamento de memória),
   registre na seção "Armadilhas conhecidas" do CLAUDE.md.

## Fatos já validados (0.6.0; os marcados com ✓ revalidados na 1.0.1)

- `ToArrow()` transfere posse: batch válido após `Dispose` do DataFrame.
- ✓ `FromArrow(RecordBatch)` funciona (na 0.4.0 era AccessViolation), e o
  DataFrame sobrevive ao `Dispose` do batch de origem (GC + realocação nativa).
- `IsIn`: tanto `Lit(Series.From(...)).Implode()` (estilo 0.4.0) quanto o
  overload direto `IsIn(IEnumerable<T>)` funcionam.
- `PolarsSchema.ToDictionary()` não existe mais → `ToFrozenDictionary()`, que
  **não preserva a ordem das colunas**; para nomes em ordem, `ToList()`
  (tuplas nome/tipo) ou `ToArrowSchema().FieldsList`.
- ✓ `WithRowIndex(nome)` e `ArgWhere(expr)` existem; índices como `UInt32Array`.
- ✓ Strings → `StringViewArray`; `Unique()` preserva null (detecção de vazias).
- `Sort(string)` e `Limit(uint)` funcionam com parâmetros default no LazyFrame.
- ✓ **Na 1.0.1, `LazyFrame` e `Expr` são reutilizáveis**: `Schema` e um 2º
  `Collect` funcionam depois do 1º, e a mesma `Expr` serve a dois filtros e
  combinada consigo mesma. (Na 0.6.0, `Collect()` consumia o handle —
  "Handle is invalid" — e combinar consumia a `Expr`.)
- `DataFrame.Lazy()` funciona e o plano sobrevive ao Dispose do DataFrame.
- ✓ `ScanCsv(path, separator:, decimalComma:, tryParseDates:)` + `SinkParquet`
  convertem CSV→parquet em streaming; vírgula decimal e datas validadas.
- `DataType.String/Int64/...` existem como estáticos, fora do XML doc (ainda
  na 1.1.0) e fora do `llms-full.txt`.
- `Collect(Engine)`: enum `[Auto, InMemory, Streaming, Gpu]`; no Windows,
  `Gpu` cai para CPU em silêncio (sem DLL CUDA no processo, sem exceção) —
  detecção por try/catch não funciona. `Streaming` venceu o bench de groupby.
  A causa está no próprio Polars: no polars-lazy 0.55.2, `Engine::Gpu` só
  liga `OptFlags::GPU` e roda o motor em memória.
- **Relatadas ao autor em 2026-11-10**, todas reproduzidas na 1.0.1 e na
  1.1.0: ErrorLSC/Polars.NET #36 (`inferSchemaLength` nulo = 100 linhas),
  #37 (`VCRUNTIME140.dll`), #38 (`ScanDatabase` + `SinkParquet` eager) e #39
  (`Engine.Gpu`). Ao subir a versão do Polars.NET, veja o estado delas
  (`gh issue list -R ErrorLSC/Polars.NET`) antes de repetir o probe.
- Membros de enum também ficam fora do XML doc — descobrir com
  `System.Enum.GetNames` (qualificado: `using static Polars` sombreia `Enum`).
