using System.Buffers;
using Mpgsql.Types;

namespace Mpgsql.Converters;

public static partial class TimestampTzConverter
{
    public static int GetByteCount(DateTime value) => GetByteCount(PgTimestampTz.FromDateTime(value));
    public static int Write(DateTime value, Span<byte> destination) => Write(PgTimestampTz.FromDateTime(value), destination);
    public static void Write(DateTime value, IBufferWriter<byte> destination) => Write(PgTimestampTz.FromDateTime(value), destination);
    public static DateTime ReadDateTime(ReadOnlySpan<byte> payload) => Read(payload).ToDateTime();
    public static DateTime ReadDateTime(ReadOnlySequence<byte> payload) => Read(payload).ToDateTime();
}