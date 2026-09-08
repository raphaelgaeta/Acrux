# ADR 001 — Datasets multi-arquivo no Acrux.Core

- **Data:** 2026-09-08
- **Status:** proposto (aguardando aval)
- **Escopo desta decisão:** `Acrux.Core` e `Acrux.Cli`. A GUI fica de fora —
  ver "Questões em aberto".

## Contexto

O `DataFrameProvider` valida `File.Exists` num caminho único, então só abre um
arquivo por vez. Isso impede abrir datasets fragmentados, que é a forma nativa
dos dados reais aqui: o corpus WorldCat está em **6.946 shards parquet,
111 GB, 440,9 milhões de linhas** (o conjunto completo em `staging/` passa de
18.000 shards). Nenhum shard passa de 17,3 MB — não existe arquivo único para
abrir.

A limitação é do Core, então afeta CLI e GUI igualmente.

## Alternativas consideradas

Todas foram medidas num probe isolado (convenção da skill `polars-probe`),
contra o dataset real, com Polars.NET 0.6.0 em Linux.

### A. Glob/diretório direto (`ScanParquet` com padrão)

`ScanParquet` aceita um único `string` e expande glob nativamente (parâmetro
`glob`, default `true`). Diretório também funciona — e **recursa**, ao
contrário do glob simples.

O problema é o custo: ler `lf.Schema` sobre o padrão lê o rodapé de **todos**
os arquivos, linearmente.

| Arquivos | Schema |
|---|---|
| 100 | 2,4 s |
| 1.000 | 24,4 s |
| 6.946 | 221 s (350 s numa segunda corrida) |

~24 ms por arquivo. Inviável para abertura interativa.

### B. Glob + `schema:` explícito (schema de referência)

Mesma chamada, passando no parâmetro `schema:` o schema lido de **um** shard.
O custo por arquivo desaparece:

| Operação | Tempo |
|---|---|
| Schema de 1 shard | 45,8 ms |
| Schema de 1.000 shards **com** hint | **17,9 ms** (era 24.393 ms) |
| Primeiras 5 linhas com hint | 699 ms |

### C. `LazyFrame.Concat(frames, ConcatType.Diagonal)`

É a única alternativa que faz **união real** de schemas divergentes (união de
colunas, null onde falta). `Vertical` exige schemas idênticos; `Horizontal`
falha com coluna duplicada.

Custo: exige abrir N `LazyFrame` — 6.946 handles nativos — e não se combina
com o `schema:` do item B, perdendo a otimização que torna a abertura viável.

### D. Manter arquivo único

Descartada: não abre o dataset que motivou o trabalho.

## Comportamento de divergência de schema (medido)

| Caso | Resultado |
|---|---|
| Ordem de colunas diferente | **OK** — casamento por nome |
| Coluna faltando num shard | Falha por padrão; `allowMissingColumns: true` preenche null |
| Coluna **extra** num shard | **Falha sempre** — sem escape na API 0.6.0 |
| Tipo divergente na mesma coluna | **Falha** — sem unificação por supertipo |

Dois pontos que valem destaque:

1. **A divergência não aparece na abertura.** O schema exposto pelo scan é o do
   primeiro arquivo; o erro só estoura no `Collect` — depois de o usuário já ter
   escolhido colunas.
2. **Coluna extra não tem contorno.** Falha mesmo com `allowMissingColumns` e
   mesmo projetando só as colunas conhecidas: o erro ocorre ao abrir o arquivo,
   antes da projeção. O nativo sugere `extra_columns='ignore'`, parâmetro que a
   API C# 0.6.0 não expõe. A checar na 0.7.0.

## Decisão

**Alternativa B**, com três regras:

1. **Aceitar diretório e glob.** A discriminação é barata (`Directory.Exists`
   vs `File.Exists` vs presença de `*`/`?`) e as três formas desembocam na
   mesma chamada. Documentar que diretório recursa e `*.parquet` não.
2. **Primeiro shard como schema de referência**, repassado em `schema:`, com
   `allowMissingColumns: true`. Isso torna a abertura O(1) em vez de O(N) e faz
   shard com coluna faltando entrar com null em vez de derrubar a query.
3. **Coluna extra e tipo divergente são erro**, não algo a contornar. Como o
   erro chega tarde (no `Collect`), a mensagem precisa nomear o shard e a
   coluna culpada.

`Concat(Diagonal)` fica registrado como modo explícito para um futuro dataset
que exija união real de schemas — não como padrão.

## Consequências

- Abrir um dataset de milhares de shards passa a custar o mesmo que abrir um
  arquivo: uma leitura de rodapé.
- O contrato do dataset fica mais estrito que o do Polars puro: shards devem
  ter o mesmo schema, com colunas ausentes toleradas.
- O Core passa a distinguir "fonte" de "arquivo". `EnsureParquetAsync`, que
  hoje devolve um caminho, precisará devolver algo que represente as duas
  formas.

## Questões em aberto (fora do escopo desta decisão)

- **Contagem de linhas versus grid virtual.** Contar é só metadados, mas toca
  um arquivo por vez: 6.946 shards frios = **113,8 s**. Contar na abertura é
  inaceitável, e a `DataGridView` virtual precisa de um `RowCount` para se
  dimensionar. As saídas — contagem parcial ajustada em background, ou um modo
  que não promete total de linhas — são **decisão de contrato da UI**, não de
  código do Core, e ficam para um ADR próprio. Por isso a primeira fatia é
  Core + CLI, onde a contagem não é obrigatória.
- `Len()` retorna `u32`: teto de 4,29 bilhões de linhas. Longe do dataset
  atual (440,9M), mas o limite existe.
- Se a 0.7.0 expuser `extra_columns='ignore'`, a regra 3 pode relaxar.
