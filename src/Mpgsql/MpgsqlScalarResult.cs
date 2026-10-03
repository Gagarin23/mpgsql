namespace Mpgsql;

/// <summary>Distinguishes no row, SQL NULL, and a value. The default value means no row.</summary>
public readonly struct MpgsqlScalarResult<T>
{
    private readonly T? _value;
    public bool HasRow { get; }
    public bool IsNull { get; }
    public T Value => HasRow && !IsNull
        ? _value!
        : throw new InvalidOperationException("The scalar result has no value.");

    internal MpgsqlScalarResult(bool isNull, T? value)
        => (HasRow, IsNull, _value) = (true, isNull, value);
}
