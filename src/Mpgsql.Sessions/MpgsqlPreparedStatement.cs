using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>An explicitly prepared, named statement owned by one message session.</summary>
public sealed class MpgsqlPreparedStatement : IDisposable
{
    private readonly Lock _gate = new Lock();
    private readonly TaskCompletionSource _prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private MpgsqlQueryBatch? _closeBatch;
    private bool _closed;
    private bool _disposed;
    private MpgsqlQueryBatch? _prepareBatch;
    private bool _prepareQueued;
    private bool _retired;

    internal MpgsqlPreparedStatement(MpgsqlMessageSession session,
        string name,
        string sql,
        ReadOnlyMemory<uint> parameterTypes)
    {
        FrontendSize.Count(parameterTypes.Length);
        var types = parameterTypes.ToArray();
        if (types.AsSpan().Contains(0U))
        {
            throw new ArgumentException("Prepared statement parameter OIDs must be nonzero.", nameof(parameterTypes));
        }
        Session = session;
        Name = name;
        Sql = sql;
        ParameterTypes = types;
        ParseMessage = FrontendMessage.Parse(sql, name, types);
        CloseMessage = FrontendMessage.Close(StatementOrPortal.Statement, name);
    }

    public string Name { get; }
    public string Sql { get; }
    /// <summary>The owned copy of the complete, explicitly declared parameter type list.</summary>
    public ReadOnlyMemory<uint> ParameterTypes { get; }
    /// <summary>Confirms the server's ParseComplete for this statement.</summary>
    /// <remarks>
    ///     Published preparation is independent of logical cancellation. Send the batch's explicit Sync
    ///     before awaiting this task; responses may be buffered.
    /// </remarks>
    public Task Prepared => _prepared.Task;

    internal MpgsqlMessageSession Session { get; }
    internal ParseMessage ParseMessage { get; }
    internal TargetMessage CloseMessage { get; }

    /// <summary>Discards a local handle without sending any protocol messages.</summary>
    /// <remarks>
    ///     Allowed before preparation is queued, after failed or canceled preparation,
    ///     after confirmed Close, or after the owning session stops. Pending or successful preparation
    ///     requires an explicit SendCloseAsync and its server acknowledgement first.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Preparation is pending or a server statement still requires Close.</exception>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (_prepareQueued && !_closed)
            {
                throw new InvalidOperationException("Wait for preparation to finish and explicitly close the server statement before disposing the handle.");
            }
            _disposed = true;
            _retired = true;
            _prepareBatch = null;
            _closeBatch = null;
            Session.ReleaseStatement(this);
            if (!_prepared.Task.IsCompleted)
            {
                _prepared.TrySetException(new ObjectDisposedException(nameof(MpgsqlPreparedStatement)));
            }
        }
    }

    internal void QueuePrepare(MpgsqlQueryBatch batch)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_prepareQueued || _retired)
            {
                throw new InvalidOperationException("A statement can be prepared only once; create a new statement after failure.");
            }
            _prepareQueued = true;
            _prepareBatch = batch;
        }
    }

    internal void ValidateExecution(MpgsqlQueryBatch batch, ReadOnlySpan<MpgsqlParameterValue> parameters)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_retired)
            {
                throw new InvalidOperationException("The statement is closing or closed.");
            }
            if (!Prepared.IsCompletedSuccessfully && (Prepared.IsCompleted || _prepareBatch != batch))
            {
                throw new InvalidOperationException("Queue preparation in this batch or await successful preparation before using another batch.");
            }
        }
        if (parameters.Length != ParameterTypes.Length)
        {
            throw new ArgumentException("The parameter count does not match the declared statement signature.", nameof(parameters));
        }
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].PostgresTypeOid != ParameterTypes.Span[i])
            {
                throw new ArgumentException("A parameter OID does not match the declared statement signature.", nameof(parameters));
            }
        }
    }

    internal bool QueueClose(MpgsqlQueryBatch batch)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_closed)
            {
                return false;
            }
            if (!_prepareQueued)
            {
                throw new InvalidOperationException("Queue statement preparation before closing it.");
            }
            if (_closeBatch is not null && !_closeBatch.Completion.IsCompleted)
            {
                throw new InvalidOperationException("Wait for the previous close batch to complete before retrying Close.");
            }
            _retired = true;
            _closeBatch = batch;
            return true;
        }
    }

    internal void ConfirmPrepared()
    {
        lock (_gate)
        {
            _prepared.TrySetResult();
            _prepareBatch = null;
        }
    }

    internal void FailPreparation(Exception error)
    {
        lock (_gate)
        {
            // No successful Parse remains outstanding here, unless the entire session is stopping.
            // Release ownership before completing Prepared so its continuations observe the cleanup.
            _retired = true;
            _closed = true;
            _prepareBatch = null;
            _closeBatch = null;
            Session.ReleaseStatement(this);
            if (error is OperationCanceledException canceled)
            {
                _prepared.TrySetCanceled(canceled.CancellationToken);
            }
            else
            {
                _prepared.TrySetException(error);
            }
        }
    }

    internal void ConfirmClosed()
    {
        lock (_gate)
        {
            _closed = true;
            _closeBatch = null;
        }
        Session.ReleaseStatement(this);
    }
}