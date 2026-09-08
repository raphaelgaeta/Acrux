# ADR 002 — `ParquetSource`: um dono para o scan

- **Data:** 2026-09-08
- **Status:** aprovado (aguardando implementação)
- **Depende de:** [ADR 001 — Datasets multi-arquivo](001-datasets-multi-arquivo.md)
- **Escopo:** `Acrux.Core` e `Acrux.Cli`. O contrato de contagem de linhas
  versus grid virtual segue fora — ver ADR 001.

## Contexto

O ADR 001 decidiu **como** abrir um dataset multi-arquivo: glob ou diretório,
com o schema de um shard repassado em `schema:` e `allowMissingColumns: true`.
Falta decidir **o que** carrega essa decisão pelo código.

Hoje a fonte é uma `string` de caminho, e o `EnsureParquetAsync` devolve
`(string ParquetPath, bool IsTemp)`.

### Os cinco sítios de scan

Todos abrem o arquivo do zero, todos recebem `string parquetPath`:

| Local | O que faz |
|---|---|
| `DataFrameProvider.GetColumnNamesAsync` | `File.Exists` + `ScanParquet` |
| `DataFrameProvider.GetDataFrameAsync` | `File.Exists` + `ScanParquet().Select()` |
| `FilterEngine.GetDistinctValuesAsync` | `ScanParquet` |
| `FilterEngine.GetVisibleRowsAsync` | `ScanParquet` |
| `ScriptHost.RunChainAsync` | `ScanParquet` |

Cinco lugares onde as flags de abertura precisariam ser repetidas — e onde
esquecê-las não dá erro, só degrada de 18 ms para 221 s (ADR 001).

### O que muda no MainForm

`_parquetPath` aparece em 11 sítios: 1 declaração, 1 atribuição, 5 guardas de
null, 3 repasses ao Core e 1 cópia local. Nove são mecânicos — trocado o tipo
do campo, as guardas seguem `is null` e os repasses passam o objeto.

O `UpdateInfo` não exibe nome de arquivo (só linhas e colunas), então não há
decisão de UI sobre "como mostrar um dataset de milhares de arquivos". O
`Acrux.Cli` chama apenas `EnsureParquetAsync` + `GetColumnNamesAsync`.

### O ciclo de vida do temporário

CSV e xlsx viram parquet temporário; dataset multi-arquivo nunca vira. Hoje o
MainForm mantém **dois campos paralelos que precisam concordar** —
`_parquetPath` e `_tempParquetPath` — e a discordância entre eles é a razão de
existir a comparação defensiva no `catch` do fluxo de abertura
(`!ReferenceEquals(...) && sourcePath != _tempParquetPath`). Um tipo que
carregue caminho e "é temporário" juntos elimina a classe inteira de erro.

## Opções consideradas

### 1. Manter `string`, só relaxar a validação

Trocar `File.Exists` por uma checagem que aceite diretório e glob. Zero
mudança de assinatura, MainForm intocado.

**Rejeitada:** não há onde guardar o schema de referência. Ou cada scan repaga
o custo por arquivo, ou o hint vira um parâmetro opcional repetido nas cinco
assinaturas — e um esquecimento não falha, trava.

### 2. `readonly record struct` com caminho, tipo da fonte e `IsTemp`

Resolve a identidade da fonte e o ciclo de vida do temporário, com diff
pequeno.

**Rejeitada:** o schema de referência continua sem dono. Viraria um sexto
parâmetro (mesmo problema da opção 1) ou um cache estático — e cache estático
com dataset sendo trocado é convite a servir o schema do arquivo anterior.

### 3. `ParquetSource`: uma classe que possui o scan

Carrega **três coisas**: o caminho (com o tipo da fonte — arquivo, diretório ou
glob — e se é temporário), o **schema de referência** lido de um shard na
abertura, e a **lista concreta de arquivos** resolvida na abertura.

Expõe a abertura como operação própria: um `Open()` que devolve o `LazyFrame`
já com `schema:` e `allowMissingColumns:` aplicados. Os cinco sítios do Core
passam a receber `ParquetSource` e chamar `source.Open()`.

## Decisão

**Opção 3.**

A razão decisiva não é encapsulamento: é que ela torna *impossível* abrir sem o
hint. Nas opções 1 e 2 as flags de scan ficam escritas cinco vezes e o custo de
esquecer uma é invisível — nenhum erro, só 221 s de espera. Na opção 3 elas
ficam escritas uma vez, no único lugar que abre o dataset.

`EnsureParquetAsync` passa a devolver um `ParquetSource` no lugar da tupla, e
`_tempParquetPath` deixa de existir como campo separado no MainForm.

O custo aceito: as cinco assinaturas públicas do Core mudam de uma vez. Todas
mudam do mesmo jeito, e o compilador aponta cada uma.

## Riscos e mitigações

### 1. Hint de schema esquecido

**Risco:** um scan aberto sem `schema:` lê o rodapé de todos os arquivos —
~24 ms por arquivo, 221 s nos 6.946 shards do WorldCat. Não há erro: o app
simplesmente para de responder.

**Mitigação:** é o motivo da decisão. Nenhum código fora do `ParquetSource`
chama `LazyFrame.ScanParquet` para a fonte aberta; quem precisa de um
`LazyFrame` chama `Open()`.

### 2. Conjunto de arquivos variável entre scans

**Risco — correção silenciosa, não desempenho.** A garantia de índices do
princípio 2 (filtros produzem `int[]` sobre o batch) depende de a ordem das
linhas ser estável entre o collect inicial e o re-scan da máscara. O glob é
reavaliado a cada scan: se resolver para um conjunto diferente de arquivos
entre esses dois momentos, os índices deslizam e a grid mostra **linha errada,
sem sinal nenhum**. Com arquivo único é remoto; com diretório de staging em
escrita — o caso real do WorldCat — é plausível.

**Mitigação: detectar, não prevenir.** Prevenir exigiria `Concat` de N
`LazyFrame` (ADR 001), que custa N handles nativos e perde o ganho do hint. O
`ParquetSource` resolve o glob na abertura e guarda a lista concreta; antes de
qualquer scan que dependa de posição de índice, re-resolve e compara. Divergiu,
falha com mensagem clara. Uma listagem de diretório custa milissegundos e troca
corrupção silenciosa por erro visível.

### 3. Divergência de schema só aparece no `Collect`

**Risco:** o scan expõe o schema do primeiro arquivo e não valida os demais.
Coluna extra ou tipo divergente num shard falham no `Collect` — depois de o
usuário já ter passado pelo seletor de colunas. Hoje o fluxo de abertura trata
exceção com um `MessageBox` genérico.

**Mitigação:** aceitar que a falha é tardia (validar todos os shards na
abertura é exatamente o custo de 221 s que a decisão evita) e tratá-la como
erro de abertura de primeira classe: a mensagem nomeia o shard e a coluna
culpada, que o erro nativo já traz. `allowMissingColumns: true` cobre o caso
benigno — coluna ausente entra como null.

## Explicitamente não verificado

**A estabilidade de ordem foi testada em 12 shards com cache quente, não nos
6.946 nem sob escrita concorrente.** O probe confirmou que o collect completo e
o collect projetado devolvem a mesma sequência de linhas, e que ela segue a
ordem alfabética dos arquivos, em 5 corridas — dado o **mesmo conjunto de
arquivos**. Isso não é garantia geral de ordem sob concorrência, sob volume,
nem com o dataset sendo escrito. É precisamente por esse limite que o risco 2
é tratado por detecção.

Também não verificado: o comportamento do risco 2 na prática — nenhum teste foi
feito com arquivos entrando ou saindo do diretório durante a sessão.

## Verificado no probe (para não re-testar)

- `PolarsSchema` não expõe `Dispose`; sobrevive ao `Dispose` do `LazyFrame` de
  origem e a três rodadas de `GC.Collect()` + finalizers, seguindo utilizável
  como `schema:`. Cachear o hint pela vida do dataset aberto é seguro.
- Ordem das linhas num scan multi-arquivo segue a ordem alfabética dos nomes de
  arquivo (ver limites acima).
