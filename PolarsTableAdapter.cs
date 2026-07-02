using System;
using System.Data;
using Polars.CSharp;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Polars.NET.Core.Arrow;

namespace PolarsGridViewer;

public static class PolarsTableAdapter
{
    public static DataTable ToDataTable(DataFrame polarsDataFrame)
    {

        DataTable table = new DataTable();

        for (int i = 0; i < polarsDataFrame.Height; i++) {
            DataRow row = table.NewRow();
            for (int c = 0; c < polarsDataFrame.Width; c++) {
                row[c] = polarsDataFrame.Row(i)[c];
            }
            table.Rows.Add(row);
        }
        return table;

        throw new NotImplementedException(
            "Implemente aqui a conversão do seu DataFrame do Polars para DataTable.");
    }

public static DataTable RecordBatchToDataTableColumnar(RecordBatch batch)
{
    var dt = new DataTable();
    int rowCount = batch.Length;

    // 1. Cria colunas e pré-aloca linhas vazias
    foreach (var field in batch.Schema.FieldsList)
        dt.Columns.Add(field.Name, typeof(object));

    dt.BeginLoadData();
    for (int i = 0; i < rowCount; i++)
        dt.Rows.Add(dt.NewRow());

    // 2. Preenche coluna inteira de uma vez
    for (int col = 0; col < batch.ColumnCount; col++)
    {
        var array = batch.Column(col);
        object[] valores = ExtractColumn(array, rowCount); // ← array inteiro

        for (int row = 0; row < rowCount; row++)
            dt.Rows[row][col] = valores[row] ?? DBNull.Value;
    }

    dt.EndLoadData();
    return dt;
}private static object? GetValue(IArrowArray array, int index)
{
    if (array.IsNull(index)) return null;

    return array switch
    {
        Int32Array   a => a.GetStringValue(index),
        Int64Array   a => a.GetStringValue(index),
        FloatArray   a => a.GetStringValue(index),
        DoubleArray  a => a.GetStringValue(index),
        BooleanArray a => a.GetStringValue(index),
        StringArray  a => a.GetStringValue(index),
        Date32Array  a => a.GetDateTimeOffset(index).Value.DateTime.ToShortDateString(),
        TimestampArray a => a.GetTimestamp(index),
        _              => array.ToString()
    };
}
public static class DataFrameProvider
{
    public static async Task<DataFrame> GetDataFrameAsync(string parquetPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("Arquivo .parquet não encontrado.", parquetPath);

        return await Task.Run(() => DataFrame.ReadParquet(parquetPath), cancellationToken);
    }
}


public static  object[] ExtractColumn(IArrowArray array, int rowCount)
{
    var result = new object[rowCount];

    switch (array)
    {
        case StringArray a:
            for (int i = 0; i < rowCount; i++)
                result[i] = a.IsNull(i) ? DBNull.Value : a.GetString(i);
            break;

        case StringViewArray a:
            for (int i = 0; i < rowCount; i++)
                result[i] = a.IsNull(i) ? DBNull.Value : a.GetString(i);
            break;

        case Int32Array a:
            for (int i = 0; i < rowCount; i++)
                result[i] = a.IsNull(i) ? DBNull.Value : a.GetValue(i)!;
            break;

        case Int64Array a:
            for (int i = 0; i < rowCount; i++)
                result[i] = a.IsNull(i) ? DBNull.Value : a.GetValue(i)!;
            break;

        case DoubleArray a:
            for (int i = 0; i < rowCount; i++)
                result[i] = a.IsNull(i) ? DBNull.Value : a.GetValue(i)!;
            break;

        case FloatArray a:
            for (int i = 0; i < rowCount; i++)
                result[i] = a.IsNull(i) ? DBNull.Value : a.GetValue(i)!;
            break;

        case BooleanArray a:
            for (int i = 0; i < rowCount; i++)
                result[i] = a.IsNull(i) ? DBNull.Value : a.GetValue(i)!;
            break;

        case Date32Array a:
            for (int i = 0; i < rowCount; i++)
                result[i] = a.IsNull(i) ? DBNull.Value : a.GetDateTimeOffset(i).Value.DateTime;
            break;

        case TimestampArray a:
            for (int i = 0; i < rowCount; i++)
                result[i] = a.IsNull(i) ? DBNull.Value : (object)a.GetTimestamp(i)!;
            break;

        default:
            for (int i = 0; i < rowCount; i++)
                result[i] = array.IsNull(i) ? DBNull.Value : array.ToString()!;
            break;
    }

    return result;
}

}





