using Mpgsql.Internal;

namespace Mpgsql;

/// <summary>Stores a typed value without boxing it into DbParameter.Value.</summary>
public sealed class MpgsqlParameter<T> : MpgsqlParameter
{
    private bool _dirty = true;
    private MpgsqlParameterValue _encoded;
    private T _value = default!;
    public MpgsqlParameter() { }
    public MpgsqlParameter(uint postgresTypeOid, T value)
    {
        PostgresTypeOid = postgresTypeOid;
        TypedValue = value;
    }
    public MpgsqlParameter(TypeOid postgresTypeOid, T value)
    {
        PostgresTypeOid = (uint)postgresTypeOid;
        TypedValue = value;
    }

    public T TypedValue
    {
        get => _value;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                _value = value;
                NullValue = value is null;
                _dirty = true;
            }
        }
    }

    public override object? Value
    {
        get => NullValue ? DBNull.Value : _value;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                if (value is null or DBNull)
                {
                    _value = default!;
                    NullValue = true;
                }
                else
                {
                    _value = value is T typed ? typed : throw new InvalidCastException($"The value must be {typeof(T)}.");
                    NullValue = false;
                }
                _dirty = true;
            }
        }
    }

    protected override void Invalidate()
    {
        _dirty = true;
    }
    internal override MpgsqlParameterValue Snapshot()
    {
        if (_dirty)
        {
            _encoded = NullValue ? MpgsqlParameterValue.Null(PostgresTypeOid) : ParameterEncoding<T>.Create(PostgresTypeOid, _value);
            _dirty = false;
        }
        return _encoded;
    }
}