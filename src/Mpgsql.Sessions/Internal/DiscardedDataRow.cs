namespace Mpgsql.Internal;

// Incrementally validates count/length prefixes while discarding value bytes.
// Required when a cancelled row exceeds the input Pipe's pause threshold.
internal sealed class DiscardedDataRow
{
    private bool _countRead;
    private int _fieldsRemaining;
    private uint _prefix;
    private int _prefixBytes;
    private int _valueRemaining;
    internal int Columns { get; private set; }

    internal void Reset()
    {
        _prefixBytes = 0;
        _prefix = 0;
        _fieldsRemaining = 0;
        _valueRemaining = 0;
        _countRead = false;
        Columns = 0;
    }

    internal void Feed(ReadOnlySpan<byte> input)
    {
        while (!input.IsEmpty)
        {
            if (_valueRemaining > 0)
            {
                var size = Math.Min
                (
                    _valueRemaining,
                    input.Length
                );
                input = input[size..];
                _valueRemaining -= size;
                continue;
            }
            if (_countRead && _fieldsRemaining == 0)
            {
                throw new InvalidDataException("Trailing bytes in discarded DataRow.");
            }
            _prefix = _prefix << 8 | input[0];
            input = input[1..];
            _prefixBytes++;
            if (_prefixBytes != (_countRead ? 4 : 2))
            {
                continue;
            }
            if (!_countRead)
            {
                Columns = (int)_prefix;
                _fieldsRemaining = Columns;
                _countRead = true;
            }
            else
            {
                var length = unchecked((int)_prefix);
                if (length < -1)
                {
                    throw new InvalidDataException("Invalid discarded DataRow value length.");
                }
                _valueRemaining = Math.Max
                (
                    0,
                    length
                );
                _fieldsRemaining--;
            }
            _prefix = 0;
            _prefixBytes = 0;
        }
    }

    internal void End()
    {
        if (!_countRead || _fieldsRemaining != 0 || _valueRemaining != 0 || _prefixBytes != 0)
        {
            throw new InvalidDataException("Truncated discarded DataRow.");
        }
    }
}