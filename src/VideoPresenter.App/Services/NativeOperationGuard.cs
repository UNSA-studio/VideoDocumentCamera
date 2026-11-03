// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
namespace VideoPresenter.App.Services;

/// <summary>
/// Single-owner operation lifetime. A timeout cancels the wait, NOT the native task.
/// RetireAsync observes that task before invoking cleanup and forbids reuse.
/// The owning UI thread serializes calls; no synchronous waits or background Dispose.
/// </summary>
internal sealed class NativeOperationGuard
{
    private Task _pending = Task.CompletedTask;
    private Task? _cleanup;
    private bool _retired;

    private void Validate()
    {
        if (_retired) throw new ObjectDisposedException(nameof(NativeOperationGuard));
        if (!_pending.IsCompleted) throw new InvalidOperationException("A native operation is still running.");
    }
    public Task RunAsync(Func<Task> start, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Validate();
        _pending = start();
        return _pending.WaitAsync(timeout, cancellationToken);
    }
    public async Task<T> RunAsync<T>(Func<Task<T>> start, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Validate();
        Task<T> operation = start();
        _pending = operation;
        return await operation.WaitAsync(timeout, cancellationToken);
    }
    public Task RetireAsync(Func<Task> cleanup, Action<Exception>? observeFailure = null)
    {
        _retired = true;
        return _cleanup ??= FinishAsync(cleanup, observeFailure);
    }
    private async Task FinishAsync(Func<Task> cleanup, Action<Exception>? observeFailure)
    {
        try { await _pending; }
        catch (Exception ex) { observeFailure?.Invoke(ex); }
        await cleanup();
    }
}
