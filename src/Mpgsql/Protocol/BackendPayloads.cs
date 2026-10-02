using System.Buffers;

namespace Mpgsql.Protocol;

public readonly record struct AuthenticationRequest
(
    AuthenticationMethod Method,
    ReadOnlySequence<byte> Data,
    ReadOnlyMemory<string> Mechanisms
);

public readonly record struct BackendKeyData(int ProcessId, int SecretKey);

public readonly record struct ParameterStatus(string Name, string Value);

public readonly record struct NotificationResponse(int ProcessId, string Channel, string Payload);

/// <summary>Wire fields of one RowDescription column, in their protocol order.</summary>
public readonly record struct RowField
(
    string Name,
    uint TableOid,
    short AttributeNumber,
    uint DataTypeOid,
    short DataTypeSize,
    int TypeModifier,
    FormatCode Format
);

public readonly record struct CopyResponse(FormatCode Format, ReadOnlyMemory<FormatCode> ColumnFormats);

public readonly record struct ProtocolVersionNegotiation(int MinorVersion, ReadOnlyMemory<string> UnrecognizedOptions);

public readonly record struct DiagnosticField(byte Code, string Value);

/// <summary>Error/notice fields, retaining their order and any unrecognized field codes.</summary>
public readonly struct DiagnosticMessage(ReadOnlyMemory<DiagnosticField> fields)
{
    public ReadOnlyMemory<DiagnosticField> Fields { get; } = fields;
    public string? Severity => GetField((byte)'S');
    public string? InvariantSeverity => GetField((byte)'V');
    public string? SqlState => GetField((byte)'C');
    public string? Message => GetField((byte)'M');

    public string? GetField(byte code)
    {
        foreach (var field in Fields.Span)
            if (field.Code == code)
            {
                return field.Value;
            }
        return null;
    }
}

/// <summary>A borrowed DataRow. Values remain segmented; null and empty are distinct.</summary>
public readonly struct DataRow
{
    private readonly ReadOnlySequence<byte> _values;
    public int Count { get; }

    internal DataRow(int count,
        ReadOnlySequence<byte> values)
    {
        Count = count;
        _values = values;
    }

    public Enumerator GetEnumerator() => new(Count,
        _values);

    public ref struct Enumerator
    {
        private WireReader _reader;
        private int _remaining;
        public ReadOnlySequence<byte>? Current { get; private set; }

        internal Enumerator(int count,
            ReadOnlySequence<byte> values)
        {
            _reader = new WireReader(values);
            _remaining = count;
            Current = null;
        }

        public bool MoveNext()
        {
            if (_remaining == 0)
            {
                Current = null;
                return false;
            }
            Current = _reader.Value();
            _remaining--;
            return true;
        }
    }
}