using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// A physical Sync boundary with independently consumed, single-query logical batches.
// Its gate orders writer registration against receive-side routing. User continuations are asynchronous.
internal sealed class SharedSyncGroup(MpgsqlQueryBatch boundary)
{
    private readonly Lock _gate = new Lock();
    private readonly Queue<MpgsqlQueryBatch> _published = new Queue<MpgsqlQueryBatch>();
    private MpgsqlQueryBatch? _active;
    private DiagnosticMessage? _error;
    private MpgsqlQueryBatch? _failed;
    private List<MpgsqlQueryBatch>? _members = [];
    private volatile bool _recovery;

    internal MpgsqlQueryBatch Boundary { get; } = boundary;
    // Create the eventual Sync work with the group: delivery can be awaited before it is queued.
    // The work owns this same multi-observer promise; there is no forwarding task or second source.
    internal OutboundWork SyncWork { get; } = new OutboundWork(boundary);
    internal Task Delivery => SyncWork.Completion;
    internal bool HasError => _recovery;
    internal MpgsqlQueryBatch? ActiveBatch => Volatile.Read(ref _active);

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _members?.Count ?? 0;
            }
        }
    }

    internal void Add(MpgsqlQueryBatch batch)
    {
        lock (_gate)
        {
            batch.SyncGroupIndex = _members!.Count;
            _members.Add(batch);
        }
    }

    internal void RegisterPublished(MpgsqlQueryBatch batch)
    {
        lock (_gate)
        {
            _published.Enqueue(batch);
            if (_active is null)
            {
                Volatile.Write(ref _active, batch);
            }
        }
    }

    internal void SealPublished()
    {
        lock (_gate)
        {
            if (_members is { } members)
            {
                foreach (var batch in members)
                {
                    batch.SealPublished();
                }
            }
        }
    }

    internal void AcceptError(DiagnosticMessage diagnostics, bool terminal)
    {
        lock (_gate)
        {
            if (!terminal && (_recovery || _active is null))
            {
                throw new InvalidDataException("ErrorResponse without an active shared-group query.");
            }
            _error = diagnostics;
            _failed = _active;
            _recovery = true;
            _failed?.AcceptError(diagnostics, terminal);
        }
    }

    internal void Accept(
        BackendMessage message, ref IMemoryOwner<byte>? owner,
        ref RowBufferBudget? reservation
    )
    {
        lock (_gate)
        {
            if (_recovery || _active is not { } batch)
            {
                throw new InvalidDataException($"Unexpected {message.Kind} in a shared Sync group.");
            }
            batch.Accept(message, ref owner, ref reservation);
            if (message.Kind is BackendMessageKind.CommandComplete or BackendMessageKind.EmptyQueryResponse)
            {
                batch.SyncGroupCommandCompleted = true;
                _published.Dequeue();
                Volatile.Write(ref _active, _published.TryPeek(out var next) ? next : null);
            }
        }
    }

    internal void Ready(TransactionStatus status)
    {
        lock (_gate)
        {
            if (!_recovery && _published.Count != 0)
            {
                throw new InvalidDataException("ReadyForQuery before shared-group query completion.");
            }
            var cause = _error is { } diagnostic
                ? new MpgsqlServerException(diagnostic, _failed is null ? null : 0, status)
                : null;
            foreach (var batch in _members ?? [])
            {
                Exception? failure = cause is null ? null
                    : ReferenceEquals(batch, _failed) ? cause
                    : new MpgsqlSyncGroupException(cause, !batch.SyncGroupCommandCompleted, _failed?.SyncGroupIndex ?? -1);
                batch.CompleteShared(status, failure);
            }
            _published.Clear();
            Volatile.Write(ref _active, null);
        }
    }

    // Called after Accept released all batch/group gates. A cancelled consumer can already have
    // disposed its logical batch before ReadyForQuery, so its session registration must be released here.
    internal void ReleaseMembers(MpgsqlMessageSession session)
    {
        List<MpgsqlQueryBatch>? members;
        lock (_gate)
        {
            members = _members;
            _members = null;
        }
        if (members is not null)
        {
            foreach (var batch in members)
            {
                session.ReleaseBatch(batch);
            }
        }
    }

    internal void Fail(Exception error)
    {
        lock (_gate)
        {
            SyncWork.TrySetException(error);
            _recovery = true;
            if (_members is { } members)
            {
                foreach (var batch in members)
                {
                    batch.Fail(error);
                }
            }
            _members = null;
            _published.Clear();
            Volatile.Write(ref _active, null);
        }
    }
}