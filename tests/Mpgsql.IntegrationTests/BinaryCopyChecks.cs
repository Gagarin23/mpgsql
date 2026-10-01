using System.Buffers;
using System.Text;
using Mpgsql.Copy;
using Mpgsql.Protocol;

namespace Mpgsql.IntegrationTests;

internal static class BinaryCopyChecks
{
    internal static void Run(TestConnection connection)
    {
        connection.Query("CREATE TEMP TABLE mpgsql_binary_copy (id bigint, values bigint[], note bytea)");
        long[] large = Enumerable.Range(0, 4097).Select(i => unchecked(long.MinValue + i)).ToArray();
        var import = Begin(connection, "COPY mpgsql_binary_copy FROM STDIN (FORMAT binary)", import: true);
        using (var frames = new CopyDataWriter(connection.CopyStream, 127))
        {
            var writer = new BinaryCopyWriter(frames, import.ColumnCount);
            writer.StartRow(); writer.WriteInt64(long.MinValue); writer.WriteLongArray(large); writer.WriteRaw([0, 255, 10]);
            writer.StartRow(); writer.WriteInt64(0); writer.WriteLongArray(ReadOnlyMemory<long>.Empty); writer.WriteRaw([]);
            writer.StartRow(); writer.WriteInt64(long.MaxValue); writer.WriteNull(); writer.WriteNull();
            Check(writer.Complete() == 3, "COPY writer row count");
            frames.WriteCopyDone();
        }
        import.CopyDoneSent();
        Finish(connection, import);
        Check(import.RowsCopied == 3, "COPY server row count");

        var export = Begin(connection,
            "COPY (SELECT id, values, note FROM mpgsql_binary_copy ORDER BY id) TO STDOUT (FORMAT binary)", import: false);
        var payloads = new List<ReadOnlyMemory<byte>>();
        while (true)
        {
            var message = connection.Receive();
            if (!export.Accept(message)) continue;
            if (message.Kind == BackendMessageKind.CopyDone) break;
            if (message.Kind != BackendMessageKind.CopyData) throw new InvalidDataException("COPY OUT ended unexpectedly.");
            foreach (var segment in message.GetCopyData()) payloads.Add(segment);
        }
        var input = Sequence(payloads);
        var reader = new BinaryCopyReader(export.ColumnCount);
        Check(reader.TryReadHeader(ref input), "COPY OUT binary header");
        var fields = new ReadOnlySequence<byte>?[3];
        Check(reader.TryReadRow(ref input, fields, out var row) == BinaryCopyReadStatus.Row, "COPY OUT first row");
        Check(row.ReadInt64(0) == long.MinValue && row.ReadLongArray(1).Span.SequenceEqual(large) &&
            row[2]!.Value.ToArray().AsSpan().SequenceEqual(new byte[] { 0, 255, 10 }), "COPY OUT large array and bytea");
        Check(reader.TryReadRow(ref input, fields, out row) == BinaryCopyReadStatus.Row && row.ReadInt64(0) == 0 &&
            row.ReadLongArray(1).IsEmpty && !row.IsNull(2) && row[2]!.Value.IsEmpty, "COPY OUT empty values");
        Check(reader.TryReadRow(ref input, fields, out row) == BinaryCopyReadStatus.Row && row.ReadInt64(0) == long.MaxValue &&
            row.IsNull(1) && row.IsNull(2), "COPY OUT NULL values");
        Check(reader.TryReadRow(ref input, fields, out _) == BinaryCopyReadStatus.Completed && input.IsEmpty, "COPY OUT trailer");
        reader.EndData();
        Finish(connection, export);
        Check(export.RowsCopied == 3, "COPY OUT completion count");

        // Simple-query CopyFail consumes ErrorResponse and ReadyForQuery.
        var cancelled = Begin(connection, "COPY mpgsql_binary_copy FROM STDIN (FORMAT binary)", import: true);
        connection.Send(FrontendMessage.CopyFail("intentional binary COPY cancellation"));
        cancelled.CopyFailSent();
        Finish(connection, cancelled);
        Check(cancelled.Error?.SqlState == "57014", "simple COPY cancellation");
        Check(Count(connection) == 3, "cancelled COPY did not insert rows");

        // Extended COPY requires a *new* Sync after COPY ends/errors.
        var extended = Begin(connection, "COPY mpgsql_binary_copy FROM STDIN (FORMAT binary)", import: true, extended: true);
        using (var frames = new CopyDataWriter(connection.CopyStream, 31))
        {
            var writer = new BinaryCopyWriter(frames, 3);
            writer.StartRow(); writer.WriteInt64(42); // Abort in the middle of a tuple.
            frames.Flush();
        }
        connection.Send(FrontendMessage.CopyFail("intentional extended binary COPY cancellation"));
        extended.CopyFailSent();
        var failure = connection.Expect(BackendMessageKind.ErrorResponse);
        extended.Accept(failure);
        Check(extended.RequiresSync && !extended.IsCompleted, "extended COPY awaits Sync");
        connection.Send(FrontendMessage.Sync()); extended.SyncSent();
        Finish(connection, extended);
        Check(extended.Error?.SqlState == "57014" && Count(connection) == 3, "extended COPY rollback and reuse");

        extended = Begin(connection, "COPY mpgsql_binary_copy FROM STDIN (FORMAT binary)", import: true, extended: true);
        using (var frames = new CopyDataWriter(connection.CopyStream))
        {
            var writer = new BinaryCopyWriter(frames, 3);
            writer.StartRow(); writer.WriteInt64(42); writer.WriteNull(); writer.WriteNull(); writer.Complete(); frames.WriteCopyDone();
        }
        extended.CopyDoneSent();
        connection.Send(FrontendMessage.Sync()); extended.SyncSent();
        Finish(connection, extended);
        Check(extended.RowsCopied == 1 && Count(connection) == 4, "extended binary COPY success");

        var extendedExport = Begin(connection, "COPY mpgsql_binary_copy TO STDOUT (FORMAT binary)", import: false, extended: true);
        while (true)
        {
            var message = connection.Receive();
            if (!extendedExport.Accept(message)) continue;
            if (message.Kind == BackendMessageKind.CopyDone) break;
            Check(message.Kind == BackendMessageKind.CopyData, "extended COPY OUT data phase");
        }
        Check(extendedExport.RequiresSync, "extended COPY OUT requires Sync after CopyDone");
        connection.Send(FrontendMessage.Sync()); extendedExport.SyncSent();
        Finish(connection, extendedExport);
        Check(extendedExport.RowsCopied == 4, "extended COPY OUT completion");

        var earlySyncedExport = Begin(connection, "COPY mpgsql_binary_copy TO STDOUT (FORMAT binary)",
            import: false, extended: true, earlySync: true);
        Finish(connection, earlySyncedExport);
        Check(earlySyncedExport.RowsCopied == 4, "COPY OUT accepts Sync queued with Execute");

        var malformed = Begin(connection, "COPY mpgsql_binary_copy FROM STDIN (FORMAT binary)", import: true, extended: true);
        connection.Send(FrontendMessage.CopyData(new byte[21])); // Invalid binary signature.
        connection.Send(FrontendMessage.CopyDone()); malformed.CopyDoneSent();
        connection.Send(FrontendMessage.Sync()); malformed.SyncSent();
        Finish(connection, malformed);
        Check(malformed.Error?.SqlState == "22P04" && Count(connection) == 4, "server-detected binary COPY error and reuse");
        Console.WriteLine("PASS binary COPY IN/OUT, bigint/bigint[]/bytea, NULL/empty, buffer boundaries, simple/extended cancellation and ReadyForQuery recovery");
    }

    private static BinaryCopyOperation Begin(TestConnection connection, string sql, bool import, bool extended = false, bool earlySync = false)
    {
        if (extended)
        {
            connection.Append(FrontendMessage.Parse(sql));
            connection.Append(FrontendMessage.Bind());
            connection.Append(FrontendMessage.Execute());
            if (earlySync) connection.Append(FrontendMessage.Sync()); else connection.Append(FrontendMessage.Flush());
            connection.Flush();
            connection.Expect(BackendMessageKind.ParseComplete); connection.Expect(BackendMessageKind.BindComplete);
        }
        else connection.Send(FrontendMessage.Query(sql));
        var operation = new BinaryCopyOperation(connection.Expect(import ? BackendMessageKind.CopyInResponse : BackendMessageKind.CopyOutResponse), extended);
        if (earlySync) operation.SyncSent();
        return operation;
    }

    private static void Finish(TestConnection connection, BinaryCopyOperation operation)
    {
        while (!operation.IsCompleted) operation.Accept(connection.Receive());
        Check(operation.TransactionStatus == TransactionStatus.Idle, "COPY ReadyForQuery status");
    }

    private static int Count(TestConnection connection)
    {
        var row = connection.Query("select count(*) from mpgsql_binary_copy").Single(m => m.Kind == BackendMessageKind.DataRow);
        var values = row.GetDataRow().GetEnumerator(); values.MoveNext();
        return int.Parse(Encoding.UTF8.GetString(values.Current!.Value.ToArray()));
    }

    private static void Check(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("Binary COPY verification failed: " + check);
    }

    private static ReadOnlySequence<byte> Sequence(List<ReadOnlyMemory<byte>> payloads)
    {
        if (payloads.Count == 0) return ReadOnlySequence<byte>.Empty;
        var first = new Segment(payloads[0]); var last = first;
        foreach (var memory in payloads.Skip(1)) last = last.Append(memory);
        return new(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length }; Next = next; return next;
        }
    }
}
