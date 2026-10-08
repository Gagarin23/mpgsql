# Mpgsql: Minimalistic PostgreSQL driver

Mpgsql is an asynchronous ADO.NET provider for PostgreSQL on .NET 10. It supports
native TCP/TLS connections, trust/cleartext/MD5/SCRAM-SHA-256 authentication,
exclusively pooled connections, reusable commands and batches, local transactions,
prepared statements, and typed binary parameters and readers.

The `Mpgsql` package contains `Mpgsql.dll` and its session implementation,
`Mpgsql.Sessions.dll`. It depends on the separate `Mpgsql.Protocol` package for
protocol codecs, converters, PostgreSQL value types, and binary COPY primitives.
All public namespaces retain their existing names.

## Execute a reusable command

```csharp
using Mpgsql;

await using var source = new MpgsqlDataSource(
    "Host=localhost;Username=app;Database=app;Password=secret;Ssl Mode=VerifyFull");
await using var connection = await source.OpenConnectionAsync();
await using var command = connection.CreateCommand("select $1::bigint");
var value = new MpgsqlParameter<long>(TypeOid.Int64, 42);
command.Parameters.Add(value);

var first = await command.ExecuteScalarAsync<long>();
Console.WriteLine(first.Value);
value.TypedValue = 43;
var second = await command.ExecuteScalarAsync<long>();
Console.WriteLine(second.Value);
```

The provider also works through `DbDataSource`, `DbConnection`, `DbCommand`,
`DbDataReader`, `DbParameter`, `DbBatch`, and `DbTransaction`.
`MpgsqlFactory.Instance` creates those provider objects.
`GetFieldValue<T>` reads built-in values and custom OID/CLR mappings registered
through `MpgsqlTypeMapper`.

## Supported contract

Only `CommandType.Text`, input parameters, and positional `$1`, `$2`, ... SQL
placeholders are supported. Supply an explicit PostgreSQL OID or supported
`DbType`; types are not inferred and SQL is not rewritten. Parameters and command
properties are frozen during execution. Each connection permits one active
operation or reader until completion and cancellation recovery finish.

Network operations are asynchronous. Synchronous network methods throw
`NotSupportedException`; synchronous getters work. Standard scalar execution
returns `null` for no row and `DBNull.Value` for SQL NULL. Typed ValueTask methods,
generic scalar results, and 64-bit affected-row counts are also available.
Raw reader values borrow row memory until movement or disposal.

Defaults: localhost:5432, required Username, database equal to Username, TLS
VerifyFull, connection timeout 15 seconds, command timeout disabled, maximum
10 pooled sessions, row budget 8 MiB per session, recovery timeout 5 seconds.
Pools belong to individual data sources and issue no automatic reset SQL.
Transactions, batch execution, and preparation require an explicitly open
connection. Unprepare sends Close/Sync; disposing a command does not close its
server statement. Ambient transactions, savepoints, DataAdapter, CommandBuilder,
and extended schema discovery are unsupported.

Independent request multiplexing belongs to the separate repository project
`Mpgsql.Multiplexing`. It is not part of this NuGet package.

## Build and package

The package metadata is defined in `src/Mpgsql/Mpgsql.csproj`; `dotnet pack`
generates the NuGet manifest from it. From the repository root:

```shell
dotnet build src/Mpgsql.slnx -c Release
dotnet pack src/Mpgsql.Protocol/Mpgsql.Protocol.csproj -c Release --no-build -o artifacts/packages
dotnet pack src/Mpgsql/Mpgsql.csproj -c Release --no-build -o artifacts/packages
```

See the [upper API guide](https://github.com/Gagarin23/mpgsql/blob/main/docs/upper-api.md)
for execution ownership, custom result converters, cancellation, and cleanup.
