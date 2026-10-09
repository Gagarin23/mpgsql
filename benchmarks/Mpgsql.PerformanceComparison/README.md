# Comparing the refactor with its frozen baseline

This runner loads the original and candidate `Mpgsql.Benchmarks.dll` into separate
assembly contexts in one process. Each uses the existing TCP peer and complete
query/result transcripts. It alternates the measurement order of 40 pairs and
reports 95% Student t confidence intervals on paired log latency ratios. It does
not filter outliers. The TCP peer is synthetic; these are driver measurements,
not PostgreSQL query-planning or execution measurements.

On Windows the runner requests High priority for its own process to reduce
interference from background work. It records the actual priority, logical CPU
count and GC mode. It changes neither processor affinity nor the power plan.

Build both versions in Release and preserve their entire output directories.
The original refactor baseline is `d67e4e2d33e6155f8d97ea2457d3c8896690c648`.

```powershell
dotnet build benchmarks/Mpgsql.PerformanceComparison -c Release
dotnet benchmarks/Mpgsql.PerformanceComparison/bin/Release/net10.0/Mpgsql.PerformanceComparison.dll `
  artifacts/ado-refactor-baseline/bin/Mpgsql.Benchmarks.dll `
  benchmarks/Mpgsql.Benchmarks/bin/Release/net10.0/Mpgsql.Benchmarks.dll `
  artifacts/ado-refactor-performance/paired.json
```

Run the command twice without other builds, tests, database containers, or
benchmarks running. The five reader cases cover empty results, one bigint,
128 rows with eight columns, 4096 rows, and 64 KiB bytea. The exclusive typed
baseline uses the original explicit connection and typed reader, with its
one-shot command construction outside the timed interval. The candidate reuses
an ADO.NET command. Both bytea paths borrow the payload and copy it once into
the same reusable destination. The multiplexing comparison uses the original
independent-query path on both versions.

Batch16 excludes caller object construction on both versions and includes
validation, encoding, execution, reading, and completion. Each invocation runs
32 batches of 16 commands. Sample duration targets 120 ms, after five seconds
of warmup per case. Allocations include all managed threads and the TCP peer;
their scope differs from BenchmarkDotNet's thread-local MemoryDiagnoser.

Append `--concurrent` for 64 callers, one/four physical connections, an in-flight
window of eight, and both independent and shared Sync groups. That mode uses
the existing load runner, checks checksums and wire counters, and records mean
and p99 latency, throughput, allocations, and GC counts. Each of 40 pairs has
65536 requests per version after three warmup blocks.

The upper confidence bound must be at most 1.05 to establish the specified
latency bound. Concurrent throughput uses the inverse rate ratio, so its
criterion has the same orientation. Allocation and p99 intervals are reported
separately. A broad interval or a contradictory repeat is inconclusive.

`--bytea-diagnostic` compares only bytea across multiplexing, exclusive typed and
raw session paths. Existing copied-byte counters are read at sample-group
boundaries, without adding a payload walk or a per-row callback.

`TcpAdoReaderBenchmarks` in the existing BenchmarkDotNet project separately
compares reused typed commands, newly constructed typed commands, and the
standard object-based `DbCommand`/`DbDataReader` API. It includes command
construction in `FreshTyped` and uses standard `GetBytes` in `ReusedObject`.

## Native Npgsql comparisons

Append `--npgsql` to compare native Npgsql from the frozen baseline with Mpgsql
from the candidate: five reader shapes across reused typed, fresh typed and
reused object ADO.NET commands, plus multiplexing and Batch16. This mode uses
the same 40 alternating pairs and records all raw latency/allocation samples.
Acceptance for a 10% latency reduction is an upper ratio bound of `0.90`.

### ADO.NET parity acceptance

Append `--npgsql-ado` for the existing 16 ADO.NET pairs only: the five reader
shapes across reused typed, fresh typed and reused object commands, plus Batch16.
This mode leaves multiplexing and converter measurements out of the matrix.
Native methods always come from the immutable baseline directory. Candidate
Mpgsql uses its ADO session ownership path through the TCP session fixture with a
plain PipeWriter: transport byte/flush counters are disabled before setup.
The native fixture already uses Npgsql's own write buffer without that counter
wrapper. Legacy `--npgsql` keeps its 21
series, counters, 40 pairs, 120-ms blocks and 10% acceptance criterion.

The existing typed Mpgsql reader and batch methods use `ValueTask` extensions;
their Npgsql counterparts use standard `Task` methods. JSON labels that API
difference per series. Append `--npgsql-ado-standard` for a separate 16-pair
matrix using standard Task-based execution, `ReadAsync` and `NextResultAsync`
on both sides. The standard typed reader pairs use `GetFieldValue<long>` and `GetBytes`
to copy the entire bytea into reusable destinations. The object pair uses
`DbCommand`/`DbDataReader`, `GetValue` and `GetBytes` on both drivers. The standard
batch consumer uses `IsDBNull`/`GetInt64` on both drivers. These modes are explicit
alternatives; neither silently replaces the original typed extension results.
The existing typed extension bytea path uses borrowed `GetRawValue.CopyTo`, while
native Npgsql uses `GetBytes`; both copy the full payload exactly once into the
same reusable destination shape. JSON keeps that representation difference visible.

For either mode, add `=Rows4096` or comma-separated case IDs to select the
existing cases. Empty, duplicate and unknown IDs are rejected before loading
fixtures. Each reader shape contributes three series, and Batch16 contributes
one; `Complete` requires exactly that count. The full matrix has 16 series.

The criterion requires both the upper paired Mpgsql/Npgsql latency ratio
confidence bound and the arithmetic mean latency ratio to be at most `1.01`
for every selected series in **each independent repeat**. A wide interval crossing
`1.01`, or an arithmetic mean above the bound when the log interval passes, is
inconclusive. A completed run is not an acceptance
claim, and choosing the best repeat does not establish parity. The intervals
describe geometric paired ratios, not arithmetic mean ratio confidence intervals.
These are individual nominal 95% Student t intervals under the serial-pair
independence assumption, not simultaneous confidence bands. An all-series
upper-bound acceptance criterion does not need a separate multiplicity adjustment.

Default calibration uses five seconds of warmup, a speed pilot, then six
alternating pilot pairs targeting 500-ms blocks. Pilot variance selects a longer
block, capped at two seconds, before any final sample is collected. Final sample
count defaults to 40 pairs; an explicit larger maximum permits pilot-based count
selection. Duration scaling assumes variance decreases with block length and is
a heuristic. The final interval remains authoritative; the precision target is
not guaranteed, and the runner never extends final sampling until a pass appears.
No pilot sample enters the final confidence interval. JSON retains pilot and
final observations, alternating order, UTC/Stopwatch pair envelope timestamps,
chosen settings, Stopwatch frequency and all raw latency/allocation pairs.

Only these sampler and peer environment variables affect the synthetic ADO modes. They are validated before
fixture setup and captured in `AdoOptions`; other comparison modes ignore them.

| Setting | Default | Allowed values |
|---|---:|---|
| `MPGSQL_ADO_PAIRS` | 40 | Even, 40–400 |
| `MPGSQL_ADO_MAX_PAIRS` | Value of `MPGSQL_ADO_PAIRS` | Even, base–400 |
| `MPGSQL_ADO_WARMUP_SECONDS` | 5 | 1–30 |
| `MPGSQL_ADO_PILOT_PAIRS` | 6 | Even, 4–20 |
| `MPGSQL_ADO_BLOCK_MS` | 500 | 120–5000 |
| `MPGSQL_ADO_MAX_BLOCK_MS` | 2000 | Base block–10000 |
| `MPGSQL_ADO_MAX_INVOCATIONS` | 65536 | 4096–1000000 |
| `MPGSQL_ADO_HALF_WIDTH_PERCENT` | 0.25 | 0.05–2, invariant decimal format |
| `MPGSQL_ADO_ADAPTIVE` | true | `true` or `false` |
| `MPGSQL_ADO_COALESCE_REPLIES` | false | `true` or `false`; native synthetic reader/batch diagnostic only |

For fixed blocks, set `MPGSQL_ADO_ADAPTIVE=false`; the speed pilot still chooses
the same invocation count for both drivers. Sample duration is approximate and
can be limited by the invocation cap. The `AdoComparisonOptions` argument also
allows explicit settings for calls from runner code. Final pairs use `t(39)=2.023`
for every count at least 40, which is conservative for larger counts under the
stated independence assumption. GC/allocations include the whole process and
synthetic peer; no instrumentation cost is subtracted. Cached health and peer
counter delegates verify every block's query/Sync deltas and unchanged connector
count outside both timing and allocation intervals.

```powershell
dotnet benchmarks/Mpgsql.PerformanceComparison/bin/Release/net10.0/Mpgsql.PerformanceComparison.dll `
  artifacts/npgsql-10percent/control/bin/Mpgsql.Benchmarks.dll `
  artifacts/ado-parity/candidate/bin/Mpgsql.Benchmarks.dll `
  artifacts/ado-parity/ado-repeat1.json --npgsql-ado
```

Repeat to a separate output file with no concurrent builds/tests/benchmarks.
Use `--npgsql-ado-standard` in another independently repeated run for the standard
typed API. Both matrices use preopened exclusive connections; SQL execution,
startup/authentication/TLS and per-operation pool leasing are excluded. Complete
reader consumption and async disposal are timed. Fresh commands include caller
construction/disposal; reused commands exclude initial construction. Batch16
excludes caller construction on both sides and includes execution, reading and
disposal of 32 fresh batches, with one Sync per 16-command batch.

The separate execution suite below covers standard scalar, nonquery zero,
parameter-value changes and reused batches through native and Mpgsql ADO consumers.
Prepared cases require the TCP peer to support named Parse/Bind/Describe/Close
lifecycle and corresponding confirmations before their performance results are
meaningful. Wide affected-row counts and generic scalar diagnostics are separate
API-contract comparisons; existing multiplexed methods are not interchangeable.

### Mpgsql-only ADO snapshot comparison

Append `--mpgsql-ado` to compare two frozen Mpgsql snapshots using the same
16 ADO reader/batch methods on both sides. `--mpgsql-ado=Rows4096` or a
comma-separated selection uses the same empty/duplicate/unknown case guards
and exact `Complete` count as the native ADO modes. It excludes converters
and multiplexing, disables CountingPipeWriter for both snapshots before setup,
and retains the same `MPGSQL_ADO_*` options, pilots, alternating pairs,
checksum/query/Sync checks, allocation scope and binary/runtime provenance.
Both snapshot harnesses must expose the transport-instrumentation setting;
the older original C5 harness cannot silently substitute its instrumented path.

JSON uses `Mode=MpgsqlAdoBaseline`, `BaselineProvider=Mpgsql`,
`CandidateProvider=Mpgsql`, `BaselineMicroseconds`, `CandidateMicroseconds`
and `CandidateOverBaseline`; pair-order labels explicitly say `Mpgsql baseline`
or `Mpgsql candidate`. API/bytea metadata describes the identical methods on
both sides. Native wrapper fields and native acceptance summaries are null,
and rows have no 1% parity verdict or 5% regression criterion. This mode reports
ratios, confidence intervals and allocations as information about the combined
snapshot change. It does not establish Npgsql parity or isolate one optimization.
Legacy `--mpgsql` remains the existing 21-series self-comparison mode.

After building and freezing a runner that includes this flag, compare Direct5
and Direct6 without rebuilding either benchmark snapshot:

```powershell
dotnet benchmarks/Mpgsql.PerformanceComparison/bin/Release/net10.0/Mpgsql.PerformanceComparison.dll `
  artifacts/ado-parity/direct5/bin/Mpgsql.Benchmarks.dll `
  artifacts/ado-parity/direct6/bin/Mpgsql.Benchmarks.dll `
  artifacts/ado-parity/direct6/ado-self-from-direct5.json --mpgsql-ado
```

Use the newly frozen runner path in place of the build-output runner path when
collecting measurements. Older frozen Direct5/Direct6 runners predate this flag.
Keep independent repeats in separate output files. A later Direct6-to-Direct7
comparison changes only the two snapshot paths and output file after Direct7
has been built, verified and frozen.

### Optional coalesced synthetic reply diagnostic

Set `MPGSQL_ADO_COALESCE_REPLIES=true` with `--npgsql-ado[=cases]` or
`--npgsql-ado-standard[=cases]` for a separate synthetic diagnostic. The peer
buffers extended replies until frontend Sync or Flush. Startup and the
setup-only Simple Query response flush immediately; an empty Flush response
still publishes already buffered replies. The original default remains false,
with its original `ReadOnlyMemory<byte>` reply channel and wave-flush writer.
The coalesced writer is selected once at setup and uses a separate channel.

Both providers load their wrappers from the candidate snapshot for this mode,
and its Npgsql binary must match the immutable baseline reference SHA-256.
This requires a wrapper that implements the flag on both providers. Methods,
payloads, checksums, query/Sync counters and alternating sampler are unchanged.
JSON records the applied policy for both fixtures and an explicit diagnostic
Mode. No 1% acceptance fields are emitted: this run does not replace either
the original synthetic policy or a real PostgreSQL acceptance run. Self,
execution-only and live modes reject this setting. Unset it before canonical
measurements. Buffering through Sync here is a controlled ADO16 diagnostic,
not a general streaming-server simulation with bounded bytes for arbitrary
long unsealed pipelines.

```powershell
$env:MPGSQL_ADO_COALESCE_REPLIES = 'true'
dotnet benchmarks/Mpgsql.PerformanceComparison/bin/Release/net10.0/Mpgsql.PerformanceComparison.dll `
  artifacts/npgsql-10percent/candidate5/bin/Mpgsql.Benchmarks.dll `
  artifacts/ado-parity/candidate/bin/Mpgsql.Benchmarks.dll `
  artifacts/ado-parity/coalesced-diagnostic.json --npgsql-ado=Empty,Rows4096,Batch16
Remove-Item Env:MPGSQL_ADO_COALESCE_REPLIES
```

The optional `--verify-ado-coalesced` flag on `Mpgsql.Benchmarks.dll` checks
complete independent P/B/D/E and backend payloads over TCP, immediate startup,
no reply publication before a boundary, Sync-only delivery without a preceding
Flush, empty Flush publication followed by Sync, byte/query/Sync/connector
counts, and all five reader shapes plus Batch16 through both providers. Full
outgoing byte equality is checked for the lower encoder against an independent
transcript; provider smoke checks retain strict peer framing and result/counter
validation instead of claiming a capture of both ADO frontend streams.

### Real PostgreSQL ADO16

Append `--npgsql-ado-live[=cases]` for the same 16 pairs against an actual
PostgreSQL endpoint. `--npgsql-ado-live-standard[=cases]` selects the separate
standard Task and full `GetBytes` matrix. Both use `PostgresAdoBenchmarks`, the
production Mpgsql DataSource/transport and native Npgsql DataSource. The five
SQL shapes come directly from `QueryScenario.Readers`; Batch16 executes the
same 16 parameterized SELECT commands, with 32 fresh batches per invocation.
No synthetic TCP peer is created. The sampler, fixed-before-final calibration,
1% log-bound plus arithmetic-mean criterion and independent-repeat requirement
are shared with the synthetic ADO modes.

Set all five `MPGSQL_TEST_HOST`, `MPGSQL_TEST_PORT`, `MPGSQL_TEST_USER`,
`MPGSQL_TEST_PASSWORD` and `MPGSQL_TEST_DATABASE` settings explicitly. Password
may be empty for trust authentication. The local fixture disables TLS and GSS;
recorded endpoint metadata contains only host, port, username, database and
TLS mode. Passwords and complete connection strings are never recorded.

Both wrappers come from the candidate snapshot, with its Npgsql SHA-256
verified against the frozen baseline reference. Startup/authentication/TLS and
the initial exclusive connection lease are outside timing. Real SQL execution,
full reader consumption and async disposal are included. Batch caller
construction is outside the allocated/timed interval on both providers. Managed
allocations cover the client process only; PostgreSQL/proxy server processes
are excluded. Before and after every block the runner checks the expected
checksum, healthy idle state and unchanged physical connection/PID without
issuing extra SQL. Query/Sync counts are intended operation counts, not captured
wire counters. Setup additionally verifies the actual `pg_backend_pid()`;
the optional SQL identity probe stays outside measurement.

```powershell
$env:MPGSQL_TEST_HOST = '127.0.0.1'
$env:MPGSQL_TEST_PORT = '6430'
$env:MPGSQL_TEST_USER = 'mpgsql'
$env:MPGSQL_TEST_DATABASE = 'mpgsql'
# Set MPGSQL_TEST_PASSWORD in the environment; it is never part of the JSON.
dotnet benchmarks/Mpgsql.PerformanceComparison/bin/Release/net10.0/Mpgsql.PerformanceComparison.dll `
  artifacts/npgsql-10percent/candidate5/bin/Mpgsql.Benchmarks.dll `
  artifacts/ado-parity/candidate/bin/Mpgsql.Benchmarks.dll `
  artifacts/ado-parity/live-repeat1.json --npgsql-ado-live
```

Run an independent repeat into a separate output file. This is a direct
PostgreSQL fixture; transaction-pooler cancellation/ownership compatibility
requires its separate integration matrix. See
[`Live/README.PostgresAdo.md`](../Mpgsql.Benchmarks/Live/README.PostgresAdo.md)
for the fixture contract.

### Additional matched ADO execution cases

Append `--npgsql-ado-execution` for seven additional standard Task-based pairs.
These leave the existing reader/batch 16-series matrices unchanged. Both wrappers
are loaded from the candidate benchmark assembly in separate assembly contexts;
the candidate `Npgsql.dll` SHA-256 must equal the immutable baseline reference
before fixture setup. JSON records the actual native wrapper path/hash separately
from the reference benchmark path and preserves the normal runtime provenance.

| Case ID | SQL and timed work | Operations / queries / Sync per invocation | Checksum |
|---|---|---:|---:|
| `ParameterMutation32` | `select $1::bigint`; set cached typed parameter to 1–32; Task-reader, `GetFieldValue<long>`, full completion and reader disposal each time | 32 / 32 / 32 | 528 |
| `ScalarEmpty` | `select $1::bigint where false`; standard `DbCommand.ExecuteScalarAsync` | 1 / 1 / 1 | -2 (null) |
| `ScalarOneBigint` | `select $1::bigint`; standard scalar | 1 / 1 / 1 | 1 |
| `ScalarNull` | `select NULL::bigint where $1::bigint>0`; standard scalar | 1 / 1 / 1 | -1 (`DBNull.Value`) |
| `ScalarRows128` | `select $1::bigint+i-1 from generate_series(1,128) i`; standard scalar, first value returned and remaining protocol drained by the provider | 1 / 1 / 1 | 1 |
| `ReusedBatch16` | Reuse one 16-command batch 32 times; `select $1::bigint` with values 1–16; Task-reader and identical typed getters | 32 / 512 / 32 | 4352 |
| `NonQueryZero` | `update benchmark_stub set value=$1::bigint where false`; standard `ExecuteNonQueryAsync` returning `int` | 1 / 1 / 1 | 0 |

Mpgsql parameters use explicit OID 20 and Npgsql parameters use explicit
`NpgsqlDbType.Bigint`; neither relies on type inference or SQL rewriting. Every
case uses one preopened exclusive connection, command timeout zero, independent
Sync boundaries and uninstrumented transport. Command and batch construction and
their final disposal occur outside timing. Parameter mutation, standard scalar/
nonquery calls, reading and per-execution reader disposal are included. ReusedBatch16
creates one batch outside timing and does not dispose it between executions;
this lifecycle differs explicitly from the existing fresh-batch benchmark.

The paired runner normalizes latency, throughput and allocations by the listed
logical operations, verifies explicit expected checksums as well as cross-driver
equality, and checks queries/Sync/health at every block boundary outside timing.
It reports operations, queries and Sync per invocation, requested and actual mean
block durations. BenchmarkDotNet reports one entire method invocation; use the
counts above when interpreting its output. Setup/reflection/checks/serialization
are excluded; allocations still cover the whole managed process and TCP peer.

This mode reuses `AdoComparisonOptions`, warmup/calibration, 40-or-more alternating
final pairs and the combined log-CI/arithmetic-mean `1.01` criterion. It does not
copy a second sampling algorithm. Use `=ParameterMutation32,ReusedBatch16` or any
nonempty subset of the seven IDs for focused runs. Unknown and duplicate IDs are
rejected; `Complete` requires exactly one series per selected case. Repeat final
runs independently rather than selecting the best outcome.

Before measurement, run the optional verification mode:

```powershell
dotnet benchmarks/Mpgsql.Benchmarks/bin/Release/net10.0/Mpgsql.Benchmarks.dll --verify-ado-execution
```

It independently constructs expected complete Parse/Bind/Describe/Execute/Sync
and backend payloads, exercises fragmented frames, then verifies all seven cases
twice through each provider with exact checksums and query/Sync/connector counts.
Complete byte equality is checked for the lower `QueryPacket` encoder against
independent expected transcripts. The provider smoke checks separately verify
results, counters, health and strict peer framing/OID/format/length checks;
they do not capture each provider's complete outgoing byte stream.
The verifier uses synthetic TCP and requires no database. Prepared commands,
generic scalar diagnostics and wide affected-row counts remain a separate next
phase; the current peer supports unnamed statements and only UPDATE 0 without
returned rows.

Append `--mpgsql` to compare the same 21 Mpgsql methods in the original and
candidate snapshots. This mode uses the current ADO.NET API on both sides,
without the legacy connection adapter. JSON labels both providers as Mpgsql,
uses baseline/candidate fields, and reports the upper paired confidence bound
for a 5% regression separately. Default read-buffer changes are recorded per
fixture; the self-comparison measures their effect together with source changes.
It records `BaselineProvider=Mpgsql` and `CandidateOverBaseline` intervals, so
optimization and ablation results remain distinct from the native Npgsql target.

Append `--mpgsql=Rows4096` or `--npgsql=Rows4096` for only that TCP shape,
or provide comma-separated case IDs after `=`. Allowed IDs are `Empty`,
`OneBigint`, `Rows128Columns8`, `Rows4096`, `Bytea64KiB` and `Batch16`;
matching is case-sensitive. Empty, duplicate and unknown IDs are rejected before
loading fixtures or measuring. Each reader shape keeps its three ADO.NET paths
and one multiplexing path; Batch16 adds one fresh-batch series. Methods, timers,
warmup, invocation selection and 40 alternating pairs stay unchanged. JSON
records `SelectedCases`, `ExpectedSeries` and `CompletedSeries`; `Complete`
requires exactly four series per selected reader shape and one per Batch16.
The flags without `=` retain all 21 series. Selected cases execute in the normal
catalog order, regardless of the order in the flag.

The TCP, concurrency and read-buffer modes capture CPU identification, JIT/GC
environment settings, driver binary hashes and Npgsql informational package
versions before warmup. They retain both benchmark and runner runtime configuration
files; the executing process uses the runner configuration.

Append `--npgsql-buffers` for four matched read-buffer settings (4096, 8192,
32768 and 65536 bytes) across OneBigint, Rows128Columns8, Rows4096 and Bytea64KiB
on the reused typed ADO.NET path. Both methods are loaded from the candidate
snapshot, and its Npgsql binary must match the baseline reference hash. An explicit
override changes only the read buffer; minimum read size stays 1024 and write
buffers retain their defaults. Configured sizes, checksum and raw pairs are saved.
Npgsql can allocate an oversized buffer for the large bytea row.

`ReadBufferSize=0` uses the default compiled into that benchmark snapshot. Original
Mpgsql snapshots use 4096 bytes; the measured candidate default is 32768 bytes,
shared by production transport and its benchmark fixture. Npgsql remains at 8192.
The sweep reports the default from the loaded Sessions assembly, falling back to
4096 only for frozen versions predating the explicit constant. A self-comparison
of snapshots with different defaults measures their combined final changes; a
causal chunk-removal ablation needs the same explicit buffer setting on both sides.

Append `--npgsql-concurrent` for five profiles against both native pool and native
multiplexed pool: C8/P1/W8, C64/P1/W8, C64/P4/W8, C64/P1/W64 and C64/P4/W64.
The ten series retain the W8 results as backpressure-policy comparisons. W64
covers the full caller population of 64, so Mpgsql's configured window cannot
reduce the effective upper bound below the native multiplexed pool's 64 submitted
requests. Native distribution across physical connectors remains its own policy;
the run does not impose an identical per-connection scheduling algorithm.
The native exclusive pool still limits physical active commands to its connection
count. Both drivers use independent Sync boundaries.
Workers consume full results through cached `ReadAsync` delegates with identical
external request timers. The Mpgsql-only row-budget observation in the older load
runner is excluded. The runner verifies per-worker checksums and exactly one
query and Sync per request, and retains request ticks, GC and allocation data.
Each JSON series records caller and connection counts, Mpgsql's per-connection
window, effective physical outstanding limit, row budget, and native scheduling
policy. Both snapshots must expose identical profile metadata. Every block has
8192 requests; three warmup blocks precede the 40 alternating measured pairs.
Mean latency, block p99 and inverse throughput have separate paired intervals;
the p99 interval describes variation between measured block tails.

Npgsql has no equivalent public in-flight or row-budget limit. Keep these policy
differences visible, compare with the fastest native mode, and run independent
repeats. A shared-Sync experiment is a separate transaction/error policy and
cannot replace the independent-Sync result.

The inventory and measured results are maintained in
[`docs/npgsql-performance.md`](../../docs/npgsql-performance.md).

## Paired converter comparison with Npgsql

Append `--npgsql-converters` to compare the three existing converter benchmark
classes. Npgsql methods come from the immutable baseline directory and Mpgsql
methods from the candidate directory. The runner uses the common catalog,
Count=256 for arrays and a NULL every eighth nullable-array element. It records
extra candidate profiles separately instead of assuming that an older baseline
can execute them.

Each Read/Write series has 350 ms of warmup, an adaptive pilot targeting a 20 ms
block, and 40 pairs with alternating order and the same invocation count on both
sides. Cached `Func<int>` delegates run synchronously; reflection, setup, payload
verification and result serialization stay outside timing. Both loops include
delegate and checksum overhead, without subtraction. This overhead matters for
very small scalar results; a borderline result needs a focused BenchmarkDotNet
confirmation. The 95% interval is computed on paired log ratios without outlier
filtering. Acceptance requires its upper Mpgsql/Npgsql bound and the arithmetic
mean ratio to be at most 0.90, with an independent repeat. The interval applies
to paired log ratios, not to the arithmetic means.

Append `--npgsql-converters-focused` for only scalar `numeric.decimal`,
`interval.pg` and `interval.clr`, Read and Write. It increases warmup to 5 seconds
and the target sample block to 120 ms, retaining 40 alternating pairs. Use this
mode to investigate short or unstable full-matrix results; inspect arithmetic
means as well as the paired geometric ratio, and retain every sample.

Append `--npgsql-converters-focused=bpchar` for just scalar bpchar Read and Write,
or provide comma-separated scalar catalog IDs after `=`. Custom selection uses
the same 5-second warmup, 120-ms target and 40 pairs. Every requested ID must
occur exactly once in both snapshots; empty, duplicate and missing IDs are
rejected before measurement. JSON records the selection in `SelectedIDs` and
expects exactly two series per selected ID. The flag without `=` retains its
three default profiles and six series.

JSON retains every pair, separate stable return values, thread-local and whole
process allocations, runtime settings, and SHA-256 hashes of the benchmark and
driver assemblies. Owned reads and sizing/writing use the existing fixture
contracts. The file is saved after every completed series so an interrupted run
is identifiable by `Complete=false`.

```powershell
dotnet benchmarks/Mpgsql.PerformanceComparison/bin/Release/net10.0/Mpgsql.PerformanceComparison.dll `
  artifacts/npgsql-10percent/control/bin/Mpgsql.Benchmarks.dll `
  artifacts/npgsql-10percent/candidate/bin/Mpgsql.Benchmarks.dll `
  artifacts/npgsql-10percent/converters.json --npgsql-converters
```

Do not append BenchmarkDotNet `--inProcess` to the full converter matrix as a
shortcut. The command-line job is combined with the `Converters` job declared
by `[Config]`, producing two jobs and doubling the measurements. This paired
runner does not use BenchmarkDotNet jobs. For focused BenchmarkDotNet runs,
inspect the discovered job list before starting the measurements.

## Exporting the measured results

After all measurements have finished, export the frozen final TCP/concurrent
results together with the converter results from the unchanged Protocol binary:

```powershell
python benchmarks/Mpgsql.PerformanceComparison/summarize.py `
  artifacts/npgsql-10percent --candidate candidate5 --converters-candidate candidate3
```

The exporter checks completeness and series counts, preserves the two repeats,
and writes CSV files under `artifacts/npgsql-10percent/summary`, including the
scenarios that miss the 10% bound. Full-matrix converter misses require two
successful focused repeats. CSV export uses only the Python standard library;
optional `--plot` requires matplotlib. Do not export large JSON files while a
benchmark is running.

The measured scope, remaining gaps, memory tradeoffs and reproduction commands
are documented in `docs/npgsql-performance.md`.
