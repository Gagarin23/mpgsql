using System.Buffers;

namespace Mpgsql.Protocol;

/// <summary>DataRow values indexed during validation into caller-owned reusable storage.</summary>
/// <remarks>Both values and their storage are borrowed. Consume the row before reusing either buffer.</remarks>
public readonly struct IndexedDataRow
{
    public ReadOnlyMemory<ReadOnlySequence<byte>?> Values { get; }
    public int Count => Values.Length;

    internal IndexedDataRow(ReadOnlyMemory<ReadOnlySequence<byte>?> values) => Values = values;

    public ReadOnlySpan<ReadOnlySequence<byte>?>.Enumerator GetEnumerator() => Values.Span.GetEnumerator();
}
