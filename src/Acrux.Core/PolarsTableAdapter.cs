using Apache.Arrow;

namespace Acrux;

public static class PolarsTableAdapter
{
    // O(1) read straight from the Arrow buffer, no column materialization.
    // This is what CellValueNeeded uses: only visible cells get converted.
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
