using System.Buffers;
using System.Collections.Frozen;

namespace Mpgsql;

/// <summary>Explicit OID/CLR mappings for custom binary result values. Readers never infer types.</summary>
public sealed class MpgsqlTypeMapper
{
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<(uint Oid, Type Clr), object>? _registrations = [];
    private readonly FrozenDictionary<(uint Oid, Type Clr), object>? _snapshot;
    public MpgsqlTypeMapper() { }
    private MpgsqlTypeMapper(FrozenDictionary<(uint Oid, Type Clr), object> snapshot)
    {
        _snapshot = snapshot;
        _registrations = null;
    }
    /// <summary>The converter runs on the caller's getter, with bytes borrowed from the current row.</summary>
    public MpgsqlTypeMapper Register<T>(uint postgresTypeOid, Func<ReadOnlySequence<byte>, T> read)
    {
        ArgumentOutOfRangeException.ThrowIfZero(postgresTypeOid);
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            if (_registrations is null)
            {
                throw new InvalidOperationException("The connection/source mapping snapshot is immutable.");
            }
            _registrations[(postgresTypeOid, typeof(T))] = read;
        }
        return this;
    }
    internal MpgsqlTypeMapper Snapshot()
    {
        lock (_gate)
        {
            return _snapshot is not null ? this : new MpgsqlTypeMapper(_registrations!.ToFrozenDictionary());
        }
    }
    internal bool TryGetReader<T>(uint oid, out Func<ReadOnlySequence<byte>, T>? read)
    {
        if (_snapshot is not null && _snapshot.TryGetValue((oid, typeof(T)), out var converter))
        {
            read = (Func<ReadOnlySequence<byte>, T>)converter;
            return true;
        }
        read = null;
        return false;
    }
}