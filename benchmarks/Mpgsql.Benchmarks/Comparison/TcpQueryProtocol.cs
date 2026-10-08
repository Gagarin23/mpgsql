using System.Buffers;
using System.Text;
using Mpgsql.Benchmarks.Queries;
using Mpgsql.Converters;
using Mpgsql.Protocol;

namespace Mpgsql.Benchmarks.Comparison;

// Fixed protocol 3.0 transcripts, not a SQL interpreter. One instance per connection.
internal sealed class TcpQueryProtocol(TcpQueryCatalog catalog, bool verifyPayloads = false)
{
    private static readonly byte[] TransactionReady = QueryWire.Frame('Z', [(byte)'T']);
    private static readonly byte[] Begin = ControlReply("BEGIN", 'T');
    private static readonly byte[] Rollback = ControlReply("ROLLBACK", 'I');
    private int _phase, _scenario, _worker;
    private bool _recovering;
    private bool _transaction;
    internal long Queries { get; private set; }
    internal long Syncs { get; private set; }
    internal bool Terminated { get; private set; }

    internal ReadOnlyMemory<byte> Process(byte tag, ReadOnlySequence<byte> payload)
    {
        if (_recovering && tag != (byte)'S' && tag != (byte)'X')
        {
            return default;
        }
        var reader = new WireReader(payload);
        ReadOnlyMemory<byte> result = default;
        switch ((char)tag)
        {
            case 'P' when _phase == 0:
                if (!reader.CStringBytes().IsEmpty)
                {
                    throw new InvalidDataException("Named statements are outside this unprepared baseline.");
                }
                var sql = reader.CStringBytes();
                _scenario = -1;
                for (var i = 0; i < catalog.Queries.Scenarios.Length; i++)
                {
                    if (QueryWire.Equal(sql, catalog.Queries.Scenarios[i].SqlUtf8))
                    {
                        _scenario = i;
                        break;
                    }
                }
                if (_scenario < 0)
                {
                    throw new InvalidDataException("Unknown TCP transcript SQL: " + Encoding.UTF8.GetString(sql.ToArray()));
                }
                int types = reader.Count();
                if (types != catalog.Queries.Inputs[_scenario][0].Length)
                {
                    throw new InvalidDataException("Parse parameter count.");
                }
                for (var i = 0; i < types; i++)
                {
                    if (reader.UInt32() != (i == 0 ? 20U : 17U))
                    {
                        throw new InvalidDataException("Parse OID.");
                    }
                }
                _phase = 1;
                result = TcpQueryCatalog.ParseComplete;
                break;
            case 'B' when _phase == 1:
                if (!reader.CStringBytes().IsEmpty || !reader.CStringBytes().IsEmpty)
                {
                    throw new InvalidDataException("Expected unnamed portal and statement.");
                }
                int formats = reader.Count();
                for (var i = 0; i < formats; i++)
                {
                    if (reader.Int16() != 1)
                    {
                        throw new InvalidDataException("Expected binary parameters.");
                    }
                }
                int parameters = reader.Count();
                if (parameters != catalog.Queries.Inputs[_scenario][0].Length || formats is not 1 && formats != parameters)
                {
                    throw new InvalidDataException("Bind parameter/format count.");
                }
                var identifier = reader.Value();
                if (identifier is not {Length: 8})
                {
                    throw new InvalidDataException("Worker bigint payload.");
                }
                _worker = checked((int)Int64Converter.Read(identifier.Value) - 1);
                if ((uint)_worker >= (uint)catalog.Executions[_scenario].Length)
                {
                    throw new InvalidDataException("Worker identifier.");
                }
                if (parameters == 2)
                {
                    var blob = reader.Value();
                    if (blob?.Length != catalog.Queries.Scenarios[_scenario].ByteaBytes)
                    {
                        throw new InvalidDataException("Bytea parameter length.");
                    }
                    if (verifyPayloads && !QueryWire.Equal(blob!.Value, catalog.Queries.Scenarios[_scenario].Blob))
                    {
                        throw new InvalidDataException("Bytea parameter bytes.");
                    }
                }
                int results = reader.Count();
                if (results != 1)
                {
                    throw new InvalidDataException("Expected one binary result format.");
                }
                if (reader.Int16() != 1)
                {
                    throw new InvalidDataException("Expected binary result format.");
                }
                _phase = 2;
                result = TcpQueryCatalog.BindComplete;
                break;
            case 'D' when _phase == 2:
                if (reader.Byte() != (byte)'P' || !reader.CStringBytes().IsEmpty)
                {
                    throw new InvalidDataException("Expected unnamed portal Describe.");
                }
                _phase = 3;
                result = catalog.Descriptions[_scenario];
                break;
            case 'E' when _phase == 3:
                if (!reader.CStringBytes().IsEmpty || reader.Int32() != 0)
                {
                    throw new InvalidDataException("Expected unlimited unnamed Execute.");
                }
                _phase = 0;
                _recovering = catalog.Queries.Scenarios[_scenario].Error;
                Queries++;
                result = catalog.Executions[_scenario][_worker];
                if (result.IsEmpty)
                {
                    throw new InvalidDataException("Disabled worker/scenario.");
                }
                break;
            case 'S' when _phase == 0 || _recovering:
                _phase = 0;
                _recovering = false;
                Syncs++;
                result = _transaction ? TransactionReady : QueryWire.Ready;
                break;
            case 'Q' when _phase == 0: // setup-only transactions pin Npgsql multiplexed leases while warming the pool
                var control = Encoding.UTF8.GetString(reader.CStringBytes().ToArray());
                if (control.StartsWith("BEGIN", StringComparison.Ordinal))
                {
                    _transaction = true;
                    result = Begin;
                }
                else if (control is "ROLLBACK" or "ROLLBACK;")
                {
                    _transaction = false;
                    result = Rollback;
                }
                else
                {
                    throw new InvalidDataException("Unknown setup control SQL: " + control);
                }
                break;
            case 'H': // Npgsql may flush an Extended Query group before Sync.
                break;
            case 'X':
                Terminated = true;
                break;
            default: throw new InvalidDataException($"Unexpected TCP frontend {(char)tag}, phase {_phase}.");
        }
        if (reader.Remaining != 0)
        {
            throw new InvalidDataException("Trailing frontend bytes.");
        }
        return result;
    }
    private static byte[] ControlReply(string command, char status)
    {
        return [.. QueryWire.Frame('C', Encoding.UTF8.GetBytes(command + '\0')), .. QueryWire.Frame('Z', [(byte)status])];
    }
}