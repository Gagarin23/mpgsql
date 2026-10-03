using System.Collections.ObjectModel;

namespace Mpgsql;

public sealed class MpgsqlCommandCollection : Collection<MpgsqlCommand>
{
    private readonly MpgsqlBatch _batch;
    internal MpgsqlCommandCollection(MpgsqlBatch batch) => _batch = batch;

    private void Validate(MpgsqlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Connection != _batch.Connection)
            throw new ArgumentException("The command belongs to another connection.", nameof(command));
        command.CheckMutable();
        if (command.Batch is not null)
            throw new InvalidOperationException("The command already belongs to a batch.");
    }

    protected override void InsertItem(int index, MpgsqlCommand item)
    {
        lock (_batch.Connection.Gate)
        {
            _batch.CheckMutable(); Validate(item);
            base.InsertItem(index, item); item.Batch = _batch;
        }
    }

    protected override void SetItem(int index, MpgsqlCommand item)
    {
        lock (_batch.Connection.Gate)
        {
            _batch.CheckMutable();
            var old = this[index];
            if (old == item) return;
            Validate(item); base.SetItem(index, item); old.Batch = null; item.Batch = _batch;
        }
    }

    protected override void RemoveItem(int index)
    {
        lock (_batch.Connection.Gate)
        {
            _batch.CheckMutable(); var old = this[index]; base.RemoveItem(index); old.Batch = null;
        }
    }

    protected override void ClearItems()
    {
        lock (_batch.Connection.Gate) { _batch.CheckMutable(); Release(); }
    }

    internal void Release()
    {
        foreach (var command in this) command.Batch = null;
        base.ClearItems();
    }
}
