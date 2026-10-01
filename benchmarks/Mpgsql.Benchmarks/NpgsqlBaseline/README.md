# Npgsql 10.0.3 bigint[] baseline

The dependency is confined to the benchmark executable. The Mpgsql library does
not reference Npgsql.

`Copied/` contains the array/scalar converter sources copied from Npgsql 10.0.3
(NuGet repository commit `d3768398c17877b3a916c3c4d87e8e11698991fc`). Each copied
file retains the full upstream copyright and license notice. Types are split into
their own files. The existing adaptations from `C:/Projects/NpgsqlConverters`
were reused and checked against the version-pinned upstream sources:

- [ArrayConverter.cs](https://github.com/npgsql/npgsql/blob/d3768398c17877b3a916c3c4d87e8e11698991fc/src/Npgsql/Internal/Converters/ArrayConverter.cs):
  `PgArrayConverter`, `ArrayConverter<T>`, `ArrayBasedArrayConverter<T,TElement>`,
  `WriteState`, `Indices`, `IndicesExtensions` and `IElementOperations`.
- [Int8Converter.cs](https://github.com/npgsql/npgsql/blob/d3768398c17877b3a916c3c4d87e8e11698991fc/src/Npgsql/Internal/Converters/Primitive/Int8Converter.cs):
  `Int8Converter<long>`.
- [PgConverter.cs](https://github.com/npgsql/npgsql/blob/d3768398c17877b3a916c3c4d87e8e11698991fc/src/Npgsql/Internal/PgConverter.cs):
  the `GetSizeOrDbNull` helper in `ConverterCompatibilityExtensions`.
- `MultiWriteState` supports the upstream write-state lifetime. Variable-size
  element converters are not instantiated by these benchmarks.

Adaptations: namespace; internal `ThrowHelper` calls replaced by equivalent
throws; the internal bool-async reader/writer overloads use public sync/async
overloads; internal async-completion helpers use ordinary `ValueTask`
continuations. List, resolver and polymorphic converter implementations are
omitted. The synchronous scalar/array loops, nested scopes, fixed-size state and
element dispatch remain intact.

`NpgsqlArrayHarness.Original` independently creates the actual internal
`ArrayBasedArrayConverter<long[],long>` and `Int8Converter<long>` from the
**Npgsql 10.0.3 assembly**, so the copied implementation is checked against both
the upstream wire bytes and upstream performance. The `NpgsqlOriginal` benchmark
is the baseline for ratios, not the adapted copy.

Reflection and expression compilation happen only during setup. Timed lifecycle
calls use `UnsafeAccessor`; buffer reset/initialization delegates are compiled
once. Construction of the data source/connector never opens a database connection.
`--verify` compares full array payloads, prepared state reuse, sync/async paths,
owned results and cross-decoding at scalar/SIMD/reader-buffer boundaries.

Write benchmarks share one preallocated `IBufferWriter<byte>`: no socket, stream,
output-buffer allocation or copy of the completed payload is measured. Both
size-and-write and prepared-write paths include their respective converter's
buffer/field lifecycle. Nonempty payloads match byte for byte. Npgsql emits a
20-byte one-dimensional empty array; Mpgsql emits the canonical 12-byte
zero-dimensional empty array. Both readers accept both encodings.

Read benchmarks return owned storage in all three cases. `ReaderBufferSize=0`
preloads Npgsql's buffer once in setup; timed reads do not copy/refill the input.
Mpgsql reads the same payload as a single `ReadOnlySequence<byte>`. With
`ReaderBufferSize=8192`, Npgsql refills its buffer from a reusable `MemoryStream`,
whereas Mpgsql consumes a sequence of existing 8192-byte segments. That case
includes Npgsql's input-copy/refill costs and is not an isolated algorithm-only
comparison. It is not a network/database throughput benchmark.

```powershell
dotnet run --project benchmarks/Mpgsql.Benchmarks -c Release -- --verify
dotnet run --project benchmarks/Mpgsql.Benchmarks -c Release -- --filter '*NpgsqlLongArray*' --artifacts artifacts/npgsql-long-array
```

## Binary COPY

`NpgsqlCopyHarness` runs the **actual Npgsql 10.0.3 importer/exporter** from the
package, without copying/reimplementing their element loops. Reflection constructs
the connector, minimal builtin catalog, resolver chain and memory buffers only in
setup. Compiled delegates reset the fixture outside each element loop; timed
methods call public `StartRow`, `Write<T>(NpgsqlDbType)` and `Read<T>(NpgsqlDbType)`.

Memory import writes the same header/trailer/buffer operations as
[`NpgsqlBinaryImporter.Complete`](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/NpgsqlBinaryImporter.cs),
but excludes COPY SQL, connection startup, CopyDone and server responses. Both
writers frame data into a preallocated MemoryStream. Parameter/type caches are
warm; upstream's per-row Bind/write-state allocations are included.

Memory export receives identical PostgreSQL-style frames in both implementations:
header plus first row, a CopyData per subsequent row, trailer, CopyDone,
CommandComplete and ReadyForQuery. Importer packets may group several rows and
cannot be fed directly to the
[`NpgsqlBinaryExporter`](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/NpgsqlBinaryExporter.cs).
The whole input is preloaded once in Npgsql's read buffer in setup; both timed
paths include backend framing and return owned array results. The fixture reuses
upstream importer/exporter objects, whereas Mpgsql creates its small operation
objects each time. There is no reflection/expression compilation in timed code.

`BinaryCopyBenchmarks --verify` cross-checks complete stream bytes and results for
0/1/64/4096 rows, scalar bigint and bigint[256]. The live `--copy-live` runner uses
ordinary Npgsql BeginBinaryImport/BeginBinaryExport and Complete, with real server
negotiation and response boundaries. Mpgsql uses its own codecs/state and
benchmark-only socket/authentication helpers. Counts and checksums are checked.
It creates only connection-local temporary tables and never saves credentials.

See [binary-copy.md](../../../docs/binary-copy.md) for scope, commands and measured
results. Npgsql is not a library dependency.
