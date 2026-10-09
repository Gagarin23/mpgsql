# Live PostgreSQL ADO comparisons

`Mpgsql.Benchmarks.PostgresAdoBenchmarks` executes real SQL against PostgreSQL.
Mpgsql uses `new MpgsqlDataSource(connectionString)` and its production transport;
Npgsql uses the version-pinned `NpgsqlDataSourceBuilder`. Both retain one open,
exclusive connection throughout a series. TLS and GSS encryption are disabled
for this local fixture, command timeout is zero, automatic preparation is off,
and no per-operation pool lease or reset query is timed.

Set all five integration environment variables explicitly before setup:

- `MPGSQL_TEST_HOST`
- `MPGSQL_TEST_PORT`
- `MPGSQL_TEST_USER`
- `MPGSQL_TEST_PASSWORD` (may be empty for trust authentication)
- `MPGSQL_TEST_DATABASE`

Connection strings and passwords are private. Public endpoint metadata contains
only host, port, username, database and TLS mode.

The five reader cases reuse `QueryScenario.Readers`: `Empty`, `OneBigint`,
`Rows128Columns8`, `Rows4096` and `Bytea64KiB`. Reused and fresh typed commands
use explicit bigint/bytea parameter types. Mpgsql's typed path uses ValueTask
movement and borrowed raw bytea copied once into a reusable destination;
Npgsql uses Task movement and one `GetBytes` copy into a reusable destination.
Object paths share the same `DbCommand`/`DbDataReader` consumer and return boxed
bigints. Additional Mpgsql standard typed methods use Task and `GetBytes`;
native counterparts use the same native typed consumer.

The paired runner can set `Case=Batch16`, then call `PrepareMpgsql` or
`PrepareNpgsql` outside timing and allocation windows before every invocation.
Each invocation executes and disposes 32 fresh batches, each containing 16
`select $1::bigint` commands with values 1 through 16. One Sync is used per batch.
The expected checksum is 4,352 per invocation, or 136 per batch; latency and
allocations are normalized by `OperationsPerInvocation=32`. BDN's attributes
cover only the five reader cases; the batch methods are exposed to the paired
runner separately. `MpgsqlFacadeBatchStandard` uses the `DbBatch` Task API and
the same per-row NULL check and `GetInt64` consumption as the native batch path.

Setup validates every reader API and records the PostgreSQL backend PID.
`CheckIdle` verifies connection state and unchanged physical-session identity
without network IO. `CheckIdleAsync` additionally runs `select pg_backend_pid()`;
that optional probe belongs outside measured and allocated windows. This fixture
is for a direct PostgreSQL endpoint; it does not claim transaction-pooler parity.
