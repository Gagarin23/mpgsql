using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

public static partial class TimestampTzConverter
{
    public static int GetByteCount(DateTime value)
    {
        return GetByteCount(PgTimestampTz.FromDateTime(value));
    }
    public static int Write(DateTime value, Span<byte> destination)
    {
        return Write(PgTimestampTz.FromDateTime(value), destination);
    }
    public static void Write(DateTime value, IBufferWriter<byte> destination)
    {
        Write(PgTimestampTz.FromDateTime(value), destination);
    }
    public static DateTime ReadDateTime(ReadOnlySpan<byte> payload)
    {
        return Read(payload).ToDateTime();
    }
    public static DateTime ReadDateTime(ReadOnlySequence<byte> payload)
    {
        return Read(payload).ToDateTime();
    }
}