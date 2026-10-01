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

    TestConnection connection;
    try
    {
        connection = TestConnection.Open(host, port, user, password, database);
    }
    catch (ServerFailure error) when (requestedDatabase is null && database != "postgres" && error.SqlState == "3D000")
    {
        database = "postgres";
        connection = TestConnection.Open(host, port, user, password, database);
    }

    using (connection)
    {
        Console.WriteLine($"Connected to PostgreSQL {connection.ServerVersion}, database {database}, {connection.Authentication}.");
        LiveProtocolChecks.Run(connection);
        connection.Send(Mpgsql.Protocol.FrontendMessage.Terminate());
    }

    Console.WriteLine("All live protocol checks passed.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
}
