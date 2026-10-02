using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mpgsql.Copy;
using Mpgsql.IntegrationTests;
using Mpgsql.Protocol;
using Npgsql;
using NpgsqlTypes;

namespace Mpgsql.Benchmarks.Live;

internal static class BinaryCopyLiveBenchmarks
{
    private const string Table = "mpgsql_copy_bench";
    private const string ImportSql = "COPY " + Table + " FROM STDIN (FORMAT binary)";
    private const string ExportSql = "COPY " + Table + " TO STDOUT (FORMAT binary)";
    private const int Warmups = 6;

    private sealed record Sample(double Milliseconds, long AllocatedBytes);

    private sealed record Result
    (
        string Direction,
        string Type,
        int Rows,
        int ArrayLength,
        string Method,
        double MeanMs,
        double MedianMs,
        double StdDevMs,
        double MinMs,
        double MaxMs,
        double AllocatedBytes,
        double RowsPerSecond,
        double PayloadMiBPerSecond,
        Sample[] Samples
    );

    internal static void Run(string[] args)
    {
        string host = Environment.GetEnvironmentVariable("MPGSQL_TEST_HOST") ?? "localhost";
        int port = int.Parse(Environment.GetEnvironmentVariable("MPGSQL_TEST_PORT") ?? "5432",
            CultureInfo.InvariantCulture);
        string user = Environment.GetEnvironmentVariable("MPGSQL_TEST_USER") ?? "test";
        string password = Environment.GetEnvironmentVariable("MPGSQL_TEST_PASSWORD")
                          ?? throw new InvalidOperationException("Set MPGSQL_TEST_PASSWORD to run live COPY benchmarks.");
        string database = Environment.GetEnvironmentVariable("MPGSQL_TEST_DATABASE") ?? user;
        int iterations = int.Parse(Environment.GetEnvironmentVariable("MPGSQL_BENCH_ITERATIONS") ?? "16",
            CultureInfo.InvariantCulture);
        if (iterations is < 4 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }
        int pathIndex = Array.IndexOf(args,
            "--artifacts");
        string directory = Path.GetFullPath(pathIndex >= 0 && pathIndex + 1 < args.Length
            ? args[pathIndex + 1]
            : "artifacts/binary-copy-live");

        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Username = user, Password = password, Database = database,
            Pooling = false, SslMode = SslMode.Disable, Timeout = 10, CommandTimeout = 30,
            WriteBufferSize = 8192, ReadBufferSize = 8192
        };
        using var native = new NpgsqlConnection(settings.ConnectionString);
        native.Open();
        using var connection = TestConnection.Open(host,
            port,
            user,
            password,
            database);
        using var packets = new BufferedCopyReader(connection.CopyStream);
        var fields = new ReadOnlySequence<byte>?[1];
        var results = new List<Result>();
        Console.WriteLine($"PostgreSQL {connection.ServerVersion}; {host}:{port}/{database}; Npgsql 10.0.3; {iterations} samples/method, {Warmups} warmups; temporary tables, plaintext TCP.");

        foreach (var (rows, arrayLength) in new[] {(1024, 0), (65536, 0), (256, 256), (4096, 256)})
        {
            bool arrays = arrayLength != 0;
            long[] values =
            [
                .. Enumerable.Range(0,
                    rows).Select(i => (long)i - rows / 2)
            ];
            long[] array =
            [
                .. Enumerable.Range(0,
                    arrayLength).Select(i => (long)i - arrayLength / 2)
            ];
            string type = arrays ? "bigint[]" : "bigint";
            string create = $"DROP TABLE IF EXISTS pg_temp.{Table}; CREATE TEMP TABLE {Table} (v {type})";
            NativeQuery(create);
            connection.Query(create);
            var imports = new List<(string Name, Func<long> Action, Action Prepare, Action Verify)>
            {
                ("Npgsql", ImportNative, () => NativeQuery("TRUNCATE " + Table), () => VerifyTable(useNpgsql: true)),
                ("MpgsqlRows", () => ImportMpgsql(batch: false), () => connection.Query("TRUNCATE " + Table), () => VerifyTable(useNpgsql: false))
            };
            if (!arrays)
            {
                imports.Add(("MpgsqlBatch", () => ImportMpgsql(batch: true),
                    () => connection.Query("TRUNCATE " + Table), () => VerifyTable(useNpgsql: false)));
            }
            if (!arrays)
            {
                imports.Add(("MpgsqlSeparateDone", () => ImportMpgsql(batch: false,
                        coalescedDone: false),
                    () => connection.Query("TRUNCATE " + Table), () => VerifyTable(useNpgsql: false)));
            }
            Measure("Import",
                imports);

            // Populate both tables once; export timings include COPY negotiation,
            // decoding/owned array results and the final ReadyForQuery boundary.
            NativeQuery("TRUNCATE " + Table);
            ImportNative();
            VerifyTable(useNpgsql: true);
            connection.Query("TRUNCATE " + Table);
            ImportMpgsql(batch: false);
            VerifyTable(useNpgsql: false);
            Measure("Export",
            [
                ("Npgsql", ExportNative, () => { }, () => { }),
                ("Mpgsql", ExportMpgsql, () => { }, () => { })
            ]);

            long ImportNative()
            {
                using var copy = native.BeginBinaryImport(ImportSql);
                for (int i = 0; i < rows; i++)
                {
                    copy.StartRow();
                    if (arrays)
                    {
                        copy.Write(array,
                            NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    }
                    else
                    {
                        copy.Write(values[i],
                            NpgsqlDbType.Bigint);
                    }
                }
                return checked((long)copy.Complete());
            }

            long ImportMpgsql(bool batch, bool coalescedDone = true)
            {
                connection.Send(FrontendMessage.Query(ImportSql));
                var operation = new BinaryCopyOperation(connection.Expect(BackendMessageKind.CopyInResponse));
                using (var frames = new CopyDataWriter(connection.CopyStream,
                           8192))
                {
                    var copy = new BinaryCopyWriter(frames,
                        1);
                    if (batch)
                    {
                        for (int offset = 0; offset < rows; offset += 512)
                            copy.WriteInt64Rows(values.AsSpan(offset,
                                Math.Min(512,
                                    rows - offset)));
                    }
                    else
                    {
                        for (int i = 0; i < rows; i++)
                        {
                            copy.StartRow();
                            if (arrays)
                            {
                                copy.WriteLongArray(array);
                            }
                            else
                            {
                                copy.WriteInt64(values[i]);
                            }
                        }
                    }
                    copy.Complete();
                    if (coalescedDone)
                    {
                        frames.WriteCopyDone();
                    }
                    else
                    {
                        frames.Flush();
                    }
                }
                if (!coalescedDone)
                {
                    connection.Send(FrontendMessage.CopyDone());
                }
                operation.CopyDoneSent();
                while (!operation.IsCompleted) operation.Accept(connection.Receive());
                CheckCompletion(operation);
                return checked((long)operation.RowsCopied!.Value);
            }

            long ExportNative()
            {
                using var copy = native.BeginBinaryExport(ExportSql);
                int count = 0;
                long sum = 0;
                while (copy.StartRow() != -1)
                {
                    if (arrays)
                    {
                        sum += ArrayChecksum(copy.Read<long[]>(NpgsqlDbType.Array | NpgsqlDbType.Bigint));
                    }
                    else
                    {
                        sum += copy.Read<long>(NpgsqlDbType.Bigint);
                    }
                    count++;
                }
                if (count != rows)
                {
                    throw new InvalidDataException("Npgsql export row count differs.");
                }
                return sum;
            }

            long ExportMpgsql()
            {
                connection.Send(FrontendMessage.Query(ExportSql));
                var operation = new BinaryCopyOperation(connection.Expect(BackendMessageKind.CopyOutResponse));
                var copy = new BinaryCopyReader(1);
                long sum = 0;
                while (!operation.IsCompleted)
                {
                    var message = packets.Receive();
                    if (!operation.Accept(message))
                    {
                        continue;
                    }
                    if (message.Kind == BackendMessageKind.CopyDone)
                    {
                        copy.EndData();
                    }
                    if (message.Kind != BackendMessageKind.CopyData)
                    {
                        continue;
                    }
                    var input = message.GetCopyData();
                    if (!copy.HeaderRead && !copy.TryReadHeader(ref input))
                    {
                        throw new InvalidDataException("Incomplete live COPY header.");
                    }
                    while (!input.IsEmpty)
                    {
                        var status = copy.TryReadRow(ref input,
                            fields,
                            out var row);
                        if (status == BinaryCopyReadStatus.NeedMoreData)
                        {
                            throw new InvalidDataException("Incomplete live COPY row.");
                        }
                        if (status == BinaryCopyReadStatus.Row)
                        {
                            sum += arrays ? ArrayChecksum(row.ReadLongArray(0).Span) : row.ReadInt64(0);
                        }
                    }
                }
                CheckCompletion(operation);
                copy.EndData();
                if (copy.RowsRead != (ulong)rows || !packets.IsDrained)
                {
                    throw new InvalidDataException("Mpgsql export boundary differs.");
                }
                return sum;
            }

            void CheckCompletion(BinaryCopyOperation operation)
            {
                if (operation.Error is not null || operation.RowsCopied != (ulong)rows || operation.TransactionStatus != TransactionStatus.Idle)
                {
                    throw new InvalidDataException("Live COPY did not complete successfully.");
                }
            }

            void VerifyTable(bool useNpgsql)
            {
                string sql = arrays
                    ? $"SELECT count(*), coalesce(sum(cardinality(v)),0)::bigint, coalesce(sum(v[1]),0)::bigint, coalesce(sum(v[{arrayLength}]),0)::bigint FROM {Table}"
                    : $"SELECT count(*), coalesce(sum(v),0)::bigint FROM {Table}";
                long[] expected = arrays ? [rows, (long)rows * arrayLength, (long)rows * array[0], (long)rows * array[^1]] : [rows, values.Sum()];
                if (useNpgsql)
                {
                    using var command = new NpgsqlCommand(sql,
                        native);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read())
                    {
                        throw new InvalidDataException();
                    }
                    for (int i = 0; i < expected.Length; i++)
                        if (reader.GetInt64(i) != expected[i])
                        {
                            throw new InvalidDataException("Npgsql table checksum differs.");
                        }
                }
                else
                {
                    var message = connection.Query(sql).Single(m => m.Kind == BackendMessageKind.DataRow);
                    var columns = message.GetDataRow().GetEnumerator();
                    foreach (long value in expected)
                    {
                        if (!columns.MoveNext() || long.Parse(Encoding.UTF8.GetString(columns.Current!.Value.ToArray()),
                                CultureInfo.InvariantCulture) != value)
                        {
                            throw new InvalidDataException("Mpgsql table checksum differs.");
                        }
                    }
                }
            }

            void Measure(string direction, List<(string Name, Func<long> Action, Action Prepare, Action Verify)> methods)
            {
                long expected = direction == "Import" ? rows : arrays ? (long)rows * ArrayChecksum(array) : values.Sum();
                var samples = methods.Select(_ => new List<Sample>()).ToArray();
                for (int iteration = -Warmups; iteration < iterations; iteration++)
                for (int j = 0; j < methods.Count; j++)
                {
                    // Rotate methods to distribute server/cache/GC drift.
                    int index = (j + iteration + Warmups) % methods.Count;
                    var method = methods[index];
                    method.Prepare();
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    long result = method.Action();
                    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    if (result != expected)
                    {
                        throw new InvalidDataException("Live COPY checksum/count differs.");
                    }
                    method.Verify();
                    if (iteration >= 0)
                    {
                        samples[index].Add(new(elapsed,
                            allocated));
                    }
                }
                for (int i = 0; i < methods.Count; i++)
                {
                    Sample[] measured = [.. samples[i]];
                    double[] ordered = [.. measured.Select(s => s.Milliseconds).Order()];
                    double mean = ordered.Average(), median = (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
                    double deviation = Math.Sqrt(ordered.Sum(t => (t - mean) * (t - mean)) / (ordered.Length - 1));
                    long payload = 21L + rows * (arrays ? 26L + 12L * arrayLength : 14L);
                    var result = new Result(direction,
                        type,
                        rows,
                        arrayLength,
                        methods[i].Name,
                        mean,
                        median,
                        deviation,
                        ordered[0],
                        ordered[^1],
                        measured.Average(s => (double)s.AllocatedBytes),
                        rows * 1000 / mean,
                        payload / 1048576.0 * 1000 / mean,
                        measured);
                    results.Add(result);
                    Console.WriteLine(
                        FormattableString.Invariant($"{direction,-6} {type,-8} rows={rows,6} {result.Method,-12} mean={mean,8:F3} ms median={median,8:F3} ms sd={deviation,7:F3} ms alloc={result.AllocatedBytes:F0} B"));
                }
            }
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,
                "results.json"),
            JsonSerializer.Serialize(new
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Host = host,
                    Port = port,
                    Database = database,
                    ServerVersion = connection.ServerVersion,
                    NpgsqlVersion = typeof(NpgsqlConnection).Assembly.GetName()
                        .Version!.ToString(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    OS = RuntimeInformation.OSDescription,
                    Iterations = iterations,
                    Warmups,
                    Notes =
                        "Connection-local temporary tables. Connections/authentication/table setup/TRUNCATE/checksum SQL excluded. COPY SQL to ReadyForQuery included. Six warmups, rotated method order. Plaintext synchronous TCP. Array reads return owned storage. Allocations measured on calling thread. Mpgsql socket/auth helpers are benchmark-only.",
                    Results = results
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        var csv = new StringBuilder("Direction,Type,Rows,ArrayLength,Method,MeanMs,MedianMs,StdDevMs,MinMs,MaxMs,AllocatedBytes,RowsPerSecond,PayloadMiBPerSecond\n");
        foreach (var r in results)
            csv.AppendLine(FormattableString.Invariant(
                $"{r.Direction},{r.Type},{r.Rows},{r.ArrayLength},{r.Method},{r.MeanMs:F6},{r.MedianMs:F6},{r.StdDevMs:F6},{r.MinMs:F6},{r.MaxMs:F6},{r.AllocatedBytes:F0},{r.RowsPerSecond:F2},{r.PayloadMiBPerSecond:F2}"));
        File.WriteAllText(Path.Combine(directory,
                "results.csv"),
            csv.ToString());
        connection.Send(FrontendMessage.Terminate());
        Console.WriteLine("All live COPY counts/checksums verified. Results: " + directory);

        void NativeQuery(string sql)
        {
            using var command = new NpgsqlCommand(sql,
                native);
            command.ExecuteNonQuery();
        }
    }

    private static long ArrayChecksum(ReadOnlySpan<long> array) => array.Length + array[0] + array[^1];
}