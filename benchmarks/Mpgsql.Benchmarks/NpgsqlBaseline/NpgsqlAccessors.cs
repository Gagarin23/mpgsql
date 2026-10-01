using System.Runtime.CompilerServices;
using Npgsql.Internal;

namespace Mpgsql.Benchmarks.NpgsqlBaseline;

// Direct calls to the exact 10.0.3 lifecycle methods. No reflection in timed code.
internal static class NpgsqlAccessors
{
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    internal static extern PgWriter CreateWriter(System.Buffers.IBufferWriter<byte> writer);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "BeginWrite")]
    internal static extern ValueTask BeginWrite(PgWriter writer, bool async, ValueMetadata current, CancellationToken cancellationToken);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Commit")]
    internal static extern void CommitWrite(PgWriter writer, int? expectedByteCount);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Init")]
    internal static extern void InitRead(PgReader reader, int fieldSize, DataFormat fieldFormat, bool resumable);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "StartRead")]
    internal static extern void StartRead(PgReader reader, Size bufferRequirement);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "StartReadAsync")]
    internal static extern ValueTask StartReadAsync(PgReader reader, Size bufferRequirement, CancellationToken cancellationToken);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EndRead")]
    internal static extern void EndRead(PgReader reader);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EndReadAsync")]
    internal static extern ValueTask EndReadAsync(PgReader reader);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Commit")]
    internal static extern void CommitRead(PgReader reader);
}
