using System.Data.Common;

namespace Mpgsql.IntegrationTests;

// Opt-in: creates and removes uniquely named roles on the disposable PostgreSQL fixture.
internal static class UnicodeAuthenticationChecks
{
    internal static async Task RunAsync(string host, int port,
        string user, string password,
        string database)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var settings = new MpgsqlConnectionStringBuilder {Host = host, Port = port, Username = user, Password = password, Database = database, SslMode = MpgsqlSslMode.Disable};
        await using var admin = new MpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync(token);
        await using var command = admin.CreateCommand("set password_encryption = 'scram-sha-256'");
        await command.ExecuteNonQueryAsync(token);
        string[] passwords = ["I\u00ADX\u00AA\u00A0Я", "x\u200By", "\u00AD", "\u0340", "\u1D2C", "\u0627x\u0627", "Я😀", "a\u0007"];
        foreach (var candidate in passwords)
        {
            var role = "mpgsql_unicode_" + Guid.NewGuid().ToString("N");
            command.CommandText = "create role " + role + " login password '" + candidate.Replace("'", "''", StringComparison.Ordinal) + "'";
            await command.ExecuteNonQueryAsync(token);
            try
            {
                settings.Username = role;
                settings.Password = candidate;
                await using DbConnection connection = new MpgsqlConnection(settings.ConnectionString);
                await connection.OpenAsync(token);
                await using var query = connection.CreateCommand();
                query.CommandText = "select 1::bigint";
                if (!Equals(await query.ExecuteScalarAsync(token), 1L))
                {
                    throw new InvalidDataException("Unicode SCRAM result.");
                }
            }
            finally
            {
                command.CommandText = "drop role " + role;
                await command.ExecuteNonQueryAsync(token);
            }
        }
        Console.WriteLine("PASS PostgreSQL Unicode SCRAM: mapping, space priority, empty/prohibited/unassigned/bidi fallback, supplementary characters.");
    }
}