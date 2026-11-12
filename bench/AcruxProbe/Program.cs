// One benchmark task through Acrux.Core, the way the desktop app runs it, for
// what the command line doesn't do:
//   AcruxProbe screen <file.parquet>    the first screen of the grid: the format
//       from the bytes, the column cache opened, the 23 columns the app loads
//       (15 on screen and half a screen to the right, all 5M rows of each) and
//       the cells of the first 30 rows painted.
//   AcruxProbe convert <file> <out.parquet>   opening a CSV, xlsx or SQLite
//       file in the app: the conversion to parquet, kept at out.parquet so
//       bench/messy.py can check what came out.
//   AcruxProbe groupby <file.parquet>   the C# terminal's
//       df.GroupBy("c005_cat").Agg(Col("c003_f64").Sum()), run by Polars.NET
//       without the Roslyn compile (the terminal's first command adds it).
// Prints one JSON line, like bench/tasks.py: "op_s" and "check".
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Acrux;
using Polars.CSharp;
using static Polars.CSharp.Polars;

string task = args[0], path = args[1];
object? check = null;
double seconds = 0;

SingleThreadContext.Run(async () =>
{
    var clock = Stopwatch.StartNew();
    switch (task)
    {
        case "screen":
        {
            if (DataFrameProvider.DetectFormat(path) != SourceFormat.Parquet)
                throw new InvalidOperationException("not a parquet file");
            using var cache = await ColumnCache.OpenAsync(path, new MemoryBudget());
            await cache.EnsureAsync([.. Enumerable.Range(0, Math.Min(23, cache.Fields.Count))]);
            for (int c = 0; c < Math.Min(15, cache.Fields.Count); c++)
                for (int r = 0; r < 30; r++)
                    _ = PolarsTableAdapter.GetCellValue(cache.TryGet(c)!, r);
            seconds = clock.Elapsed.TotalSeconds;
            check = new { cols = Math.Min(23, cache.Fields.Count), rows = cache.RowCount };
            break;
        }
        case "screen-batch":   // the same screen, its columns asked for k at a time (args[2]): what loading them all at once costs
        {
            int k = int.Parse(args[2]);
            using var cache = await ColumnCache.OpenAsync(path, new MemoryBudget());
            int n = Math.Min(23, cache.Fields.Count);
            for (int c = Math.Min(k, n); ; c = Math.Min(c + k, n))
            {
                await cache.EnsureAsync([.. Enumerable.Range(0, c)]);   // pins the ones before, loads only the new ones
                if (c == n) break;
            }
            for (int c = 0; c < Math.Min(15, cache.Fields.Count); c++)
                for (int r = 0; r < 30; r++)
                    _ = PolarsTableAdapter.GetCellValue(cache.TryGet(c)!, r);
            seconds = clock.Elapsed.TotalSeconds;
            check = new { cols = n, rows = cache.RowCount };
            break;
        }
        case "convert":
        {
            try
            {
                var (parquet, isTemp) = await DataFrameProvider.EnsureParquetAsync(path);
                seconds = clock.Elapsed.TotalSeconds;
                File.Copy(parquet, args[2], overwrite: true);
                if (isTemp) File.Delete(parquet);
                check = new { parquet = args[2] };
            }
            catch (Exception ex)
            {
                seconds = clock.Elapsed.TotalSeconds;
                check = new { error = $"{ex.GetType().Name}: {PolarsErrors.Brief(ex)}" };
            }
            break;
        }
        case "groupby":
        {
            using var df = LazyFrame.ScanParquet(path).GroupBy("c005_cat").Agg(Col("c003_f64").Sum()).Collect();
            using var batch = df.ToArrow();
            var sums = new Dictionary<string, double>();
            for (int r = 0; r < batch.Length; r++)
                sums[PolarsTableAdapter.GetCellValue(batch.Column(0), r) is string key ? key : "null"] =
                    Convert.ToDouble(PolarsTableAdapter.GetCellValue(batch.Column(1), r));
            seconds = clock.Elapsed.TotalSeconds;
            check = sums;
            break;
        }
        default:
            throw new ArgumentException($"unknown task {task}");
    }
});
Console.WriteLine(JsonSerializer.Serialize(new { op_s = Math.Round(seconds, 4), check }));
Environment.Exit(0);   // like the app: Polars' threads must not hold the process

/// <summary>The app's UI thread in a console: the cache runs its continuations here, one at a time.</summary>
sealed class SingleThreadContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    public static void Run(Func<Task> main)
    {
        var context = new SingleThreadContext();
        SetSynchronizationContext(context);
        var task = main();
        task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
        foreach (var (callback, state) in context._queue.GetConsumingEnumerable()) callback(state);
        task.GetAwaiter().GetResult();
    }
}
