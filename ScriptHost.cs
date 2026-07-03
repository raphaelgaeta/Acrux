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
    /// Roda a cadeia de scripts por replay: o passo 1 recebe em <c>lf</c> o
    /// scan do arquivo e cada passo seguinte recebe o LazyFrame resultante do
    /// anterior. Nada é coletado no meio — os passos compõem um único plano
    /// lazy, coletado só no final (com <see cref="RowCap"/> opcional).
    /// Devolve o RecordBatch do resultado (posse do chamador). Lança
    /// <see cref="CompilationErrorException"/> para erro de sintaxe e a
    /// exceção do Polars para erros de consulta (ex.: coluna inexistente).
    /// </summary>
    public static async Task<RecordBatch> RunChainAsync(
        IReadOnlyList<string> steps, string parquetPath, bool applyRowCap, CancellationToken cancellationToken)
    {
        if (steps.Count == 0)
            throw new ArgumentException("Cadeia de scripts vazia.", nameof(steps));

        return await Task.Run(async () =>
        {
            var lf = LazyFrame.ScanParquet(parquetPath);
            try
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    var state = await CSharpScript.RunAsync(
                        steps[i], Options.Value, new ScriptGlobals { lf = lf }, typeof(ScriptGlobals), cancellationToken);

                    var next = state.ReturnValue switch
                    {
                        LazyFrame result => result,
                        // passo com .Collect(): volta ao lazy via df.Lazy() —
                        // validado por probe que o plano sobrevive ao Dispose do
                        // DataFrame; os dados coletados ficam materializados no
                        // plano até o fim do replay (prefira passos lazy)
                        DataFrame df => Relazify(df),
                        null => throw new InvalidOperationException(
                            $"O passo {i + 1} não retornou nada. Termine com uma expressão LazyFrame ou DataFrame (sem ponto e vírgula no final)."),
                        var other => throw new InvalidOperationException(
                            $"O passo {i + 1} deve terminar numa expressão LazyFrame ou DataFrame; recebi {other.GetType().Name}.")
                    };

                    // validado por probe: derivar/coletar após Dispose da origem é seguro
                    if (!ReferenceEquals(next, lf))
                    {
                        lf.Dispose();
                        lf = next;
                    }
                }

                var query = applyRowCap ? lf.Limit(RowCap) : lf;
                try
                {
                    using var df = query.Collect();
                    return df.ToArrow();
                }
                finally
                {
                    if (!ReferenceEquals(query, lf))
                        query.Dispose();
                }
            }
            finally
            {
                lf.Dispose();
            }
        }, cancellationToken);
    }

    private static LazyFrame Relazify(DataFrame df)
    {
        using (df)
            return df.Lazy();
    }
}
