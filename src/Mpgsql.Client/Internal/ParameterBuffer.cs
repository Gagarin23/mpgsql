using System.Collections;

namespace Mpgsql.Internal;

// Collection-owned storage. A started one-shot command borrows its populated prefix until its
// producer finishes; collection mutation is protected by the command's connection gate.
internal sealed class ParameterBuffer : IList<MpgsqlParameter>
{
    private MpgsqlParameter[] _items = [];
    private int _version;
    public int Count { get; private set; }
    public bool IsReadOnly => false;
    internal ReadOnlyMemory<MpgsqlParameter> Memory => _items.AsMemory(0, Count);

    public MpgsqlParameter this[int index]
    {
        get { CheckIndex(index); return _items[index]; }
        set { CheckIndex(index); _items[index] = value; _version++; }
    }

    public void Add(MpgsqlParameter item) => Insert(Count, item);
    public void Insert(int index, MpgsqlParameter item)
    {
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (Count == _items.Length)
            Array.Resize(ref _items, _items.Length == 0 ? 4 : checked(_items.Length * 2));
        if (index < Count) Array.Copy(_items, index, _items, index + 1, Count - index);
        _items[index] = item;
        Count++;
        _version++;
    }

    public void RemoveAt(int index)
    {
        CheckIndex(index);
        Count--;
        if (index < Count) Array.Copy(_items, index + 1, _items, index, Count - index);
        _items[Count] = default;
        _version++;
    }

    public bool Remove(MpgsqlParameter item)
    {
        int index = IndexOf(item);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    public int IndexOf(MpgsqlParameter item) => Array.IndexOf(_items, item, 0, Count);
    public bool Contains(MpgsqlParameter item) => IndexOf(item) >= 0;
    public void CopyTo(MpgsqlParameter[] array, int arrayIndex)
        => Array.Copy(_items, 0, array, arrayIndex, Count);

    public void Clear()
    {
        Array.Clear(_items, 0, Count);
        Count = 0;
        _version++;
    }

    internal void Release()
    {
        Clear();
        _items = [];
    }

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
    }

    public IEnumerator<MpgsqlParameter> GetEnumerator() => new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private struct Enumerator(ParameterBuffer buffer) : IEnumerator<MpgsqlParameter>
    {
        private readonly int _version = buffer._version;
        private int _next;
        public MpgsqlParameter Current { get; private set; }
        object IEnumerator.Current => _next == 0 || _next > buffer.Count
            ? throw new InvalidOperationException("The enumerator is not positioned on an element.") : Current;
        public bool MoveNext()
        {
            CheckVersion();
            if (_next < buffer.Count) { Current = buffer._items[_next++]; return true; }
            _next = buffer.Count + 1;
            Current = default;
            return false;
        }
        public void Reset() { CheckVersion(); _next = 0; Current = default; }
        public void Dispose() { }
        private readonly void CheckVersion()
        {
            if (_version != buffer._version)
                throw new InvalidOperationException("The parameter collection changed during enumeration.");
        }
    }
}
