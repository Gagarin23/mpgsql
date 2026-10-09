namespace Mpgsql.Internal;

// Access is serialized by the owning batch gate. A promise is needed only before completion;
// already received confirmations return a cached task without creating a completion source.
internal struct BatchCompletionSignal
{
    private Task<bool>? _task;
    private TaskCompletionSource<bool>? _source;
    private Exception? _error;
    private bool _completed;
    private bool _result;

    internal bool IsCompleted => _completed;

    internal Task<bool> Task => _task ??= _completed
        ? _error is null ? System.Threading.Tasks.Task.FromResult(_result) : System.Threading.Tasks.Task.FromException<bool>(_error)
        : (_source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    internal bool TrySetResult(bool result = false)
    {
        if (_completed)
        {
            return false;
        }
        _completed = true;
        _result = result;
        _source?.TrySetResult(result);
        return true;
    }

    internal bool TrySetException(Exception error)
    {
        if (_completed)
        {
            return false;
        }
        _completed = true;
        _error = error;
        _source?.TrySetException(error);
        return true;
    }
}
