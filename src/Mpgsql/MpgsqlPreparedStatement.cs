using Mpgsql.Protocol;

namespace Mpgsql;

/// <summary>An explicitly prepared, named statement owned by one message session.</summary>
public sealed class MpgsqlPreparedStatement
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MpgsqlQueryBatch? _prepareBatch;
    private MpgsqlQueryBatch? _closeBatch;
    private bool _prepareQueued;
    private bool _retired;
    private bool _closed;

    internal MpgsqlPreparedStatement(MpgsqlMessageSession session,
        string name,
        string sql,
        ReadOnlyMemory<uint> parameterTypes)
    {
        FrontendSize.Count(parameterTypes.Length);
        uint[] types = parameterTypes.ToArray();
        if (types.AsSpan().Contains(0U))
            throw new ArgumentException("Prepared statement parameter OIDs must be nonzero.", nameof(parameterTypes));
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
    /// <remarks>Published preparation is independent of logical cancellation. Send the batch's explicit Sync
    /// before awaiting this task; responses may be buffered.</remarks>
    public Task Prepared => _prepared.Task;

    internal MpgsqlMessageSession Session { get; }
    internal ParseMessage ParseMessage { get; }
    internal TargetMessage CloseMessage { get; }

    internal void QueuePrepare(MpgsqlQueryBatch batch)
    {
        lock (_gate)
        {
            if (_prepareQueued || _retired)
                throw new InvalidOperationException("A statement can be prepared only once; create a new statement after failure.");
            _prepareQueued = true;
            _prepareBatch = batch;
        }
    }

    internal void ValidateExecution(MpgsqlQueryBatch batch, ReadOnlySpan<MpgsqlParameter> parameters)
    {
        lock (_gate)
        {
            if (_retired)
                throw new InvalidOperationException("The statement is closing or closed.");
            if (!Prepared.IsCompletedSuccessfully && (Prepared.IsCompleted || _prepareBatch != batch))
                throw new InvalidOperationException("Queue preparation in this batch or await successful preparation before using another batch.");
        }
        if (parameters.Length != ParameterTypes.Length)
            throw new ArgumentException("The parameter count does not match the declared statement signature.", nameof(parameters));
        for (int i = 0; i < parameters.Length; i++)
            if (parameters[i].PostgresTypeOid != ParameterTypes.Span[i])
                throw new ArgumentException("A parameter OID does not match the declared statement signature.", nameof(parameters));
    }

    internal bool QueueClose(MpgsqlQueryBatch batch)
    {
        lock (_gate)
        {
            if (_closed)
                return false;
            if (!_prepareQueued)
                throw new InvalidOperationException("Queue statement preparation before closing it.");
            if (_closeBatch is not null && !_closeBatch.Completion.IsCompleted)
                throw new InvalidOperationException("Wait for the previous close batch to complete before retrying Close.");
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
            if (error is OperationCanceledException canceled)
                _prepared.TrySetCanceled(canceled.CancellationToken);
            else
                _prepared.TrySetException(error);
            _prepareBatch = null;
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
