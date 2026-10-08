# All built-in converter comparisons

`ConverterScalarBenchmarks`, `ConverterArrayBenchmarks`, and
`ConverterNullableArrayBenchmarks` compare all 72 public Mpgsql converter classes
against actual Npgsql **10.0.3** converters. The catalog has 34 scalar, 34 array,
and 34 nullable-array profiles: all 26 PostgreSQL types, CLR date/time/numeric
overloads, the byte-array representation, and a wide numeric fixture.

Run from `benchmarks/Mpgsql.Benchmarks` after a Release solution build:

```powershell
New-Item -ItemType Directory -Force ../../artifacts/converter-comparison | Out-Null
dotnet bin/Release/net10.0/Mpgsql.Benchmarks.dll --verify-converters `
  --converter-catalog ../../artifacts/converter-comparison/catalog.json
dotnet bin/Release/net10.0/Mpgsql.Benchmarks.dll `
  --filter '*ConverterScalarBenchmarks*' '*ConverterArrayBenchmarks*' '*ConverterNullableArrayBenchmarks*' `
  --join --exporters fulljson csv markdown --artifacts ../../artifacts/converter-comparison/run
```

Npgsql resolution and fixture setup use reflection and compiled delegates only
outside measurements. The reader
is reset over bytes loaded once; its stream is never read during timing. The writer
uses `FlushMode.None` and a preallocated `FixedBufferWriter`, including Npgsql's
normal sizing, BeginWrite/Commit, and pooled write-state disposal. Fixed-size
Npgsql scalar converters use the same exact-size shortcut as `PgTypeInfo.Bind`.
There is no connection, simulated server, scalar-converter copy, or JSON
deserialization in this comparison. Raw jsonb compares UTF-8 bytes on both sides.

The Mpgsql path uses public converter APIs, including the specialized Int64
converter and the shared 128-bit array SIMD implementation. Both readers return
owned results. Borrowed reads and reusable destinations are separate APIs and are
not mixed into the allocation comparison. SQL NULL scalar fields have no payload
conversion; NULL elements are measured by the nullable-array matrix instead.

Each profile verifies both writes and both decoders before timing. Array checks
normalize only the valid has-NULL flag and the equivalent empty-array headers
(Mpgsql ndim=0 versus Npgsql ndim=1/length=0); element OIDs, element lengths,
payload bytes, NULL positions, and trailing bytes must match. The verification
command also uses counts 0, 1, 3, 4, 7, 8, 9, 256, 4096, NULL intervals 0/1/8,
and segment sizes 1/7/4096. It fails if a public converter is missing from coverage.

Default arrays have 256 elements; nullable arrays have a NULL every eight elements.
For size scaling, set `$env:MPGSQL_BENCHMARK_ARRAY_COUNTS='1,256,4096'` before
starting BenchmarkDotNet. Remove that environment variable for the README's
representative matrix. One launch, 3 warmups, 8 measured iterations, and 150 ms per
iteration are configured in `ConverterBenchmarkConfig`; the machine's power
policy is preserved. Avoid competing CPU workloads during the run.

Write the measured matrix into `src/Mpgsql/README.md` using the joined full JSON
report (all 408 results, Count=256 only):

```powershell
./Converters/Write-ConverterResults.ps1 `
  -Report ../../artifacts/converter-comparison/run/results/BenchmarkRun-joined-<timestamp>-report-full.json `
  -Catalog ../../artifacts/converter-comparison/catalog.json `
  -Readme ../../src/Mpgsql/README.md `
  -ResultsCsv ./Converters/results-2026-10-08.csv `
  -Cpu 'AMD Ryzen 7 5800X' -Date '2026-10-08'
```

The generator refuses incomplete or duplicate measurements and retains unrounded
statistics in CSV. It replaces only its marked README section. Reported times are
Mean ± the 99.9% confidence interval half-width; overlapping intervals are marked
with †. Npgsql/Mpgsql ratios above 1 favor Mpgsql for that exact API and fixture.
Different result representations are stated explicitly; these microbenchmarks
do not establish an end-to-end performance advantage for the entire driver.
