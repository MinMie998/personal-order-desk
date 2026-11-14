namespace PersonalOrderDesk.Services;

/// <summary>Drains active HTTP requests before maintenance and rejects new ones until released.</summary>
public sealed class MaintenanceCoordinator
{
    private readonly object sync = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly SemaphoreSlim maintenanceGate = new(1, 1);
    private int activeRequests;
    private bool maintenance;
    private TaskCompletionSource drained = NewSignal();

    public bool IsMaintenance => Volatile.Read(ref maintenance);

    public async Task<bool> EnterRequestAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (maintenance) return false;
            activeRequests++;
            return true;
        }
    }

    public void LeaveRequest()
    {
        lock (sync)
        {
            if (--activeRequests == 0 && maintenance) drained.TrySetResult();
        }
    }

    public async Task<IAsyncDisposable> BeginMaintenanceAsync(CancellationToken cancellationToken)
    {
        await maintenanceGate.WaitAsync(cancellationToken);
        Task wait;
        lock (sync)
        {
            maintenance = true;
            drained = NewSignal();
            if (activeRequests == 0) drained.TrySetResult();
            wait = drained.Task;
        }
        try
        {
            await wait.WaitAsync(cancellationToken);
            await operationGate.WaitAsync(cancellationToken);
            return new Lease(this);
        }
        catch { EndMaintenance(); maintenanceGate.Release(); throw; }
    }

    public async Task<IAsyncDisposable> BeginBackupOperationAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        return new OperationLease(operationGate);
    }

    private void EndMaintenance() { lock (sync) maintenance = false; }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Lease(MaintenanceCoordinator owner) : IAsyncDisposable
    { public ValueTask DisposeAsync() { owner.EndMaintenance(); owner.operationGate.Release(); owner.maintenanceGate.Release(); return ValueTask.CompletedTask; } }
    private sealed class OperationLease(SemaphoreSlim gate) : IAsyncDisposable
    { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
}

public sealed class MaintenanceRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, MaintenanceCoordinator coordinator, DailyBackupScheduler dailyBackups)
    {
        // The UI socket stays open; data operations use the counted API requests.
        if (context.Request.Path.StartsWithSegments("/_blazor")) { await next(context); return; }
        // Only the two actual Settings routes that invoke the drain bypass active-request counting.
        if (MaintenanceRequestPolicy.IsRestoreHandler(context))
        {
            await next(context);
            return;
        }
        if (!await coordinator.EnterRequestAsync(context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("工作台正在执行维护，请稍后刷新。");
            return;
        }
        try
        {
            if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
                || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
            {
                if (MaintenanceRequestPolicy.RequiresDailyBackup(context))
                {
                    try { await dailyBackups.EnsureTodayBackupAsync(context.RequestAborted); }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        await context.Response.WriteAsync($"今日自动备份尚未成功，普通写操作暂时暂停。请在设置中修复备份目录、立即备份或退出应用。原因：{ex.Message}", context.RequestAborted);
                        return;
                    }
                }
            }
            await next(context);
        }
        finally { coordinator.LeaveRequest(); }
    }
}

public static class MaintenanceRequestPolicy
{
    private static bool IsSettingsPath(PathString path) =>
        path.Equals(new PathString("/Settings"), StringComparison.OrdinalIgnoreCase)
        || path.Equals(new PathString("/Settings/Index"), StringComparison.OrdinalIgnoreCase);

    public static bool IsRestoreHandler(HttpContext context) => IsSettingsPath(context.Request.Path)
        && HttpMethods.IsPost(context.Request.Method)
        && string.Equals(context.Request.Query["handler"], "Restore", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresDailyBackup(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsPut(context.Request.Method)
            && !HttpMethods.IsPatch(context.Request.Method) && !HttpMethods.IsDelete(context.Request.Method)) return false;
        if (context.Request.Path=="/api/v1/session") return false;
        if (!IsSettingsPath(context.Request.Path)) return true;
        return context.Request.Query["handler"].ToString() is not ("Save" or "CreateBackup" or "Backup" or "Exit" or "PreviewRestore" or "Restore");
    }
}
