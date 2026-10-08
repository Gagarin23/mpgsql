using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Mpgsql.Benchmarks.NpgsqlBaseline;
using Npgsql;
using Npgsql.Internal;
using Npgsql.Internal.Postgres;

namespace Mpgsql.Benchmarks.Converters;

// Version-pinned, actual Npgsql converters. Reflection and buffer loading happen
// only during setup; measured calls include sizing and the normal field lifecycle.
internal sealed class NpgsqlConverterHarness<T> : IDisposable
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly NpgsqlDataSource _source;
    private delegate Size SizeGetter(SizeContext context, T value, ref object? state);
    private delegate bool NullPredicate(T value, ref object? state);
    private readonly PgConverter _converter;
    private readonly Func<PgReader, T> _read;
    private readonly Action<PgWriter, T> _write;
    private readonly SizeGetter _getSize;
    private readonly NullPredicate _isNull;
    private readonly BufferRequirements _requirements;
    private readonly PgWriter _writer;
    private readonly PgReader _reader;
    private readonly IDisposable _readBuffer;
    private readonly MemoryStream _stream;
    private readonly Action _initWriter;
    private readonly Action _resetReader;
    private readonly int _length;
    internal FixedBufferWriter Output { get; }
    internal string ConverterName => _converter.GetType().ToString();

    internal NpgsqlConverterHarness(uint oid, T sample, byte[] input, int capacity)
    {
        _source = NpgsqlDataSource.Create("Host=localhost;Username=benchmark;Database=benchmark");
        object catalog = Type("Npgsql.PostgresMinimalDatabaseInfo").GetProperty("DefaultTypeCatalog", Members)!.GetValue(null)!;
        object chain = typeof(NpgsqlDataSource).GetField("_resolverChain", Members)!.GetValue(_source)!;
        var options = (PgSerializerOptions)Create("Npgsql.Internal.PgSerializerOptions", catalog, chain, null);
        var info = options.GetTypeInfo(typeof(T), new PgTypeId(new Oid(oid)))
            ?? throw new NotSupportedException($"No Npgsql mapping for {typeof(T)} / OID {oid}.");
        _converter = info.GetObjectResolution(sample).Converter;
        if (!_converter.CanConvert(DataFormat.Binary, out _requirements))
            throw new NotSupportedException($"{ConverterName} has no binary format.");
        // Npgsql's array mapping exposes PgConverter<Array> with a typed
        // unboxed result. Compile the reference upcast/downcast once; neither
        // boxing nor reflection is needed in the measured path.
        var converterType = _converter.GetType();
        while (!converterType.IsGenericType || converterType.GetGenericTypeDefinition() != typeof(PgConverter<>))
            converterType = converterType.BaseType!;
        Type valueType = converterType.GetGenericArguments()[0];
        var converter = Expression.Constant(_converter, converterType);
        var value = Expression.Parameter(typeof(T), "value");
        var typedValue = Expression.Convert(value, valueType);
        var reader = Expression.Parameter(typeof(PgReader), "reader");
        var writer = Expression.Parameter(typeof(PgWriter), "writer");
        var context = Expression.Parameter(typeof(SizeContext), "context");
        var state = Expression.Parameter(typeof(object).MakeByRefType(), "state");
        _read = Expression.Lambda<Func<PgReader, T>>(Expression.Convert(
            Expression.Call(converter, converterType.GetMethod("Read")!, reader), typeof(T)), reader).Compile();
        _write = Expression.Lambda<Action<PgWriter, T>>(Expression.Call(converter,
            converterType.GetMethod("Write")!, writer, typedValue), writer, value).Compile();
        _getSize = Expression.Lambda<SizeGetter>(Expression.Call(converter,
            converterType.GetMethod("GetSize")!, context, typedValue, state), context, value, state).Compile();
        _isNull = Expression.Lambda<NullPredicate>(Expression.Call(converter,
            converterType.GetMethod("IsDbNull")!, typedValue, state), value, state).Compile();

        Output = new(capacity);
        _writer = NpgsqlAccessors.CreateWriter(Output);
        var init = typeof(PgWriter).GetMethod("Init", Members)!;
        _initWriter = Expression.Lambda<Action>(Expression.Block(Expression.Call(Expression.Constant(_writer), init,
            Expression.Constant(catalog, init.GetParameters()[0].ParameterType),
            Expression.Constant(Enum.Parse(Type("Npgsql.Internal.FlushMode"), "None"), init.GetParameters()[1].ParameterType)),
            Expression.Empty())).Compile();

        object connector = Create("Npgsql.Internal.NpgsqlConnector", _source);
        _stream = new MemoryStream(input, writable: false);
        object buffer = Create("Npgsql.Internal.NpgsqlReadBuffer", connector, _stream, null,
            Math.Max(4096, input.Length), Encoding.UTF8, Encoding.UTF8, false);
        _readBuffer = (IDisposable)buffer;
        _reader = (PgReader)buffer.GetType().GetProperty("PgReader", Members)!.GetValue(buffer)!;
        input.CopyTo((byte[])buffer.GetType().GetField("Buffer", Members)!.GetValue(buffer)!);
        _length = input.Length;
        var target = Expression.Constant(buffer, buffer.GetType());
        _resetReader = Expression.Lambda<Action>(Expression.Block(
            Expression.Assign(Expression.Property(target, "ReadPosition"), Expression.Constant(0)),
            Expression.Assign(Expression.Field(target, "FilledBytes"), Expression.Constant(input.Length)),
            Expression.Empty())).Compile();
    }

    internal int Write(T value)
    {
        object? state = null;
        // PgTypeInfo.Bind uses this exact-size fast path: buffered scalar
        // converters deliberately do not implement GetSize.
        if (_isNull(value, ref state))
            throw new InvalidOperationException("The benchmark value must have a non-NULL outer payload.");
        var size = _requirements.Write.Kind == SizeKind.Exact ? _requirements.Write
            : _getSize(new SizeContext(DataFormat.Binary, _requirements.Write), value, ref state);
        var metadata = new ValueMetadata { Format = DataFormat.Binary, Size = size,
            BufferRequirement = _requirements.Write, WriteState = state };
        try
        {
            Output.Reset();
            _initWriter();
            NpgsqlAccessors.BeginWrite(_writer, false, metadata, default).GetAwaiter().GetResult();
            _write(_writer, value);
            NpgsqlAccessors.CommitWrite(_writer, size.Value);
            return Output.WrittenCount;
        }
        finally
        {
            (state as IDisposable)?.Dispose();
        }
    }

    internal T Read()
    {
        _resetReader();
        NpgsqlAccessors.InitRead(_reader, _length, DataFormat.Binary, false);
        NpgsqlAccessors.StartRead(_reader, _requirements.Read);
        T value = _read(_reader);
        NpgsqlAccessors.EndRead(_reader);
        NpgsqlAccessors.CommitRead(_reader);
        return value;
    }

    private static Type Type(string name) => typeof(NpgsqlConnection).Assembly.GetType(name, throwOnError: true)!;
    private static object Create(string name, params object?[] args)
        => Activator.CreateInstance(Type(name), Members, null, args, null)!;

    public void Dispose()
    {
        _readBuffer.Dispose();
        _stream.Dispose();
        _source.Dispose();
    }
}
