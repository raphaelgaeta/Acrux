---
name: polars-probe
description: Validate Polars.NET 0.6.0 APIs in an isolated console before wiring them into the WinForms app. Use when adding any new Polars/Arrow call (expressions, IO, casts) or when the XML docs are ambiguous or silent about an API.
---

# Polars API Probe

O XML doc do Polars.NET (0.4.0 e 0.6.0) é incompleto (membros estáticos
ausentes) e APIs já crasharam nativamente (`DataFrame.FromArrow` na 0.4.0).
Nunca ligue uma API nova direto no app WinForms — valide antes num console
isolado, porque um crash nativo derruba o processo sem stack .NET
aproveitável.

## Fluxo

1. **Descoberta estática** — procure a assinatura no XML doc do pacote:
   ```bash
   grep -o 'M:Polars.CSharp.<Tipo>.<Metodo>[^"(]*' \
     ~/.nuget/packages/polars.net*/0.6.0/lib/net8.0/*.xml | sort -u
   ```
   (Na 0.6.0 o assembly principal é `Polars.NET.Core` — use o glob acima.
   Métodos sem parâmetros não têm parênteses no XML doc; não exija `(...)`
   no padrão.) Se não aparecer, o membro ainda pode existir: descubra por
   reflexão (passo 3) — foi assim que `DataType.String` foi encontrado.

2. **Console de teste** — crie (ou reutilize) um projeto console no
   scratchpad da sessão com este csproj:
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <OutputType>Exe</OutputType>
       <TargetFramework>net8.0</TargetFramework>
       <Nullable>enable</Nullable>
       <ImplicitUsings>enable</ImplicitUsings>
     </PropertyGroup>
     <ItemGroup>
       <PackageReference Include="Apache.Arrow" Version="23.0.0" />
       <PackageReference Include="Polars.NET" Version="0.6.0" />
       <PackageReference Include="Polars.NET.Native.win-x64" Version="0.6.0" />
     </ItemGroup>
   </Project>
   ```
   Gere um parquet sintético pequeno com `DataFrame.FromSeries(...)` +
   `WriteParquet`, exercite a API alvo e imprima um resultado verificável
   (soma, índices esperados, contagem). Rode com `dotnet run -c Release`.

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

## Fatos já validados na 0.6.0 (não re-testar)

- `ToArrow()` transfere posse: batch válido após `Dispose` do DataFrame.
- `FromArrow(RecordBatch)` funciona (na 0.4.0 era AccessViolation).
- `IsIn`: tanto `Lit(Series.From(...)).Implode()` (estilo 0.4.0) quanto o
  overload direto `IsIn(IEnumerable<T>)` funcionam.
- `PolarsSchema.ToDictionary()` não existe mais → `ToFrozenDictionary()`.
- `WithRowIndex(nome)` e `ArgWhere(expr)` existem; índices como `UInt32Array`.
- Strings → `StringViewArray`; `Unique()` preserva null (detecção de vazias).
- `Sort(string)` e `Limit(uint)` funcionam com parâmetros default no LazyFrame.
- `DataType.String/Int64/...` existem como estáticos, fora do XML doc.
- `Collect(Engine)`: enum `[Auto, InMemory, Streaming, Gpu]`; no Windows,
  `Gpu` cai para CPU em silêncio (sem DLL CUDA no processo, sem exceção) —
  detecção por try/catch não funciona. `Streaming` venceu o bench de groupby.
- Membros de enum também ficam fora do XML doc — descobrir com
  `System.Enum.GetNames` (qualificado: `using static Polars` sombreia `Enum`).
