# Mpgsql benchmarks

The executable targets .NET 10 and BenchmarkDotNet 0.15.8. Converter and loopback
TCP comparisons use native Npgsql 10.0.3. The in-memory Pipe and loopback TCP query
fixtures require no PostgreSQL installation; the TCP fixture implements fixed
wire transcripts without executing SQL. Live binary COPY comparisons require a
PostgreSQL server. Live ADO.NET query comparisons are not yet implemented.

## Build and verify

From the repository root:

```powershell
dotnet build src/Mpgsql.slnx -c Release
dotnet test tests/Mpgsql.Tests/Mpgsql.Tests.csproj -c Release --no-build
dotnet benchmarks/Mpgsql.Benchmarks/bin/Release/net10.0/Mpgsql.Benchmarks.dll --verify-query
dotnet benchmarks/Mpgsql.Benchmarks/bin/Release/net10.0/Mpgsql.Benchmarks.dll --verify
```

`--verify-query` checks the new transcripts, complete query bytes against the
public frontend encoders, an independent complete bigint backend wire vector,
NULL/empty parameter payloads, capacity failure, all reader cases, split headers
and payloads, batch QueryIndex, error/Sync recovery including a skipped query,
peer transport failures, controlled row-budget backpressure and every worker
profile. Unexpected errors produce a nonzero exit code. The full `--verify`
also runs the existing converter/protocol/COPY verification.

## BenchmarkDotNet

The five new benchmark classes contain 58 cases in total:

| Class                           | Reported operation                                | Cases |
|---------------------------------|---------------------------------------------------|------:|
| QueryPacketBenchmarks           | GetByteCount, Write or GetByteCount + Write       |    24 |
| QueryPipelineBenchmarks         | One full raw Session or DataSource Reader request |    20 |
| QueryBatchBenchmarks            | A whole sixteen-query group with one Sync         |     1 |
| DataSourceConsumptionBenchmarks | Scalar, NonQuery or early reader Dispose          |     7 |
| DataSourceConcurrencyBenchmarks | One request, normalized from a 256-request wave   |     6 |

The default job launches once, performs three warmup iterations and eight measured
iterations. Sequential cases target 150 ms/iteration. Concurrency cases use one
256-request wave per iteration and unroll factor one. These fixed waves can have
substantial sampling noise; compare raw measurements, not only the mean.
For these waves, BDN's Mean is wave wall time divided by 256, not the mean
latency of individual requests; use `--query-load` for request latency.
The job leaves the machine's power plan unchanged.

Full baseline:

```powershell
dotnet benchmarks/Mpgsql.Benchmarks/bin/Release/net10.0/Mpgsql.Benchmarks.dll `
  --filter '*QueryPacketBenchmarks*' '*QueryPipelineBenchmarks*' '*QueryBatchBenchmarks*' `
           '*DataSourceConsumptionBenchmarks*' '*DataSourceConcurrencyBenchmarks*' `
  --stopOnFirstError --artifacts artifacts/query-path/bdn
```

Smoke run, which verifies execution and is **not a performance baseline**:

```powershell
dotnet benchmarks/Mpgsql.Benchmarks/bin/Release/net10.0/Mpgsql.Benchmarks.dll `
  --filter '*QueryPacketBenchmarks*' '*QueryPipelineBenchmarks*' '*QueryBatchBenchmarks*' `
           '*DataSourceConsumptionBenchmarks*' '*DataSourceConcurrencyBenchmarks*' `
  --launchCount 1 --warmupCount 0 --iterationCount 1 --invocationCount 1 --unrollFactor 1 `
  --stopOnFirstError --artifacts artifacts/query-path/bdn-smoke
```

CSV, GitHub Markdown and complete JSON reports are written under `results/`.
The executable returns a nonzero exit code if BDN reports a failed build/run.

The sequential Reader comparison uses identical inputs, replies, field
consumption and one Sync per request. It includes request/group creation,
encoding, publication, flush, peer work, response processing, row ownership,
typed bigint reads, borrowed bytea inspection, terminal completion and disposal.
Raw groups explicitly queue Sync before waiting for send completion. Shared-Sync
batch results are separate and must not be compared to independent DataSource
requests as equivalent transaction boundaries.

`ForcedReplyChunk=4096` means artificial chunk delivery: the peer waits for each
chunk to be consumed by the receiver. It exercises partial backend frames and
adds scheduling overhead. It is not a model of TCP packet fragmentation.

All physical sessions, input parameters and encoded replies are created before
measurement. Each pool is fully created by holding all exclusive leases at once.
Benchmark inputs are borrowed immutable memory reused by sequential workers.
Cold connection establishment, authentication, SQL execution/planning and
caller parameter construction are excluded.

The packet `Write` method calls the existing `QueryPacket.Write`, including its
internal size calculation and capacity check. `MeasureAndWrite` adds an explicit
GetByteCount call before that Write. These measure the current implementation;
there is no separate encoding-only entry point and no estimated cost subtraction.

## Per-request load runner

```powershell
dotnet benchmarks/Mpgsql.Benchmarks/bin/Release/net10.0/Mpgsql.Benchmarks.dll --query-load
```

Defaults are 256 warmup requests, five samples of 4096 requests for each profile,
with artifacts in `artifacts/query-path`. Overrides:

```powershell
dotnet benchmarks/Mpgsql.Benchmarks/bin/Release/net10.0/Mpgsql.Benchmarks.dll `
  --query-load --requests 256 --samples 1 --artifacts artifacts/query-path/load-smoke
```

The four ordinary profiles are `(callers, prewarmed sessions, in-flight/session)`:
`(1,1,1)`, `(8,1,8)`, `(64,1,8)`, `(64,4,8)`. Two mixed profiles use 64 callers,
one or four sessions, in-flight eight and row-payload budget 65536 bytes/session.
Every eighth mixed worker reads 128 rows with an 8192-byte bytea field and delays
1 ms after every eight rows. Remaining workers read one bigint row. Each worker
runs requests sequentially; the runner uses a shared start gate and does not
create a Task.Run per request. When fewer requests than callers are requested,
some workers remain idle; use at least 64 requests to exercise all profiles.

Timing starts immediately before ExecuteReaderAsync and ends after all results,
ReadyForQuery and Dispose. First-row timing stops after the first successful
ReadAsync. The runner stores ticks for every request and uses nearest-rank
percentiles (`ceil(p*N)-1`). Each profile's aggregate percentiles are calculated
from all requests, not from averages of sample percentiles. Aggregate throughput
is total requests divided by total sample time. Short/slow request p99 values
are also reported separately for mixed profiles.

Artifacts:

- `load-results.json`: run completion marker, commit, source and assembly SHA-256,
  runtime/OS/architecture, GC/JIT settings, fixture reply storage, complete profile
  settings, per-sample counters and every worker's request/first-row ticks.
- `load-results.csv`: per-sample and aggregate throughput, latency, allocations,
  GC counts, client flush/byte counters, copied row bytes and observed row budget.
- `load-report.md`: readable aggregate table and interpretation limits.

Completed profiles are saved incrementally. A later failure leaves a JSON file
with `Completed=false` and returns a nonzero exit code. A timeout disposes the
source to stop pending requests; the deadline is five minutes per sample.

## Interpretation

Allocation counters are **process-wide** (`GC.GetTotalAllocatedBytes(true)`):
driver, in-process peer and timed runner work are included. BDN's MemoryDiagnoser
also uses the process-wide counter on .NET 10. Setup buffers, warmup and
preallocated timing arrays are outside the load measurement. No estimated peer
cost is subtracted. These are comparable baselines for this harness, not an
isolated allocation profile of the driver.
See
the [BDN 0.15.8 allocation counter implementation](https://raw.githubusercontent.com/dotnet/BenchmarkDotNet/v0.15.8/src/BenchmarkDotNet/Engines/GcStats.cs).

The maximum observed BufferedRowBytes is a consumer-sampled **per-session row
payload reservation**, not exact peak memory, backing-buffer capacity, working
set or the sum of all sessions. CopiedRowBytes is the library's existing counter;
it does not count every copy performed by the peer or Pipe infrastructure.
Client FlushAsync counts are not socket writes or network round trips.

Load measurements are closed-loop and include admission waits. They have no
fixed arrival rate and no coordinated-omission correction. The slow-reader
scenarios include Task.Delay timer granularity and OS/ThreadPool scheduling;
a requested 1 ms delay can be much longer on Windows. BDN averages and a load
runner's individual-request p99 answer different questions.

The peer implements fixed transcripts, not PostgreSQL: it validates frontend
framing/OIDs/formats and responds to known SQL but does not execute SQL. Large
preencoded replies and Pipe flow control are part of the synthetic workload.
Keep CPU load, power policy and GC/JIT settings stable between comparisons.
The initial baseline has no pass/fail speed threshold.

## Offline or restricted build environment

The repository's `.packages` directory can serve as the local NuGet source:

```powershell
dotnet restore src/Mpgsql.slnx --source C:/Projects/mpgsql/.packages `
  --packages C:/Projects/mpgsql/.packages -p:NuGetAudit=false
$env:RestoreSources = 'C:/Projects/mpgsql/.packages'
$env:RestorePackagesPath = 'C:/Projects/mpgsql/.packages'
$env:NuGetAudit = 'false'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:UseSharedCompilation = 'false'
$env:MSBuildNodeReuse = 'false'
```

Apply these only to the shell used for the run. They also reach BDN's generated
project restore/build; `--packages` alone does not select an offline source.
Reports may show an unknown processor if Windows CIM access is restricted;
the load metadata preserves PROCESSOR_IDENTIFIER when available.

## External timeout

Use the PowerShell 7 wrapper to bound the entire process, including setup,
verification, BDN builds/children and cleanup. It passes arguments without shell
interpolation, saves stdout/stderr plus execution metadata, propagates a failed
run's exit code and returns 124 after stopping its own process tree on timeout:

```powershell
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 120 `
  -BenchmarkArguments @('--verify-query')
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 1800 `
  -BenchmarkArguments @('--query-load', '--artifacts', 'artifacts/query-path')
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 1800 `
  -BenchmarkArguments @('--filter', '*QueryPacketBenchmarks*', '*QueryPipelineBenchmarks*',
    '*QueryBatchBenchmarks*', '*DataSourceConsumptionBenchmarks*',
    '*DataSourceConcurrencyBenchmarks*', '--stopOnFirstError',
    '--artifacts', 'artifacts/query-path/bdn')
```

The wrapper does not build or restore packages. An invocation overwrites only its
log files; pass `-LogDirectory` to retain separate invocations. Offline build
environment variables above also apply to child processes started by the wrapper.

## Native Npgsql comparison on loopback TCP

Npgsql 10.0.3 is compared through its public native API, using the existing package
reference. The four `Tcp*ComparisonBenchmarks` classes add 66 cases: five Reader
shapes across Mpgsql raw Session/DataSource, native Npgsql persistent connection,
Npgsql DataSource pool and multiplexed pool; seven consumption shapes across the
three DataSource modes; six worker profiles across those modes; and two sixteen
query batches with one Sync. Batch results are per whole group, separately from
independent requests. Npgsql has result ordinals; only Mpgsql has QueryIndex.

`TcpFacadeBatchComparisonBenchmarks` adds two cases for the public explicit
connection Batch path. Both sides use fresh batch/command groups, constructed in
IterationSetup outside the timed method. One invocation consumes and disposes 32
sequential groups, each containing 16 queries and one Sync; OperationsPerInvoke
normalizes the result to one whole group. Both public APIs now permit reuse, but
this series deliberately creates and disposes fresh groups. It remains distinct from raw Session versus a reused native
NpgsqlBatch in `TcpBatchComparisonBenchmarks`. Verification checks ordinals,
Mpgsql QueryIndex, values, all rows, Sync counts, the complete wave checksum and
disposal. Allocation figures retain BDN's process-wide scope; they are not a
library-only counter.

Both drivers use the same loopback TCP peer, SQL, bigint/bytea parameters, binary
backend reply templates and field consumption. Startup advertises synthetic
PostgreSQL 17.0 and AuthenticationOk; it does not run PostgreSQL. Physical sessions
are warmed before measurements. Setup-only BEGIN/ROLLBACK commands pin every
Npgsql multiplexed connector while warming the whole pool. The peer reads and
writes in separate tasks and bounds its reply queue. No library changes or
Npgsql compatibility layer are involved.

```powershell
dotnet build src/Mpgsql.slnx -c Release
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 240 `
  -BenchmarkArguments @('--verify-query-compare') `
  -LogDirectory artifacts/query-compare/validation/verify-compare
# Smoke: all cases, one measured iteration, no warmup/calibration.
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 1800 `
  -BenchmarkArguments @('--filter', '*Tcp*ComparisonBenchmarks*', '--launchCount', '1',
    '--warmupCount', '0', '--iterationCount', '1', '--invocationCount', '1',
    '--unrollFactor', '1', '--stopOnFirstError', '--artifacts', 'artifacts/query-compare/smoke') `
  -LogDirectory artifacts/query-compare/validation/smoke
# Baseline: one launch, three warmup iterations, eight measured iterations.
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 3600 `
  -BenchmarkArguments @('--filter', '*Tcp*ComparisonBenchmarks*', '--stopOnFirstError',
    '--artifacts', 'artifacts/query-compare/bdn') `
  -LogDirectory artifacts/query-compare/validation/bdn
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 3600 `
  -BenchmarkArguments @('--query-compare-load', '--artifacts', 'artifacts/query-compare') `
  -LogDirectory artifacts/query-compare/validation/load
```

`--verify-query-compare` verifies deterministic segmented frontend headers/payloads,
complete response bytes, parameter lengths/OIDs, all fields/row counts and full
bytea payloads, NULL/empty results, cached-command reuse, SQL error recovery,
early drain, all batch ordinals/QueryIndex, one-Sync boundaries, peer failure and
all worker profiles. It is also part of `--verify`; `--verify-query` retains the
original Pipe-only scope. TCP delivery can coalesce writes; the measured TCP
cases use ordinary delivery. The original forced-consumption Pipe fragmentation
series stays separate.

`--query-compare-load` defaults to 256 warmups and five samples of 4096 requests
per driver/profile (18 series). It accepts `--requests`, `--samples`, `--artifacts`
and writes `comparison-load.json` (raw per-request ticks, metadata, driver
settings), `comparison-load.csv` (sample/aggregate metrics) and
`comparison-load.md`. Completed series are saved with `Completed=false` until
the whole run succeeds. A sample has a five-minute deadline; the external wrapper
also bounds setup and teardown. Use at least 64 requests to exercise every worker.

Npgsql settings: Pooling=true, MinPoolSize=MaxPoolSize=the connection count,
Multiplexing=false/true in separate series, NoResetOnClose=true, MaxAutoPrepare=0,
Enlist=false, SSL/GSS disabled, type loading disabled, command timeout disabled.
These explicitly selected settings are not an Npgsql default-configuration
comparison. Npgsql commands and typed parameters are reused per sequential
worker; caller command creation is excluded. Mpgsql's independent request path
still creates its normal internal one-shot batches. For Scalar, Npgsql uses its
native object-returning API (including bigint boxing), Mpgsql its typed API.
See [Npgsql connection settings](https://www.npgsql.org/doc/connection-string-parameters.html)
and
the [native DataSource command lifetime](https://raw.githubusercontent.com/npgsql/npgsql/v10.0.3/src/Npgsql/NpgsqlDataSourceCommand.cs).

Both drivers copy the **entire** bytea field into a reusable caller destination (Mpgsql GetRawValue/CopyTo, Npgsql
GetBytes). The original Pipe benchmark inspected
borrowed bytea endpoints; do not compare its timings directly with TCP timings.
Native Npgsql public APIs do not expose client FlushAsync, CopiedRowBytes or
BufferedRowBytes, so its fields are null/blank. No reflection or estimated zeros
are used. Mpgsql's in-flight cap and row budget have no direct Npgsql counterpart;
the same callers/physical connection counts do not guarantee identical scheduling.

BDN and the runner count whole-process allocations, including socket/Pipe adapter,
peer and runner. No estimated fixture cost is subtracted. These results measure
this synthetic TCP workload and command lifecycle, not real PostgreSQL performance.
Treat small gaps within timing uncertainty cautiously. Slow-reader delay and
ThreadPool/OS scheduling can dominate. All load latency is closed-loop; no fixed
arrival-rate claims or speed threshold are imposed.

## ADO.NET reader comparisons

`TcpAdoReaderBenchmarks` compares the current Mpgsql ADO.NET provider with native
Npgsql over the same loopback TCP transcript fixture. It contains 30 cases:
Empty, OneBigint, Rows128Columns8, Rows4096 and Bytea64KiB, each with three pairs.
The Npgsql method is the BenchmarkDotNet baseline within each category:

| Category | Mpgsql method | Npgsql method | Caller work in timing |
|---|---|---|---|
| ReusedTyped | ReusedTyped | NpgsqlReusedTyped | Reused command and typed parameters; complete typed reader consumption and disposal |
| FreshTyped | FreshTyped | NpgsqlFreshTyped | Command and typed parameter construction, execution, complete consumption and disposal |
| ReusedObject | ReusedObject | NpgsqlReusedObject | Reused command through DbCommand/DbDataReader; GetValue boxing and GetBytes |

Both sides hold an open exclusive connection, use `$1`/`$2`, explicit bigint/bytea
types and generic typed parameters, disable command timeouts, and consume every
result and ReadyForQuery before completing the operation. All input values and
reply payloads are identical. Connection establishment, authentication and SQL
execution are outside measurement. No pool lease or multiplexing is timed here.
Setup checks all three consumption methods against the independent expected
checksum before measurement.

Typed bigint reads use `GetFieldValue<long>` on both providers. Typed bytea reads
use Mpgsql's borrowed `GetRawValue`/`CopyTo` and Npgsql's `GetBytes`; both copy the
entire payload once into an equally sized reusable caller destination. The
standard object category uses one shared DbDataReader consumer and `GetBytes`
on both sides. Borrowed bytea inspection alone is not used in this comparison.
Allocations include the in-process peer and transport. Prepared statements need
a separate fixture: the existing peer deliberately rejects named statements.

Run from `benchmarks/Mpgsql.Benchmarks` after a Release build:

```powershell
dotnet bin/Release/net10.0/Mpgsql.Benchmarks.dll --filter '*TcpAdoReaderBenchmarks*' `
  --join --exporters fulljson csv markdown --artifacts ../../artifacts/ado-npgsql-comparison
```

Ratios within each category now have the same orientation: Mpgsql/Npgsql latency,
so lower values favor Mpgsql. Compare fresh and reused command results separately
and retain repeat runs and confidence intervals when evaluating a speed target.

## Admission window experiments

`BackendFrameBenchmarks` isolates backend framing for three prebuilt transcripts:
control messages, one bigint row, and 128 rows with eight bigint fields. Each
transcript is measured on an array, a single linked segment, linked 4096-byte
segments and with every five-byte header split after its second byte. The 4096-byte
layout measures framing of already available bytes, not forced transport delivery.
`PublicFrames` validates the full body; `SessionFrames`
uses the internal framing path that defers row indexing to the ownership layer.
Neither method includes transport, copied-row storage, budget waits or result
consumption. Both return a checksum independently verified in setup. Inputs are
reused, and allocation figures retain the same process-wide scope.

```powershell
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 900 -BenchmarkArguments @('--filter', '*BackendFrameBenchmarks*', '--launchCount', '1', '--warmupCount', '3', '--iterationCount', '8', '--artifacts', 'C:/Projects/mpgsql/artifacts/query-framing/bdn') -LogDirectory 'C:/Projects/mpgsql/artifacts/query-framing/validation/bdn'
```

Use `TcpReaderComparisonBenchmarks`, the Batch comparison classes and
`TcpAdmissionComparisonBenchmarks` to verify effects on complete operations.
These framing timings alone do not establish a query or throughput improvement.

`RowStorageBenchmarks` isolates copied-row rent, indexing, field access and
release. It covers 1, 8 and 16 fields with 8 or 8192 bytes per field. Payloads
are created before timing; setup checks checksums and warms storage. The timed
operation returns a checksum. This synchronous diagnostic excludes transport,
row-budget waiting and asynchronous producer/consumer contention; it cannot
establish a full-query or Npgsql speed advantage.

```powershell
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 900 -BenchmarkArguments @('--filter', '*RowStorageBenchmarks*', '--artifacts', 'C:/Projects/mpgsql/artifacts/query-rows/bdn') -LogDirectory 'C:/Projects/mpgsql/artifacts/query-rows/validation/bdn'
```

`RowBufferBudgetBenchmarks` is a separate internal notification diagnostic:
uncontended reservation versus a single blocked reservation/release, with and
without a cancellable token. It includes the async method/runner allocations;
it is not an end-to-end query benchmark or an Npgsql comparison. Setup and
cleanup validate successful reservations and zero retained bytes. The complete
verification modes exercise its setup outside timing.

`TcpPipelineWindowBenchmarks` holds 64 sequential workers, a one-row bigint
response, and 1 or 4 prewarmed physical connections constant while selecting
Mpgsql windows 8, 16, 32 and 64. Each operation still includes complete reader
consumption, ReadyForQuery and disposal. Native Npgsql pool and multiplexing
remain independent comparison series; `InFlight` is only an Mpgsql setting.
Choose the fastest native Npgsql series when assessing the speed criterion.
The original six baseline profiles and the driver's default window are unchanged.

The load runner accepts an optional comma-separated `--profiles` list, including
`C64_P1_W16`, `C64_P1_W32`, `C64_P1_W64`, their `P4` counterparts, and the
corresponding `Mixed_` profiles. Omitting the option runs the original six profiles.
For example, after a Release build:

```powershell
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 1800 -BenchmarkArguments @('--filter', '*TcpPipelineWindowBenchmarks*', '--artifacts', 'C:/Projects/mpgsql/artifacts/query-windows/bdn') -LogDirectory 'C:/Projects/mpgsql/artifacts/query-windows/validation/bdn'
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 1800 -BenchmarkArguments @('--query-compare-load', '--profiles', 'C64_P1_W8,C64_P1_W16,C64_P1_W32,C64_P1_W64,C64_P4_W8,C64_P4_W16,C64_P4_W32,C64_P4_W64', '--requests', '4096', '--samples', '5', '--artifacts', 'C:/Projects/mpgsql/artifacts/query-windows/load') -LogDirectory 'C:/Projects/mpgsql/artifacts/query-windows/validation/load'
```

The window bounds admitted requests and their protocol/lifecycle state until
the reader finishes, including abandoned-reader drain. The row budget bounds
retained payload bytes; it does not bound request state or server work queued
behind a slow query. Larger windows can improve coalescing and throughput but
can increase queueing latency. The mixed profiles keep the 64 KiB row budget and
the original slow-reader behavior. These are admission-policy experiments,
reported separately from the fixed-window baseline. `--verify` and
`--verify-query-compare` verify all fast window cases outside timed measurements.

## Longer admission control

`TcpAdmissionComparisonBenchmarks` repeats the same 256-request fixed-worker
workload as `TcpConcurrencyComparisonBenchmarks` 64 times per iteration: 16,384
requests, with the same SQL, parameters, complete reader disposal and fixtures.
It covers the four ordinary profiles and all three drivers. Mixed/slow-reader
profiles remain in the original class; their iterations are already long.

```powershell
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 900 -BenchmarkArguments @('--filter', '*TcpAdmissionComparisonBenchmarks*', '--launchCount', '1', '--warmupCount', '3', '--iterationCount', '8', '--keepFiles', '--artifacts', 'C:/Projects/mpgsql/artifacts/query-admission/bdn') -LogDirectory 'C:/Projects/mpgsql/artifacts/query-admission/validation/bdn'
```

The invocation count is defined by the benchmark attribute. In BDN 0.15.8 that
attribute can override a CLI `--invocationCount`, so check the actual workload
operations in the full JSON instead of assuming the requested option applied.
Longer iterations reduce timing overhead but do not guarantee narrow confidence
intervals. Compare both driver versions with the same benchmark class and job.
These are closed-loop measurements; process-wide allocations include the peer
and runner. No in-flight limit or transaction boundary is changed by this class.

## Optional shared Sync experiments

`TcpSharedSyncBenchmarks` reports the independent Mpgsql baseline, Mpgsql shared
Sync N=8/X=1 ms, and native Npgsql Multiplexing on all original profiles. Shared
Sync changes transaction/error semantics; its result must not replace the original
like-for-like baseline or establish the original speed criterion. Full consumption,
ReadyForQuery and reader disposal are included. `TcpSharedSyncCohortBenchmarks`
provides a separate control with exactly one Sync for N=8/16 queries in both drivers:
separate Mpgsql DataSource readers versus a reused native Npgsql Batch reader.
SQL, parameters, transcripts and field consumption match. The cohort timeout is
1000 ms; all members are submitted together and verification requires exactly one
Sync per cohort. Caller/runner allocations remain included.

The TCP load runner accepts `--sync-group-size N` and `--sync-group-timeout-ms X`;
both default to 1. These settings affect Mpgsql only and are saved in JSON settings
and each Mpgsql result. Npgsql native pool/multiplexing references keep independent
boundaries. Wire counts validate one query per completed request and the legal
shared Sync count range; actual Sync counts reveal effective grouping. Timer
resolution, OS scheduling, slow readers and incomplete groups can increase latency.
This remains a closed-loop synthetic workload. The peer can reply before Sync;
real PostgreSQL may buffer small replies, so synthetic first-row latency is not a
promise about real-server latency.

```powershell
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 1800 -BenchmarkArguments @('--filter', '*TcpSharedSync*', '--artifacts', 'C:/Projects/mpgsql/artifacts/query-shared/bdn') -LogDirectory 'C:/Projects/mpgsql/artifacts/query-shared/validation/bdn'
./benchmarks/Mpgsql.Benchmarks/run-query-benchmarks.ps1 -TimeoutSeconds 3600 -BenchmarkArguments @('--query-compare-load', '--sync-group-size', '8', '--sync-group-timeout-ms', '1', '--requests', '4096', '--samples', '5', '--artifacts', 'C:/Projects/mpgsql/artifacts/query-shared/load') -LogDirectory 'C:/Projects/mpgsql/artifacts/query-shared/validation/load'
```

`--verify` and `--verify-query-compare` include all shared profiles and the Npgsql
cohort controls outside timing. Unit tests exercise count/time closure, exact wire
bytes, segmented headers/payloads, cancellation before/after publication, budget
backpressure, early disposal, SQL error/skipped requests, following-query recovery,
shutdown and timer races. Live tests can run separately with
`MPGSQL_TEST_SHARED_SYNC_ONLY=1` using the documented integration-test credentials.

## Longer batch measurement iterations

`TcpLongBatchComparisonBenchmarks` repeats the original complete Raw Batch16
operation 4096 times per measurement iteration. `TcpLongFacadeBatchComparisonBenchmarks`
prepares 2048 fresh batches per iteration for each driver; construction remains
outside timing. Both report time and allocations per complete sixteen-query
batch, including consumption, ReadyForQuery and disposal. These classes use the
same operations and fixtures as the original batch comparisons. Their additional
checksum/boundary/idle checks are included in `--verify-query-compare` and `--verify`.

Use longer warmup when comparing batch changes:

```powershell
dotnet run --project benchmarks/Mpgsql.Benchmarks -c Release --no-build -- --filter '*TcpLong*BatchComparisonBenchmarks*' --launchCount 2 --warmupCount 32 --iterationCount 16 --artifacts artifacts/query-path/long-batch
```

Keep results for these long iterations separate from the original shorter jobs.
Their durations and observed stability are recorded in the exported measurements;
the workload remains closed loop.

## Batch diagnostics with dotTrace and dotMemory

`--query-batch-profile` repeats the same complete Batch16 operations as the two
TCP Batch benchmark classes. `--batch-path raw` keeps native Npgsql batch reuse;
`--batch-path facade` prepares fresh caller groups for both drivers. All groups
contain 16 bigint queries and one Sync and finish after full consumption and
reader/batch disposal. Select `--driver mpgsql|npgsql` and `--profile-kind
trace|memory`; `--cohorts` defaults to 1024 for CPU and 128 for memory, with 32
groups in a cohort and 32 warmup cohorts. Fixture and caller group preparation
are outside profiler collection windows. Checksums, query/Sync counts, idle
session health and a following cohort are checked outside those windows.

The runner loads `JetBrains.Profiler.Api.dll` only when explicitly requested
through `--profiler-api`; the benchmark project and driver have no profiler
package dependency. `trace` requires an active dotTrace session started with
`--use-api`, and uses StartCollectingData/StopCollectingData around each cohort,
then SaveData. `memory` requires an active dotMemory session with allocation
control, enables full allocation data only during execution, and takes baseline
and final heap snapshots outside execution. Snapshots force GC. Caller groups
are released before the final snapshot. Other sampled allocation data may still
be recorded outside the full-data windows; select the corresponding intervals
when analyzing traffic. Heap differences alone do not prove a leak.

`--verify-query-batch-profile` checks all four driver/path combinations without
profiling or timing; these checks are also part of `--verify-query-compare` and
`--verify`. `--profile-kind none` is available for diagnostics without a profiler,
but results from this runner are not a BDN baseline.

`run-batch-profiling.ps1 -PrepareOnly` prepares eight CLI commands without
launching workloads. It requires a built frozen source snapshot and the locally
downloaded JetBrains tools manifest in `artifacts/query-profile/tools`. Running
the script without `-PrepareOnly` executes that exact plan sequentially. Every
process has an external timeout; nonzero exit, missing completion metadata or a
missing snapshot stops the suite. CPU snapshots use Sampling/ThreadTime and
registry-free profiling; dotMemory uses API-controlled snapshots. No GUI or
administrative Timeline service is launched. Logs, commands, hashes, exit codes
and `runner.json` are saved alongside `.dtp`/`.dmw` files. Diagnostic durations
and process allocation counters include profiler overhead and must not be used
to claim a speed advantage.

Keep `JetBrains.HabitatDetector.dll` alongside the optional API assembly; this is
an upstream API dependency. `--verify-profiler-api --profiler-api <path>` actually
invokes both GetFeatures methods without starting a fixture or workload, so
missing lazy dependencies fail before profiling. A zero feature mask is
expected for that check when no profiler is attached.

Profiler
controls: [dotTrace API](https://www.jetbrains.com/help/profiler/Profiling_Guidelines__Advanced_Profiling_Using_dotTrace_API.html),
[dotMemory API](https://www.jetbrains.com/help/dotmemory/API_Reference.html).

## Concurrent DataSource diagnostics with dotTrace

`--query-pipeline-profile` calls the existing `TcpAdmissionComparisonBenchmarks`
workload: 256 independent requests per cohort, fixed sequential workers, and
full consumption and disposal. Select `--profile C1_P1_W1|C8_P1_W8|C64_P1_W8|C64_P4_W8`
and `--driver mpgsql|npgsql-pool|npgsql-multiplexed`. Mpgsql retains the profile's
per-connection in-flight limit; native Npgsql has no additional runner limit.
The default is 4096 cohorts after 32 warmup cohorts. Physical connections and
fixtures are prepared before profiling.

Start dotTrace with `--use-api` and pass `--profile-kind trace --profiler-api <path>`.
The runner collects continuously across measured cohorts, then stops collection
before checksum, query/Sync boundary, health and following-cohort checks. This
avoids profiler API calls between short cohorts. Elapsed time and allocations
include profiler overhead and are diagnostics; they are not a BDN baseline or
individual request latency. Choose a fresh `--artifacts` directory.

`--verify-query-pipeline-profile` verifies all twelve driver/profile combinations
without profiling, including each caller's returned value, aggregate checksums,
one Sync per request, healthy idle sessions, released row budgets and a following
cohort. These checks also run in `--verify-query-compare` and `--verify`.
`--profile-kind none` permits a diagnostic run without an attached profiler.
The program has a three-minute limit; CLI scripts should also bound the entire
profiler process externally.
