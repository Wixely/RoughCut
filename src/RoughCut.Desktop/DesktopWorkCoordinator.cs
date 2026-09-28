namespace RoughCut.Desktop;

public sealed class DesktopWorkCoordinator
{
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _selection;
    private CancellationTokenSource? _command;
    private long _generation;
    private bool _commandActive;

    public bool IsBusy
    {
        get { lock (_sync) return _commandActive || _selection is not null; }
    }

    public bool StartLatest(Func<CancellationToken, Task> action, Action<Exception?> completed)
    {
        CancellationTokenSource cancellation;
        long generation;
        lock (_sync)
        {
            if (_commandActive) return false;
            _selection?.Cancel();
            cancellation = _selection = new();
            generation = ++_generation;
        }
        _ = Task.Run(() => ExecuteLatestAsync(action, completed, cancellation, generation));
        return true;
    }

    public bool StartCommand(Func<Task> action, Action<Exception?> completed)
    {
        lock (_sync)
        {
            if (_commandActive) return false;
            _commandActive = true;
            _selection?.Cancel();
            ++_generation;
        }
        _ = Task.Run(() => ExecuteCommandAsync(action, completed));
        return true;
    }

    /// A command that can be stopped: fetching a video, or copying one into an edit folder, runs for minutes
    /// and a person who changed their mind should not have to wait for it or kill the window. The token is
    /// the command's own, so cancelling one cannot disturb the selection work that runs beside it.
    public bool StartCancellableCommand(Func<CancellationToken, Task> action, Action<Exception?> completed)
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            if (_commandActive) return false;
            _commandActive = true;
            _selection?.Cancel();
            ++_generation;
            cancellation = _command = new();
        }
        _ = Task.Run(async () =>
        {
            try { await ExecuteCommandAsync(() => action(cancellation.Token), completed); }
            finally
            {
                lock (_sync) { if (ReferenceEquals(_command, cancellation)) _command = null; }
                cancellation.Dispose();
            }
        });
        return true;
    }

    /// Asks the running command to stop. Returns false when there is nothing to stop, so a caller can say so
    /// rather than pretending it cancelled something.
    public bool CancelCommand()
    {
        CancellationTokenSource? running;
        lock (_sync) running = _command;
        if (running is null) return false;
        try { running.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    private async Task ExecuteLatestAsync(Func<CancellationToken, Task> action, Action<Exception?> completed,
        CancellationTokenSource cancellation, long generation)
    {
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellation.Token);
            entered = true;
            await action(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_generation != generation || !ReferenceEquals(_selection, cancellation)) return;
            }
            completed(null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { completed(exception); }
        finally
        {
            if (entered) _gate.Release();
            lock (_sync)
            {
                if (ReferenceEquals(_selection, cancellation)) _selection = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task ExecuteCommandAsync(Func<Task> action, Action<Exception?> completed)
    {
        await _gate.WaitAsync();
        Exception? failure = null;
        try { await action(); }
        catch (Exception exception) { failure = exception; }
        finally
        {
            _gate.Release();
            lock (_sync) _commandActive = false;
        }
        completed(failure);
    }
}
