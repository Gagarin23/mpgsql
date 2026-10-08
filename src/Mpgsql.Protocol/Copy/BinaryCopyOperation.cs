using System.Globalization;
using Mpgsql.Protocol;

namespace Mpgsql.Copy;

/// <summary>Tracks the exclusive COPY subprotocol and its command/ReadyForQuery boundary.</summary>
/// <remarks>
///     The connection scheduler must reserve the physical connection until IsCompleted.
///     Asynchronous backend messages are returned to the caller for separate routing;
///     no callbacks run here. COPY BOTH/replication is a different subprotocol.
/// </remarks>
public sealed class BinaryCopyOperation
{
    private readonly bool _extendedQuery;
    private bool _failedByClient;

    private Phase _phase;
    private bool _syncSent;

    public BinaryCopyOperation(
        BackendMessage response,
        bool extendedQuery = false
    )
    {
        if (response.Kind is not (BackendMessageKind.CopyInResponse or BackendMessageKind.CopyOutResponse))
        {
            throw new ArgumentException
            (
                "A binary COPY IN/OUT response is required.",
                nameof(response)
            );
        }
        var copy = response.GetCopyResponse();
        if (copy.Format != FormatCode.Binary)
        {
            throw new NotSupportedException("This operation requires FORMAT binary.");
        }
        foreach (var format in copy.ColumnFormats.Span)
        {
            if (format != FormatCode.Binary)
            {
                throw new InvalidDataException("A binary COPY column has a text format.");
            }
        }
        ColumnCount = copy.ColumnFormats.Length;
        if (ColumnCount > short.MaxValue)
        {
            throw new InvalidDataException("Too many binary COPY columns.");
        }
        IsImport = response.Kind == BackendMessageKind.CopyInResponse;
        _phase = IsImport ? Phase.Import : Phase.Export;
        _extendedQuery = extendedQuery;
    }

    public int ColumnCount { get; }
    public bool IsImport { get; }
    public bool IsCompleted => _phase == Phase.Completed;
    public bool RequiresSync => _extendedQuery && !_syncSent && _phase is Phase.Command or Phase.Ready or Phase.Recovery;
    public bool CanSendData => _phase == Phase.Import;
    public ulong? RowsCopied { get; private set; }
    public DiagnosticMessage? Error { get; private set; }
    public TransactionStatus? TransactionStatus { get; private set; }

    public void CopyDoneSent()
    {
        RequireImport();
        _phase = Phase.Command;
    }

    public void CopyFailSent()
    {
        RequireImport();
        _failedByClient = true;
        _phase = Phase.Command;
    }

    public void SyncSent()
    {
        if (!_extendedQuery || _syncSent || _phase is not (Phase.Export or Phase.Command or Phase.Ready or Phase.Recovery))
        {
            throw new InvalidOperationException("A recovery Sync cannot be sent during COPY IN data.");
        }
        _syncSent = true;
    }

    /// <summary>Returns false for asynchronous messages which the connection must route independently.</summary>
    public bool Accept(BackendMessage message)
    {
        if (message.IsAsynchronous)
        {
            return false;
        }
        if (IsCompleted)
        {
            throw new InvalidOperationException("The COPY operation is completed.");
        }
        if (message.Kind == BackendMessageKind.ErrorResponse)
        {
            if (_phase == Phase.Recovery)
            {
                throw new InvalidDataException("Repeated COPY ErrorResponse.");
            }
            Error = message.GetDiagnostics();
            RowsCopied = null;
            _phase = Phase.Recovery;
            return true;
        }
        switch (_phase, message.Kind)
        {
            case (Phase.Export, BackendMessageKind.CopyData): return true;
            case (Phase.Export, BackendMessageKind.CopyDone):
                _phase = Phase.Command;
                return true;
            case (Phase.Command, BackendMessageKind.CommandComplete) when !_failedByClient:
                var tag = message.GetCommandTag();
                if (!tag.StartsWith
                    (
                        "COPY ",
                        StringComparison.Ordinal
                    ) ||
                    !ulong.TryParse
                    (
                        tag.AsSpan(5),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var count
                    ))
                {
                    throw new InvalidDataException("Invalid COPY command tag.");
                }
                RowsCopied = count;
                _phase = Phase.Ready;
                return true;
            case (Phase.Ready or Phase.Recovery, BackendMessageKind.ReadyForQuery):
                if (_extendedQuery && !_syncSent)
                {
                    throw new InvalidDataException("An extended COPY requires a new Sync/ReadyForQuery boundary.");
                }
                TransactionStatus = message.GetTransactionStatus();
                _phase = Phase.Completed;
                return true;
            default: throw new InvalidDataException($"Unexpected COPY message {message.Kind} in {_phase}.");
        }
    }

    private void RequireImport()
    {
        if (_phase != Phase.Import)
        {
            throw new InvalidOperationException("The COPY IN data phase is not active.");
        }
    }

    private enum Phase
    {
        Import,
        Export,
        Command,
        Ready,
        Recovery,
        Completed
    }
}