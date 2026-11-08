using System.Globalization;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Acrux;

public static class PolarsTableAdapter
{
    private const int MaxBinaryBytes = 32;
    private const int MaxListItems = 20;

    // O(1) read straight from the Arrow buffer, no column materialization.
    // This is what CellValueNeeded uses: only visible cells get converted.
    // Order matters: StringArray derives from BinaryArray (and the View/Large
    // variants likewise), so text cases come before binary ones.
    public static object GetCellValue(IArrowArray array, int index)
    {
        if (array.IsNull(index)) return DBNull.Value;

        return array switch
        {
            StringArray a      => a.GetString(index),
            StringViewArray a  => a.GetString(index),
            LargeStringArray a => a.GetString(index),
            Int8Array a        => a.GetValue(index)!,
            Int16Array a       => a.GetValue(index)!,
            Int32Array a       => a.GetValue(index)!,
            Int64Array a       => a.GetValue(index)!,
            UInt8Array a       => a.GetValue(index)!,
            UInt16Array a      => a.GetValue(index)!,
            UInt32Array a      => a.GetValue(index)!,   // Polars' counts (Count/Len) come as u32
            UInt64Array a      => a.GetValue(index)!,
            DoubleArray a      => a.GetValue(index)!,
            FloatArray a       => a.GetValue(index)!,
            BooleanArray a     => a.GetValue(index)!,
            Decimal128Array a  => DecimalValue(a, index),
            Date32Array a      => a.GetDateTimeOffset(index)!.Value.DateTime,
            Date64Array a      => a.GetDateTime(index)!.Value,
            TimestampArray a   => TimestampValue(a, index),
            Time32Array a      => TimeText(a.GetTime(index)!.Value),
            Time64Array a      => TimeText(a.GetTime(index)!.Value),
            DurationArray a    => a.GetTimeSpan(index)!.Value,
            BinaryViewArray a  => Hex(a.GetBytes(index)),
            BinaryArray a      => Hex(a.GetBytes(index)),
            LargeBinaryArray a => Hex(a.GetBytes(index)),
            ListArray a        => ListText(a.GetSlicedValues(index)),
            LargeListArray a   => ListText(a.GetSlicedValues(index)),
            // categorical/enum: the index points into the dictionary of values
            DictionaryArray a  => GetCellValue(a.Dictionary, Convert.ToInt32(GetCellValue(a.Indices, index), CultureInfo.InvariantCulture)),
            _                  => array.ToString()!
        };
    }

    // System.Decimal holds 28-29 digits; a wider decimal128 shows as exact text
    private static object DecimalValue(Decimal128Array array, int index)
    {
        try
        {
            return array.GetValue(index)!.Value;
        }
        catch (OverflowException)
        {
            return array.GetString(index);
        }
    }

    // A timestamp without a zone is a wall-clock value: shown as is, no offset
    // (it used to read as UTC). A zoned one is an instant: shown in the
    // viewer's local zone, with its offset (owner's decision, 2026-11-07).
    private static object TimestampValue(TimestampArray array, int index)
    {
        var instant = array.GetTimestamp(index)!.Value;
        // an if, not ?: — a ternary would turn the DateTime into a DateTimeOffset
        // too (implicit conversion), stamping the local offset on wall-clock values
        if (string.IsNullOrEmpty(((TimestampType)array.Data.DataType).Timezone))
            return instant.DateTime;
        return instant.ToLocalTime();
    }

    // TimeOnly's default text is the short time pattern, which drops the
    // seconds (23:59:59 → "23:59"); the fraction shows only when present
    private static string TimeText(TimeOnly time) =>
        time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    private static string Hex(ReadOnlySpan<byte> bytes) => bytes.Length <= MaxBinaryBytes
        ? "0x" + Convert.ToHexString(bytes)
        : "0x" + Convert.ToHexString(bytes[..MaxBinaryBytes]) + $"… ({bytes.Length} bytes)";

    // Invariant culture inside lists: with a decimal comma, "[1,5, 2,5]" would be ambiguous
    private static string ListText(IArrowArray values)
    {
        var text = new StringBuilder("[");
        int shown = Math.Min(values.Length, MaxListItems);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) text.Append(", ");
            text.Append(GetCellValue(values, i) switch
            {
                DBNull => "null",
                string s => $"\"{s}\"",   // ["a", "b, c"] would read as three items unquoted
                var item => Convert.ToString(item, CultureInfo.InvariantCulture),
            });
        }
        if (values.Length > shown) text.Append(", …");
        return text.Append(']').ToString();
    }
}
