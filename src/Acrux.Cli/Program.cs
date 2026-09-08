using System.Runtime.InteropServices;
using Acrux;

// Runtime proof that Acrux.Core reaches the Polars native library on this
// platform: open a file through DataFrameProvider and print its schema.
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: Acrux.Cli <file.parquet|file.csv|file.xlsx>");
    return 1;
}

var path = Path.GetFullPath(args[0]);
Console.WriteLine($"RID:  {RuntimeInformation.RuntimeIdentifier}");
Console.WriteLine($"OS:   {RuntimeInformation.OSDescription}");
Console.WriteLine($"File: {path}");

var (parquetPath, isTemp) = await DataFrameProvider.EnsureParquetAsync(path);
try
{
    if (isTemp)
        Console.WriteLine($"Temp parquet: {parquetPath}");

    var columns = await DataFrameProvider.GetColumnNamesAsync(parquetPath);
    Console.WriteLine($"Schema: {columns.Length} column(s)");
    foreach (var column in columns)
        Console.WriteLine($"  {column}");
}
finally
{
    if (isTemp)
        File.Delete(parquetPath);
}

return 0;
