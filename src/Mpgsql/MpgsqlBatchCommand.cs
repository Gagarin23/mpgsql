using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Mpgsql.Internal;

namespace Mpgsql;

public sealed class MpgsqlBatchCommand : DbBatchCommand
{
    internal readonly Lock Gate = new Lock();
    internal MpgsqlBatch? Owner;
    internal MpgsqlPreparedStatement? Statement;
    private string _sql = "";
    public MpgsqlBatchCommand()
    {
        Parameters = new MpgsqlParameterCollection(Gate, CheckMutable, () => Statement is not null);
    }
    public MpgsqlBatchCommand(string sql) : this()
    {
        CommandText = sql;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _sql;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                if (Statement is not null && value != _sql)
                {
                    throw new InvalidOperationException("Call UnprepareAsync before changing SQL.");
                }
                _sql = value ?? "";
            }
        }
    }

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            lock (Gate)
            {
                CheckMutable();
                if (value != CommandType.Text)
                {
                    throw new NotSupportedException("Only text commands are supported.");
                }
            }
        }
    }

    protected override DbParameterCollection DbParameterCollection => Parameters;
    public new MpgsqlParameterCollection Parameters { get; }
    public override bool CanCreateParameter => true;
    public long RecordsAffected64 { get; internal set; } = -1;
    public override int RecordsAffected => checked((int)RecordsAffected64);
    internal void CheckMutable()
    {
        if (Owner?.IsBusy == true)
        {
            throw new InvalidOperationException("The batch is executing.");
        }
        Owner?.CheckDisposed();
    }
    public override DbParameter CreateParameter()
    {
        return new MpgsqlParameter();
    }
    internal QueryDefinition Snapshot()
    {
        var query = new QueryDefinition(_sql, Parameters.Snapshot(), PreparedStatement: Statement);
        return query with
        {
            EncodedSize = query.Measure()
        };
    }
}