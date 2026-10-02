using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks.Live;

// Benchmark-only synchronous transport. Messages borrow the reusable network
// buffer until Receive is called again; no allocation for each exported row.
internal sealed class BufferedCopyReader(Stream stream) : IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(8192);
    private int _position, _filled;
    internal bool IsDrained => _position == _filled;

    internal BackendMessage Receive()
    {
        Ensure(5);
        int length = BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(_position + 1,
            4));
        if (length < 4 || length > BackendMessageReader.DefaultMaxMessageLength)
        {
            throw new InvalidDataException("Invalid COPY backend packet length.");
        }
        int total = checked(length + 1);
        Ensure(total);
        var input = new ReadOnlySequence<byte>(_buffer.AsMemory(_position,
            total));
        if (!BackendMessageReader.TryRead(ref input,
                out var message) || !input.IsEmpty)
        {
            throw new InvalidDataException("Incomplete COPY backend packet.");
        }
        _position += total;
        return message;
    }

    private void Ensure(int count)
    {
        if (_filled - _position >= count)
        {
            return;
        }
        int remaining = _filled - _position;
        if (count > _buffer.Length)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(count);
            _buffer.AsSpan(_position,
                remaining).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }
        else
        {
            _buffer.AsSpan(_position,
                remaining).CopyTo(_buffer);
        }
        _filled = remaining;
        _position = 0;
        while (_filled < count)
        {
            int read = stream.Read(_buffer.AsSpan(_filled));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            _filled += read;
        }
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}