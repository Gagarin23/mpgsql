using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Internal;

// A bounded cache of inactive storage. It retains no original payload, frame owner or field memory.
// A session's sole receive loop can detach 32 rentals together; consumers still return under
// the shared gate. At most 31 detached inactive objects supplement the shared capacity of 4096.
// Driver-owned small buffers retain their high-water tier up to 128 bytes: at most
// 4127 * 128 = 528256 bytes of inactive private-buffer capacity, plus array headers and storage objects,
// independently of row budget. Keeping a whole 4096-row burst avoids recreating its storage.
internal sealed class RowStoragePool
{
    private const int Capacity = 4096;
    private const int RentalChunkSize = 32;
    private readonly Lock _gate = new Lock();
    private readonly bool _singleRenter;
    private int _count;
    private RowStorage? _head;
    private int _rentalCount;
    private RowStorage? _rentals;

    internal RowStoragePool(bool singleRenter = false)
    {
        _singleRenter = singleRenter;
    }

    internal int RetainedStorageCount
    {
        get
        {
            lock (_gate)
            {
                return _count + Volatile.Read(ref _rentalCount);
            }
        }
    }

    internal OwnedRow Rent(
        BackendMessage message, IMemoryOwner<byte>? owner,
        RowBufferBudget? budget
    )
    {
        RowStorage? storage;
        if (_singleRenter && _rentals is { } rental)
        {
            storage = rental;
            _rentals = rental.Next;
            rental.Next = null;
            _rentalCount--;
        }
        else
        {
            lock (_gate)
            {
                storage = _head;
                if (storage is not null)
                {
                    _head = storage.Next;
                    storage.Next = null;
                    _count--;
                    if (_singleRenter)
                    {
                        // The current rental is already removed, leaving at most 31 cached
                        // objects even if concurrent returns refill the shared list to capacity.
                        for (var i = 1; i < RentalChunkSize && _head is { } next; i++)
                        {
                            _head = next.Next;
                            next.Next = _rentals;
                            _rentals = next;
                            _rentalCount++;
                            _count--;
                        }
                    }
                }
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
