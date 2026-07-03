using Apache.Arrow;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Polars.CSharp;

namespace PolarsGridViewer;

/// <summary>
/// Globals expostos ao script do terminal. Precisa ser público para o
/// Roslyn conseguir vincular o script a ele.
/// </summary>
public class ScriptGlobals
{
    /// <summary>LazyFrame do arquivo parquet aberto (scan fresco, stateless).</summary>
    public LazyFrame lf = null!;
}

/// <summary>
/// Executa scripts C# Polars do terminal via Roslyn. Stateless: cada execução
/// recebe um `lf` novo de <c>LazyFrame.ScanParquet</c>; o script deve terminar
/// numa expressão <c>LazyFrame</c> ou <c>DataFrame</c>, que vira o RecordBatch
/// exibido no grid (mesmo caminho zero-materialização do restante do app).
/// </summary>
public static class ScriptHost
{
    /// <summary>Teto de linhas aplicado ao resultado quando o usuário deixa o limite ligado.</summary>
    public const uint RowCap = 1_000_000;

    // Lazy (e não cctor): se a montagem das referências falhar, a mensagem
    // real chega ao terminal em vez de um TypeInitializationException opaco.
    // ATENÇÃO: a API de scripting do Roslyn resolve referências (inclusive o
    // corlib, internamente) via Assembly.Location — em publish single-file
    // "puro" o Location é vazio e QUALQUER script falha com
    // NotSupportedException (dotnet/roslyn#50719). Por isso o csproj publica
    // com IncludeAllContentForSelfExtract=true; não remover essa propriedade.
    private static readonly Lazy<ScriptOptions> Options = new(() => ScriptOptions.Default
        .WithReferences(typeof(LazyFrame).Assembly, typeof(RecordBatch).Assembly)
        .WithImports("System", "System.Linq", "Polars.CSharp", "Polars.CSharp.Polars"));

    /// <summary>
    /// Roda o script e devolve o resultado como RecordBatch (posse do chamador).
    /// Lança <see cref="CompilationErrorException"/> para erro de sintaxe e a
    /// exceção do Polars para erros de consulta (ex.: coluna inexistente).
    /// </summary>
    public static async Task<RecordBatch> RunAsync(
        string code, string parquetPath, bool applyRowCap, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            using var lf = LazyFrame.ScanParquet(parquetPath);
            var state = await CSharpScript.RunAsync(
                code, Options.Value, new ScriptGlobals { lf = lf }, typeof(ScriptGlobals), cancellationToken);

            switch (state.ReturnValue)
            {
                case LazyFrame result:
                    using (result)
                    {
                        // validado por probe: coletar um LazyFrame derivado após o
                        // Dispose da origem é seguro nesta versão
                        var query = applyRowCap ? result.Limit(RowCap) : result;
                        try
                        {
                            using var df = query.Collect();
                            return df.ToArrow();
                        }
                        finally
                        {
                            if (!ReferenceEquals(query, result))
                                query.Dispose();
                        }
                    }

                case DataFrame df:
                    using (df)
                        return df.ToArrow();

                case null:
                    throw new InvalidOperationException(
                        "O script não retornou nada. Termine com uma expressão LazyFrame ou DataFrame (sem ponto e vírgula no final).");

                default:
                    throw new InvalidOperationException(
                        $"O script deve terminar numa expressão LazyFrame ou DataFrame; recebi {state.ReturnValue.GetType().Name}.");
            }
        }, cancellationToken);
    }
}
