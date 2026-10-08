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

// Compatibility adapters for internal Npgsql overloads, using the public API.
internal static class ConverterCompatibilityExtensions
{
    public static ValueTask Buffer(
        this PgReader reader,
        bool async,
        int byteCount,
        CancellationToken cancellationToken
    )
    {
        if (async)
        {
            return reader.BufferAsync
            (
                byteCount,
                cancellationToken
            );
        }

        reader.Buffer(byteCount);
        return default;
    }

    public static ValueTask<NestedReadScope> BeginNestedRead(
        this PgReader reader,
        bool async,
        int size,
        Size bufferRequirement,
        CancellationToken cancellationToken
    )
    {
        return async
            ? reader.BeginNestedReadAsync
            (
                size,
                bufferRequirement,
                cancellationToken
            )
            : new ValueTask<NestedReadScope>
            (
                reader.BeginNestedRead
                (
                    size,
                    bufferRequirement
                )
            );
    }

    public static ValueTask Flush(
        this PgWriter writer,
        bool async,
        CancellationToken cancellationToken
    )
    {
        if (async)
        {
            return writer.FlushAsync(cancellationToken);
        }

        writer.Flush();
        return default;
    }

    public static ValueTask<NestedWriteScope> BeginNestedWrite(
        this PgWriter writer,
        bool async,
        Size bufferRequirement,
        int byteCount,
        object? state,
        CancellationToken cancellationToken
    )
    {
        return async
            ? writer.BeginNestedWriteAsync
            (
                bufferRequirement,
                byteCount,
                state,
                cancellationToken
            )
            : new ValueTask<NestedWriteScope>
            (
                writer.BeginNestedWrite
                (
                    bufferRequirement,
                    byteCount,
                    state
                )
            );
    }

    public static Size? GetSizeOrDbNull<T>(
        this PgConverter<T> converter,
        DataFormat format,
        Size writeRequirement,
        T? value,
        ref object? writeState
    )
    {
        if (converter.IsDbNull
            (
                value,
                ref writeState
            ))
        {
            return null;
        }

        if (writeRequirement is {Kind: SizeKind.Exact, Value: var byteCount})
        {
            return byteCount;
        }
        var size = converter.GetSize
        (
            new SizeContext
            (
                format,
                writeRequirement
            ),
            value,
            ref writeState
        );

        switch (size.Kind)
        {
            case SizeKind.UpperBound:
                throw new InvalidOperationException($"{nameof(SizeKind.UpperBound)} is not a valid return value for GetSize.");
            case SizeKind.Unknown:
                // Not valid yet.
                throw new InvalidOperationException($"{nameof(SizeKind.Unknown)} is not a valid return value for GetSize.");
        }

        return size;
    }
}

#pragma warning restore NPG9001