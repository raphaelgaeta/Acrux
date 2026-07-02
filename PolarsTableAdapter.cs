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
}


// Acesso O(1) direto ao buffer Arrow, sem materializar a coluna.
// É o que o CellValueNeeded usa: só as células visíveis são convertidas.
public static object GetCellValue(IArrowArray array, int index)
{
    if (array.IsNull(index)) return DBNull.Value;

    return array switch
    {
        StringArray a     => a.GetString(index),
        StringViewArray a => a.GetString(index),
        Int32Array a      => a.GetValue(index)!,
        Int64Array a      => a.GetValue(index)!,
        DoubleArray a     => a.GetValue(index)!,
        FloatArray a      => a.GetValue(index)!,
        BooleanArray a    => a.GetValue(index)!,
        Date32Array a     => a.GetDateTimeOffset(index)!.Value.DateTime,
        TimestampArray a  => (object?)a.GetTimestamp(index) ?? DBNull.Value,
        _                 => array.ToString()!
    };
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





