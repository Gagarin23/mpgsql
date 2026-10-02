using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

internal sealed class OwnedRow : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private ReadOnlySequence<byte>?[]? _values;
    internal int Count { get; }
    internal ReadOnlySequence<byte>? this[int ordinal] => _values![ordinal];

    internal OwnedRow(BackendMessage message, IMemoryOwner<byte>? frameOwner)
    {
        Count = message.GetDataRow().Count;
        _owner = frameOwner ?? MemoryPool<byte>.Shared.Rent((int)message.Payload.Length);
        try
        {
            ReadOnlySequence<byte> payload;
            if (frameOwner is null)
            {
                message.Payload.CopyTo(_owner.Memory.Span);
                payload = new(_owner.Memory[..(int)message.Payload.Length]);
            }
            else payload = message.Payload;
            _values = ArrayPool<ReadOnlySequence<byte>?>.Shared.Rent(Math.Max(1, Count));
            var reader = new WireReader(payload.Slice(2));
            for (int i = 0; i < Count; i++) _values[i] = reader.Value();
        }
        catch
        {
            if (frameOwner is not null) _owner = null; // the caller still owns it on constructor failure
            Dispose(); throw;
        }
    }

    public void Dispose()
    {
        if (_values is { } values)
        {
            _values = null;
            ArrayPool<ReadOnlySequence<byte>?>.Shared.Return(values, clearArray: true);
        }
        _owner?.Dispose();
        _owner = null;
    }
}
