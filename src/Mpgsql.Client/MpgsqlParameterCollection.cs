using System.Collections;
using System.Data.Common;

namespace Mpgsql;

public sealed class MpgsqlParameterCollection : DbParameterCollection
{
    private readonly Action _checkMutable;
    private readonly List<MpgsqlParameter> _items = [];
    private readonly Func<bool> _prepared;
    private MpgsqlParameterValue[] _encoded = [];
    internal MpgsqlParameterCollection(Lock gate, Action checkMutable,
        Func<bool> prepared)
    {
        (Gate, _checkMutable, _prepared) = (gate, checkMutable, prepared);
    }
    internal Lock Gate { get; }
    public override int Count => _items.Count;
    public override object SyncRoot { get; } = new object();

    public new MpgsqlParameter this[int index]
    {
        get => _items[index];
        set => SetParameter(index, value);
    }

    public new MpgsqlParameter this[string name]
    {
        get => (MpgsqlParameter)GetParameter(name);
        set => SetParameter(name, value);
    }

    internal void CheckMutable()
    {
        _checkMutable();
    }
    internal void CheckTypeChange(MpgsqlParameter parameter, uint oid)
    {
        if (_prepared() && oid != parameter.PostgresTypeOid)
        {
            throw new InvalidOperationException("Call UnprepareAsync before changing a parameter OID.");
        }
    }
    private void CheckStructure()
    {
        CheckMutable();
        if (_prepared())
        {
            throw new InvalidOperationException("Call UnprepareAsync before changing the parameter collection.");
        }
    }
    public int Add(MpgsqlParameter value)
    {
        return Add((object)value);
    }
    public int Add(MpgsqlParameterValue value)
    {
        return Add(MpgsqlParameter.FromValue(value));
    }
    public override int Add(object value)
    {
        lock (Gate)
        {
            CheckStructure();
            var item = Validate(value);
            var index = _items.Count;
            _items.Add(item);
            item.Owner = this;
            return index;
        }
    }
    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (Gate)
        {
            CheckStructure();
            var pending = new HashSet<MpgsqlParameter>();
            foreach (var value in values)
            {
                if (!pending.Add(Validate(value)))
                {
                    throw new ArgumentException("Duplicate parameter.", nameof(values));
                }
            }
            foreach (MpgsqlParameter value in values)
            {
                _items.Add(value);
                value.Owner = this;
            }
        }
    }
    private static MpgsqlParameter Validate(object? value)
    {
        if (value is not MpgsqlParameter parameter)
        {
            throw new ArgumentException("Expected MpgsqlParameter.", nameof(value));
        }
        if (parameter.Owner is not null)
        {
            throw new InvalidOperationException("The parameter already belongs to a collection.");
        }
        return parameter;
    }
    public override bool Contains(object value)
    {
        return value is MpgsqlParameter item && _items.Contains(item);
    }
    public override bool Contains(string value)
    {
        return IndexOf(value) >= 0;
    }
    public override int IndexOf(object value)
    {
        return value is MpgsqlParameter item ? _items.IndexOf(item) : -1;
    }
    public override int IndexOf(string name)
    {
        return _items.FindIndex(item => item.ParameterName == name);
    }
    public override IEnumerator GetEnumerator()
    {
        return _items.GetEnumerator();
    }
    public override void CopyTo(Array array, int index)
    {
        ((ICollection)_items).CopyTo(array, index);
    }
    public override void Clear()
    {
        lock (Gate)
        {
            CheckStructure();
            foreach (var item in _items) item.Owner = null;
            _items.Clear();
            Array.Clear(_encoded);
        }
    }
    public override void Insert(int index, object value)
    {
        lock (Gate)
        {
            CheckStructure();
            var item = Validate(value);
            _items.Insert(index, item);
            item.Owner = this;
        }
    }
    public override void Remove(object value)
    {
        lock (Gate)
        {
            CheckStructure();
            var index = IndexOf(value);
            if (index >= 0)
            {
                RemoveAt(index);
            }
        }
    }
    public override void RemoveAt(int index)
    {
        lock (Gate)
        {
            CheckStructure();
            var item = _items[index];
            _items.RemoveAt(index);
            item.Owner = null;
        }
    }
    public override void RemoveAt(string name)
    {
        var index = IndexOf(name);
        if (index < 0)
        {
            throw new IndexOutOfRangeException(name);
        }
        RemoveAt(index);
    }
    protected override DbParameter GetParameter(int index)
    {
        return _items[index];
    }
    protected override DbParameter GetParameter(string name)
    {
        var index = IndexOf(name);
        return index >= 0 ? _items[index] : throw new IndexOutOfRangeException(name);
    }
    protected override void SetParameter(int index, DbParameter value)
    {
        lock (Gate)
        {
            CheckStructure();
            if (ReferenceEquals(_items[index], value))
            {
                return;
            }
            var item = Validate(value);
            _items[index].Owner = null;
            _items[index] = item;
            item.Owner = this;
        }
    }
    protected override void SetParameter(string name, DbParameter value)
    {
        var index = IndexOf(name);
        if (index < 0)
        {
            throw new IndexOutOfRangeException(name);
        }
        SetParameter(index, value);
    }
    internal ReadOnlyMemory<MpgsqlParameterValue> Snapshot()
    {
        if (_encoded.Length < Count)
        {
            Array.Resize(ref _encoded, Count);
        }
        for (var i = 0; i < Count; i++)
        {
            _encoded[i] = _items[i].Snapshot();
        }
        return _encoded.AsMemory(0, Count);
    }
    internal void Release()
    {
        foreach (var item in _items) item.Owner = null;
        _items.Clear();
        _encoded = [];
    }
}