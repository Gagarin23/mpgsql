using System.Buffers;

namespace Mpgsql.Protocol;

/// <summary>Reads protocol 3.0 backend frames without consuming incomplete or invalid input.</summary>
public static class BackendMessageReader
{
    /// <summary>Default maximum Int32 wire length, including the length prefix but excluding the tag.</summary>
    public const int DefaultMaxMessageLength = 64 * 1024 * 1024;

    /// <remarks>
    /// False means more bytes are needed. Invalid complete bodies or invalid length headers throw
    /// InvalidDataException. The returned message borrows input; use it before releasing Pipe buffers.
    /// This method is stateless and does not assign responses to commands or requests.
    /// </remarks>
    public static bool TryRead(
        ref ReadOnlySequence<byte> input,
        out BackendMessage message,
        int maxMessageLength = DefaultMaxMessageLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessageLength,
            4);
        message = default;
        var header = new SequenceReader<byte>(input);
        if (!header.TryRead(out byte type) || !header.TryReadBigEndian(out int length))
        {
            return false;
        }
        if (length < 4 || length > maxMessageLength)
        {
            throw new InvalidDataException($"Invalid PostgreSQL message length: {length}.");
        }
        if (header.Remaining < length - 4)
        {
            return false;
        }

        var payload = input.Slice(header.Position,
            length - 4);
        var kind = Validate(type,
            payload,
            default,
            indexRows: false,
            out int rowCount);
        message = new BackendMessage(type,
            kind,
            payload,
            rowCount);
        input = input.Slice(1L + length);
        return true;
    }

    /// <summary>Validates and indexes DataRow values once using caller-owned reusable storage.</summary>
    /// <remarks>
    /// Storage must fit the row's column count. Indexed rows borrow both input and this storage;
    /// consume them before releasing input or reusing storage. Other message types ignore storage.
    /// </remarks>
    public static bool TryRead(
        ref ReadOnlySequence<byte> input,
        Memory<ReadOnlySequence<byte>?> rowValues,
        out BackendMessage message,
        out IndexedDataRow row,
        int maxMessageLength = DefaultMaxMessageLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessageLength,
            4);
        message = default;
        row = default;
        var header = new SequenceReader<byte>(input);
        if (!header.TryRead(out byte type) || !header.TryReadBigEndian(out int length))
        {
            return false;
        }
        if (length < 4 || length > maxMessageLength)
        {
            throw new InvalidDataException($"Invalid PostgreSQL message length: {length}.");
        }
        if (header.Remaining < length - 4)
        {
            return false;
        }

        var payload = input.Slice(header.Position,
            length - 4);
        var kind = Validate(type,
            payload,
            rowValues.Span,
            indexRows: true,
            out int rowCount);
        message = new BackendMessage(type,
            kind,
            payload,
            rowCount);
        if (kind == BackendMessageKind.DataRow)
        {
            row = new IndexedDataRow(rowValues[..rowCount]);
        }
        input = input.Slice(1L + length);
        return true;
    }

    /// <summary>
    /// Reads the single unframed reply to SSLRequest (S/N) or GSSENCRequest (G/N).
    /// Call only in the corresponding startup negotiation phase, before reading normal frames.
    /// </summary>
    public static bool TryReadEncryptionResponse(
        ref ReadOnlySequence<byte> input,
        EncryptionRequestKind request,
        out bool accepted)
    {
        if (request is not (EncryptionRequestKind.Ssl or EncryptionRequestKind.Gss))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        accepted = false;
        var reader = new SequenceReader<byte>(input);
        if (!reader.TryRead(out byte response))
        {
            return false;
        }
        byte affirmative = request == EncryptionRequestKind.Ssl ? (byte)'S' : (byte)'G';
        if (response != affirmative && response != (byte)'N')
        {
            throw new InvalidDataException("Unexpected PostgreSQL encryption negotiation response.");
        }
        accepted = response == affirmative;
        input = input.Slice(reader.Position);
        return true;
    }

    private static BackendMessageKind Validate(byte type,
        ReadOnlySequence<byte> payload,
        Span<ReadOnlySequence<byte>?> rowValues,
        bool indexRows,
        out int rowCount)
    {
        // Known enum values are the wire tags: one dispatch performs classification and validation.
        var kind = (BackendMessageKind)type;
        rowCount = 0;
        var reader = new WireReader(payload);
        switch (kind)
        {
            case BackendMessageKind.CopyData:
                reader.Rest();
                break;
            case BackendMessageKind.Authentication:
                ValidateAuthentication(ref reader);
                break;
            case BackendMessageKind.BackendKeyData:
                reader.Int32(); // PID, then the 32-bit secret key in protocol 3.0.
                reader.Int32();
                break;
            case BackendMessageKind.CommandComplete:
                reader.CStringBytes();
                break;
            case BackendMessageKind.ReadyForQuery:
                if ((TransactionStatus)reader.Byte() is not
                    (TransactionStatus.Idle or TransactionStatus.InTransaction or TransactionStatus.FailedTransaction))
                {
                    throw new InvalidDataException("Unknown PostgreSQL transaction status.");
                }
                break;
            case BackendMessageKind.ParameterStatus:
                reader.CStringBytes(); // name, value
                reader.CStringBytes();
                break;
            case BackendMessageKind.NotificationResponse:
                reader.Int32(); // notifying PID, channel, payload
                reader.CStringBytes();
                reader.CStringBytes();
                break;
            case BackendMessageKind.ParameterDescription:
                int parameterCount = reader.Count();
                reader.RequireElements(parameterCount,
                    4);
                reader.Bytes(parameterCount * 4);
                break;
            case BackendMessageKind.RowDescription:
                ValidateRowDescription(ref reader);
                break;
            case BackendMessageKind.DataRow:
                rowCount = ValidateDataRow(ref reader,
                    rowValues,
                    indexRows);
                break;
            case BackendMessageKind.FunctionCallResponse:
                reader.SkipValue();
                break;
            case BackendMessageKind.CopyInResponse:
            case BackendMessageKind.CopyOutResponse:
            case BackendMessageKind.CopyBothResponse:
                ValidateCopyResponse(ref reader);
                break;
            case BackendMessageKind.ErrorResponse:
            case BackendMessageKind.NoticeResponse:
                while (reader.Byte() != 0)
                    reader.CStringBytes(); // Preserve unknown field identifiers in typed accessors.
                break;
            case BackendMessageKind.NegotiateProtocolVersion:
                if (reader.Int32() < 0)
                {
                    throw new InvalidDataException("Invalid PostgreSQL minor protocol version.");
                }
                int optionCount = reader.Int32();
                reader.RequireElements(optionCount,
                    1);
                for (int i = 0; i < optionCount; i++)
                    reader.CStringBytes();
                break;
            case BackendMessageKind.ParseComplete:
            case BackendMessageKind.BindComplete:
            case BackendMessageKind.CloseComplete:
            case BackendMessageKind.EmptyQueryResponse:
            case BackendMessageKind.NoData:
            case BackendMessageKind.PortalSuspended:
            case BackendMessageKind.CopyDone:
                break; // These packets must have an empty body.
            default:
                kind = BackendMessageKind.Unknown;
                reader.Rest(); // Preserve unknown, length-delimited bodies.
                break;
        }
        reader.End();
        return kind;
    }

    private static int ValidateDataRow(ref WireReader reader,
        Span<ReadOnlySequence<byte>?> rowValues,
        bool indexRows)
    {
        int count = reader.Count();
        reader.RequireElements(count,
            4);
        if (indexRows)
        {
            if (rowValues.Length < count)
            {
                throw new ArgumentException("DataRow storage is smaller than the column count.",
                    nameof(rowValues));
            }
            for (int i = 0; i < count; i++)
                rowValues[i] = reader.Value();
        }
        else
        {
            for (int i = 0; i < count; i++)
                reader.SkipValue();
        }
        return count;
    }

    private static void ValidateRowDescription(ref WireReader reader)
    {
        int count = reader.Count();
        reader.RequireElements(count,
            19); // NUL and the 18 fixed bytes of each field.
        for (int i = 0; i < count; i++)
        {
            reader.CStringBytes();
            reader.UInt32(); // table OID
            reader.Int16(); // attribute number; negative for system columns
            reader.UInt32(); // type OID
            reader.Int16(); // type size; negative for variable-width types
            reader.Int32(); // type modifier
            reader.Format();
        }
    }

    private static void ValidateCopyResponse(ref WireReader reader)
    {
        var format = (FormatCode)reader.Byte();
        if (format is not (FormatCode.Text or FormatCode.Binary))
        {
            throw new InvalidDataException("Unknown PostgreSQL COPY format.");
        }
        int count = reader.Count();
        reader.RequireElements(count,
            2);
        for (int i = 0; i < count; i++)
            if (reader.Format() == FormatCode.Binary && format == FormatCode.Text)
            {
                throw new InvalidDataException("Text COPY requires text column formats.");
            }
    }

    private static void ValidateAuthentication(ref WireReader reader)
    {
        var method = (AuthenticationMethod)reader.Int32();
        switch (method)
        {
            case AuthenticationMethod.Ok:
            case AuthenticationMethod.KerberosV5:
            case AuthenticationMethod.CleartextPassword:
            case AuthenticationMethod.Gss:
            case AuthenticationMethod.Sspi:
                break;
            case AuthenticationMethod.Md5Password:
                reader.Bytes(4);
                break;
            case AuthenticationMethod.Sasl:
                int count = 0;
                while (!reader.CStringBytes().IsEmpty)
                    count++;
                if (count == 0)
                {
                    throw new InvalidDataException("No SASL authentication mechanisms were offered.");
                }
                break;
            // GSS/SASL continuation data and unrecognized method codes are opaque.
            default:
                reader.Rest();
                break;
        }
    }
}