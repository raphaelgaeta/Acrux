using Apache.Arrow;

namespace PolarsGridViewer;

public static class PolarsTableAdapter
{
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
}
