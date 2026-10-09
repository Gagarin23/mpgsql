namespace Mpgsql.Internal;

// Publication and physical Sync ownership shared by the two concrete engines.
// Row consumption never dispatches through this control-plane contract.
internal interface IQueryGroup
{
    CancellationToken RequestToken { get; }
    bool CancellationRequested { get; }
    bool IsAdoSession { get; }
    object DeliveryGate { get; }
    Task Completion { get; }
    void ThrowForSend();
    void SyncQueued();
    void SealPublished();
    void RegisterOperation(MessageOperationKind kind, MpgsqlPreparedStatement? statement);
    void AddWrite(OutboundWork work);
    void RemoveWrite(OutboundWork work);
}
