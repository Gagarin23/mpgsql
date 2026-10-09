namespace Mpgsql;

/// <summary>Materializes one row during a typed portion read.</summary>
/// <remarks>Implement as a struct. Returned records must own any retained data;
/// borrowed field bytes are valid only during this call.</remarks>
public interface IMpgsqlRowMapper<T>
{
    T Read(MpgsqlRow row);
}
