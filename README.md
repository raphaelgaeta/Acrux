# PolarsGridViewer

Esqueleto de projeto WinForms para visualizar um DataFrame do Polars em uma grade tipo Excel.

## Estrutura

- `Program.cs` → inicialização da aplicação
- `MainForm.cs` → interface gráfica e DataGridView
- `DataFrameProvider.cs` → ponto onde você encaixa sua leitura real do DataFrame
- `PolarsTableAdapter.cs` → ponto onde você converte o DataFrame para `DataTable`
- `PolarsGridViewer.csproj` → projeto base WinForms

## O que já funciona

- Janela WinForms
- Grade visual (`DataGridView`)
- Navegação por células
- Scroll
- Ordenação de colunas
- Filtro textual simples
- Contador de linhas/colunas

## O que você precisa completar

### 1. Inserir sua leitura real do DataFrame
No arquivo `DataFrameProvider.cs`, troque:

```csharp
public static object GetDataFrame()
{
    throw new System.NotImplementedException(...);
}
```

por algo como:

```csharp
public static object GetDataFrame()
{
    return SeuMetodoQueLeODataFrame();
}
```

### 2. Converter seu DataFrame do Polars para `DataTable`
No arquivo `PolarsTableAdapter.cs`, implemente a conversão com base na API exata do Polars .NET que você já usa.

## Como abrir

1. Extraia a pasta
2. Abra `PolarsGridViewer.csproj` no Visual Studio
3. Adicione sua referência/pacote do Polars no `.csproj`
4. Implemente `DataFrameProvider.cs`
5. Implemente `PolarsTableAdapter.cs`
6. Rode o projeto

## Sugestão de próximo passo

Se você quiser, eu posso montar uma segunda versão já adaptada ao **seu código real**, se você me mandar:

- a assinatura do método que retorna o DataFrame
- o tipo exato do DataFrame
- como você acessa colunas/linhas hoje na sua biblioteca Polars .NET
