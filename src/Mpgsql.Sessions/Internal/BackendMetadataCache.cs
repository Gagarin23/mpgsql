using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// Receive-loop-owned, bounded, one-entry caches of exact validated wire payloads. These never
// suppress Describe or infer metadata from SQL. Decoded arrays/strings are immutable and remain
// owned by any reader that captured them, even after cache replacement or session disposal.
internal sealed class BackendMetadataCache
{
    private const int DescriptionLimit = 4096;
    private const int CommandLimit = 1024;
    private byte[]? _descriptionBytes;
    private int _descriptionLength;
    private ReadOnlyMemory<RowField> _description;
    private byte[]? _commandBytes;
    private int _commandLength;
    private string? _command;

    internal ReadOnlyMemory<RowField> RowDescription(BackendMessage message)
    {
        var payload = message.Payload;
        if (_descriptionBytes is { } previous && Matches(payload, previous, _descriptionLength))
        {
            return _description;
        }
        var decoded = message.GetRowDescription();
        if (payload.Length <= DescriptionLimit)
        {
            Store(payload, ref _descriptionBytes, ref _descriptionLength, DescriptionLimit);
            _description = decoded;
        }
        return decoded;
    }

    internal string CommandTag(BackendMessage message)
    {
        var payload = message.Payload;
        if (_commandBytes is { } previous && Matches(payload, previous, _commandLength))
        {
            return _command!;
        }
        string decoded = message.GetCommandTag();
        if (payload.Length <= CommandLimit)
        {
            Store(payload, ref _commandBytes, ref _commandLength, CommandLimit);
            _command = decoded;
        }
        return decoded;
    }

    private static bool Matches(ReadOnlySequence<byte> payload, byte[] bytes, int length)
    {
        if (payload.Length != length)
        {
            return false;
        }
        if (payload.IsSingleSegment)
        {
            return payload.FirstSpan.SequenceEqual(bytes.AsSpan(0, length));
        }
        int offset = 0;
        foreach (var segment in payload)
        {
            if (!segment.Span.SequenceEqual(bytes.AsSpan(offset, segment.Length)))
            {
                return false;
            }
            offset += segment.Length;
        }
        return true;
    }

    private static void Store(ReadOnlySequence<byte> payload, ref byte[]? bytes, ref int length, int limit)
    {
        int size = (int)payload.Length;
        if (bytes is null || bytes.Length < size)
        {
            bytes = new byte[Math.Min(limit, Math.Max(size, (bytes?.Length ?? 0) * 2))];
        }
        payload.CopyTo(bytes);
        length = size;
    }
}
