namespace Pos.Infrastructure.Modules;

/// <summary>Drains active HTTP operations and blocks new ones while a module install is running.</summary>
public sealed class ModuleMaintenanceGate
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Queue<Waiter> _waiters = new();
    private int _activeOperations;
    private bool _installationActive;

    public async Task<IAsyncDisposable> EnterOperationAsync(CancellationToken cancellationToken = default)
    {
        Task<IAsyncDisposable>? pending = null;
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (!_installationActive && !_waiters.Any(waiter => waiter.IsInstallation && !waiter.Completion.Task.IsCompleted))
            {
                _activeOperations++;
                return new Lease(ReleaseOperationAsync);
            }
            var waiter = new Waiter(false, cancellationToken);
            _waiters.Enqueue(waiter);
            pending = waiter.Completion.Task;
        }
        finally { _mutex.Release(); }
        return await pending!;
    }

    public async Task<IAsyncDisposable> EnterInstallationAsync(CancellationToken cancellationToken = default)
    {
        Task<IAsyncDisposable>? pending = null;
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (!_installationActive && _activeOperations == 0)
            {
                _installationActive = true;
                return new Lease(ReleaseInstallationAsync);
            }
            var waiter = new Waiter(true, cancellationToken);
            _waiters.Enqueue(waiter);
            pending = waiter.Completion.Task;
        }
        finally { _mutex.Release(); }
        return await pending!;
    }

    private async ValueTask ReleaseOperationAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            if (--_activeOperations < 0) throw new InvalidOperationException("El contador de operaciones POS quedó inconsistente.");
            if (_activeOperations == 0) GrantNextWaiters();
        }
        finally { _mutex.Release(); }
    }

    private async ValueTask ReleaseInstallationAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            if (!_installationActive) throw new InvalidOperationException("No hay una instalación de módulo activa.");
            _installationActive = false;
            GrantNextWaiters();
        }
        finally { _mutex.Release(); }
    }

    private void GrantNextWaiters()
    {
        if (_installationActive || _activeOperations != 0) return;
        while (_waiters.TryPeek(out var waiter))
        {
            _waiters.Dequeue();
            waiter.Cancellation.Dispose();
            if (waiter.IsInstallation)
            {
                var lease = new Lease(ReleaseInstallationAsync);
                if (!waiter.Completion.TrySetResult(lease)) continue;
                _installationActive = true;
                return;
            }

            var operationLease = new Lease(ReleaseOperationAsync);
            if (!waiter.Completion.TrySetResult(operationLease)) continue;
            _activeOperations++;
            while (_waiters.TryPeek(out var next) && !next.IsInstallation)
            {
                _waiters.Dequeue();
                next.Cancellation.Dispose();
                var nextLease = new Lease(ReleaseOperationAsync);
                if (next.Completion.TrySetResult(nextLease)) _activeOperations++;
            }
            return;
        }
    }

    private sealed class Waiter
    {
        public Waiter(bool isInstallation, CancellationToken cancellationToken)
        {
            IsInstallation = isInstallation;
            if (cancellationToken.CanBeCanceled)
                Cancellation = cancellationToken.Register(static state =>
                    ((TaskCompletionSource<IAsyncDisposable>)state!).TrySetCanceled(), Completion);
        }
        public bool IsInstallation { get; }
        public TaskCompletionSource<IAsyncDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Cancellation { get; }
    }

    private sealed class Lease(Func<ValueTask> release) : IAsyncDisposable
    {
        private Func<ValueTask>? _release = release;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _release, null)?.Invoke() ?? ValueTask.CompletedTask;
    }
}
