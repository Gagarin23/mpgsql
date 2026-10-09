# Live ADO.NET checks

Run from the repository root against the isolated containers defined in
`tests/poolers/compose.yaml`:

```powershell
$env:MPGSQL_TEST_HOST = '127.0.0.1'
$env:MPGSQL_TEST_PORT = '16430'
$env:MPGSQL_TEST_USER = 'mpgsql'
$env:MPGSQL_TEST_PASSWORD = 'mpgsql-local-only'
$env:MPGSQL_TEST_DATABASE = 'mpgsql'
$env:MPGSQL_TEST_ADO_ONLY = '1'
$env:MPGSQL_TEST_CANCEL_MAPPING_ONCE = '0'
dotnet run --project tests/Mpgsql.IntegrationTests -c Release
```

These credentials belong to the local test fixture. The checks use plain TCP
(`SslMode=Disable`), perform real startup/authentication, and exercise `Db*`
command reuse, NULL, batch, transactions, Prepare/Unprepare, token and manual
cancellation, recovery, reader disposal, CloseConnection, and reopening.

| Endpoint | Port | `MPGSQL_TEST_CANCEL_MAPPING_ONCE` |
|---|---:|---:|
| PostgreSQL | 16430 | 0 |
| PgBouncer session | 16431 | 0 |
| PgBouncer transaction | 16432 | 0 |
| pg_doorman session | 16433 | 1 |
| pg_doorman transaction | 16434 | 0 |

The session-mode pg_doorman 3.10.6 fixture has a known repeated CancelRequest
mapping limitation. Its `1` setting asserts bounded recovery and retirement of
the affected session, followed by an explicit reopen; it does not skip the
cancellation check or retry a query in the provider.

`MPGSQL_TEST_ADO_ONLY=1` selects ADO.NET checks. To also run the separate
multiplexer and session lifecycle harness, unset that flag and use
`MPGSQL_TEST_UPPER_ONLY=1`; set `MPGSQL_TEST_POOL_MODE=transaction` for the two
transaction-mode endpoints. The complete default integration runner additionally
exercises the protocol/converter/COPY checks.
