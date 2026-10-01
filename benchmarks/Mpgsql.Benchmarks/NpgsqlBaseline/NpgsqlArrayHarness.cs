using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Npgsql;
using Npgsql.Internal;
using Npgsql.Internal.Postgres;
using Mpgsql.Benchmarks.NpgsqlBaseline.Copied;

namespace Mpgsql.Benchmarks.NpgsqlBaseline;

// Constructs the internal upstream types once. Timed paths use PgConverter<T>,
// direct lifecycle accessors and setup-compiled buffer-reset delegates.
internal sealed class NpgsqlArrayHarness : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly NpgsqlDataSource _dataSource;
    private readonly PgWriter _writer;
    private readonly Action _initWriter;
    private readonly BufferRequirements _requirements;
    private readonly PgReader? _reader;
    private readonly IDisposable? _readBuffer;
    private readonly Action? _resetReadBuffer;
    private readonly MemoryStream? _readStream;
    private readonly int _fieldLength;
    private readonly bool _bufferedRead;

    internal PgConverter<long[]> Original { get; }
    internal PgConverter<long[]> Copy { get; } = CopiedConverterFactory.CreateLongArrayConverter();
    internal FixedBufferWriter Output { get; }

    internal NpgsqlArrayHarness(int outputCapacity, byte[]? input = null, int readBufferSize = 0)
    {
        // Constructing a data source does not open a connection.
        _dataSource = NpgsqlDataSource.Create("Host=localhost;Username=benchmark;Database=benchmark");
        object connector = Create("Npgsql.Internal.NpgsqlConnector", _dataSource);
        object catalog = Type("Npgsql.PostgresMinimalDatabaseInfo").GetProperty("DefaultTypeCatalog", Members)!.GetValue(null)!;
        var elementType = Type("Npgsql.Internal.Converters.Int8Converter`1").MakeGenericType(typeof(long));
        var element = (PgConverter<long>)Activator.CreateInstance(elementType, nonPublic: true)!;
        var arrayType = Type("Npgsql.Internal.Converters.ArrayBasedArrayConverter`2").MakeGenericType(typeof(long[]), typeof(long));
        Original = (PgConverter<long[]>)Activator.CreateInstance(arrayType, Members, binder: null,
            [new PgConverterResolution(element, new Oid(20)), null, 1], culture: null)!;
        if (!Original.CanConvert(DataFormat.Binary, out _requirements) ||
            !Copy.CanConvert(DataFormat.Binary, out var copiedRequirements) ||
            !copiedRequirements.Equals(_requirements))
            throw new InvalidOperationException("Original and copied converter requirements differ.");

        Output = new(outputCapacity);
        _writer = NpgsqlAccessors.CreateWriter(Output);
        var init = typeof(PgWriter).GetMethod("Init", Members)!;
        var flushMode = Enum.Parse(Type("Npgsql.Internal.FlushMode"), "None");
        var call = Expression.Call(Expression.Constant(_writer), init,
            Expression.Constant(catalog, init.GetParameters()[0].ParameterType),
            Expression.Constant(flushMode, init.GetParameters()[1].ParameterType));
        _initWriter = Expression.Lambda<Action>(Expression.Block(call, Expression.Empty())).Compile();

        if (input is null) return;
        _fieldLength = input.Length;
        _bufferedRead = readBufferSize != 0;
        _readStream = new MemoryStream(input, writable: false);
        object buffer = Create("Npgsql.Internal.NpgsqlReadBuffer", connector, _readStream, null,
            _bufferedRead ? readBufferSize : Math.Max(4096, input.Length), Encoding.UTF8, Encoding.UTF8, false);
        _readBuffer = (IDisposable)buffer;
        _reader = (PgReader)buffer.GetType().GetProperty("PgReader", Members)!.GetValue(buffer)!;
        if (!_bufferedRead)
            input.CopyTo((byte[])buffer.GetType().GetField("Buffer", Members)!.GetValue(buffer)!);

        var target = Expression.Constant(buffer, buffer.GetType());
        _resetReadBuffer = Expression.Lambda<Action>(Expression.Block(
            Expression.Assign(Expression.Property(target, "ReadPosition"), Expression.Constant(0)),
            Expression.Assign(Expression.Field(target, "FilledBytes"), Expression.Constant(_bufferedRead ? 0 : input.Length)),
            Expression.Empty())).Compile();
    }

    internal ValueMetadata Prepare(PgConverter<long[]> converter, long[] value)
    {
        object? state = null;
        var size = converter.GetSize(new SizeContext(DataFormat.Binary, _requirements.Write), value, ref state);
        return new ValueMetadata { Format = DataFormat.Binary, Size = size, BufferRequirement = _requirements.Write, WriteState = state };
    }

    internal int Write(PgConverter<long[]> converter, long[] value)
    {
        var metadata = Prepare(converter, value);
        int count = WritePrepared(converter, value, metadata);
        if (metadata.WriteState is IDisposable disposable) disposable.Dispose();
        return count;
    }

    internal int WritePrepared(PgConverter<long[]> converter, long[] value, ValueMetadata metadata)
    {
        Output.Reset();
        _initWriter();
        NpgsqlAccessors.BeginWrite(_writer, false, metadata, default).GetAwaiter().GetResult();
        converter.Write(_writer, value);
        NpgsqlAccessors.CommitWrite(_writer, metadata.Size.Value);
        return Output.WrittenCount;
    }

    internal async ValueTask<int> WriteAsync(PgConverter<long[]> converter, long[] value)
    {
        var metadata = Prepare(converter, value);
        Output.Reset();
        _initWriter();
        await NpgsqlAccessors.BeginWrite(_writer, true, metadata, default);
        await converter.WriteAsync(_writer, value);
        NpgsqlAccessors.CommitWrite(_writer, metadata.Size.Value);
        if (metadata.WriteState is IDisposable disposable) disposable.Dispose();
        return Output.WrittenCount;
    }

    internal long[] Read(PgConverter<long[]> converter)
    {
        ResetReader();
        var reader = _reader!;
        NpgsqlAccessors.StartRead(reader, _requirements.Read);
        var result = converter.Read(reader);
        NpgsqlAccessors.EndRead(reader);
        NpgsqlAccessors.CommitRead(reader);
        return result;
    }

    internal async ValueTask<long[]> ReadAsync(PgConverter<long[]> converter)
    {
        ResetReader();
        var reader = _reader!;
        await NpgsqlAccessors.StartReadAsync(reader, _requirements.Read, default);
        var result = await converter.ReadAsync(reader);
        await NpgsqlAccessors.EndReadAsync(reader);
        NpgsqlAccessors.CommitRead(reader);
        return result;
    }

    private void ResetReader()
    {
        _resetReadBuffer!();
        if (_bufferedRead) _readStream!.Position = 0;
        NpgsqlAccessors.InitRead(_reader!, _fieldLength, DataFormat.Binary, false);
    }

    private static Type Type(string name) => typeof(PgReader).Assembly.GetType(name, throwOnError: true)!;
    private static object Create(string name, params object?[] args)
        => Activator.CreateInstance(Type(name), Members, binder: null, args, culture: null)!;

    public void Dispose()
    {
        _readBuffer?.Dispose();
        _readStream?.Dispose();
        _dataSource.Dispose();
    }
}
