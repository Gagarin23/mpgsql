using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// A bounded, session-local cache of empty storage. It never retains payloads or field memories.
internal sealed class RowStoragePool
{
    private const int Capacity = 1024;
    private readonly Lock _gate = new Lock();
    private int _count;
    private RowStorage? _head;

    internal OwnedRow Rent(
        BackendMessage message, IMemoryOwner<byte>? owner,
        RowBufferBudget? budget
    )
    {
        RowStorage? storage;
        lock (_gate)
        {
            storage = _head;
            if (storage is not null)
            {
                _head = storage.Next;
                storage.Next = null;
                _count--;
            }
        }
        return new OwnedRow(storage ?? new RowStorage(this), message, owner, budget);
    }

    internal void Return(RowStorage storage)
    {
        if (!storage.CanReuse)
        {
            return; // a generation can never wrap and alias a stale handle
        }
        lock (_gate)
        {
            if (_count == Capacity)
            {
                return;
            }
            storage.Next = _head;
            _head = storage;
            _count++;
        }
    }
}