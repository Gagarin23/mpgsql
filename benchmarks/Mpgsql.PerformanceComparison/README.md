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
