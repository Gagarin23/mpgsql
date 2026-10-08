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
using Npgsql.Internal;

namespace Mpgsql.Benchmarks.NpgsqlBaseline.Copied;

internal class MultiWriteState : IDisposable
{
    public required ArrayPool<(Size Size, object? WriteState)>? ArrayPool { get; init; }
    public required ArraySegment<(Size Size, object? WriteState)> Data { get; init; }
    public required bool AnyWriteState { get; init; }

    public void Dispose()
    {
        if (Data.Array is not { } array)
        {
            return;
        }

        if (AnyWriteState)
        {
            for (var i = Data.Offset; i < array.Length; i++)
            {
                if (array[i].WriteState is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            Array.Clear(Data.Array,
                Data.Offset,
                Data.Count);
        }

        ArrayPool?.Return(Data.Array);
    }
}

#pragma warning restore NPG9001