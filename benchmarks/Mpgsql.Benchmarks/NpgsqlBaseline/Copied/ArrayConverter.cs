// Copied and adapted from Npgsql 10.0.3 (commit recorded in the NuGet package):
// https://github.com/npgsql/npgsql/tree/d3768398c17877b3a916c3c4d87e8e11698991fc
// Sources: src/Npgsql/Internal/Converters/ArrayConverter.cs,
//          src/Npgsql/Internal/Converters/Primitive/Int8Converter.cs,
//          src/Npgsql/Internal/PgConverter.cs.
// long[] uses the generic array converter together with Int8Converter<long>.
// Adaptations: project namespace, direct throws, public reader/writer adapters,
// and ordinary async continuations in place of internal Npgsql async helpers.
// List, resolver and polymorphic converters are omitted.
/*
Copyright (c) 2002-2025, Npgsql

Permission to use, copy, modify, and distribute this software and its
documentation for any purpose, without fee, and without a written agreement
is hereby granted, provided that the above copyright notice and this
paragraph and the following two paragraphs appear in all copies.

IN NO EVENT SHALL NPGSQL BE LIABLE TO ANY PARTY FOR DIRECT, INDIRECT,
SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES, INCLUDING LOST PROFITS,
ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS DOCUMENTATION, EVEN IF
Npgsql HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

NPGSQL SPECIFICALLY DISCLAIMS ANY WARRANTIES, INCLUDING, BUT NOT LIMITED
TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR
PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS ON AN "AS IS" BASIS, AND Npgsql
HAS NO OBLIGATIONS TO PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS,
OR MODIFICATIONS.
*/

#pragma warning disable NPG9001 // Npgsql converter API is experimental; this copy targets 10.0.3.

using Npgsql.Internal;

namespace Mpgsql.Benchmarks.NpgsqlBaseline.Copied;

internal abstract class ArrayConverter<T> : PgStreamingConverter<T> where T : notnull
{
    private readonly PgArrayConverter _pgArrayConverter;

    protected private ArrayConverter(
        int? expectedDimensions,
        PgConverterResolution elemResolution,
        int pgLowerBound = 1
    )
    {
        if (!elemResolution.Converter.CanConvert
            (
                DataFormat.Binary,
                out var bufferRequirements
            ))
        {
            throw new NotSupportedException("Element converter has to support the binary format to be compatible.");
        }

        _pgArrayConverter = new PgArrayConverter
        (
            (IElementOperations)this,
            elemResolution.Converter.IsDbNullable,
            expectedDimensions,
            bufferRequirements,
            elemResolution.PgTypeId,
            pgLowerBound
        );
    }

    public override T Read(PgReader reader)
    {
        return (T)_pgArrayConverter.Read
            (
                false,
                reader
            )
            .Result;
    }

    // Adapted: AsyncHelpers is internal to Npgsql; retain the completed fast path.
    public override ValueTask<T> ReadAsync(
        PgReader reader,
        CancellationToken cancellationToken = default
    )
    {
        var task = _pgArrayConverter.Read
        (
            true,
            reader,
            cancellationToken
        );
        return task.IsCompletedSuccessfully ? new ValueTask<T>((T)task.Result) : AwaitResult(task);

        static async ValueTask<T> AwaitResult(ValueTask<object> task)
        {
            return (T)await task.ConfigureAwait(false);
        }
    }

    public override Size GetSize(
        SizeContext context,
        T values,
        ref object? writeState
    )
    {
        return _pgArrayConverter.GetSize
        (
            context,
            values,
            ref writeState
        );
    }

    public override void Write(
        PgWriter writer,
        T values
    )
    {
        _pgArrayConverter
            .Write
            (
                false,
                writer,
                values,
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
    }

    public override ValueTask WriteAsync(
        PgWriter writer,
        T values,
        CancellationToken cancellationToken = default
    )
    {
        return _pgArrayConverter.Write
        (
            true,
            writer,
            values,
            cancellationToken
        );
    }

    protected static int GetLengths(
        Array array,
        out int[]? lengths
    )
    {
        var dimensions = array.Rank;

        if (dimensions is 1)
        {
            lengths = null;
            return array.Length;
        }

        lengths = new int[dimensions];
        for (var i = 0;
             i < lengths.Length;
             i++)
        {
            lengths[i] = array.GetLength(i);
        }

        // If we have a multidim array it may throw an overflow exception for large arrays (LongLength exists for these cases)
        // however anything over int.MaxValue wouldn't fit in a parameter anyway so easier to throw here than deal with a long.
        return array.Length;
    }
}

#pragma warning restore NPG9001