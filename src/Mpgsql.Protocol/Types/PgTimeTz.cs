namespace Mpgsql.Types;

/// <summary>Local PostgreSQL time plus a UTC offset in seconds east of UTC.</summary>
/// <remarks>The converter reverses the offset's sign for PostgreSQL's seconds-west wire field.</remarks>
public readonly record struct PgTimeTz(PgTime Time, int OffsetSeconds);