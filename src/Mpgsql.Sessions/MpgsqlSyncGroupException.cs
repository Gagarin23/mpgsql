namespace Mpgsql;

/// <summary>
///     A different DataSource request failed before the shared Sync boundary.
///     Results already read do not confirm that this request's transaction committed.
/// </summary>
public sealed class MpgsqlSyncGroupException : Exception
{

    internal MpgsqlSyncGroupException(
        MpgsqlServerException cause, bool wasSkipped,
        int failedRequestIndex
    )
        : base
        (
            wasSkipped
                ? "PostgreSQL skipped this request after another request failed in the shared Sync group."
                : "Another request failed before the shared Sync boundary; this request's results do not confirm a commit.", cause
        )
    {
        (Cause, WasSkipped, FailedRequestIndex) = (cause, wasSkipped, failedRequestIndex);
    }
    /// <summary>The SQL error from the failing logical request, with its confirmed ReadyForQuery status.</summary>
    public MpgsqlServerException Cause { get; }
    /// <summary>
    ///     True when PostgreSQL skipped this request after the error. False when its
    ///     CommandComplete was received before the error; its effects can still have been rolled back.
    /// </summary>
    public bool WasSkipped { get; }
    /// <summary>Zero-based admission index of the failing request within the shared Sync group.</summary>
    public int FailedRequestIndex { get; }
}