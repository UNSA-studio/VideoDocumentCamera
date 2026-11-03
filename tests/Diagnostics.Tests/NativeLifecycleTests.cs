using VideoPresenter.App.Services;

internal static class NativeLifecycleTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL " + label);
        Console.WriteLine("PASS " + label);
    }
    private static async Task Expect<T>(Task task) where T : Exception
    {
        try { await task; } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    public static async Task RunAsync()
    {
        var guard = new NativeOperationGuard();
        var native = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Expect<TimeoutException>(guard.RunAsync(() => native.Task, TimeSpan.Zero));
        Check(!native.Task.IsCompleted, "timeout does not complete/cancel native task");
        bool factoryCalled = false;
        Task overlap;
        try { overlap = guard.RunAsync(() => { factoryCalled = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(1)); }
        catch (InvalidOperationException) { overlap = Task.FromException(new InvalidOperationException()); }
        await Expect<InvalidOperationException>(overlap);
        Check(!factoryCalled, "reject overlap before invoking native factory");
        int releases = 0;
        var cleanup = guard.RetireAsync(() => { releases++; return Task.CompletedTask; });
        Check(!cleanup.IsCompleted && releases == 0, "cleanup waits for timed-out native operation");
        Check(ReferenceEquals(cleanup, guard.RetireAsync(() => { releases++; return Task.CompletedTask; })), "retirement is idempotent");
        native.SetResult();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(3));
        Check(releases == 1, "native completion releases exactly once");
        bool blocked = false;
        try { await guard.RunAsync(() => Task.CompletedTask, TimeSpan.FromSeconds(1)); }
        catch (ObjectDisposedException) { blocked = true; }
        Check(blocked, "retired session cannot be reused");

        var cancelGuard = new NativeOperationGuard();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var waiting = cancelGuard.RunAsync(() => pending.Task, TimeSpan.FromMinutes(1), cancellation.Token);
        cancellation.Cancel();
        await Expect<OperationCanceledException>(waiting);
        bool observed = false, disposed = false;
        var cancelledCleanup = cancelGuard.RetireAsync(() => { disposed = true; return Task.CompletedTask; }, ex => observed = ex.Message == "late driver failure");
        Check(!disposed && !pending.Task.IsCompleted, "cancel only waiting; ownership retained");
        pending.SetException(new IOException("late driver failure"));
        await cancelledCleanup.WaitAsync(TimeSpan.FromSeconds(3));
        Check(observed && disposed, "late native failure observed before cleanup");

        var createGuard = new NativeOperationGuard();
        var created = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int resource = 0, freed = 0;
        await Expect<TimeoutException>(createGuard.RunAsync(async () => { resource = await created.Task; }, TimeSpan.Zero));
        var lateCleanup = createGuard.RetireAsync(() => { freed = resource; return Task.CompletedTask; });
        created.SetResult(42);
        await lateCleanup.WaitAsync(TimeSpan.FromSeconds(3));
        Check(resource == 42 && freed == 42, "late-created resource is retained and reclaimed");

        var completed = new NativeOperationGuard();
        int answer = await completed.RunAsync(() => Task.FromResult(123), TimeSpan.FromSeconds(1));
        Check(answer == 123, "normal generic native result preserved");
        var failedCleanup = completed.RetireAsync(() => Task.FromException(new IOException("cleanup failure")));
        await Expect<IOException>(failedCleanup);
        Check(failedCleanup.IsFaulted, "cleanup failure remains visible to reconnect barrier");
    }
}