using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// A copied handle has the same generation. Repeated release cannot dispose a later rental.
internal readonly struct OwnedRow : IDisposable
{
    private readonly RowStorage? _storage;
    private readonly long _generation;

    internal OwnedRow(BackendMessage message,
        IMemoryOwner<byte>? frameOwner,
        RowBufferBudget? budget = null) : this(new RowStorage(null), message, frameOwner, budget) { }

    internal OwnedRow(RowStorage storage, BackendMessage message,
        IMemoryOwner<byte>? owner, RowBufferBudget? budget)
    {
        _storage = storage;
        _generation = storage.BeginLease();
        try { storage.Initialize(message, owner, budget); }
        catch
        {
            storage.Release(_generation);
            throw;
        }
    }

    internal ReadOnlySequence<byte>? this[int ordinal]
        => (_storage ?? throw new ObjectDisposedException(nameof(OwnedRow))).GetValue(_generation, ordinal);

    internal static void ValidateValues(BackendMessage message)
    {
        var reader = new WireReader(message.Payload.Slice(2));
        var count = message.GetDataRow().Count;
        for (var i = 0; i < count; i++)
        {
            reader.SkipValue();
        }
        reader.End();
    }

    public void Dispose()
    {
        _storage?.Release(_generation);
    }
}