# Mpgsql: Minimalistic postgresql driver

Mpgsql is a standalone PostgreSQL protocol and binary conversion library for .NET 10.
It gives callers the building blocks to implement their own transport, connection
lifecycle, scheduling, and PostgreSQL pipeline behavior.

The `Mpgsql` NuGet package contains:

- `Mpgsql.Protocol`: protocol 3.0 frontend messages, raw message construction,
  backend parsing, and indexed access to DataRow fields.
- `Mpgsql.Converters`: binary scalar and one-dimensional array payload converters,
  including nullable values and reusable buffer APIs.
- `Mpgsql.Types` and `Mpgsql.TypeOid`: PostgreSQL value representations and built-in OIDs.
- `Mpgsql.Copy`: binary COPY framing, row conversion, and COPY operation state tracking.

The package has no additional NuGet dependencies. Its message and payload APIs use
`Span`, `Memory`, `ReadOnlySequence<byte>`, and `IBufferWriter<byte>`.
Converters write the type payload; the surrounding Bind, DataRow, or COPY field owns
the outer length and SQL NULL marker.

## Encode an Extended Query group

```csharp
using System.Buffers;
using Mpgsql.Converters;
using Mpgsql.Protocol;

var output = new ArrayBufferWriter<byte>();
byte[] payload = new byte[Int64Converter.ByteCount];
Int64Converter.Write(42, payload);

FrontendMessageWriter.Write(FrontendMessage.Parse(
    "select $1::bigint", parameterTypes: new uint[] { Int64Converter.TypeOid }), output);
FrontendMessageWriter.Write(FrontendMessage.Bind(
    parameters: new ReadOnlyMemory<byte>?[] { payload },
    parameterFormats: new[] { FormatCode.Binary },
    resultFormats: new[] { FormatCode.Binary }), output);
FrontendMessageWriter.Write(FrontendMessage.Describe(StatementOrPortal.Portal), output);
FrontendMessageWriter.Write(FrontendMessage.Execute(), output);
FrontendMessageWriter.Write(FrontendMessage.Sync(), output);

// Send output.WrittenMemory through a ready, authenticated UTF8 transport.
```

The caller controls message order, transmission, authentication, cancellation, and
recovery. After an Extended Query error, consume responses through `ReadyForQuery`
for the corresponding `Sync` boundary. Borrowed backend payloads must be consumed
or copied before advancing or reusing their input buffer. COPY requires exclusive
ownership of its transport until its subprotocol completes.

## Repository projects

`src/Mpgsql/Mpgsql.csproj` builds this package and `Mpgsql.dll`.
`src/Mpgsql.Sessions/` builds TCP/TLS authentication, explicit pipeline sessions,
query encoding, prepared statements and the low-level reader on top of this package.
`src/Mpgsql.Client/` provides asynchronous ADO.NET and an exclusive session pool.
`src/Mpgsql.Multiplexing/` schedules independent requests with backpressure and
optional shared Sync. Both upper projects reference Sessions independently; all
three retain the `Mpgsql` namespaces and are not packed.

Build and create a local prerelease package from the repository root:

```shell
dotnet build src/Mpgsql.slnx -c Release
dotnet test tests/Mpgsql.Tests/Mpgsql.Tests.csproj -c Release --no-build
dotnet pack src/Mpgsql/Mpgsql.csproj -c Release --no-build -o artifacts/packages
```

The default package version is `0.1.0-alpha.1`; override it with
`-p:PackageVersion=<version>` when packing a release.

<!-- converter-benchmarks:start -->

## Binary converter benchmarks against Npgsql

Measured on 2026-10-08 on AMD Ryzen 7 5800X, Windows 11 (10.0.26200.9445/25H2/2025Update/HudsonValley2), .NET 10.0.12
x64 (SDK 10.0.204), BenchmarkDotNet 0.15.8, Npgsql 10.0.3.
All 72 public converter classes are covered by 102 profiles and 408 measured scenarios. Before timing, 1258 fixtures
passed complete payload, cross-decoding, and segmented-read checks, including empty arrays, SIMD boundaries, and NULL
patterns.

Each operation handles one scalar or one array of **256 elements**. Nullable arrays contain **12.5% NULLs** (every
eighth element). Reads allocate owned results on both sides from the same fully buffered PostgreSQL binary payload.
Writes include size determination and encode into the same kind of preallocated `IBufferWriter<byte>`; Npgsql includes
its normal `PgWriter` field lifecycle and write-state disposal. Converter resolution, reflection, fixture construction,
input buffering, outer field framing, SQL, sockets, and PostgreSQL execution are outside timing.

The harness calls the actual
version-pinned [Npgsql converters](https://github.com/npgsql/npgsql/tree/v10.0.3/src/Npgsql/Internal/Converters) with
cached typed delegates. Mpgsql uses its public writer and `ReadOnlySequence<byte>` read APIs. These figures describe
these API paths, including dispatch and buffer lifecycle costs; they are not end-to-end driver throughput.

Values are deterministic: bytea is 64 bytes; strings contain ASCII and Cyrillic UTF-8; bpchar retains trailing spaces;
inet/cidr arrays alternate IPv4 and IPv6. Ordinary numeric has four fractional decimal places; `numeric.wide` is an
integer with 128 base-10000 digits. Where representations differ, the table lists **Mpgsql / Npgsql** types. Conversions
between those representations happen during setup. In particular, raw `PgNumeric` versus decimal/BigInteger, money cents
versus decimal, and `PgInet` versus IPAddress-backed types perform different representation work.

Times are **Mean ± Error** (BenchmarkDotNet 99.9% confidence interval half-width), with 3 warmup and 8 measurement
iterations targeting 150 ms each, one process launch, default outlier handling, and the existing machine power policy.
**N/M** is Npgsql time divided by Mpgsql time: above 1 means Mpgsql is faster for this fixture. **†** marks overlapping
time confidence intervals, so a small apparent difference should not be treated as an established win. **B/op M/N**
reports managed bytes allocated per operation by Mpgsql / Npgsql.

### Scalars — read (ns/op)

| Profile                | Representation M/N                    | Payload B |       Mpgsql |          Npgsql |     N/M |    B/op M/N |
|------------------------|---------------------------------------|----------:|-------------:|----------------:|--------:|------------:|
| `bool`                 | `bool`                                |         1 |  9.92 ± 0.03 |    14.07 ± 0.07 |   1.42× |       0 / 0 |
| `int2`                 | `short`                               |         2 |  7.19 ± 0.02 |    13.56 ± 0.13 |   1.89× |       0 / 0 |
| `int4`                 | `int`                                 |         4 |  7.19 ± 0.04 |    13.64 ± 0.26 |   1.90× |       0 / 0 |
| `int8`                 | `long`                                |         8 |  7.12 ± 0.03 |    13.83 ± 0.13 |   1.94× |       0 / 0 |
| `float4`               | `float`                               |         4 |  7.09 ± 0.06 |    14.00 ± 0.08 |   1.98× |       0 / 0 |
| `float8`               | `double`                              |         8 |  7.49 ± 0.08 |    13.59 ± 0.45 |   1.82× |       0 / 0 |
| `oid`                  | `uint`                                |         4 |  7.19 ± 0.04 |    13.96 ± 0.59 |   1.94× |       0 / 0 |
| `money`                | `long cents / decimal`                |         8 |  7.51 ± 0.09 |    16.77 ± 0.14 |   2.23× |       0 / 0 |
| `uuid`                 | `Guid`                                |        16 | 18.18 ± 0.17 |    19.00 ± 0.18 |   1.05× |       0 / 0 |
| `numeric.pg`           | `PgNumeric / decimal`                 |        16 | 52.49 ± 0.74 |   100.70 ± 0.64 |   1.92× |      32 / 0 |
| `numeric.decimal`      | `decimal`                             |        16 | 49.23 ± 0.33 |    69.97 ± 0.53 |   1.42× |       0 / 0 |
| `numeric.wide`         | `PgNumeric / BigInteger (128 digits)` |       264 | 61.81 ± 1.05 | 7187.25 ± 82.91 | 116.28× | 280 / 34608 |
| `date.pg`              | `PgDate / int days`                   |         4 | 11.66 ± 0.04 |    13.76 ± 0.12 |   1.18× |       0 / 0 |
| `date.clr`             | `DateOnly`                            |         4 | 12.46 ± 0.08 |    13.64 ± 0.21 |   1.09× |       0 / 0 |
| `time.pg`              | `PgTime / long microseconds`          |         8 | 11.79 ± 0.10 |    13.67 ± 0.10 |   1.16× |       0 / 0 |
| `time.clr`             | `TimeOnly`                            |         8 | 14.51 ± 0.09 |    13.99 ± 0.22 |   0.96× |       0 / 0 |
| `timetz`               | `PgTimeTz / DateTimeOffset`           |        12 | 27.54 ± 0.27 |    15.67 ± 0.27 |   0.57× |       0 / 0 |
| `timestamp.pg`         | `PgTimestamp / long microseconds`     |         8 | 12.18 ± 0.10 |    13.59 ± 0.11 |   1.12× |       0 / 0 |
| `timestamp.clr`        | `DateTime Unspecified`                |         8 | 13.08 ± 0.08 |    14.74 ± 0.11 |   1.13× |       0 / 0 |
| `timestamptz.pg`       | `PgTimestampTz / long microseconds`   |         8 | 12.02 ± 0.15 |    13.54 ± 0.08 |   1.13× |       0 / 0 |
| `timestamptz.clr`      | `DateTimeOffset UTC`                  |         8 | 17.34 ± 0.05 |    16.59 ± 0.06 |   0.96× |       0 / 0 |
| `timestamptz.datetime` | `DateTime UTC`                        |         8 | 12.27 ± 0.05 |    15.56 ± 0.12 |   1.27× |       0 / 0 |
| `interval.pg`          | `PgInterval / NpgsqlInterval`         |        16 | 20.06 ± 0.15 |    15.08 ± 0.15 |   0.75× |       0 / 0 |
| `interval.clr`         | `TimeSpan`                            |        16 | 14.88 ± 0.24 |    14.39 ± 0.07 |   0.97× |       0 / 0 |
| `inet`                 | `PgInet / NpgsqlInet (IPv4 + IPv6)`   |        20 | 13.50 ± 0.12 |    30.24 ± 0.79 |   2.24× |      0 / 80 |
| `cidr`                 | `PgInet / IPNetwork (IPv4 + IPv6)`    |        20 | 13.86 ± 0.07 |    34.14 ± 0.48 |   2.46× |      0 / 80 |
| `bytea`                | `ReadOnlyMemory<byte> (64 B)`         |        64 | 15.22 ± 0.26 |    38.13 ± 0.33 |   2.50× |     88 / 88 |
| `jsonb`                | `Memory<byte> / byte[] UTF-8`         |        65 | 35.21 ± 0.30 |    41.13 ± 0.95 |   1.17× |     88 / 88 |
| `text`                 | `string UTF-8`                        |        56 | 34.18 ± 0.32 |    48.81 ± 0.86 |   1.43× |   128 / 128 |
| `varchar`              | `string UTF-8`                        |        56 | 36.39 ± 1.17 |    50.19 ± 0.83 |   1.38× |   128 / 128 |
| `bpchar`               | `string UTF-8`                        |        59 | 35.59 ± 0.53 |    49.88 ± 0.44 |   1.40× |   128 / 128 |
| `name`                 | `string UTF-8`                        |        24 | 28.88 ± 0.57 |    44.79 ± 0.19 |   1.55× |     64 / 64 |
| `json`                 | `string UTF-8`                        |        64 | 36.25 ± 1.22 |    51.87 ± 1.88 |   1.43× |   144 / 144 |
| `xml`                  | `string UTF-8`                        |        54 | 34.44 ± 0.50 |    49.45 ± 0.93 |   1.44× |   120 / 120 |

### Scalars — write (ns/op)

| Profile                | Representation M/N                    | Payload B |        Mpgsql |            Npgsql |      N/M | B/op M/N |
|------------------------|---------------------------------------|----------:|--------------:|------------------:|---------:|---------:|
| `bool`                 | `bool`                                |         1 |   5.05 ± 0.03 |      40.59 ± 0.43 |    8.03× |    0 / 0 |
| `int2`                 | `short`                               |         2 |   5.04 ± 0.04 |      21.23 ± 0.06 |    4.21× |    0 / 0 |
| `int4`                 | `int`                                 |         4 |   5.03 ± 0.05 |      29.17 ± 0.23 |    5.80× |    0 / 0 |
| `int8`                 | `long`                                |         8 |  11.21 ± 0.10 |      20.52 ± 0.05 |    1.83× |    0 / 0 |
| `float4`               | `float`                               |         4 |   4.69 ± 0.02 |      21.24 ± 0.06 |    4.53× |    0 / 0 |
| `float8`               | `double`                              |         8 |   4.66 ± 0.07 |      22.09 ± 0.15 |    4.73× |    0 / 0 |
| `oid`                  | `uint`                                |         4 |   5.02 ± 0.04 |      21.57 ± 0.32 |    4.30× |    0 / 0 |
| `money`                | `long cents / decimal`                |         8 |   5.11 ± 0.05 |      31.54 ± 0.78 |    6.18× |    0 / 0 |
| `uuid`                 | `Guid`                                |        16 |   6.66 ± 0.05 |      24.13 ± 0.32 |    3.62× |    0 / 0 |
| `numeric.pg`           | `PgNumeric / decimal`                 |        16 |  15.52 ± 0.19 |      98.56 ± 3.85 |    6.35× |   0 / 48 |
| `numeric.decimal`      | `decimal`                             |        16 | 103.35 ± 2.63 |      91.32 ± 0.25 |    0.88× |   0 / 48 |
| `numeric.wide`         | `PgNumeric / BigInteger (128 digits)` |       264 |  22.58 ± 0.42 | 36835.65 ± 373.98 | 1631.19× |  0 / 296 |
| `date.pg`              | `PgDate / int days`                   |         4 |   5.11 ± 0.06 |      20.95 ± 0.08 |    4.10× |    0 / 0 |
| `date.clr`             | `DateOnly`                            |         4 |   5.06 ± 0.04 |      22.67 ± 0.34 |    4.48× |    0 / 0 |
| `time.pg`              | `PgTime / long microseconds`          |         8 |   5.19 ± 0.09 |      21.30 ± 0.45 |    4.10× |    0 / 0 |
| `time.clr`             | `TimeOnly`                            |         8 |   5.07 ± 0.07 |      22.57 ± 0.31 |    4.45× |    0 / 0 |
| `timetz`               | `PgTimeTz / DateTimeOffset`           |        12 |   6.68 ± 0.11 |      23.04 ± 0.22 |    3.45× |    0 / 0 |
| `timestamp.pg`         | `PgTimestamp / long microseconds`     |         8 |   5.43 ± 0.03 |      21.24 ± 0.15 |    3.91× |    0 / 0 |
| `timestamp.clr`        | `DateTime Unspecified`                |         8 |   6.02 ± 0.11 |      21.23 ± 0.09 |    3.53× |    0 / 0 |
| `timestamptz.pg`       | `PgTimestampTz / long microseconds`   |         8 |   5.35 ± 0.05 |      23.73 ± 0.34 |    4.44× |    0 / 0 |
| `timestamptz.clr`      | `DateTimeOffset UTC`                  |         8 |   8.41 ± 0.07 |      26.03 ± 0.53 |    3.10× |    0 / 0 |
| `timestamptz.datetime` | `DateTime UTC`                        |         8 |   6.74 ± 0.04 |      23.18 ± 0.25 |    3.44× |    0 / 0 |
| `interval.pg`          | `PgInterval / NpgsqlInterval`         |        16 |   6.76 ± 0.03 |      25.07 ± 0.14 |    3.71× |    0 / 0 |
| `interval.clr`         | `TimeSpan`                            |        16 |   6.98 ± 0.06 |      24.65 ± 0.56 |    3.53× |    0 / 0 |
| `inet`                 | `PgInet / NpgsqlInet (IPv4 + IPv6)`   |        20 |   7.45 ± 0.14 |      35.12 ± 0.49 |    4.71× |    0 / 0 |
| `cidr`                 | `PgInet / IPNetwork (IPv4 + IPv6)`    |        20 |  13.70 ± 0.18 |      35.25 ± 0.22 |    2.57× |    0 / 0 |
| `bytea`                | `ReadOnlyMemory<byte> (64 B)`         |        64 |   9.05 ± 0.04 |      28.99 ± 0.37 |    3.20× |    0 / 0 |
| `jsonb`                | `Memory<byte> / byte[] UTF-8`         |        65 |  10.47 ± 0.21 |      45.69 ± 0.77 |    4.36× |    0 / 0 |
| `text`                 | `string UTF-8`                        |        56 |  20.81 ± 0.12 |      54.95 ± 0.39 |    2.64× |    0 / 0 |
| `varchar`              | `string UTF-8`                        |        56 |  20.95 ± 0.35 |      54.69 ± 0.53 |    2.61× |    0 / 0 |
| `bpchar`               | `string UTF-8`                        |        59 |  23.77 ± 0.15 |      57.75 ± 0.56 |    2.43× |    0 / 0 |
| `name`                 | `string UTF-8`                        |        24 |  19.14 ± 0.08 |      53.36 ± 0.13 |    2.79× |    0 / 0 |
| `json`                 | `string UTF-8`                        |        64 |  21.86 ± 0.04 |      56.95 ± 0.21 |    2.61× |    0 / 0 |
| `xml`                  | `string UTF-8`                        |        54 |  21.38 ± 0.19 |      57.17 ± 0.94 |    2.67× |    0 / 0 |

### Arrays without NULLs — read (µs/op)

| Profile                 | Representation M/N                    | Payload B |       Mpgsql |           Npgsql |     N/M |        B/op M/N |
|-------------------------|---------------------------------------|----------:|-------------:|-----------------:|--------:|----------------:|
| `bool.array`            | `bool`                                |      1300 |  0.44 ± 0.10 |     13.69 ± 1.29 |  31.30× |       280 / 280 |
| `int2.array`            | `short`                               |      1556 |  0.13 ± 0.01 |     11.62 ± 0.21 |  92.64× |       536 / 536 |
| `int4.array`            | `int`                                 |      2068 |  0.13 ± 0.02 |    18.83 ± 12.11 | 141.25× |     1048 / 1048 |
| `int8.array`            | `long`                                |      3092 |  0.20 ± 0.00 |     11.67 ± 0.18 |  59.62× |     2072 / 2072 |
| `float4.array`          | `float`                               |      2068 |  0.12 ± 0.00 |     11.39 ± 0.15 |  91.42× |     1048 / 1048 |
| `float8.array`          | `double`                              |      3092 |  0.20 ± 0.00 |     11.27 ± 0.08 |  56.23× |     2072 / 2072 |
| `oid.array`             | `uint`                                |      2068 |  0.13 ± 0.00 |     11.51 ± 0.09 |  88.43× |     1048 / 1048 |
| `money.array`           | `long cents / decimal`                |      3092 |  0.21 ± 0.00 |     13.03 ± 0.23 |  63.55× |     2072 / 4120 |
| `uuid.array`            | `Guid`                                |      5140 |  0.45 ± 0.01 |     12.25 ± 0.16 |  27.00× |     4120 / 4120 |
| `numeric.pg.array`      | `PgNumeric / decimal`                 |      5140 |  3.61 ± 0.09 |     27.10 ± 0.59 |   7.51× |    14360 / 4120 |
| `numeric.decimal.array` | `decimal`                             |      5140 | 11.39 ± 0.08 |     27.00 ± 0.91 |   2.37× |     4120 / 4120 |
| `numeric.wide.array`    | `PgNumeric / BigInteger (128 digits)` |     68628 |  7.21 ± 0.57 | 2079.59 ± 109.07 | 288.31× | 77848 / 8866696 |
| `date.pg.array`         | `PgDate / int days`                   |      2068 |  0.41 ± 0.01 |     11.24 ± 0.16 |  27.52× |     1048 / 1048 |
| `date.clr.array`        | `DateOnly`                            |      2068 |  0.52 ± 0.01 |     11.67 ± 0.18 |  22.61× |     1048 / 1048 |
| `time.pg.array`         | `PgTime / long microseconds`          |      3092 |  0.37 ± 0.01 |     11.43 ± 0.33 |  30.85× |     2072 / 2072 |
| `time.clr.array`        | `TimeOnly`                            |      3092 |  0.43 ± 0.01 |     11.53 ± 0.23 |  26.60× |     2072 / 2072 |
| `timetz.array`          | `PgTimeTz / DateTimeOffset`           |      4116 |  0.59 ± 0.02 |     12.48 ± 0.20 |  21.19× |     4120 / 4120 |
| `timestamp.pg.array`    | `PgTimestamp / long microseconds`     |      3092 |  0.46 ± 0.01 |     11.22 ± 0.12 |  24.20× |     2072 / 2072 |
| `timestamp.clr.array`   | `DateTime Unspecified`                |      3092 |  0.68 ± 0.01 |     12.44 ± 0.20 |  18.40× |     2072 / 2072 |
| `timestamptz.pg.array`  | `PgTimestampTz / long microseconds`   |      3092 |  0.47 ± 0.01 |     11.61 ± 0.12 |  24.93× |     2072 / 2072 |
| `timestamptz.clr.array` | `DateTimeOffset UTC`                  |      3092 |  0.86 ± 0.01 |     12.44 ± 0.16 |  14.49× |     4120 / 4120 |
| `interval.pg.array`     | `PgInterval / NpgsqlInterval`         |      5140 |  0.61 ± 0.02 |     12.05 ± 0.14 |  19.62× |     4120 / 4120 |
| `interval.clr.array`    | `TimeSpan`                            |      5140 |  0.99 ± 0.01 |     12.09 ± 0.10 |  12.25× |     2072 / 2072 |
| `inet.array`            | `PgInet / NpgsqlInet (IPv4 + IPv6)`   |      4628 |  0.99 ± 0.05 |     13.35 ± 0.25 |  13.55× |    8216 / 19480 |
| `cidr.array`            | `PgInet / IPNetwork (IPv4 + IPv6)`    |      4628 |  1.29 ± 0.01 |     14.57 ± 0.14 |  11.32× |    8216 / 19480 |
| `bytea.array`           | `ReadOnlyMemory<byte> (64 B)`         |     17428 |  2.39 ± 0.19 |     15.10 ± 0.22 |   6.31× |   26648 / 26648 |
| `bytea.bytes.array`     | `byte[] (64 B)`                       |     17428 |  2.49 ± 0.05 |     12.91 ± 0.20 |   5.18× |   24600 / 24600 |
| `jsonb.array`           | `Memory<byte> / byte[] UTF-8`         |     18086 |  2.32 ± 0.09 |     17.71 ± 0.27 |   7.62× |   28616 / 26568 |
| `text.array`            | `string UTF-8`                        |     15380 |  8.17 ± 0.22 |     20.54 ± 0.34 |   2.51× |   34840 / 34840 |
| `varchar.array`         | `string UTF-8`                        |     15380 |  8.32 ± 0.28 |     21.41 ± 0.34 |   2.57× |   34840 / 34840 |
| `bpchar.array`          | `string UTF-8`                        |     16148 |  7.71 ± 0.16 |     21.31 ± 1.28 |   2.76× |   34840 / 34840 |
| `name.array`            | `string UTF-8`                        |      7188 |  8.76 ± 0.20 |     19.31 ± 0.34 |   2.20× |   18456 / 18456 |
| `json.array`            | `string UTF-8`                        |     17830 |  9.20 ± 0.97 |     22.06 ± 0.54 |   2.40× |   38936 / 38936 |
| `xml.array`             | `string UTF-8`                        |     14868 |  7.46 ± 0.13 |     23.47 ± 3.64 |   3.15× |   32792 / 32792 |

### Arrays without NULLs — write (µs/op)

| Profile                 | Representation M/N                    | Payload B |      Mpgsql |          Npgsql |      N/M |  B/op M/N |
|-------------------------|---------------------------------------|----------:|------------:|----------------:|---------:|----------:|
| `bool.array`            | `bool`                                |      1300 | 0.29 ± 0.00 |     2.71 ± 0.03 |    9.28× |    0 / 80 |
| `int2.array`            | `short`                               |      1556 | 0.05 ± 0.00 |     2.67 ± 0.03 |   57.89× |    0 / 80 |
| `int4.array`            | `int`                                 |      2068 | 0.06 ± 0.00 |     2.70 ± 0.04 |   44.40× |    0 / 80 |
| `int8.array`            | `long`                                |      3092 | 0.11 ± 0.00 |     2.75 ± 0.01 |   25.94× |    0 / 80 |
| `float4.array`          | `float`                               |      2068 | 0.07 ± 0.00 |     2.71 ± 0.04 |   40.45× |    0 / 80 |
| `float8.array`          | `double`                              |      3092 | 0.10 ± 0.00 |     2.69 ± 0.02 |   27.61× |    0 / 80 |
| `oid.array`             | `uint`                                |      2068 | 0.07 ± 0.00 |     2.67 ± 0.03 |   39.60× |    0 / 80 |
| `money.array`           | `long cents / decimal`                |      3092 | 0.10 ± 0.00 |     5.34 ± 0.03 |   55.57× |    0 / 80 |
| `uuid.array`            | `Guid`                                |      5140 | 0.56 ± 0.01 |     3.92 ± 0.05 |    6.95× |    0 / 80 |
| `numeric.pg.array`      | `PgNumeric / decimal`                 |      5140 | 3.13 ± 0.03 |    23.13 ± 0.28 |    7.38× | 0 / 12368 |
| `numeric.decimal.array` | `decimal`                             |      5140 | 7.62 ± 0.07 |    24.15 ± 0.31 |    3.17× | 0 / 12368 |
| `numeric.wide.array`    | `PgNumeric / BigInteger (128 digits)` |     68628 | 4.24 ± 0.02 | 9521.96 ± 61.51 | 2247.34× | 0 / 75856 |
| `date.pg.array`         | `PgDate / int days`                   |      2068 | 0.27 ± 0.00 |     2.69 ± 0.03 |   10.07× |    0 / 80 |
| `date.clr.array`        | `DateOnly`                            |      2068 | 0.29 ± 0.00 |     2.69 ± 0.01 |    9.25× |    0 / 80 |
| `time.pg.array`         | `PgTime / long microseconds`          |      3092 | 0.26 ± 0.00 |     2.67 ± 0.01 |   10.16× |    0 / 80 |
| `time.clr.array`        | `TimeOnly`                            |      3092 | 0.40 ± 0.00 |     2.69 ± 0.02 |    6.65× |    0 / 80 |
| `timetz.array`          | `PgTimeTz / DateTimeOffset`           |      4116 | 0.46 ± 0.01 |     3.28 ± 0.03 |    7.14× |    0 / 80 |
| `timestamp.pg.array`    | `PgTimestamp / long microseconds`     |      3092 | 0.27 ± 0.00 |     2.68 ± 0.02 |   10.04× |    0 / 80 |
| `timestamp.clr.array`   | `DateTime Unspecified`                |      3092 | 0.61 ± 0.01 |     2.79 ± 0.04 |    4.57× |    0 / 80 |
| `timestamptz.pg.array`  | `PgTimestampTz / long microseconds`   |      3092 | 0.34 ± 0.00 |     2.69 ± 0.04 |    7.91× |    0 / 80 |
| `timestamptz.clr.array` | `DateTimeOffset UTC`                  |      3092 | 0.69 ± 0.00 |     3.49 ± 0.11 |    5.07× |    0 / 80 |
| `interval.pg.array`     | `PgInterval / NpgsqlInterval`         |      5140 | 0.57 ± 0.00 |     3.34 ± 0.02 |    5.90× |    0 / 80 |
| `interval.clr.array`    | `TimeSpan`                            |      5140 | 0.93 ± 0.04 |     3.42 ± 0.02 |    3.68× |    0 / 80 |
| `inet.array`            | `PgInet / NpgsqlInet (IPv4 + IPv6)`   |      4628 | 0.93 ± 0.01 |     8.39 ± 0.06 |    9.03× |  0 / 9296 |
| `cidr.array`            | `PgInet / IPNetwork (IPv4 + IPv6)`    |      4628 | 1.88 ± 0.05 |     8.98 ± 0.16 |    4.78× |  0 / 9296 |
| `bytea.array`           | `ReadOnlyMemory<byte> (64 B)`         |     17428 | 1.59 ± 0.02 |     4.76 ± 0.09 |    2.99× |    0 / 80 |
| `bytea.bytes.array`     | `byte[] (64 B)`                       |     17428 | 1.32 ± 0.01 |     4.38 ± 0.02 |    3.31× |    0 / 80 |
| `jsonb.array`           | `Memory<byte> / byte[] UTF-8`         |     18086 | 2.14 ± 0.01 |    10.90 ± 0.06 |    5.10× |    0 / 80 |
| `text.array`            | `string UTF-8`                        |     15380 | 5.38 ± 0.13 |    10.98 ± 0.09 |    2.04× |    0 / 80 |
| `varchar.array`         | `string UTF-8`                        |     15380 | 5.31 ± 0.13 |    11.10 ± 0.07 |    2.09× |    0 / 80 |
| `bpchar.array`          | `string UTF-8`                        |     16148 | 6.00 ± 0.03 |   20.44 ± 14.36 |    3.41× |    0 / 80 |
| `name.array`            | `string UTF-8`                        |      7188 | 4.78 ± 0.03 |     9.94 ± 0.04 |    2.08× |    0 / 80 |
| `json.array`            | `string UTF-8`                        |     17830 | 5.72 ± 0.10 |   19.90 ± 14.68 |   3.48†× |    0 / 80 |
| `xml.array`             | `string UTF-8`                        |     14868 | 5.42 ± 0.04 |    10.92 ± 0.06 |    2.01× |    0 / 80 |

### Arrays with NULLs — read (µs/op)

| Profile                | Representation M/N                    | Payload B |       Mpgsql |          Npgsql |     N/M |        B/op M/N |
|------------------------|---------------------------------------|----------:|-------------:|----------------:|--------:|----------------:|
| `bool.null`            | `bool`                                |      1268 |  0.65 ± 0.03 |    10.89 ± 0.08 |  16.87× |       536 / 536 |
| `int2.null`            | `short`                               |      1492 |  0.65 ± 0.01 |    10.98 ± 0.11 |  16.90× |     1048 / 1048 |
| `int4.null`            | `int`                                 |      1940 |  0.65 ± 0.00 |    11.21 ± 0.10 |  17.37× |     2072 / 2072 |
| `int8.null`            | `long`                                |      2836 |  0.46 ± 0.00 |    11.46 ± 0.12 |  25.09× |     4120 / 4120 |
| `float4.null`          | `float`                               |      1940 |  0.65 ± 0.01 |    11.34 ± 0.10 |  17.54× |     2072 / 2072 |
| `float8.null`          | `double`                              |      2836 |  0.65 ± 0.01 |    11.27 ± 0.08 |  17.35× |     4120 / 4120 |
| `oid.null`             | `uint`                                |      1940 |  0.63 ± 0.00 |    11.42 ± 0.08 |  18.00× |     2072 / 2072 |
| `money.null`           | `long cents / decimal`                |      2836 |  0.61 ± 0.02 |    13.58 ± 0.21 |  22.14× |     4120 / 6168 |
| `uuid.null`            | `Guid`                                |      4628 |  0.77 ± 0.01 |    11.82 ± 0.33 |  15.41× |     5144 / 5144 |
| `numeric.pg.null`      | `PgNumeric / decimal`                 |      4628 |  3.35 ± 0.22 |    25.55 ± 0.20 |   7.63× |    15384 / 6168 |
| `numeric.decimal.null` | `decimal`                             |      4628 | 11.15 ± 0.23 |    29.37 ± 0.27 |   2.64× |     6168 / 6168 |
| `numeric.wide.null`    | `PgNumeric / BigInteger (128 digits)` |     60180 |  6.45 ± 0.11 | 1760.25 ± 33.22 | 272.93× | 70936 / 7760936 |
| `date.pg.null`         | `PgDate / int days`                   |      1940 |  0.67 ± 0.01 |    11.50 ± 0.10 |  17.22× |     2072 / 2072 |
| `date.clr.null`        | `DateOnly`                            |      1940 |  0.77 ± 0.01 |    11.52 ± 0.28 |  14.98× |     2072 / 2072 |
| `time.pg.null`         | `PgTime / long microseconds`          |      2836 |  0.64 ± 0.00 |    10.81 ± 0.03 |  16.76× |     4120 / 4120 |
| `time.clr.null`        | `TimeOnly`                            |      2836 |  0.64 ± 0.01 |    10.89 ± 0.07 |  16.95× |     4120 / 4120 |
| `timetz.null`          | `PgTimeTz / DateTimeOffset`           |      3732 |  0.80 ± 0.01 |    11.91 ± 0.19 |  14.91× |     6168 / 6168 |
| `timestamp.pg.null`    | `PgTimestamp / long microseconds`     |      2836 |  0.76 ± 0.06 |    11.34 ± 0.10 |  14.91× |     4120 / 4120 |
| `timestamp.clr.null`   | `DateTime Unspecified`                |      2836 |  0.84 ± 0.04 |    11.36 ± 0.14 |  13.58× |     4120 / 4120 |
| `timestamptz.pg.null`  | `PgTimestampTz / long microseconds`   |      2836 |  0.70 ± 0.01 |    11.44 ± 0.27 |  16.38× |     4120 / 4120 |
| `timestamptz.clr.null` | `DateTimeOffset UTC`                  |      2836 |  0.98 ± 0.02 |    12.66 ± 0.10 |  12.98× |     6168 / 6168 |
| `interval.pg.null`     | `PgInterval / NpgsqlInterval`         |      4628 |  0.81 ± 0.01 |    11.48 ± 0.09 |  14.11× |     6168 / 6168 |
| `interval.clr.null`    | `TimeSpan`                            |      4628 |  1.09 ± 0.02 |    11.51 ± 0.06 |  10.54× |     4120 / 4120 |
| `inet.null`            | `PgInet / NpgsqlInet (IPv4 + IPv6)`   |      4372 |  1.01 ± 0.02 |    24.63 ± 0.56 |  24.45× |   12312 / 20248 |
| `cidr.null`            | `PgInet / IPNetwork (IPv4 + IPv6)`    |      4372 |  1.22 ± 0.01 |    14.19 ± 0.16 |  11.63× |   12312 / 20248 |
| `bytea.null`           | `ReadOnlyMemory<byte> (64 B)`         |     15380 |  1.95 ± 0.09 |    14.68 ± 0.23 |   7.51× |   25880 / 25880 |
| `bytea.bytes.null`     | `byte[] (64 B)`                       |     15380 |  2.14 ± 0.07 |    12.92 ± 0.45 |   6.03× |   21784 / 21784 |
| `jsonb.null`           | `Memory<byte> / byte[] UTF-8`         |     15957 |  2.23 ± 0.11 |    16.23 ± 0.15 |   7.29× |   27608 / 23512 |
| `text.null`            | `string UTF-8`                        |     13588 |  7.30 ± 0.15 |    18.55 ± 0.27 |   2.54× |   30744 / 30744 |
| `varchar.null`         | `string UTF-8`                        |     13588 |  7.13 ± 0.15 |    19.11 ± 0.36 |   2.68× |   30744 / 30744 |
| `bpchar.null`          | `string UTF-8`                        |     14260 |  7.65 ± 0.18 |    19.37 ± 0.56 |   2.53× |   30744 / 30744 |
| `name.null`            | `string UTF-8`                        |      6420 |  5.68 ± 0.16 |    17.05 ± 0.44 |   3.00× |   16408 / 16408 |
| `json.null`            | `string UTF-8`                        |     15733 |  7.88 ± 0.20 |    20.08 ± 0.27 |   2.55× |   34328 / 34328 |
| `xml.null`             | `string UTF-8`                        |     13140 |  7.18 ± 0.48 |    18.45 ± 0.64 |   2.57× |   28952 / 28952 |

### Arrays with NULLs — write (µs/op)

| Profile                | Representation M/N                    | Payload B |      Mpgsql |          Npgsql |      N/M |  B/op M/N |
|------------------------|---------------------------------------|----------:|------------:|----------------:|---------:|----------:|
| `bool.null`            | `bool`                                |      1268 | 0.39 ± 0.00 |     4.29 ± 0.09 |   10.97× |    0 / 80 |
| `int2.null`            | `short`                               |      1492 | 0.39 ± 0.00 |     4.34 ± 0.03 |   11.05× |    0 / 80 |
| `int4.null`            | `int`                                 |      1940 | 0.37 ± 0.00 |     4.27 ± 0.04 |   11.63× |    0 / 80 |
| `int8.null`            | `long`                                |      2836 | 0.44 ± 0.00 |     4.27 ± 0.05 |    9.72× |    0 / 80 |
| `float4.null`          | `float`                               |      1940 | 0.38 ± 0.00 |     4.43 ± 0.04 |   11.75× |    0 / 80 |
| `float8.null`          | `double`                              |      2836 | 0.40 ± 0.00 |     4.38 ± 0.04 |   10.94× |    0 / 80 |
| `oid.null`             | `uint`                                |      1940 | 0.37 ± 0.01 |     4.22 ± 0.03 |   11.44× |    0 / 80 |
| `money.null`           | `long cents / decimal`                |      2836 | 0.40 ± 0.00 |     6.68 ± 0.10 |   16.72× |    0 / 80 |
| `uuid.null`            | `Guid`                                |      4628 | 0.65 ± 0.01 |     5.66 ± 0.02 |    8.74× |    0 / 80 |
| `numeric.pg.null`      | `PgNumeric / decimal`                 |      4628 | 4.01 ± 0.07 |    23.66 ± 1.95 |    5.90× | 0 / 10832 |
| `numeric.decimal.null` | `decimal`                             |      4628 | 6.79 ± 0.07 |    22.69 ± 0.25 |    3.34× | 0 / 10832 |
| `numeric.wide.null`    | `PgNumeric / BigInteger (128 digits)` |     60180 | 5.10 ± 0.05 | 8268.74 ± 86.72 | 1622.18× | 0 / 66384 |
| `date.pg.null`         | `PgDate / int days`                   |      1940 | 0.39 ± 0.01 |     4.23 ± 0.04 |   10.76× |    0 / 80 |
| `date.clr.null`        | `DateOnly`                            |      1940 | 0.38 ± 0.00 |     4.27 ± 0.03 |   11.26× |    0 / 80 |
| `time.pg.null`         | `PgTime / long microseconds`          |      2836 | 0.40 ± 0.00 |     4.19 ± 0.04 |   10.59× |    0 / 80 |
| `time.clr.null`        | `TimeOnly`                            |      2836 | 0.53 ± 0.00 |     4.21 ± 0.04 |    8.00× |    0 / 80 |
| `timetz.null`          | `PgTimeTz / DateTimeOffset`           |      3732 | 0.52 ± 0.01 |     4.87 ± 0.02 |    9.34× |    0 / 80 |
| `timestamp.pg.null`    | `PgTimestamp / long microseconds`     |      2836 | 0.40 ± 0.00 |     4.24 ± 0.03 |   10.65× |    0 / 80 |
| `timestamp.clr.null`   | `DateTime Unspecified`                |      2836 | 0.67 ± 0.01 |     4.32 ± 0.03 |    6.48× |    0 / 80 |
| `timestamptz.pg.null`  | `PgTimestampTz / long microseconds`   |      2836 | 0.40 ± 0.00 |     4.30 ± 0.09 |   10.80× |    0 / 80 |
| `timestamptz.clr.null` | `DateTimeOffset UTC`                  |      2836 | 0.71 ± 0.01 |     5.43 ± 0.04 |    7.63× |    0 / 80 |
| `interval.pg.null`     | `PgInterval / NpgsqlInterval`         |      4628 | 0.62 ± 0.00 |     5.23 ± 0.04 |    8.47× |    0 / 80 |
| `interval.clr.null`    | `TimeSpan`                            |      4628 | 0.94 ± 0.01 |     5.12 ± 0.12 |    5.46× |    0 / 80 |
| `inet.null`            | `PgInet / NpgsqlInet (IPv4 + IPv6)`   |      4372 | 0.89 ± 0.02 |     8.59 ± 0.09 |    9.60× |  0 / 8272 |
| `cidr.null`            | `PgInet / IPNetwork (IPv4 + IPv6)`    |      4372 | 1.15 ± 0.02 |     9.47 ± 0.67 |    8.26× |  0 / 8272 |
| `bytea.null`           | `ReadOnlyMemory<byte> (64 B)`         |     15380 | 1.66 ± 0.07 |     5.53 ± 0.02 |    3.33× |    0 / 80 |
| `bytea.bytes.null`     | `byte[] (64 B)`                       |     15380 | 1.21 ± 0.01 |     4.98 ± 2.03 |    4.11× |    0 / 80 |
| `jsonb.null`           | `Memory<byte> / byte[] UTF-8`         |     15957 | 2.03 ± 0.01 |     9.92 ± 0.13 |    4.88× |    0 / 80 |
| `text.null`            | `string UTF-8`                        |     13588 | 4.65 ± 0.01 |    10.50 ± 0.33 |    2.26× |    0 / 80 |
| `varchar.null`         | `string UTF-8`                        |     13588 | 4.69 ± 0.06 |    10.40 ± 0.12 |    2.22× |    0 / 80 |
| `bpchar.null`          | `string UTF-8`                        |     14260 | 6.10 ± 0.61 |    14.84 ± 5.82 |    2.43× |    0 / 80 |
| `name.null`            | `string UTF-8`                        |      6420 | 4.29 ± 0.04 |   17.31 ± 14.49 |   4.04†× |    0 / 80 |
| `json.null`            | `string UTF-8`                        |     15733 | 5.20 ± 0.07 |    11.12 ± 0.04 |    2.14× |    0 / 80 |
| `xml.null`             | `string UTF-8`                        |     13140 | 5.02 ± 0.17 |    10.41 ± 0.04 |    2.07× |    0 / 80 |

Reproduction commands and the result-table generator are
in [the converter benchmark guide](../../benchmarks/Mpgsql.Benchmarks/Converters/README.md).
The [machine-readable snapshot](../../benchmarks/Mpgsql.Benchmarks/Converters/results-2026-10-08.csv) retains unrounded
means, errors, and allocations. Full measurement JSON, CSV, logs, coverage metadata, and assembly hashes are saved
locally under `artifacts/converter-comparison/`.
<!-- converter-benchmarks:end -->
