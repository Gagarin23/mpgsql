using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Mpgsql.Internal;

namespace Mpgsql;

/// <summary>An explicitly typed, positional input parameter. Memory is borrowed during execution.</summary>
public partial class MpgsqlParameter : DbParameter
{
    private static readonly Lock DetachedGate = new Lock();
    protected bool NullValue = true;
    protected object? ObjectValue;
    internal MpgsqlParameterCollection? Owner;
    private bool _dirty = true;
    private MpgsqlParameterValue _encodedValue;

    private string _name = "",
        _source = "";

    private bool _nullable,
        _sourceNull;

    private uint _oid;

    private byte _precision,
        _scale;

    private int _size;
    private DataRowVersion _version = DataRowVersion.Current;
    public MpgsqlParameter() { }
    public MpgsqlParameter(uint postgresTypeOid, object? value)
    {
        PostgresTypeOid = postgresTypeOid;
        Value = value;
    }
    public MpgsqlParameter(TypeOid postgresTypeOid, object? value) : this((uint)postgresTypeOid, value) { }
    internal Lock Gate => Owner?.Gate ?? DetachedGate;

    public uint PostgresTypeOid
    {
        get => _oid;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                Owner?.CheckTypeChange(this, value);
                _oid = value;
                Invalidate();
            }
        }
    }

    public override DbType DbType
    {
        get => ParameterTypeMapping.ToDbType(_oid);
        set => PostgresTypeOid = ParameterTypeMapping.ToOid(value);
    }

    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
            {
                throw new NotSupportedException("Only input parameters are supported.");
            }
            lock (Gate)
            {
                CheckMutable();
            }
        }
    }

    [AllowNull]
    public override string ParameterName
    {
        get => _name;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                _name = value ?? "";
            }
        }
    }

    [AllowNull]
    public override string SourceColumn
    {
        get => _source;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                _source = value ?? "";
            }
        }
    }

    public override object? Value
    {
        get => NullValue ? DBNull.Value : ObjectValue;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                NullValue = value is null or DBNull;
                ObjectValue = NullValue ? null : value;
                Invalidate();
            }
        }
    }

    public override bool IsNullable
    {
        get => _nullable;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                _nullable = value;
            }
        }
    }

    public override bool SourceColumnNullMapping
    {
        get => _sourceNull;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                _sourceNull = value;
            }
        }
    }

    public override DataRowVersion SourceVersion
    {
        get => _version;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                _version = value;
            }
        }
    }

    public override int Size
    {
        get => _size;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (Gate)
            {
                CheckMutable();
                _size = value;
            }
        }
    }

    public override byte Precision
    {
        get => _precision;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                _precision = value;
            }
        }
    }

    public override byte Scale
    {
        get => _scale;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                _scale = value;
            }
        }
    }

    protected void CheckMutable()
    {
        Owner?.CheckMutable();
    }
    public override void ResetDbType()
    {
        PostgresTypeOid = 0;
    }
    protected virtual void Invalidate()
    {
        _dirty = true;
    }
    virtual internal MpgsqlParameterValue Snapshot()
    {
        if (_dirty)
        {
            _encodedValue = NullValue ? MpgsqlParameterValue.Null(_oid) : ObjectParameterEncoding.Create(_oid, ObjectValue!);
            _dirty = false;
        }
        return _encodedValue;
    }
    public static MpgsqlParameter FromValue(MpgsqlParameterValue value)
    {
        return new EncodedParameter(value);
    }

    private sealed class EncodedParameter : MpgsqlParameter
    {
        private readonly MpgsqlParameterValue _encoded;
        private bool _changed;
        internal EncodedParameter(MpgsqlParameterValue value)
        {
            _encoded = value;
            ObjectValue = value.ClrValue;
            NullValue = ObjectValue is null;
            PostgresTypeOid = value.PostgresTypeOid;
            _changed = false;
        }

        public override object? Value
        {
            get => _changed ? base.Value : _encoded.ClrValue ?? DBNull.Value;
            set
            {
                base.Value = value;
                _changed = true;
            }
        }

        protected override void Invalidate()
        {
            _changed = true;
            base.Invalidate();
        }
        internal override MpgsqlParameterValue Snapshot()
        {
            return _changed ? base.Snapshot() : _encoded;
        }
    }
}