# ADO.NET refactor verification

Baseline: `d67e4e2d33e6155f8d97ea2457d3c8896690c648`. Its source archive,
Release benchmark binaries and identity are preserved under
`artifacts/ado-refactor-baseline/`. Before source changes the Release build had
zero warnings/errors and the standalone xUnit runner passed 1081 tests.

The candidate is the working tree with four assemblies: Mpgsql,
Mpgsql.Sessions, Mpgsql.Client and Mpgsql.Multiplexing. The lower package remains
independent; neither of the two upper assemblies references the other.

## Local checks

```powershell
dotnet build src/Mpgsql.slnx -c Release --nologo -m:1 -p:UseSharedCompilation=false -nr:false
dotnet tests/Mpgsql.Tests/bin/Release/net10.0/Mpgsql.Tests.dll -nologo -parallel none
dotnet benchmarks/Mpgsql.Benchmarks/bin/Release/net10.0/Mpgsql.Benchmarks.dll --verify
dotnet pack src/Mpgsql/Mpgsql.csproj -c Release --no-build --no-restore -o artifacts/ado-refactor-package
```

Release build: zero warnings and errors. Standalone xUnit: **1129 passed**, zero
errors, failures or skips. VSTest is not the evidence for these counts; its
testhost connection failed during baseline planning. The existing `--verify`
harness covers converters, complete protocol vectors, fragmentation, COPY,
TCP peers, consumer profiles, batching, recovery and backpressure.

Parallel full-suite repeats intermittently failed the unchanged
`NumericArraySimdTests` zero-allocation assertion (one observed repeat counted
1344 bytes). The isolated 16-test numeric suite and the complete sequential
1129-test suite pass. The lower converter implementation was not changed; the
cause of this intermittent parallel-run allocation observation is unresolved.
The failed log is preserved as `artifacts/unit-tests-intermittent.log` rather
than treated as a successful run. Concurrency scenarios within individual
protocol/pool tests still run in the sequential suite.

New tests exercise the ADO.NET provider through `Db*` base types, reusable
commands/parameters/batches, SQL NULL versus no row, metadata, HasRows,
GetFieldValue<T>, custom OID/CLR mappings, affected-row attribution and checked
Int32 overflow after ReadyForQuery. They also cover full Prepare/Close payloads,
partial batch preparation, cancellation after ParseComplete, all supported
local isolation levels, reader disposal and pool ownership.

Preparation with command timeout zero can exceed the recovery timeout. Recovery
timeouts start on cancellation, disposal or a server error; they do not create
an implicit timeout for a healthy PrepareAsync. A missing ReadyForQuery after a
server error retires the session. A concurrent close cannot turn a failed
preparation into successful completion by observing its error first.

Cancellation tests hold the cancellation channel and/or ReadyForQuery boundary
to prove that a following execution cannot inherit an earlier cancellation.
They include cancellation before publication, while reading, before consuming a
prefetched row, and the preservation of the first cancellation reason when a
later timeout fires during recovery. Prepared-handle retention, asynchronous
messages, buffer ownership and raw pipeline-group tests remain in the suite.

Startup tests assert complete startup/password/SCRAM vectors, fragmented input,
retained BackendKeyData/ParameterStatus and unread bytes, invalid challenges,
incorrect server signatures and PostgreSQL-compatible Unicode SASLprep fallback.
Certificate tests distinguish Disable, Require, VerifyCA and VerifyFull.

## Live checks

The existing Docker matrix uses PostgreSQL **17.11**, PgBouncer **1.25.2** and
pg_doorman **3.10.6**. Direct PostgreSQL and both session/transaction modes of
each pooler run the ADO.NET and independent multiplexing harnesses. The direct
PostgreSQL harness also covers raw messages, converters, binary COPY,
prepared statements and multiple explicit pipeline groups. Native SCRAM was
checked with eight Unicode passwords against PostgreSQL; temporary test roles
are removed by the harness.

A separate local TLS server verifies complete SSLRequest/startup/CancelRequest
payloads, fragmented startup, a custom root CA, hostname mismatch, an untrusted
root, VerifyCA and Require. CancelRequest is tested over TLS as a separate
channel, not only over a mocked transport.

```powershell
tests/poolers/run.ps1 -KeepRunning
# Preserve the test database/containers after verification:
docker compose -f tests/poolers/compose.yaml stop
```

The pg_doorman harness retains its documented active self-termination EOF
behavior; its idle termination carries its own FATAL diagnostic. The checks do
not treat that pooler behavior as a PostgreSQL protocol guarantee.

**pg_doorman 3.10.6 session mode cannot forward a second CancelRequest on the
same client session.** This was reproduced twice in the final checks: the first
cancel reaches PostgreSQL, the second appears in the pooler log without a
forwarding entry, and no ReadyForQuery arrives before the recovery deadline.
The driver retires the session; the fixture asserts Broken and explicitly
closes/reopens before testing the next timeout. No query is retried in the
driver. Direct PostgreSQL, PgBouncer and pg_doorman transaction mode check all
three cancellation methods and subsequent execution on the same connection.

The installed pooler's source explains the result: the cancel-mode Client uses
the target client's ID/secret, and its unconditional Drop removes that same
cancel-map entry. A session-mode backend is not reclaimed between queries to
reinsert it. See [Client Drop in v3.10.6](https://github.com/ozontech/pg_doorman/blob/v3.10.6/src/client/core.rs#L681)
and [cancel-client construction](https://github.com/ozontech/pg_doorman/blob/v3.10.6/src/client/startup.rs#L462).
The unmodified failing runs and pooler/server logs are retained as
`pooler-matrix-repeated-cancel-failure.log`, `doorman-session-repeat.log` and
`doorman-cancellation-failure.log`. A passing matrix with the explicit fixture
limitation is not evidence that repeated cancellation works in that pooler mode.

## Package boundary

`Mpgsql.0.1.0-alpha.1.nupkg` contains the README, `lib/net10.0/Mpgsql.dll` and XML
documentation, plus NuGet metadata. Its net10.0 dependency group is empty. It
contains no Client, Sessions or Multiplexing assemblies. A separate consumer
restored only this package from the local package source into an isolated
package directory and passed bigint endian/roundtrip, full Sync bytes, COPY
assembly identity and lower-assembly reference checks. It has no project
references.

Logs and package contents are under `artifacts/`: `final-build.log`,
`unit-tests.log`, `verification-harness.log`, `live-full.log`,
`pooler-matrix.log`, `native-tls.log`, `unicode-auth.log`,
`package-consumer.log`, `package-contents.txt` and `package-nuspec.xml`.
Performance methodology and confidence bounds are reported separately in
`ado-refactor-performance.md`; successful functional checks do not establish
the performance bound.
