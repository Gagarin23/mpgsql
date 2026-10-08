using Mpgsql.IntegrationTests;

try
{
    string host = Environment.GetEnvironmentVariable("MPGSQL_TEST_HOST") ?? "localhost";
    int port = int.Parse(Environment.GetEnvironmentVariable("MPGSQL_TEST_PORT") ?? "5432");
    string user = Environment.GetEnvironmentVariable("MPGSQL_TEST_USER") ?? "test";
    string password = Environment.GetEnvironmentVariable("MPGSQL_TEST_PASSWORD")
                      ?? throw new InvalidOperationException("Set MPGSQL_TEST_PASSWORD to run live tests.");
    string? requestedDatabase = Environment.GetEnvironmentVariable("MPGSQL_TEST_DATABASE");
    string database = requestedDatabase ?? user;

    if (Environment.GetEnvironmentVariable("MPGSQL_TEST_SHARED_SYNC_ONLY") == "1")
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await UpperApiChecks.SharedSyncAsync(host, port, user, password, database, lifetime.Token);
        return;
    }

    if (Environment.GetEnvironmentVariable("MPGSQL_TEST_UPPER_ONLY") == "1")
    {
        await UpperApiChecks.RunAsync(host, port, user, password, database,
            Environment.GetEnvironmentVariable("MPGSQL_TEST_POOL_MODE") == "transaction",
            activeTerminalEofOnly: Environment.GetEnvironmentVariable("MPGSQL_TEST_ACTIVE_TERMINAL_EOF_ONLY") == "1");
        return;
    }

    TestConnection connection;
    try
    {
        connection = TestConnection.Open(host,
            port,
            user,
            password,
            database);
    }
    catch (ServerFailure error) when (requestedDatabase is null && database != "postgres" && error.SqlState == "3D000")
    {
        database = "postgres";
        connection = TestConnection.Open(host,
            port,
            user,
            password,
            database);
    }

    using (connection)
    {
        Console.WriteLine($"Connected to PostgreSQL {connection.ServerVersion}, database {database}, {connection.Authentication}.");
        if (Environment.GetEnvironmentVariable("MPGSQL_TEST_CONVERTERS_ONLY") == "1")
        {
            BuiltinConverterChecks.Run(connection);
            connection.Send(Mpgsql.Protocol.FrontendMessage.Terminate());
            return;
        }
        LiveProtocolChecks.Run(connection);
        await MessageSessionChecks.RunAsync(connection);
        connection.Send(Mpgsql.Protocol.FrontendMessage.Terminate());
    }

    Console.WriteLine("All live protocol checks passed.");
    await UpperApiChecks.RunAsync(host, port, user, password, database, transactionPool: false);
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
