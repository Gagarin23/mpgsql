using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;

namespace Mpgsql.Tests.Queries;

internal sealed class ScriptedSession : IAsyncDisposable
{
    internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    internal readonly Pipe Incoming = new(new PipeOptions(pauseWriterThreshold: 32,
        resumeWriterThreshold: 16,
        useSynchronizationContext: false));

    internal readonly Pipe Outgoing;
    internal MpgsqlMessageSession Session { get; }

    internal ScriptedSession(bool blockWrites = false,
        CancellationToken lifetime = default)
    {
        Outgoing = new(new PipeOptions(pauseWriterThreshold: blockWrites
                ? 1
                : 0,
            resumeWriterThreshold: blockWrites
                ? 1
                : 0,
            useSynchronizationContext: false));
        Session = new(Incoming.Reader,
            Outgoing.Writer,
            lifetime);
    }

    internal async Task WriteAsync(byte[] bytes,
        int fragment = int.MaxValue)
    {
        for (int i = 0;
             i < bytes.Length;
             i += Math.Min(fragment,
                 bytes.Length - i))
            await Incoming.Writer.WriteAsync(bytes.AsMemory(i,
                    Math.Min(fragment,
                        bytes.Length - i)))
                .AsTask().WaitAsync(TestTimeout);
    }

    internal async Task<byte[]> ReadOutputAsync()
    {
        var read = await Outgoing.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
        var bytes = read.Buffer.ToArray();
        Outgoing.Reader.AdvanceTo(read.Buffer.End);
        return bytes;
    }

    internal bool HasOutput()
    {
        if (!Outgoing.Reader.TryRead(out var read))
        {
            return false;
        }
        bool result = !read.Buffer.IsEmpty;
        Outgoing.Reader.AdvanceTo(read.Buffer.Start,
            read.Buffer.Start);
        return result;
    }

    internal static byte[] Packet(char type,
        params byte[] payload)
    {
        byte[] result = new byte[payload.Length + 5];
        result[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(1),
            payload.Length + 4);
        payload.CopyTo(result,
            5);
        return result;
    }

    internal static byte[] Join(params byte[][] frames) => [.. frames.SelectMany(x => x)];
    internal static byte[] Ready(char status = 'I') => Packet('Z',
        (byte)status);
    internal static byte[] Command(string tag = "SELECT 1") => Packet('C',
        Encoding.UTF8.GetBytes(tag + '\0'));
    internal static byte[] Error(string state = "22012")
        => Packet('E',
            Encoding.UTF8.GetBytes("SERROR\0C" + state + "\0Mbad query\0\0"));
    internal static byte[] Int64(long value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes,
            value);
        return bytes;
    }

    internal static byte[] Description(params uint[] oids)
    {
        byte[] payload = new byte[2 + 21 * oids.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload,
            (ushort)oids.Length);
        int offset = 2;
        foreach (uint oid in oids)
        {
            payload[offset++] = (byte)'c';
            payload[offset++] = (byte)'x';
            payload[offset++] = 0;
            offset += 6;
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset),
                oid);
            offset += 4;
            BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(offset),
                oid == 20
                    ? (short)8
                    : (short)-1);
            offset += 2;
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(offset),
                -1);
            offset += 4;
            BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(offset),
                1);
            offset += 2;
        }
        return Packet('T',
            payload);
    }

    internal static byte[] Row(params byte[]?[] values)
    {
        byte[] payload = new byte[2 + values.Sum(x => 4 + (x?.Length ?? 0))];
        BinaryPrimitives.WriteUInt16BigEndian(payload,
            (ushort)values.Length);
        int offset = 2;
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(offset),
                value?.Length ?? -1);
            offset += 4;
            if (value is not null)
            {
                value.CopyTo(payload,
                    offset);
                offset += value.Length;
            }
        }
        return Packet('D',
            payload);
    }

    internal static byte[] Begin(params uint[] oids) => Join(Packet('1'),
        Packet('2'),
        Description(oids));
    internal static byte[] Query(long value) => Join(Begin(20),
        Row(Int64(value)),
        Command());

    internal static char[] Tags(byte[] bytes)
    {
        var tags = new List<char>();
        for (int i = 0; i < bytes.Length;)
        {
            tags.Add((char)bytes[i]);
            i += 1 + BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i + 1));
        }
        return [.. tags];
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        await Incoming.Writer.CompleteAsync();
        await Outgoing.Reader.CompleteAsync();
    }
}