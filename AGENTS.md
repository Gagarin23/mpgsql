# Mpgsql contributor guide

## Project intent

Mpgsql is a standalone PostgreSQL driver written in C#. It is not an Npgsql
adapter or extension. The library is designed around request multiplexing and
PostgreSQL pipeline mode, while retaining control over the Extended Query
Protocol for advanced callers.

The repository is an early .NET 10 library. Keep changes narrow, prove each
protocol path end-to-end, and do not introduce general-purpose abstractions
without a concrete use in the driver.

## Repository layout

- `src/Mpgsql/` contains protocol, converters, PostgreSQL types, and binary COPY;
  it targets `net10.0` and produces the `Mpgsql` NuGet package.
- `src/Mpgsql.Client/` contains sessions, query helpers, scheduling, and the upper
  API; it references `Mpgsql` and is not packed.
- `src/Mpgsql.slnx` is the solution entry point.
- `docs/ideas.md` holds goals and unresolved product decisions.
- `docs/extended-query-protocol.md` is the living description of Extended Query
  behavior; update it when a protocol decision changes.

## Design constraints

- Allow callers to construct raw Extended Query messages when they need
  low-level control. Add factory methods for common messages without removing
  that capability.
- Design connection and scheduling code with multiplexed, pipelined requests in
  mind. Do not silently assume one active request per physical connection.
- High performance is a mandatory, primary requirement of this project.
  Do not add content-validation scans (`Contains`, UTF-8 prevalidation, or
  separate array walks) to hot paths when the server validates the value or
  decoding already performs the necessary work. Keep checks required for buffer
  bounds, protocol framing, and explicit API contracts; combine necessary
  element checks with existing size, encoding, or decoding loops where possible.
  Make performance claims measurable: identify removed scans, allocations,
  copies, or contention and benchmark complex or `unsafe` alternatives before
  retaining them.
- Use modern idiomatic C#. Nullable reference types and implicit usings are
  enabled; maintain nullable correctness in new code.

## PostgreSQL protocol rules

- The PostgreSQL documentation is authoritative for wire behavior. State field
  order, length prefixes, text encoding, and endianness explicitly in code and
  tests.
- Default to frontend/backend protocol 3.0 compatibility. Negotiate and test a
  later protocol feature before depending on it.
- Model Extended Query state transitions explicitly: `Parse`, `Bind`,
  `Describe`, `Execute`, and `Sync` each have distinct responses and lifecycle
  effects.
- On an `ErrorResponse`, recover only through the `Sync` / `ReadyForQuery`
  boundary; do not continue treating skipped command confirmations as valid.
- The reader loop must accept asynchronous or interleaved messages such as
  `NoticeResponse`, `ParameterStatus`, and `NotificationResponse` without
  losing the command-response state. Never run user callbacks synchronously on
  the network reader loop.
- Treat COPY as a separate connection subprotocol, not as ordinary row flow.

## Testing and validation

- Add focused unit tests with every message encoder or parser. Encoder tests
  should assert the complete byte payload, including type tag and length.
- Keep unit tests independent of a local database. Add integration tests only
  where a live PostgreSQL server is essential.
- Before handing off code changes, run `dotnet build src/Mpgsql.slnx` and the
  relevant test project(s) once they exist.

## Scope and hygiene

- Put unresolved architectural or protocol questions in `docs/` rather than
  silently committing a broad design decision.
- Keep public APIs small and intentional. Do not add Npgsql-compatibility
  shims unless that becomes an explicit requirement.
- Do not commit generated `bin/`, `obj/`, or IDE workspace files.
