// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// Required Notice: Copyright (C) 2026 UNSA Studio
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace VideoPresenter.App.Services;

/// <summary>Background heartbeat reports a stalled UI without blocking it or dumping every frame.</summary>
internal sealed class UiWatchdog : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly System.Threading.Timer _timer;
    private long _lastResponse = Environment.TickCount64;
    private long _lastReport;
    private int _pingPending;
    private bool _disposed;
    public UiWatchdog(DispatcherQueue ui)
    {
        _ui = ui;
        _timer = new System.Threading.Timer(Tick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }
    private void Tick(object? state)
    {
        if (_disposed) return;
        try
        {
            long now = Environment.TickCount64;
            long delay = now - Interlocked.Read(ref _lastResponse);
            if (delay > 15000 && now - _lastReport > 60000)
            {
                _lastReport = now;
                using var process = Process.GetCurrentProcess();
                AppLog.Write($"UI heartbeat delayed {delay}ms; workingSet={process.WorkingSet64}; managedBytes={GC.GetTotalMemory(false)}; " +
                    "This is a suspected hang, not a stack trace or proof of a specific cause.", "ERROR", "Watchdog");
            }
            if (Interlocked.Exchange(ref _pingPending, 1) == 0)
                if (!_ui.TryEnqueue(() =>
                {
                    Interlocked.Exchange(ref _lastResponse, Environment.TickCount64);
                    Interlocked.Exchange(ref _pingPending, 0);
                })) Interlocked.Exchange(ref _pingPending, 0);
        }
        catch (Exception ex) { AppLog.Error("UI watchdog", ex); }
    }
    public void Dispose() { _disposed = true; _timer.Dispose(); }
}