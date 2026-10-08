using System.Collections.ObjectModel;
using Mpgsql.Internal;

namespace Mpgsql;

public sealed class MpgsqlParameterCollection : Collection<MpgsqlParameter>
{
    private readonly MpgsqlCommand _command;
    internal MpgsqlParameterCollection(MpgsqlCommand command) : base(new ParameterBuffer()) => _command = command;
    protected override void InsertItem(int index, MpgsqlParameter item)
    { lock (_command.Connection.Gate) { _command.CheckMutable(); base.InsertItem(index, item); } }
    protected override void SetItem(int index, MpgsqlParameter item)
    { lock (_command.Connection.Gate) { _command.CheckMutable(); base.SetItem(index, item); } }
    protected override void RemoveItem(int index)
    { lock (_command.Connection.Gate) { _command.CheckMutable(); base.RemoveItem(index); } }
    protected override void ClearItems()
    { lock (_command.Connection.Gate) { _command.CheckMutable(); base.ClearItems(); } }
    internal ReadOnlyMemory<MpgsqlParameter> Freeze() => ((ParameterBuffer)Items).Memory;
    internal void Release() => ((ParameterBuffer)Items).Release();
}
