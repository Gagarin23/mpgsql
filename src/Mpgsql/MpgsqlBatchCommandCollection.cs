using System.Data.Common;

namespace Mpgsql;

public sealed class MpgsqlBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<MpgsqlBatchCommand> _items = [];
    private readonly MpgsqlBatch _owner;
    internal MpgsqlBatchCommandCollection(MpgsqlBatch owner)
    {
        _owner = owner;
    }
    public override int Count => _items.Count;
    public override bool IsReadOnly => false;

    public new MpgsqlBatchCommand this[int index]
    {
        get => _items[index];
        set => SetBatchCommand(index, value);
    }

    public void Add(MpgsqlBatchCommand command)
    {
        Add((DbBatchCommand)command);
    }
    public override void Add(DbBatchCommand item)
    {
        Insert(Count, item);
    }
    public override void Insert(int index, DbBatchCommand item)
    {
        lock (_owner.Gate)
        {
            _owner.CheckMutable();
            var command = item as MpgsqlBatchCommand ?? throw new ArgumentException("Expected MpgsqlBatchCommand.");
            lock (command.Gate)
            {
                Validate(command);
                command.CheckMutable();
                _items.Insert(index, command);
                command.Owner = _owner;
            }
        }
    }
    private static MpgsqlBatchCommand Validate(DbBatchCommand item)
    {
        if (item is not MpgsqlBatchCommand command)
        {
            throw new ArgumentException("Expected MpgsqlBatchCommand.");
        }
        if (command.Owner is not null)
        {
            throw new InvalidOperationException("The command already belongs to a batch.");
        }
        if (command.Statement is not null)
        {
            throw new InvalidOperationException("Unprepare before transferring a command.");
        }
        return command;
    }
    public override void Clear()
    {
        lock (_owner.Gate)
        {
            _owner.CheckMutable();
            foreach (var command in _items)
            {
                if (command.Statement is not null)
                {
                    throw new InvalidOperationException("Unprepare the batch first.");
                }
            }
            foreach (var command in _items)
            {
                lock (command.Gate)
                {
                    command.Owner = null;
                }
            }
            _items.Clear();
        }
    }
    public override bool Contains(DbBatchCommand item)
    {
        return item is MpgsqlBatchCommand command && _items.Contains(command);
    }
    public override int IndexOf(DbBatchCommand item)
    {
        return item is MpgsqlBatchCommand command ? _items.IndexOf(command) : -1;
    }
    public override void CopyTo(DbBatchCommand[] array, int arrayIndex)
    {
        foreach (var command in _items)
        {
            array[arrayIndex++] = command;
        }
    }
    public override IEnumerator<DbBatchCommand> GetEnumerator()
    {
        return _items.GetEnumerator();
    }
    public override bool Remove(DbBatchCommand item)
    {
        lock (_owner.Gate)
        {
            _owner.CheckMutable();
            var index = IndexOf(item);
            if (index < 0)
            {
                return false;
            }
            RemoveAt(index);
            return true;
        }
    }
    public override void RemoveAt(int index)
    {
        lock (_owner.Gate)
        {
            _owner.CheckMutable();
            var command = _items[index];
            lock (command.Gate)
            {
                if (command.Statement is not null)
                {
                    throw new InvalidOperationException("Unprepare the batch first.");
                }
                _items.RemoveAt(index);
                command.Owner = null;
            }
        }
    }
    protected override DbBatchCommand GetBatchCommand(int index)
    {
        return _items[index];
    }
    protected override void SetBatchCommand(int index, DbBatchCommand item)
    {
        lock (_owner.Gate)
        {
            _owner.CheckMutable();
            if (ReferenceEquals(_items[index], item))
            {
                return;
            }
            var command = item as MpgsqlBatchCommand ?? throw new ArgumentException("Expected MpgsqlBatchCommand.");
            lock (command.Gate)
            {
                Validate(command);
                var previous = _items[index];
                lock (previous.Gate)
                {
                    if (previous.Statement is not null)
                    {
                        throw new InvalidOperationException("Unprepare the batch first.");
                    }
                    previous.Owner = null;
                    _items[index] = command;
                    command.Owner = _owner;
                }
            }
        }
    }
    internal void Release()
    {
        foreach (var command in _items)
        {
            lock (command.Gate)
            {
                command.Parameters.Release();
                command.Owner = null;
            }
        }
        _items.Clear();
    }
}