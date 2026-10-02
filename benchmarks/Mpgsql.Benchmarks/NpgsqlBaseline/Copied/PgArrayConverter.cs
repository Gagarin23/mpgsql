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

using System.Buffers;
using System.Diagnostics;
using Npgsql.Internal;
using Npgsql.Internal.Postgres;

namespace Mpgsql.Benchmarks.NpgsqlBaseline.Copied;

readonly partial struct PgArrayConverter
(
    IElementOperations elemOps,
    bool elemTypeDbNullable,
    int? expectedDimensions,
    BufferRequirements bufferRequirements,
    PgTypeId elemTypeId,
    int pgLowerBound = 1
)
{
    public const string ReadNonNullableCollectionWithNullsExceptionMessage =
        "Cannot read a non-nullable collection of elements because the returned array contains nulls. Call GetFieldValue with a nullable collection type instead.";

    public const int MaxDimensions = 8;

    public bool ElemTypeDbNullable { get; } = elemTypeDbNullable;

    bool IsDbNull(object values,
        Indices indices)
    {
        object? state = null;
        return elemOps.GetSizeOrDbNull(new(DataFormat.Binary,
                bufferRequirements.Write),
            values,
            indices,
            ref state) is null;
    }

    Size GetElemsSize(object values,
        (Size, object?)[] elemStates,
        out bool anyElementState,
        DataFormat format,
        int count,
        Indices indices,
        int[]? lengths = null)
    {
        Debug.Assert(elemStates.Length >= count);
        var totalSize = Size.Zero;
        var context = new SizeContext(format,
            bufferRequirements.Write);
        anyElementState = false;
        var lastLength = lengths?[^1] ?? count;
        ref var lastIndex = ref indices.GetItem(indices.Count - 1);
        var i = 0;
        do
        {
            ref var elemItem = ref elemStates[i++];
            var elemState = (object?)null;
            var size = elemOps.GetSizeOrDbNull(context,
                values,
                indices,
                ref elemState);
            anyElementState = anyElementState || elemState is not null;
            elemItem = (size ?? -1, elemState);
            totalSize = totalSize.Combine(size ?? 0);
        }
        // We can immediately continue if we didn't reach the end of the last dimension.
        while (++lastIndex < lastLength || (indices.Count > 1 && CarryIndices(lengths!,
                   indices)));

        return totalSize;
    }

    Size GetFixedElemsSize(Size elemSize,
        object values,
        int count,
        Indices indices,
        int[]? lengths = null)
    {
        var nulls = 0;
        var lastLength = lengths?[^1] ?? count;
        ref var lastIndex = ref indices.GetItem(indices.Count - 1);
        if (ElemTypeDbNullable)
        {
            do
            {
                if (IsDbNull(values,
                        indices))
                {
                    nulls++;
                }
            }
            // We can immediately continue if we didn't reach the end of the last dimension.
            while (++lastIndex < lastLength || (indices.Count > 1 && CarryIndices(lengths!,
                       indices)));
        }

        return (count - nulls) * elemSize.Value;
    }

    int GetFormatSize(int count,
        int dimensions)
        => sizeof(int) + // Dimensions
           sizeof(int) + // Flags
           sizeof(int) + // Element OID
           dimensions * (sizeof(int) + sizeof(int)) + // Dimensions * (array length and lower bound)
           sizeof(int) * count; // Element length integers

    public Size GetSize(SizeContext context,
        object values,
        ref object? writeState)
    {
        var count = elemOps.GetCollectionCount(values,
            out var lengths);
        var dimensions = lengths?.Length ?? 1;
        if (dimensions > MaxDimensions)
        {
            throw new ArgumentException($"Postgres arrays can have at most {MaxDimensions} dimensions.",
                nameof(values));
        }

        var formatSize = Size.Create(GetFormatSize(count,
            dimensions));
        if (count is 0)
        {
            return formatSize;
        }

        Size elemsSize;
        var indices = Indices.Create(dimensions);
        if (bufferRequirements.Write is {Kind: SizeKind.Exact} req)
        {
            elemsSize = GetFixedElemsSize(req,
                values,
                count,
                indices,
                lengths);
            writeState = new WriteState {Count = count, Indices = indices, Lengths = lengths, ArrayPool = null, Data = default, AnyWriteState = false};
        }
        else
        {
            var arrayPool = ArrayPool<(Size, object?)>.Shared;
            var data = ArrayPool<(Size, object?)>.Shared.Rent(count);
            elemsSize = GetElemsSize(values,
                data,
                out var elemStateDisposable,
                context.Format,
                count,
                indices,
                lengths);
            writeState = new WriteState
            {
                Count = count, Indices = indices, Lengths = lengths,
                ArrayPool = arrayPool, Data = new(data,
                    0,
                    count), AnyWriteState = elemStateDisposable
            };
        }

        return formatSize.Combine(elemsSize);
    }

    object ReadDimsAndCreateCollection(PgReader reader,
        int dimensions,
        out int lastDimLength)
    {
        Debug.Assert(!reader.ShouldBuffer((sizeof(int) + sizeof(int)) * dimensions));

        Span<int> dimLengths = stackalloc int[MaxDimensions];
        lastDimLength = 0;
        for (var i = 0; i < dimensions; i++)
        {
            lastDimLength = reader.ReadInt32();
            _ = reader.ReadInt32(); // Lower bound
            dimLengths[i] = lastDimLength;
        }

        var collection = elemOps.CreateCollection(dimLengths.Slice(0,
            dimensions));
        Debug.Assert(dimensions <= 1 || collection is Array a && a.Rank == dimensions);
        return collection;
    }

    public async ValueTask<object> Read(bool async,
        PgReader reader,
        CancellationToken cancellationToken = default)
    {
        if (reader.ShouldBuffer(sizeof(int) + sizeof(int) + sizeof(uint)))
        {
            await reader.Buffer(async,
                sizeof(int) + sizeof(int) + sizeof(uint),
                cancellationToken).ConfigureAwait(false);
        }

        var dimensions = reader.ReadInt32();
        if (dimensions > MaxDimensions)
        {
            throw new InvalidOperationException($"Postgres arrays can have at most {MaxDimensions} dimensions.");
        }

        var containsNulls = reader.ReadInt32() is 1;
        _ = reader.ReadUInt32(); // Element OID.

        if (dimensions is not 0 && expectedDimensions is not null && dimensions != expectedDimensions)
        {
            throw new InvalidCastException(
                $"Cannot read an array value with {dimensions} dimension{(dimensions == 1 ? "" : "s")} into a "
                + $"collection type with {expectedDimensions} dimension{(expectedDimensions == 1 ? "" : "s")}. "
                + $"Call GetValue or a version of GetFieldValue<TElement[,,,]> with the commas being the expected amount of dimensions.");
        }

        if (containsNulls && !ElemTypeDbNullable)
        {
            throw new InvalidCastException(ReadNonNullableCollectionWithNullsExceptionMessage);
        }

        // Make sure we can read length + lower bound N dimension times.
        if (reader.ShouldBuffer((sizeof(int) + sizeof(int)) * dimensions))
        {
            await reader.Buffer(async,
                (sizeof(int) + sizeof(int)) * dimensions,
                cancellationToken).ConfigureAwait(false);
        }

        var collection = ReadDimsAndCreateCollection(reader,
            dimensions,
            out var lastDimLength);
        if (dimensions is 0 || lastDimLength is 0)
        {
            return collection;
        }

        _ = elemOps.GetCollectionCount(collection,
            out var dimLengths);
        var indices = Indices.Create(dimensions);

        do
        {
            if (reader.ShouldBuffer(sizeof(int)))
            {
                await reader.Buffer(async,
                    sizeof(int),
                    cancellationToken).ConfigureAwait(false);
            }

            var length = reader.ReadInt32();
            var isDbNull = length == -1;
            if (!isDbNull)
            {
                var scope = await reader.BeginNestedRead(async,
                    length,
                    bufferRequirements.Read,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    await elemOps.Read(async,
                        reader,
                        isDbNull,
                        collection,
                        indices,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (async)
                    {
                        await scope.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        scope.Dispose();
                    }
                }
            }
            else
            {
                await elemOps.Read(async,
                    reader,
                    isDbNull,
                    collection,
                    indices,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        // We can immediately continue if we didn't reach the end of the last dimension.
        while (++indices.GetItem(indices.Count - 1) < lastDimLength || (dimLengths is not null && CarryIndices(dimLengths,
                   indices)));

        return collection;
    }

    static bool CarryIndices(int[] lengths,
        Indices indices)
    {
        Debug.Assert(lengths.Length > 1);
        Debug.Assert(indices.Count > 1);

        // Find the first dimension from the end that isn't at or past its length, increment it and bring all previous dimensions to zero.
        for (var dim = indices.Count - 1; dim >= 0; dim--)
        {
            if (indices.GetItem(dim) >= lengths[dim] - 1)
            {
                continue;
            }

            indices.Many.AsSpan().Slice(dim + 1).Clear();
            indices.GetItem(dim)++;
            return true;
        }

        // We're done if we can't find any dimension that isn't at its length.
        return false;
    }

    public async ValueTask Write(bool async,
        PgWriter writer,
        object values,
        CancellationToken cancellationToken)
    {
        var (count, dims, state) = writer.Current.WriteState switch
        {
            WriteState writeState => (writeState.Count, writeState.Lengths?.Length ?? 1, writeState),
            null                  => (0, values is Array a ? a.Rank : 1, null),
            _                     => throw new InvalidCastException($"Invalid write state, expected {typeof(WriteState).FullName}.")
        };

        if (writer.ShouldFlush(GetFormatSize(count,
                dims)))
        {
            await writer.Flush(async,
                cancellationToken).ConfigureAwait(false);
        }

        writer.WriteInt32(dims); // Dimensions
        writer.WriteInt32(0); // Flags (not really used)
        writer.WriteAsOid(elemTypeId);
        for (var dim = 0; dim < dims; dim++)
        {
            writer.WriteInt32(state?.Lengths?[dim] ?? count);
            writer.WriteInt32(pgLowerBound); // Lower bound
        }

        // We can stop here for empty collections.
        if (state is null)
        {
            return;
        }

        var elemTypeDbNullable = ElemTypeDbNullable;
        var elemData = state.Data.Array;

        var indices = state.Indices;
        if (indices.Many is not null)
        {
            Array.Clear(indices.Many,
                0,
                indices.Many.Length);
        }
        var lastLength = state.Lengths?[^1] ?? state.Count;
        var i = state.Data.Offset;
        do
        {
            if (writer.ShouldFlush(sizeof(int)))
            {
                await writer.Flush(async,
                    cancellationToken).ConfigureAwait(false);
            }

            var elem = elemData?[i++];
            var size = elem?.Size ?? (elemTypeDbNullable && IsDbNull(values,
                indices) ? -1 : bufferRequirements.Write);
            if (size.Kind is SizeKind.Unknown)
            {
                throw new InvalidOperationException(nameof(size.Kind) + " must be known at this point.");
            }

            var length = size.Value;
            writer.WriteInt32(length);
            if (length != -1)
            {
                using var _ = await writer.BeginNestedWrite(async,
                    bufferRequirements.Write,
                    length,
                    elem?.WriteState,
                    cancellationToken).ConfigureAwait(false);
                await elemOps.Write(async,
                    writer,
                    values,
                    indices,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        // We can immediately continue if we didn't reach the end of the last dimension.
        while (++indices.GetItem(indices.Count - 1) < lastLength || (state.Lengths is not null && CarryIndices(state.Lengths,
                   indices)));
    }


}

#pragma warning restore NPG9001