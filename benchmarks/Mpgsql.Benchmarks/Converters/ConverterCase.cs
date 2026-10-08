using System.Buffers;
using System.Buffers.Binary;
using Mpgsql.Benchmarks.NpgsqlBaseline;

namespace Mpgsql.Benchmarks.Converters;

internal abstract class ConverterCase : IDisposable
{
    internal abstract int MpgsqlWrite();
    internal abstract int NpgsqlWrite();
    internal abstract int MpgsqlRead();
    internal abstract int NpgsqlRead();
    internal abstract int PayloadLength { get; }
    internal abstract string NpgsqlConverter { get; }
    public abstract void Dispose();
}

internal sealed class ConverterCase<TM, TN> : ConverterCase
{
    private readonly TM _mValue;
    private readonly TN _nValue;
    private TM _mResult = default!;
    private TN _nResult = default!;
    private readonly Action<TM, IBufferWriter<byte>> _write;
    private readonly Func<ReadOnlySequence<byte>, TM> _read;
    private readonly FixedBufferWriter _output;
    private readonly ReadOnlySequence<byte> _input;
    private readonly NpgsqlConverterHarness<TN> _upstream;
    internal override int PayloadLength => (int)_input.Length;
    internal override string NpgsqlConverter => _upstream.ConverterName;

    internal ConverterCase(string name, uint oid, TM mValue, TN nValue,
        Action<TM, IBufferWriter<byte>> write, Func<ReadOnlySequence<byte>, TM> read, bool array)
    {
        _mValue = mValue;
        _nValue = nValue;
        _write = write;
        _read = read;
        var initial = new ArrayBufferWriter<byte>();
        write(mValue, initial);
        byte[] input = initial.WrittenSpan.ToArray();
        _input = new(input);
        _output = new(Math.Max(4096, input.Length + 64));
        _upstream = new(oid, nValue, input, Math.Max(4096, input.Length + 64));
        try
        {
            MpgsqlWrite();
            NpgsqlWrite();
            Check(input, _output.WrittenSpan, name, array);
            Check(input, _upstream.Output.WrittenSpan, name, array);
            // Both decoders consume the same complete PostgreSQL type payload.
            // Re-encoding detects changed values, NULL positions and metadata.
            TM mRead = _read(_input);
            _output.Reset();
            _write(mRead, _output);
            Check(input, _output.WrittenSpan, name + " Mpgsql roundtrip", array);
            TN nRead = _upstream.Read();
            _upstream.Write(nRead);
            Check(input, _upstream.Output.WrittenSpan, name + " Npgsql cross-read", array);
            // Conversely, Mpgsql must decode the payload written by Npgsql.
            byte[] nBytes = _upstream.Output.WrittenSpan.ToArray();
            _output.Reset();
            _write(_read(new(nBytes)), _output);
            Check(input, _output.WrittenSpan, name + " Mpgsql cross-read", array);
            foreach (int segmentSize in new[] { 1, 7, 4096 })
            {
                _output.Reset();
                _write(_read(NpgsqlArrayVerification.Sequence(input, segmentSize)), _output);
                Check(input, _output.WrittenSpan, name + " segmented read", array);
            }
        }
        catch
        {
            _upstream.Dispose();
            throw;
        }
    }

    internal override int MpgsqlWrite()
    {
        _output.Reset();
        _write(_mValue, _output);
        return _output.WrittenCount;
    }
    internal override int NpgsqlWrite() => _upstream.Write(_nValue);
    internal override int MpgsqlRead()
    {
        _mResult = _read(_input); // Preserve the typed result without boxing.
        return PayloadLength;
    }
    internal override int NpgsqlRead()
    {
        _nResult = _upstream.Read();
        return PayloadLength;
    }

    private static void Check(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string name, bool array)
    {
        // Npgsql sets the array "has NULLs" flag conservatively. PostgreSQL
        // accepts either flag for arrays without NULLs. Check every other byte.
        if (array && expected.Length >= 12 && actual.Length >= 12)
        {
            int expectedFlags = BinaryPrimitives.ReadInt32BigEndian(expected[4..]);
            int actualFlags = BinaryPrimitives.ReadInt32BigEndian(actual[4..]);
            if (expectedFlags is not (0 or 1) || actualFlags is not (0 or 1))
                throw new InvalidDataException(name + ": invalid PostgreSQL array flags.");
            int ndimExpected = BinaryPrimitives.ReadInt32BigEndian(expected);
            int ndimActual = BinaryPrimitives.ReadInt32BigEndian(actual);
            if (ndimExpected == 0 && expected.Length == 12 && ndimActual == 1 && actual.Length == 20
                && BinaryPrimitives.ReadInt32BigEndian(actual[12..]) == 0
                && BinaryPrimitives.ReadInt32BigEndian(actual[16..]) == 1
                && expected[8..12].SequenceEqual(actual[8..12])) return;
            if (ndimExpected == 0 && ndimActual == 0 && expected.Length == 12 && actual.Length == 12)
            {
                if (expected[8..].SequenceEqual(actual[8..])) return;
            }
            else if (expected[..4].SequenceEqual(actual[..4]) && expected[8..].SequenceEqual(actual[8..]))
                return;
        }
        else if (expected.SequenceEqual(actual)) return;
        throw new InvalidDataException($"{name}: converter payloads differ ({expected.Length} / {actual.Length} bytes). " +
            $"Expected {Convert.ToHexString(expected[..Math.Min(64, expected.Length)])}; " +
            $"actual {Convert.ToHexString(actual[..Math.Min(64, actual.Length)])}.");
    }

    public override void Dispose() => _upstream.Dispose();
}
