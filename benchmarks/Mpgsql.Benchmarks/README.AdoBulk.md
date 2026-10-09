# Typed bulk destination filling

`Mpgsql.Benchmarks.TcpAdoBulkReaderBenchmarks` is a separate workload for the
typed bulk APIs. It compares complete destination filling with ordinary
row-by-row filling. The existing reader comparisons accumulate a checksum
without filling an output array, so their timings are not bulk baselines.

Two complete 72-case repetitions ran on 2026-10-09. All setup/cleanup checks
passed. At 65,536 rows, neither bulk API established a repeatable 10% advantage.
Full-column bulk mean latency was 7.60% / 4.01% below Npgsql, with overlapping
individual 99.9% confidence intervals. The two repetitions and noisy cases are
preserved in the [local report](../../artifacts/ado-bulk/run-20261009-180015/report.md).
These are exploratory measurements, not performance acceptance.

| Category | Mpgsql bulk | Mpgsql row-by-row | Npgsql row-by-row baseline |
| --- | --- | --- | --- |
| Column | `ReadColumnAsync<long>(0, Memory<long>)` | `ReadValueTaskAsync` + `GetFieldValue<long>(0)` + destination write | `ReadAsync` + `GetFieldValue<long>(0)` + destination write |
| Records | `ReadRowsAsync<BigintRecord, BigintRecordMapper>(Memory<BigintRecord>, mapper)` | movement + typed getter + struct construction + destination write | movement + typed getter + the same struct construction + destination write |

The records category uses an eight-byte `readonly record struct` and a
stateless struct mapper. It exercises the row-mapper API on the same single
bigint field; it does not claim coverage of wide records, custom conversions,
SQL NULL, or bytea ownership.

Both categories reuse one command, one explicitly typed bigint parameter with
value 1, and one exclusive open connection. Mpgsql uses
`ExecuteReaderValueTaskAsync` and its ValueTask movement/bulk APIs. Npgsql
10.0.3 uses `ExecuteReaderAsync` and Task movement. All benchmark entry points
return `Task<long>`. These are equivalent destination-filling workloads with
different provider APIs; this suite is separate from standard Task-only ADO
acceptance.

Each row-by-row method traverses the same destination slices as its bulk
counterpart. Every result value is decoded and written once. After filling,
every method performs the same checksum traversal of the filled destination
within its category and requires the expected sum on every invocation. That
common traversal is included in timing and makes every written element
observable. It is benchmark work, not an additional
driver payload-validation pass. Command/parameter creation, destination
allocation, socket startup, transcript construction, metadata capture and
full element-by-element setup validation are outside timing.

## Shapes and capacities

| Rows | SQL | Decoded bigint bytes | DataRow frame bytes | Checksum |
| --- | --- | ---: | ---: | ---: |
| 64 | `select $1::bigint+i-1 from generate_series(1,64) i` | 512 | 1,216 | 2,080 |
| 256 | `select $1::bigint+i-1 from generate_series(1,256) i` | 2,048 | 4,864 | 32,896 |
| 4,096 | `select $1::bigint+i-1 from generate_series(1,4096) i` | 32,768 | 77,824 | 8,390,656 |
| 65,536 | `select $1::bigint+i-1 from generate_series(1,65536) i` | 524,288 | 1,245,184 | 2,147,516,416 |

Each DataRow is 19 bytes: tag + big-endian length (5), field count (2), field
length (4), and binary bigint (8). The DataRow total excludes RowDescription,
CommandComplete, ReadyForQuery and other control frames. Decoded bytes describe
the destination values, not measured network bandwidth.

The matrix has four row counts, three capacity settings and six methods: 72
cases, including duplicate effective capacities. `Capacity` is 256, 4,096 or 0;
0 selects the full row count, and other settings are clamped to the row count.
`EffectiveCapacity` records the actual maximum slice size. Every invocation
still fills a reusable array for the entire result by passing successive
`Memory<T>` slices. Smaller capacities do not reduce rows, copied values or
the checksum. All three settings select the full result for 64 and 256 rows;
4,096 and 0 also coincide for the 4,096-row case. These duplicates remain
explicit and must not be treated as independent evidence.

A full bulk destination does not establish EOF. Both bulk methods make another
call with nonempty reusable one-element scratch memory and require a zero
count. Row-by-row methods make the corresponding final `Read` and require
false. All methods require `NextResult` false and include asynchronous reader
disposal, so CommandComplete/ReadyForQuery and reader completion are included.

Setup fills destinations with sentinel values, runs every method of the selected
provider, verifies the complete result in order and its checksum, checks healthy
idle ownership, and requires one query/one Sync per invocation. Cleanup checks
the final connection, peer health and query/Sync counts outside timing. These
checks passed in both full repetitions and the separate provenance capture.

The existing synthetic TCP peer accepts a standalone `QueryScenario` built by
this class; the shared scenario catalog and wire implementation are unchanged.
The peer sends fixed binary transcripts and executes no SQL. There is no
artificial delay, multiplexing, prepared statement, pool lease, reset query,
TLS, authentication, or real PostgreSQL execution in the timed workload.
Mpgsql transport counting is disabled; peer replies retain the default flush
policy. Each provider retains its production read-buffer default, recorded in
setup metadata. Safe metadata also records row count, effective capacity,
volume, checksum, runtime, Npgsql informational version and loaded assembly
SHA-256 hashes. No credentials or connection string are emitted.

## Reproducing the benchmark

The existing assembly-wide `BenchmarkSwitcher` discovers the new class.
`Program.cs`, the existing 16/7-case matrices and paired comparison samplers
need no change. Run from the benchmark project directory after an authorized
Release build, using fresh artifact directories for each repetition:

```powershell
Set-Location C:\Projects\mpgsql\benchmarks\Mpgsql.Benchmarks
dotnet run -c Release --no-build -- --filter '*TcpAdoBulkReaderBenchmarks*' --artifacts '..\..\artifacts\ado-bulk\repeat1'
dotnet run -c Release --no-build -- --filter '*TcpAdoBulkReaderBenchmarks*' --artifacts '..\..\artifacts\ado-bulk\repeat2'
```

Optional category selections are `--anyCategories Column` or
`--anyCategories Records`. Execute repetitions sequentially, with no competing
builds, tests, profilers or other benchmark suites. Preserve the complete JSON,
logs and setup metadata from both repetitions.

Use `--keepFiles` to retain BenchmarkDotNet's generated job and actual loaded
assemblies. BDN rebuilds project references into a separate output directory;
a normal Release snapshot is not necessarily the measured binary. The first
two runs had identical measured assembly hashes, but BDN removed their generated
directories. A separate 12-case `ColumnBulk` capture with `--keepFiles` preserved
all five participant assemblies with hashes matching both full repetitions.
Its timings were excluded from the comparison. Snapshots, manifests and raw
results are retained under `artifacts/ado-bulk/run-20261009-180015`.

The existing `QueryBenchmarkConfig` uses one launch, three warmup iterations,
eight measurement iterations and a 150 ms iteration target. These BDN runs are
exploratory comparisons; they are not the fixed 40-pair confidence-interval
acceptance experiment. A future paired bulk runner must retain identical
destination/checksum/completion work, freeze assemblies and
runtime settings, use two independent repetitions, and report disagreements
without selecting a favorable repetition. No such runner or acceptance
threshold has been added here.

BDN allocation output must not be attributed solely to bulk decoding: the
operation also owns a command execution and reader, and the synthetic peer runs
in the same process. This suite creates no output array per invocation. It
contains no pooling/state-lifetime changes to the driver.
