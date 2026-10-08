using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using Mpgsql.Converters;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks.Queries;

internal sealed class QueryPeer : IAsyncDisposable
{
    private readonly List<byte>? _capture;
    private readonly QueryCatalog _catalog;
    private readonly int _chunk;
    private readonly Pipe _incoming;
    private readonly Pipe _outgoing;
    private readonly Task _read;

    private readonly Channel<ReadOnlyMemory<byte>> _replies = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(256) {SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false});

    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly Task _write;
    private long _chunks;
    private int _disposed;
    private Exception? _failure;
    private int _phase;
    private long _queries;
    private long _ready;
    private bool _recovering;
    private int _scenario;
    private long _syncs;
    private int _worker;

    internal QueryPeer(QueryCatalog catalog, int chunk = 0,
        bool capture = false)
    {
        _catalog = catalog;
        _chunk = chunk;
        _capture = capture ? [] : null;
        // A threshold of one forces each chunk to be consumed, including partial frames.
        _incoming = new Pipe(new PipeOptions(pauseWriterThreshold: chunk > 0 ? 1 : 65536,
            resumeWriterThreshold: chunk > 0 ? 1 : 32768, minimumSegmentSize: 4096,
            useSynchronizationContext: false));
        _outgoing = new Pipe(new PipeOptions(pauseWriterThreshold: 65536, resumeWriterThreshold: 32768,
            useSynchronizationContext: false));
        ClientWriter = new CountingPipeWriter(_outgoing.Writer);
        Session = new MpgsqlMessageSession(_incoming.Reader, ClientWriter);
        _read = ReadAsync();
        _write = WriteAsync();
    }

    internal MpgsqlMessageSession Session { get; }
    internal CountingPipeWriter ClientWriter { get; }
    internal long Queries => Interlocked.Read(ref _queries);
    internal long Syncs => Interlocked.Read(ref _syncs);
    internal long ReadyReplies => Interlocked.Read(ref _ready);
    internal long ReplyChunks => Interlocked.Read(ref _chunks);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _stop.Cancel();
        await Session.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_read, _write).ConfigureAwait(false);
        _stop.Dispose();
        ThrowIfFailed();
    }

    internal byte[] Captured()
    {
        return _capture?.ToArray() ?? throw new InvalidOperationException("Capture is disabled.");
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var read = await _outgoing.Reader.ReadAsync(_stop.Token).ConfigureAwait(false);
                var remaining = read.Buffer;
                try
                {
                    while (FrontendFrameParser.TryRead(ref remaining, out var tag, out var payload, out var frame))
                    {
                        if (_capture is not null)
                        {
                            foreach (var segment in frame) _capture.AddRange(segment.ToArray());
                        }
                        var response = Process(tag, payload);
                        if (!response.IsEmpty)
                        {
                            await _replies.Writer.WriteAsync(response, _stop.Token).ConfigureAwait(false);
                        }
                    }
                    if (read.IsCompleted)
                    {
                        if (!remaining.IsEmpty)
                        {
                            throw new InvalidDataException("Truncated frontend frame at EOF.");
                        }
                        break;
                    }
                }
                finally { _outgoing.Reader.AdvanceTo(remaining.Start, read.Buffer.End); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            failure = error;
            Fail(error);
        }
        finally
        {
            _replies.Writer.TryComplete(failure);
            await _outgoing.Reader.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    private ReadOnlyMemory<byte> Process(byte tag, ReadOnlySequence<byte> payload)
    {
        if (_recovering && tag != (byte)'S')
        {
            return default;
        }
        var reader = new WireReader(payload);
        switch ((char)tag)
        {
            case 'P' when _phase == 0:
                reader.SkipCString();
                var sql = reader.CStringBytes();
                _scenario = -1;
                for (var i = 0; i < _catalog.Scenarios.Length; i++)
                {
                    if (QueryWire.Equal(sql, _catalog.Scenarios[i].SqlUtf8))
                    {
                        _scenario = i;
                        break;
                    }
                }
                if (_scenario < 0)
                {
                    throw new InvalidDataException("Unknown synthetic SQL transcript.");
                }
                int count = reader.Count();
                if (count != _catalog.Inputs[_scenario][0].Length)
                {
                    throw new InvalidDataException("Parse parameter count.");
                }
                for (var i = 0; i < count; i++)
                {
                    if (reader.UInt32() != (i == 0 ? 20U : 17U))
                    {
                        throw new InvalidDataException("Parse parameter OID.");
                    }
                }
                _phase = 1;
                break;
            case 'B' when _phase == 1:
                reader.SkipCString();
                reader.SkipCString();
                if (reader.Count() != 1 || reader.Int16() != 1)
                {
                    throw new InvalidDataException("Binary parameter formats.");
                }
                int parameters = reader.Count();
                if (parameters != _catalog.Inputs[_scenario][0].Length)
                {
                    throw new InvalidDataException("Bind parameter count.");
                }
                var id = reader.Value();
                if (id is not {Length: 8})
                {
                    throw new InvalidDataException("Worker identifier length.");
                }
                _worker = checked((int)Int64Converter.Read(id.Value) - 1);
                if ((uint)_worker >= (uint)_catalog.Replies[_scenario].Length)
                {
                    throw new InvalidDataException("Worker identifier.");
                }
                if (parameters == 2 && reader.Value()?.Length != _catalog.Scenarios[_scenario].ByteaBytes)
                {
                    throw new InvalidDataException("Bytea parameter length.");
                }
                if (reader.Count() != 1 || reader.Int16() != 1)
                {
                    throw new InvalidDataException("Binary result formats.");
                }
                _phase = 2;
                break;
            case 'D' when _phase == 2:
                if (reader.Byte() != (byte)'P')
                {
                    throw new InvalidDataException("Expected portal Describe.");
                }
                reader.SkipCString();
                _phase = 3;
                break;
            case 'E' when _phase == 3:
                reader.SkipCString();
                if (reader.Int32() != 0)
                {
                    throw new InvalidDataException("Expected unlimited Execute.");
                }
                if (reader.Remaining != 0)
                {
                    throw new InvalidDataException("Execute trailing bytes.");
                }
                _phase = 0;
                _recovering = _catalog.Scenarios[_scenario].Error;
                Interlocked.Increment(ref _queries);
                var response = _catalog.Replies[_scenario][_worker];
                if (response.Length == 0)
                {
                    throw new InvalidDataException("Worker/scenario is not enabled.");
                }
                return response;
            case 'S' when _phase == 0 || _recovering:
                if (payload.Length != 0)
                {
                    throw new InvalidDataException("Sync body must be empty.");
                }
                _phase = 0;
                _recovering = false;
                Interlocked.Increment(ref _syncs);
                return QueryWire.Ready;
            default:
                throw new InvalidDataException($"Unexpected frontend tag {(char)tag}, phase {_phase}.");
        }
        if (reader.Remaining != 0)
        {
            throw new InvalidDataException("Frontend trailing bytes.");
        }
        return default;
    }

    private async Task WriteAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var response in _replies.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var chunk = _chunk == 0 ? response.Length : _chunk;
                for (var offset = 0; offset < response.Length; offset += chunk)
                {
                    var length = Math.Min(chunk, response.Length - offset);
                    var flush = await _incoming.Writer.WriteAsync(response.Slice(offset, length), _stop.Token).ConfigureAwait(false);
                    if (flush.IsCanceled)
                    {
                        throw new OperationCanceledException(_stop.Token);
                    }
                    if (flush.IsCompleted)
                    {
                        return;
                    }
                    Interlocked.Increment(ref _chunks);
                }
                if (response.Equals(QueryWire.Ready.AsMemory()))
                {
                    Interlocked.Increment(ref _ready);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            failure = error;
            Fail(error);
        }
        finally { await _incoming.Writer.CompleteAsync(failure).ConfigureAwait(false); }
    }

    private void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref _failure, error, null);
        _stop.Cancel();
        _replies.Writer.TryComplete(error);
    }

    internal void ThrowIfFailed()
    {
        if (_failure is { } error)
        {
            throw new InvalidOperationException("Synthetic peer failed.", error);
        }
        if (_read.IsFaulted)
        {
            _read.GetAwaiter().GetResult();
        }
        if (_write.IsFaulted)
        {
            _write.GetAwaiter().GetResult();
        }
    }
}