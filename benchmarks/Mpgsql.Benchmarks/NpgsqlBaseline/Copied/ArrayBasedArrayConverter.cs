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

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Npgsql.Internal;

namespace Mpgsql.Benchmarks.NpgsqlBaseline.Copied;

sealed class ArrayBasedArrayConverter<T, TElement>(PgConverterResolution elemResolution, Type? effectiveType = null, int pgLowerBound = 1)
    : ArrayConverter<T>(expectedDimensions: effectiveType is null
            ? 1
            : effectiveType.IsArray
                ? effectiveType.GetArrayRank()
                : null,
        elemResolution,
        pgLowerBound), IElementOperations
    where T : class
{
    readonly PgConverter<TElement> _elemConverter = elemResolution.GetConverter<TElement>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static TElement? GetValue(object collection,
        Indices indices)
    {
        Debug.Assert(indices.Count > 0);
        switch (indices.Count)
        {
        case 1:
            // Justification: exact type Unsafe.As used to avoid the cast overhead for per element calls.
            Debug.Assert(collection is TElement?[]);
            return Unsafe.As<TElement?[]>(collection)[indices.One];
        default:
            // Justification: exact type Unsafe.As used to avoid the cast overhead for per element calls.
            Debug.Assert(collection is Array);
            return (TElement?)Unsafe.As<Array>(collection).GetValue(indices.Many!);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void SetValue(object collection,
        Indices indices,
        TElement? value)
    {
        Debug.Assert(indices.Count > 0);
        switch (indices.Count)
        {
            case 1:
                // Justification: exact type Unsafe.As used to avoid the cast overhead for per element calls.
                Debug.Assert(collection is TElement?[]);
                Unsafe.As<TElement?[]>(collection)[indices.One] = value;
                break;
            default:
                // Justification: exact type Unsafe.As used to avoid the cast overhead for per element calls.
                Debug.Assert(collection is Array);
                Unsafe.As<Array>(collection).SetValue(value,
                    indices.Many!);
                break;
        }
    }

    object IElementOperations.CreateCollection(ReadOnlySpan<int> lengths)
        => lengths.Length switch
        {
            0 => Array.Empty<TElement?>(),
            1 when lengths[0] == 0 => Array.Empty<TElement?>(),
            1 => new TElement?[lengths[0]],
            2 => new TElement?[lengths[0], lengths[1]],
            3 => new TElement?[lengths[0], lengths[1], lengths[2]],
            4 => new TElement?[lengths[0], lengths[1], lengths[2], lengths[3]],
            5 => new TElement?[lengths[0], lengths[1], lengths[2], lengths[3], lengths[4]],
            6 => new TElement?[lengths[0], lengths[1], lengths[2], lengths[3], lengths[4], lengths[5]],
            7 => new TElement?[lengths[0], lengths[1], lengths[2], lengths[3], lengths[4], lengths[5], lengths[6]],
            8 => new TElement?[lengths[0], lengths[1], lengths[2], lengths[3], lengths[4], lengths[5], lengths[6], lengths[7]],
            _ => throw new InvalidOperationException("Postgres arrays can have at most 8 dimensions.")
        };

    int IElementOperations.GetCollectionCount(object collection,
        out int[]? lengths)
        => GetLengths((Array)collection,
            out lengths);

    Size? IElementOperations.GetSizeOrDbNull(SizeContext context,
        object collection,
        Indices indices,
        ref object? writeState)
        => _elemConverter.GetSizeOrDbNull(context.Format,
            context.BufferRequirement,
            GetValue(collection,
                indices),
            ref writeState);

    ValueTask IElementOperations.Read(bool async,
        PgReader reader,
        bool isDbNull,
        object collection,
        Indices indices,
        CancellationToken cancellationToken)
    {
        if (!isDbNull && async && _elemConverter is PgStreamingConverter<TElement> streamingConverter)
        {
            return ReadAsync(streamingConverter,
                reader,
                collection,
                indices,
                cancellationToken);
        }

        SetValue(collection,
            indices,
            isDbNull
                ? default
                : _elemConverter.Read(reader));
        return new();
    }

    // Adapted: ReadAsyncAsTask and the function-pointer continuation are internal.
    async ValueTask ReadAsync(PgStreamingConverter<TElement> converter,
        PgReader reader,
        object collection,
        Indices indices,
        CancellationToken cancellationToken)
    {
        var value = await converter.ReadAsync(reader,
            cancellationToken).ConfigureAwait(false);
        SetValue(collection,
            indices,
            value);
    }

    ValueTask IElementOperations.Write(bool async,
        PgWriter writer,
        object collection,
        Indices indices,
        CancellationToken cancellationToken)
    {
        if (async)
        {
            return _elemConverter.WriteAsync(writer,
                GetValue(collection,
                    indices)!,
                cancellationToken);
        }

        _elemConverter.Write(writer,
            GetValue(collection,
                indices)!);
        return new();
    }
}

#pragma warning restore NPG9001
