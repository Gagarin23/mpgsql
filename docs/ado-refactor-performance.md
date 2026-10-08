# ADO.NET refactor performance

**The complete 5% acceptance bound is not established.** The final independent
repeats agree for all requested reader cases except Bytea64KiB. Its second
repeat exceeds the bound on both the exclusive typed and multiplexing paths.
Concurrent throughput and mean latency meet the bound in both long repeats;
the second four-connection repeat does not establish it for p99 latency.
Do not infer performance acceptance from a passing single run.

Baseline: `d67e4e2d33e6155f8d97ea2457d3c8896690c648`, frozen Release binaries.
Candidate: the final working-tree Release build. Windows 11, AMD Ryzen 7 5800X (8 physical / 16 logical cores), .NET
10.0.12, SDK 10.0.204, workstation GC.
Both versions run in separate assembly contexts in one process, with identical
synthetic TCP protocol peers. The process uses High priority; CPU affinity and
the system power plan are unchanged. Database containers, builds and other
tests are stopped during measurements.

There are 40 pairs per case after five seconds of warmup. Order alternates.
Each block targets 120 ms. Ratios are geometric means of paired latency ratios;
95% confidence intervals use Student t (39)=2.023 on log ratios. These are
per-case intervals, not simultaneous bounds or a guarantee under arbitrary
machine load. No outlier filtering is applied. Throughput is also reported in
the raw JSON as the reciprocal of mean operation time.

The exclusive baseline uses the previous explicit connection and typed reader.
Its one-shot command construction is outside the timed/allocated interval;
the candidate reuses its command. Both bytea paths borrow bytes and copy once
into the same reusable destination. Batch16 excludes caller construction on
both versions and includes validation, encoding, execution and reading.
Allocations below cover all managed threads, including the identical TCP peer.
They are not directly interchangeable with BDN MemoryDiagnoser results.

## Final paired repeats

| Path / case                      | Run 12 baseline → candidate, µs |   Run 12 ratio [95% CI] | Run 13 baseline → candidate, µs |   Run 13 ratio [95% CI] | Run 13 B/op baseline → candidate |
|----------------------------------|--------------------------------:|------------------------:|--------------------------------:|------------------------:|---------------------------------:|
| Multiplexing / Empty             |                   70.68 → 70.30 | 0.9948 [0.9867, 1.0029] |                   71.92 → 71.57 | 0.9951 [0.9897, 1.0006] |                      1392 → 1376 |
| ExclusiveTyped / Empty           |                   75.48 → 71.77 | 0.9512 [0.9441, 0.9583] |                   76.03 → 71.13 | 0.9358 [0.9276, 0.9441] |                      1449 → 1497 |
| Multiplexing / OneBigint         |                   70.99 → 70.25 | 0.9896 [0.9842, 0.9950] |                   70.78 → 69.95 | 0.9884 [0.9817, 0.9951] |                      1392 → 1376 |
| ExclusiveTyped / OneBigint       |                   77.66 → 73.48 | 0.9462 [0.9375, 0.9549] |                   77.14 → 73.12 | 0.9478 [0.9425, 0.9531] |                      1521 → 1569 |
| Multiplexing / Rows128Columns8   |                 154.71 → 152.15 | 0.9833 [0.9727, 0.9940] |                 151.33 → 149.51 | 0.9879 [0.9853, 0.9905] |                      1512 → 1493 |
| ExclusiveTyped / Rows128Columns8 |                 158.49 → 157.75 | 0.9953 [0.9909, 0.9997] |                 156.47 → 157.29 | 1.0053 [1.0003, 1.0103] |                      1692 → 1735 |
| Multiplexing / Rows4096          |               1608.85 → 1568.34 | 0.9747 [0.9683, 0.9812] |               1580.38 → 1575.45 | 0.9969 [0.9943, 0.9994] |                      1904 → 1949 |
| ExclusiveTyped / Rows4096        |               1585.02 → 1553.29 | 0.9799 [0.9751, 0.9848] |               1617.73 → 1571.17 | 0.9712 [0.9675, 0.9749] |                      2047 → 2141 |
| Multiplexing / Bytea64KiB        |                 483.27 → 481.40 | 0.9961 [0.9851, 1.0073] |                 454.18 → 480.01 | 1.0568 [1.0524, 1.0613] |                      1575 → 1635 |
| ExclusiveTyped / Bytea64KiB      |                 455.40 → 452.36 | 0.9933 [0.9898, 0.9969] |                 449.12 → 477.25 | 1.0625 [1.0557, 1.0693] |                      1767 → 1822 |
| FreshBatchExecution / Batch16    |                   92.10 → 94.39 | 1.0249 [1.0202, 1.0297] |                   91.27 → 93.04 | 1.0193 [1.0122, 1.0264] |                      1795 → 3522 |

Batch16 means time/allocations per batch of 16 commands; each invocation runs
32 such batches. Its first-execution descriptor/affected-row buffers remain in
the timed/allocated interval; caller construction is excluded.

## Diagnostics and changes supported by measurements

The bytea diagnostic compares raw, multiplexed and exclusive paths. All copy **65,559 bytes per operation** in the
existing session copied-byte counters.
There is no additional payload pass. The raw session comparison is within
the bound in those diagnostics. The upper results vary across repeats, and
the remaining cause has not been established.

Two intermediate async wrappers were removed: ADO.NET first-row prefetch now
awaits the raw movement directly, and normal multiplexed reader completion
forwards the existing ValueTask. Healthy completion does not start a recovery
timer; preparation with command timeout zero is allowed to take longer than
the recovery deadline. Error, cancellation and discard recovery remain bounded.
These changes reduced the observed upper-path allocations, but they do not
justify asserting the remaining bytea bound.

Exploratory runs 1–10 and bytea diagnostics are retained separately. Early
exclusive comparisons against an independent multiplexed source were not an
equivalent baseline, and are excluded from acceptance. Short Batch16 blocks
and uncontrolled scheduling produced broad/contradictory intervals. The final
report uses the corrected explicit-connection baseline and longer blocks.

## concurrent-2.json

64 callers, 40 pairs, 65,536 requests/version/pair after three warmup blocks.
Throughput ratio is baseline/candidate; latency ratio is candidate/baseline.
Checksum and complete query/Sync counters are checked by the existing runner.

| Profile / Sync group | req/s baseline → candidate | inverse throughput ratio [95% CI] | mean latency ratio [95% CI] |      p99 ratio [95% CI] | B/request baseline → candidate |
|----------------------|---------------------------:|----------------------------------:|----------------------------:|------------------------:|-------------------------------:|
| C64_P1_W8 / 1        |              88457 → 88002 |           1.0051 [0.9985, 1.0118] |     1.0059 [0.9992, 1.0126] | 0.9704 [0.9418, 0.9999] |                    1630 → 1613 |
| C64_P4_W8 / 1        |            245290 → 248054 |           0.9888 [0.9750, 1.0027] |     0.9872 [0.9783, 0.9963] | 0.9542 [0.9192, 0.9906] |                    1655 → 1641 |
| C64_P1_W8 / 8        |              78006 → 78214 |           0.9974 [0.9927, 1.0022] |     0.9974 [0.9927, 1.0022] | 0.9763 [0.9538, 0.9994] |                    1932 → 1922 |

## concurrent-3.json

64 callers, 40 pairs, 65,536 requests/version/pair after three warmup blocks.
Throughput ratio is baseline/candidate; latency ratio is candidate/baseline.
Checksum and complete query/Sync counters are checked by the existing runner.

| Profile / Sync group | req/s baseline → candidate | inverse throughput ratio [95% CI] | mean latency ratio [95% CI] |      p99 ratio [95% CI] | B/request baseline → candidate |
|----------------------|---------------------------:|----------------------------------:|----------------------------:|------------------------:|-------------------------------:|
| C64_P1_W8 / 1        |              87355 → 87402 |           0.9995 [0.9932, 1.0058] |     0.9995 [0.9933, 1.0057] | 0.9993 [0.9770, 1.0221] |                    1631 → 1613 |
| C64_P4_W8 / 1        |            244758 → 243546 |           1.0054 [0.9887, 1.0224] |     1.0025 [0.9889, 1.0162] | 1.0282 [0.9634, 1.0973] |                    1655 → 1638 |
| C64_P1_W8 / 8        |              78093 → 77511 |           1.0076 [1.0016, 1.0136] |     1.0077 [1.0017, 1.0137] | 1.0088 [0.9739, 1.0450] |                    1934 → 1920 |

## Creation, reuse and the standard object API

BenchmarkDotNet 0.15.8, in-process, four warmup / 20 measurement iterations.
The table reports its per-method mean and 99.9% confidence interval.
FreshTyped includes command/parameter creation. ReusedObject uses DbCommand,
DbDataReader, GetValue boxing and standard GetBytes. MemoryDiagnoser has a
different allocation scope from the paired process-wide comparisons.

| Case                 | Method       |              µs [99.9% CI] |   B/op |
|----------------------|--------------|---------------------------:|-------:|
| Case=Bytea64KiB      | ReusedTyped  |    488.85 [464.10, 513.60] |   2209 |
| Case=Bytea64KiB      | FreshTyped   |    474.66 [456.15, 493.16] |   3306 |
| Case=Bytea64KiB      | ReusedObject |    463.61 [458.68, 468.55] |   2310 |
| Case=Empty           | ReusedTyped  |       77.32 [76.62, 78.01] |   2101 |
| Case=Empty           | FreshTyped   |       80.38 [77.71, 83.05] |   2876 |
| Case=Empty           | ReusedObject |       77.37 [76.68, 78.05] |   2199 |
| Case=OneBigint       | ReusedTyped  |       78.60 [77.82, 79.39] |   2181 |
| Case=OneBigint       | FreshTyped   |       80.08 [79.22, 80.94] |   2930 |
| Case=OneBigint       | ReusedObject |       78.52 [77.07, 79.97] |   2171 |
| Case=Rows128Columns8 | ReusedTyped  |    158.04 [157.03, 159.05] |   2253 |
| Case=Rows128Columns8 | FreshTyped   |    160.45 [158.76, 162.14] |   3008 |
| Case=Rows128Columns8 | ReusedObject |    168.25 [167.02, 169.48] |  26785 |
| Case=Rows4096        | ReusedTyped  | 1432.17 [1416.89, 1447.46] |   2995 |
| Case=Rows4096        | FreshTyped   | 1440.47 [1432.96, 1447.98] |   3489 |
| Case=Rows4096        | ReusedObject | 1437.99 [1426.06, 1449.92] | 101624 |

## Reproduction and limits

Use `benchmarks/Mpgsql.PerformanceComparison/README.md` for commands and exact
measurement scope. Raw paired/concurrent JSON, individual pairs, logs and
assembly hashes are in `artifacts/ado-refactor-performance/`. Standard object
API costs are reported separately and are not used to establish the typed
5% bound. Live PostgreSQL/PgBouncer/pg_doorman checks establish protocol/API
behavior; these TCP benchmarks do not establish live-server performance.
