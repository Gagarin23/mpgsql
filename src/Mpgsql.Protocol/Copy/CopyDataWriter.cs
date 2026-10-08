using System.Buffers;
using System.Buffers.Binary;

namespace Mpgsql.Copy;

/// <summary>A pooled, bounded output buffer that writes complete frontend CopyData frames.</summary>
/// <remarks>
///     The stream remains open. WriteCopyDone coalesces the last data frame and CopyDone;
///     otherwise flush before sending CopyDone/CopyFail through the connection.
///     Dispose only returns the buffer; it never completes or flushes an unfinished COPY.
///     Large reservations grow the buffer to fit one field; there is no intermediate field copy.
///     A failed stream write faults this buffer: the caller must discard the connection,
///     since retrying could duplicate a partially transmitted frame.
/// </remarks>
public sealed class CopyDataWriter : IBufferWriter<byte>, IDisposable
{
    private readonly int _bufferSize;
    private readonly Stream _stream;
    private byte[]? _buffer;
    private bool _completed;
    private bool _faulted;
    private int _limit;
    private int _position = 5;

    public CopyDataWriter(
        Stream stream,
        int bufferSize = 8192
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException
            (
                "The stream is not writable.",
                nameof(stream)
            );
        }
        ArgumentOutOfRangeException.ThrowIfLessThan
        (
            bufferSize,
            19
        );
        _stream = stream;
        _bufferSize = bufferSize;
        _limit = checked(bufferSize + 5);
        _buffer = ArrayPool<byte>.Shared.Rent(checked(_limit + 5));
    }

    public void Advance(int count)
    {
        RequireWritable();
        if ((uint)count > (uint)(_limit - _position))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        _position += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        RequireWritable();
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        sizeHint = Math.Max
        (
            1,
            sizeHint
        );
        if (sizeHint > _limit - _position)
        {
            var required = checked(sizeHint + 5);
            var capacity = checked(required + 5);
            Flush();
            if (required > _limit)
            {
                var replacement = ArrayPool<byte>.Shared.Rent
                (
                    Math.Max
                    (
                        capacity,
                        checked(_bufferSize + 10)
                    )
                );
                ArrayPool<byte>.Shared.Return(_buffer!);
                _buffer = replacement;
                _limit = required;
            }
        }
        return _buffer!.AsMemory
        (
            _position,
            _limit - _position
        );
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        return GetMemory(sizeHint)
            .Span;
    }

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Flush()
    {
        RequireWritable();
        if (_position == 5)
        {
            return;
        }
        WriteDataHeader();
        WritePacket(_position);
        _position = 5;
    }

    /// <summary>Sends the final data frame and frontend CopyDone in one stream write.</summary>
    /// <remarks>
    ///     Complete BinaryCopyWriter first. This ends sending, not the server operation;
    ///     still consume CommandComplete/ReadyForQuery and update BinaryCopyOperation.
    /// </remarks>
    public void WriteCopyDone()
    {
        RequireWritable();
        var offset = _position == 5 ? 0 : _position;
        if (offset != 0)
        {
            WriteDataHeader();
        }
        var done = _buffer!.AsSpan
        (
            offset,
            5
        );
        done[0] = (byte)'c';
        BinaryPrimitives.WriteInt32BigEndian
        (
            done[1..],
            4
        );
        WritePacket(offset + 5);
        _position = 5;
        _completed = true;
    }

    private void WriteDataHeader()
    {
        _buffer![0] = (byte)'d';
        BinaryPrimitives.WriteInt32BigEndian
        (
            _buffer.AsSpan
            (
                1,
                4
            ),
            _position - 1
        );
    }

    private void WritePacket(int count)
    {
        try
        {
            _stream.Write
            (
                _buffer!.AsSpan
                (
                    0,
                    count
                )
            );
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void RequireWritable()
    {
        ObjectDisposedException.ThrowIf
        (
            _buffer is null,
            this
        );
        if (_faulted)
        {
            throw new InvalidOperationException("The COPY output stream failed; discard the connection.");
        }
        if (_completed)
        {
            throw new InvalidOperationException("CopyDone has already been sent.");
        }
    }
}