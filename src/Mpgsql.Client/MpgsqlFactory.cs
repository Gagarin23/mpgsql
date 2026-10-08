using System.Data.Common;

namespace Mpgsql;

public sealed class MpgsqlFactory : DbProviderFactory
{
    public static readonly MpgsqlFactory Instance = new MpgsqlFactory();
    private MpgsqlFactory() { }
    public override bool CanCreateBatch => true;
    public override DbConnection CreateConnection()
    {
        return new MpgsqlConnection();
    }
    public override DbDataSource CreateDataSource(string connectionString)
    {
        return new MpgsqlDataSource(connectionString);
    }
    public override DbCommand CreateCommand()
    {
        return new MpgsqlCommand();
    }
    public override DbParameter CreateParameter()
    {
        return new MpgsqlParameter();
    }
    public override DbConnectionStringBuilder CreateConnectionStringBuilder()
    {
        return new MpgsqlConnectionStringBuilder();
    }
    public override DbBatch CreateBatch()
    {
        return new MpgsqlBatch();
    }
    public override DbBatchCommand CreateBatchCommand()
    {
        return new MpgsqlBatchCommand();
    }
}