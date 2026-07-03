using Apache.Arrow;
using Polars.CSharp;
using static Polars.CSharp.Polars;

namespace PolarsGridViewer;

/// <summary>Filtro ativo de uma coluna (estilo Excel: valores marcados + vazias).</summary>
public sealed class ColumnFilter
{
    public required HashSet<string> SelectedValues { get; init; }
    public bool IncludeBlanks { get; init; }
}

/// <summary>
/// Consultas de filtro sobre o arquivo parquet via LazyFrame.
/// O rescan usa projection pushdown (só lê as colunas do predicado),
/// mantendo o uso de memória do app plano.
/// </summary>
public static class FilterEngine
{
    public const int DistinctCap = 10_000;

    /// <summary>
    /// Valores distintos da coluna para o dropdown, já como string e ordenados.
    /// Semântica Excel: aplica os filtros das OUTRAS colunas antes de coletar.
    /// </summary>
    public static async Task<(string[] Values, bool HasBlanks, bool Capped)> GetDistinctValuesAsync(
        string parquetPath,
        string column,
        IReadOnlyDictionary<string, ColumnFilter> activeFilters,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var lf = LazyFrame.ScanParquet(parquetPath);

            var predicate = BuildPredicate(activeFilters, excludeColumn: column);
            if (predicate is not null)
                lf = lf.Filter(predicate);

            // +1 além do cap para detectar corte. Engine.Streaming: ~2× mais
            // rápida que Auto/InMemory neste formato Unique+Sort+Limit
            // (benchmark no arquivo de 700col×5M; demais consultas empatam)
            using var df = lf
                .Select(Col(column).Cast(DataType.String).Alias("v"))
                .Unique()
                .Sort("v")
                .Limit((uint)DistinctCap + 1)
                .Collect(Engine.Streaming);
            var batch = df.ToArrow();
            var array = batch.Column(0);

            bool hasBlanks = false;
            var values = new List<string>(batch.Length);
            for (int i = 0; i < batch.Length; i++)
            {
                if (array.IsNull(i))
                {
                    hasBlanks = true;
                    continue;
                }
                values.Add((string)PolarsTableAdapter.GetCellValue(array, i));
            }
            bool capped = values.Count > DistinctCap;
            if (capped)
                values.RemoveRange(DistinctCap, values.Count - DistinctCap);

            return (values.ToArray(), hasBlanks, capped);
        }, cancellationToken);
    }

    /// <summary>
    /// Índices (0-based, na ordem do arquivo) das linhas que passam em TODOS
    /// os filtros ativos. Retorna null quando não há filtro (grid mostra tudo).
    /// </summary>
    public static async Task<int[]?> GetVisibleRowsAsync(
        string parquetPath,
        IReadOnlyDictionary<string, ColumnFilter> activeFilters,
        CancellationToken cancellationToken = default)
    {
        if (activeFilters.Count == 0)
            return null;

        return await Task.Run(() =>
        {
            var predicate = BuildPredicate(activeFilters, excludeColumn: null)!;

            using var df = LazyFrame.ScanParquet(parquetPath)
                .Select(predicate.Alias("mask"))
                .Collect();

            var batch = df.ToArrow();
            var mask = (BooleanArray)batch.Column(0);

            var indices = new List<int>();
            for (int i = 0; i < mask.Length; i++)
            {
                if ((i & 0xFFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                // null (linha vazia numa coluna filtrada sem "(Vazias)") conta como falso
                if (mask.GetValue(i) == true)
                    indices.Add(i);
            }

            return indices.ToArray();
        }, cancellationToken);
    }

    /// <summary>
    /// AND de todas as colunas filtradas; dentro de cada coluna,
    /// IsIn(valores) OR IsNull() quando "(Vazias)" está marcado.
    /// </summary>
    private static Expr? BuildPredicate(
        IReadOnlyDictionary<string, ColumnFilter> filters,
        string? excludeColumn)
    {
        Expr? acc = null;

        foreach (var (column, filter) in filters)
        {
            if (column == excludeColumn)
                continue;

            Expr cond = Col(column)
                .Cast(DataType.String)
                .IsIn(Lit(Series.From("__values", filter.SelectedValues.ToArray())).Implode());

            if (filter.IncludeBlanks)
                cond = cond | Col(column).IsNull();

            acc = acc is null ? cond : acc & cond;
        }

        return acc;
    }
}
