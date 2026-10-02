using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// Consume incomplete frames instead of pinning an entire message in a backpressured Pipe.
internal sealed class BackendFrameBuffer : IDisposable
{
    private readonly byte[] _header = new byte[5];
    private readonly DiscardedDataRow _skip = new();
    private int _headerBytes;
    private int _size;
    private int _written;
    private bool _skipping;
    private IMemoryOwner<byte>? _owner;
    internal long CopiedRowBytes;

    internal bool TryRead(ref ReadOnlySequence<byte> input, bool discardRows,
        out BackendMessage message, out IMemoryOwner<byte>? owner, out int skippedColumns)
    {
        message = default; owner = null; skippedColumns = -1;
        if (_headerBytes == 0 && input.Length >= 5 && BackendMessageReader.TryRead(ref input, out message))
            return true;

        if (_headerBytes < 5)
        {
            int size = (int)Math.Min(5 - _headerBytes, input.Length);
            input.Slice(0, size).CopyTo(_header.AsSpan(_headerBytes));
            input = input.Slice(size); _headerBytes += size;
            if (_headerBytes != 5) return false;
            int length = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(1));
            if (length < 4 || length > BackendMessageReader.DefaultMaxMessageLength)
                throw new InvalidDataException($"Invalid PostgreSQL message length: {length}.");
            _size = length + 1; _written = 5;
            _skipping = discardRows && _header[0] == (byte)'D';
            if (_skipping) _skip.Reset();
            else
            {
                _owner = MemoryPool<byte>.Shared.Rent(_size);
                _header.CopyTo(_owner.Memory.Span);
                if (_header[0] == (byte)'D') CopiedRowBytes += 5;
            }
        }

        if (discardRows && _header[0] == (byte)'D' && !_skipping)
        {
            _skip.Reset();
            _skip.Feed(_owner!.Memory.Span.Slice(5, _written - 5));
            _owner.Dispose(); _owner = null; _skipping = true;
        }

        int available = (int)Math.Min(_size - _written, input.Length);
        var bytes = input.Slice(0, available);
        if (_skipping)
        {
            foreach (var segment in bytes) _skip.Feed(segment.Span);
        }
        else
        {
            bytes.CopyTo(_owner!.Memory.Span[_written..]);
            if (_header[0] == (byte)'D') CopiedRowBytes += available;
        }
        input = input.Slice(available); _written += available;
        if (_written != _size) return false;
        if (_skipping)
        {
            _skip.End(); skippedColumns = _skip.Columns;
        }
        else
        {
            var frame = new ReadOnlySequence<byte>(_owner!.Memory[.._size]);
            if (!BackendMessageReader.TryRead(ref frame, out message) || !frame.IsEmpty)
                throw new InvalidDataException("Incomplete assembled backend frame.");
            owner = _owner; _owner = null;
        }
        _headerBytes = 0; _written = 0;
        return true;
    }

    internal bool HasPartialFrame => _headerBytes != 0;
    public void Dispose() { _owner?.Dispose(); _owner = null; }
}
