using System.Buffers;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Mpgsql.Copy;
using Mpgsql.Protocol;
using Npgsql;
using Npgsql.Internal;
using NpgsqlTypes;

namespace Mpgsql.Benchmarks.NpgsqlBaseline;

// Actual 10.0.3 importer/exporter code, with in-memory transport and builtin
// serializer metadata. No database, reflection or delegate compilation in timed code.
// COPY SQL/response negotiation is excluded here; live benchmarks measure it too.
internal sealed class NpgsqlCopyHarness : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly NpgsqlDataSource _source;
    private readonly object _connector;
    private readonly IDisposable _writeBuffer;
    private readonly Action _clearWrite, _startCopy, _endCopy, _flushWrite, _resetImporter;
    private readonly Action<short> _writeInt16;
    private readonly Func<int> _writeSpace;
    private readonly MemoryStream _output;
    private readonly NpgsqlBinaryImporter _importer;
    private IDisposable? _readBuffer;
    private MemoryStream? _input;
    private NpgsqlBinaryExporter? _exporter;
    private Action? _resetExporter;
    private Func<bool, Task>? _readHeader;

    internal NpgsqlCopyHarness(int outputCapacity)
    {
        _source = NpgsqlDataSource.Create("Host=localhost;Username=benchmark;Database=benchmark");
        _connector = Create("Npgsql.Internal.NpgsqlConnector",
            _source);
        object catalog = Type("Npgsql.PostgresMinimalDatabaseInfo").GetProperty("DefaultTypeCatalog",
            Members)!.GetValue(null)!;
        object chain = typeof(NpgsqlDataSource).GetField("_resolverChain",
            Members)!.GetValue(_source)!;
        object options = Create("Npgsql.Internal.PgSerializerOptions",
            catalog,
            chain,
            null);
        object reloadable = Create("Npgsql.NpgsqlDataSource+ReloadableState",
            catalog,
            options,
            null);
        _connector.GetType().GetField("ReloadableState",
            Members)!.SetValue(_connector,
            reloadable);
        _output = new MemoryStream(outputCapacity);
        object buffer = Create("Npgsql.Internal.NpgsqlWriteBuffer",
            _connector,
            _output,
            null,
            8192,
            Encoding.UTF8);
        _writeBuffer = (IDisposable)buffer;
        _connector.GetType().GetProperty("WriteBuffer",
            Members)!.SetValue(_connector,
            buffer);
        _importer = (NpgsqlBinaryImporter)Create("Npgsql.NpgsqlBinaryImporter",
            _connector);
        var importerType = typeof(NpgsqlBinaryImporter);
        importerType.GetField("_params",
            Members)!.SetValue(_importer,
            new NpgsqlParameter[1]);
        object pgWriter = buffer.GetType().GetMethod("GetWriter",
            Members)!.Invoke(buffer,
        [
            catalog, Enum.Parse(Type("Npgsql.Internal.FlushMode"),
                "None")
        ])!;
        importerType.GetField("_pgWriter",
            Members)!.SetValue(_importer,
            pgWriter);
        _clearWrite = Action(buffer,
            "Clear");
        _startCopy = Action(buffer,
            "StartCopyMode");
        _endCopy = Action(buffer,
            "EndCopyMode");
        _flushWrite = Action(buffer,
            "Flush");
        _writeInt16 = buffer.GetType().GetMethod("WriteInt16",
            Members)!.CreateDelegate<Action<short>>(buffer);
        _writeSpace = buffer.GetType().GetProperty("WriteSpaceLeft",
            Members)!.GetMethod!.CreateDelegate<Func<int>>(buffer);
        _resetImporter = Reset(importerType,
            _importer,
            ("_column", (short)-1),
            ("_rowsImported", 0ul),
            ("_state", Enum.Parse(importerType.GetField("_state",
                    Members)!.FieldType,
                "Ready")));
    }

    internal int Write(long[] values,
        long[] array,
        int rowCount,
        bool arrays)
    {
        _output.Position = 0;
        _output.SetLength(0);
        _clearWrite();
        _startCopy();
        _resetImporter();
        WriteHeader(_importer);
        for (int i = 0; i < rowCount; i++)
        {
            _importer.StartRow();
            if (arrays)
            {
                _importer.Write(array,
                    NpgsqlDbType.Array | NpgsqlDbType.Bigint);
            }
            else
            {
                _importer.Write(values[i],
                    NpgsqlDbType.Bigint);
            }
        }
        // Same trailer/buffer operations as Complete; no fabricated server timing.
        if (_writeSpace() < 2)
        {
            _flushWrite();
        }
        _writeInt16(-1);
        _flushWrite();
        _endCopy();
        return (int)_output.Length;
    }

    internal byte[] Payload()
    {
        var input = new ReadOnlySequence<byte>(_output.GetBuffer().AsMemory(0,
            (int)_output.Length));
        var body = new ArrayBufferWriter<byte>();
        while (!input.IsEmpty)
        {
            if (!BackendMessageReader.TryRead(ref input,
                    out var message) || message.Kind != BackendMessageKind.CopyData)
            {
                throw new InvalidDataException("Invalid upstream COPY frame.");
            }
            foreach (var memory in message.GetCopyData()) body.Write(memory.Span);
        }
        return [.. body.WrittenSpan];
    }

    internal byte[] PrepareRead(byte[] payload,
        int rows)
    {
        using var bytes = new MemoryStream();
        // PostgreSQL COPY OUT sends a message per row, with the header attached
        // to the first row and a separate trailer. Importer framing can group
        // several rows and cannot be fed to NpgsqlBinaryExporter unchanged.
        int rowSize = rows == 0 ? 0 : (payload.Length - 21) / rows;
        if (rows == 0)
        {
            WriteFrame(payload);
        }
        else
        {
            WriteFrame(payload.AsSpan(0,
                19 + rowSize));
            for (int i = 1; i < rows; i++)
                WriteFrame(payload.AsSpan(19 + i * rowSize,
                    rowSize));
            WriteFrame(payload.AsSpan(payload.Length - 2));
        }
        var footer = new ArrayBufferWriter<byte>();
        footer.Write(new byte[] {(byte)'c', 0, 0, 0, 4});
        byte[] tag = Encoding.ASCII.GetBytes($"COPY {rows}\0");
        footer.Write([(byte)'C']);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length,
            tag.Length + 4);
        footer.Write(length);
        footer.Write(tag);
        footer.Write(new byte[] {(byte)'Z', 0, 0, 0, 5, (byte)'I'});
        bytes.Write(footer.WrittenSpan);
        byte[] input = bytes.ToArray();
        _input = new MemoryStream(input,
            writable: false);
        object buffer = Create("Npgsql.Internal.NpgsqlReadBuffer",
            _connector,
            _input,
            null,
            Math.Max(8192,
                input.Length),
            Encoding.UTF8,
            Encoding.UTF8,
            false);
        _readBuffer = (IDisposable)buffer;
        _connector.GetType().GetProperty("ReadBuffer",
            Members)!.SetValue(_connector,
            buffer);
        input.CopyTo((byte[])buffer.GetType().GetField("Buffer",
            Members)!.GetValue(buffer)!);
        _exporter = (NpgsqlBinaryExporter)Create("Npgsql.NpgsqlBinaryExporter",
            _connector);
        var type = typeof(NpgsqlBinaryExporter);
        type.GetProperty("NumColumns",
            Members)!.SetValue(_exporter,
            1);
        type.GetField("_columnInfoCache",
            Members)!.SetValue(_exporter,
            Array.CreateInstance(Type("Npgsql.Internal.PgConverterInfo"),
                1));
        var resetBuffer = Reset(buffer.GetType(),
            buffer,
            ("ReadPosition", 0),
            ("FilledBytes", input.Length),
            ("_flushedBytes", 0L));
        var resetExporter = Reset(type,
            _exporter,
            ("_column", (short)-2),
            ("_rowsExported", 0ul),
            ("_endOfMessagePos", 0L),
            ("_state", Enum.Parse(type.GetField("_state",
                    Members)!.FieldType,
                "Ready")));
        _resetExporter = () =>
        {
            resetBuffer();
            resetExporter();
        };
        _readHeader = type.GetMethod("ReadHeader",
            Members)!.CreateDelegate<Func<bool, Task>>(_exporter);
        return input;

        void WriteFrame(ReadOnlySpan<byte> body)
        {
            Span<byte> header = stackalloc byte[5];
            header[0] = (byte)'d';
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[1..],
                body.Length + 4);
            bytes.Write(header);
            bytes.Write(body);
        }
    }

    internal long Read(bool arrays)
    {
        _resetExporter!();
        _readHeader!(false).GetAwaiter().GetResult();
        long sum = 0;
        while (_exporter!.StartRow() != -1)
        {
            if (arrays)
            {
                long[] value = _exporter.Read<long[]>(NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                sum += value.Length;
            }
            else
            {
                sum = unchecked(sum + _exporter.Read<long>(NpgsqlDbType.Bigint));
            }
        }
        return sum;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WriteHeader")]
    private static extern void WriteHeader(NpgsqlBinaryImporter importer);

    private static Type Type(string name) => typeof(NpgsqlConnection).Assembly.GetType(name,
        throwOnError: true)!;
    private static object Create(string name,
        params object?[] args)
        => Activator.CreateInstance(Type(name),
            Members,
            binder: null,
            args,
            culture: null)!;
    private static Action Action(object target,
        string method)
        => target.GetType().GetMethod(method,
            Members,
            binder: null,
            System.Type.EmptyTypes,
            modifiers: null)!.CreateDelegate<Action>(target);

    private static Action Reset(Type type,
        object instance,
        params (string Member, object Value)[] values)
    {
        var target = Expression.Constant(instance,
            type);
        var expressions = new List<Expression>();
        foreach (var (name, value) in values)
        {
            Expression member = type.GetField(name,
                Members) is { } field
                ? Expression.Field(target,
                    field)
                : Expression.Property(target,
                    type.GetProperty(name,
                        Members)!);
            expressions.Add(Expression.Assign(member,
                Expression.Constant(value,
                    member.Type)));
        }
        expressions.Add(Expression.Empty());
        return Expression.Lambda<Action>(Expression.Block(expressions)).Compile();
    }

    public void Dispose()
    {
        _readBuffer?.Dispose();
        _input?.Dispose();
        _writeBuffer.Dispose();
        _output.Dispose();
        _source.Dispose();
    }
}