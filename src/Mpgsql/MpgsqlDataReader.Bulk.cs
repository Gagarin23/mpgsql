using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Mpgsql.Internal;

namespace Mpgsql;

public sealed partial class MpgsqlDataReader
{
    /// <summary>Reads the next rows of the current result into a typed column buffer.</summary>
    /// <returns>The number written. A full buffer does not read ahead; a short fill
    /// means the current result has ended. Use NextResultAsync explicitly.</returns>
    /// <remarks>The destination must remain valid until completion. Conversion errors
    /// leave the failing row positioned and the written prefix intact, without returning
    /// a count. Custom converters must return values that own retained data.</remarks>
    public ValueTask<int> ReadColumnAsync<T>(int ordinal, Memory<T> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if ((uint)ordinal >= (uint)_reader.Columns.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (_complete)
            return new(0);
        if (!TryEnterReaderMovement())
            return FailCancelledPortionAsync();
        try
        {
            var decoder = new AdoColumnReader<T>(ordinal, _reader.Columns.Span[ordinal], _typeMapper);
            if (destination.IsEmpty)
            {
                _reader.ExitReaderMovement();
                return new(0);
            }
            return ReadColumnCoreAsync(destination, decoder, cancellationToken);
        }
        catch
        {
            _reader.ExitReaderMovement();
            throw;
        }
    }

    /// <summary>Reads the next rows of the current result into records using a struct mapper.</summary>
    /// <remarks>The mapper runs on the consuming call, never the session's background
    /// reader loop. It must return records that own retained data. Full/short buffers
    /// and partial conversion errors follow ReadColumnAsync semantics.</remarks>
    public ValueTask<int> ReadRowsAsync<T, TMapper>(Memory<T> destination, TMapper mapper, CancellationToken cancellationToken = default)
        where TMapper : struct, IMpgsqlRowMapper<T>
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_complete)
            return new(0);
        if (!TryEnterReaderMovement())
            return FailCancelledPortionAsync();
        if (destination.IsEmpty)
        {
            _reader.ExitReaderMovement();
            return new(0);
        }
        return ReadRowsCoreAsync(destination, mapper, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ReadColumnCoreAsync<T>(Memory<T> destination, AdoColumnReader<T> decoder, CancellationToken token)
    {
        CancellationTokenRegistration registration = default;
        var count = 0;
        var moving = true;
        Exception? failure = null;
        try
        {
            registration = RegisterPortionCancellation(token);
            while (count < destination.Length)
            {
                moving = true;
                _execution.ThrowIfCancelled();
                var row = await ReadWithinMovementAsync().ConfigureAwait(false);
                _execution.ThrowIfCancelled();
                _positioned = row;
                if (!row)
                    break;
                moving = false;
                var value = decoder.Read(_reader);
                CheckPortionOwner();
                destination.Span[count] = value;
                count++;
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            _reader.ExitReaderMovement();
            registration.Dispose();
        }
        if (failure is not null)
            await FailPortionAsync(failure, moving).ConfigureAwait(false);
        return count;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ReadRowsCoreAsync<T, TMapper>(Memory<T> destination, TMapper mapper, CancellationToken token)
        where TMapper : struct, IMpgsqlRowMapper<T>
    {
        CancellationTokenRegistration registration = default;
        var count = 0;
        var moving = true;
        Exception? failure = null;
        try
        {
            registration = RegisterPortionCancellation(token);
            while (count < destination.Length)
            {
                moving = true;
                _execution.ThrowIfCancelled();
                var row = await ReadWithinMovementAsync().ConfigureAwait(false);
                _execution.ThrowIfCancelled();
                _positioned = row;
                if (!row)
                    break;
                moving = false;
                var value = mapper.Read(new MpgsqlRow(_reader, _typeMapper));
                CheckPortionOwner();
                destination.Span[count] = value;
                count++;
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            _reader.ExitReaderMovement();
            registration.Dispose();
        }
        if (failure is not null)
            await FailPortionAsync(failure, moving).ConfigureAwait(false);
        return count;
    }

    private CancellationTokenRegistration RegisterPortionCancellation(CancellationToken token)
        => token.CanBeCanceled ? token.UnsafeRegister(static (state, cancelled) => ((QueryExecution)state!).Cancel(cancelled), _execution) : default;

    private void CheckPortionOwner()
    {
        _execution.ThrowIfCancelled();
        ObjectDisposedException.ThrowIf(_closed || _reader.IsDisposed, this);
    }

    private async ValueTask FailPortionAsync(Exception error, bool moving)
    {
        if (moving || _execution.IsCancellationRequested || _reader.IsDisposed || _closed)
            await FailAsync(error).ConfigureAwait(false);
        // A decode/projector failure has the same recoverable current-row contract
        // as an ordinary getter. No discard, exception wrapper, or payload rollback.
        ExceptionDispatchInfo.Throw(error);
    }

    private async ValueTask<int> FailCancelledPortionAsync()
    {
        await FailAsync(new OperationCanceledException()).ConfigureAwait(false);
        return 0;
    }
}
