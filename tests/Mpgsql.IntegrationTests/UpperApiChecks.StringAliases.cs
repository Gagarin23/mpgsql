namespace Mpgsql.IntegrationTests;

internal static partial class UpperApiChecks
{
    private static async Task StringAliasesAsync(MpgsqlMultiplexingDataSource source, MpgsqlDataSource client,
        CancellationToken token)
    {
        foreach (var sql in new[] {"select 'abc'::varchar", "select 'abc'::char(3)", "select 'abc'::name"})
            Check((await source.ExecuteScalarAsync<string>(sql, cancellationToken: token)).Value == "abc", "alias scalar literal");

        // No ::text: pg_class.relname is name, and ARRAY[relname] is name[].
        await using (var catalog = await source.ExecuteReaderAsync(
                         "select relname, ARRAY[relname] from pg_catalog.pg_class where oid = 'pg_catalog.pg_type'::regclass",
                         cancellationToken: token))
        {
            Check(await catalog.ReadAsync(), "catalog name row");
            Check(catalog.Columns.Span[0].DataTypeOid == 19 && catalog.Columns.Span[1].DataTypeOid == 1003, "catalog name OIDs");
            Check(catalog.GetFieldValue<string>(0) == "pg_type", "catalog name getter");
            Check(catalog.GetFieldValue<ReadOnlyMemory<string?>>(1).Span.SequenceEqual(new[] {"pg_type"}), "catalog name array getter");
            Check(!await catalog.NextResultAsync(), "catalog boundary");
        }

        // PostgreSQL's default NAMEDATALEN=64 counts bytes, not UTF16 code units.
        var longestName = new string('Я', 31) + "a";
        Check((await source.ExecuteScalarAsync<string>("select $1", new[] {MpgsqlParameterValue.Name(longestName)}, token)).Value == longestName,
            "63-byte binary name parameter");
        try
        {
            await source.ExecuteScalarAsync<string>("select $1", new[] {MpgsqlParameterValue.Name(longestName + "a")}, token);
            throw new InvalidDataException("PostgreSQL accepted an oversized name.");
        }
        catch (MpgsqlServerException error) { Check(error.SqlState == "42622", "server validates binary name length"); }

        await using var connection = await client.OpenConnectionAsync(token);
        // A real table, pinned in an explicit transaction even through transaction poolers.
        await NonQuery(connection, "begin", token);
        try
        {
            await NonQuery(connection, "create temporary table mpgsql_r03_strings (id int, v varchar(8), c char(5), n name, "
                                       + "va varchar(8)[], ca char(5)[], na name[]) on commit drop", token);
            await using (var insert = connection.CreateCommand("insert into mpgsql_r03_strings values (1,$1,$2,$3,$4,$5,$6)"))
            {
                insert.Parameters.Add(MpgsqlParameterValue.VarChar("Я😀"));
                insert.Parameters.Add(MpgsqlParameterValue.BpChar("Я"));
                insert.Parameters.Add(MpgsqlParameterValue.Name("имя"));
                insert.Parameters.Add(MpgsqlParameterValue.VarCharArray(new[] {"Я😀", null, ""}));
                insert.Parameters.Add(MpgsqlParameterValue.BpCharArray(new[] {"x", null, ""}));
                insert.Parameters.Add(MpgsqlParameterValue.NameArray(new[] {"pg_type", null, ""}));
                Check(await insert.ExecuteNonQueryAsync(token) == 1, "string alias insert");
            }
            await NonQuery(connection, "insert into mpgsql_r03_strings values (2,'','','','{}','{}','{}'), (3,NULL,NULL,NULL,NULL,NULL,NULL)", token);
            ReadOnlyMemory<string?> retained;
            await using (var select = connection.CreateCommand("select v,c,n,va,ca,na from mpgsql_r03_strings order by id"))
            await using (var reader = await select.ExecuteReaderAsync(token))
            {
                Check(await reader.ReadAsync(), "real string table row");
                uint[] expectedOids = [1043, 1042, 19, 1015, 1014, 1003];
                for (var i = 0; i < expectedOids.Length; i++)
                {
                    Check(reader.Columns.Span[i].DataTypeOid == expectedOids[i], "real table alias OID");
                    Check(reader.Columns.Span[i].TableOid != 0, "real table origin metadata");
                }
                try
                {
                    reader.GetFieldValue<long>(0);
                    throw new InvalidDataException("varchar accepted a bigint getter.");
                }
                catch (InvalidCastException) { }
                Check(reader.GetFieldValue<string>(0) == "Я😀", "varchar table getter");
                Check(reader.GetFieldValue<string>(1) == "Я    ", "char(5) table getter preserves padding");
                Check(reader.GetFieldValue<string>(2) == "имя", "name table getter");
                Check(reader.GetFieldValue<ReadOnlyMemory<string?>>(3).Span.SequenceEqual(new[] {"Я😀", null, ""}), "varchar table array");
                Check(reader.GetFieldValue<ReadOnlyMemory<string?>>(4).Span.SequenceEqual(new[] {"x    ", null, "     "}), "char(5) table array padding");
                retained = reader.GetFieldValue<ReadOnlyMemory<string?>>(5);
                Check(retained.Span.SequenceEqual(new[] {"pg_type", null, ""}), "name table array");
                Check(await reader.ReadAsync(), "empty table row");
                Check(reader.GetFieldValue<string>(0) == "" && reader.GetFieldValue<string>(1) == "     "
                                                            && reader.GetFieldValue<string>(2) == "", "empty strings and server char padding");
                for (var i = 3; i < 6; i++)
                {
                    Check(reader.GetFieldValue<ReadOnlyMemory<string?>>(i).IsEmpty, "empty alias array");
                }
                Check(await reader.ReadAsync(), "NULL table row");
                for (var i = 0; i < 3; i++)
                {
                    Check(reader.GetFieldValue<string?>(i) is null, "SQL NULL string");
                }
                for (var i = 3; i < 6; i++)
                {
                    Check(reader.GetFieldValue<ReadOnlyMemory<string?>?>(i) is null, "SQL NULL array");
                }
                Check(!await reader.ReadAsync() && !await reader.NextResultAsync(), "string table boundary");
            }
            Check(retained.Span.SequenceEqual(new[] {"pg_type", null, ""}), "decoded alias array outlives reader");
            Check(await Scalar<string>(connection, "select n from mpgsql_r03_strings where id=1", token) == "имя",
                "connection usable after invalid getter");
            await NonQuery(connection, "commit", token);
        }
        catch
        {
            await NonQuery(connection, "rollback", token);
            throw;
        }
        Console.WriteLine("PASS string aliases: scalar/array parameters, real varchar/char/name table, NULL/empty/padding, pg_class catalog, server name limits");
    }
}