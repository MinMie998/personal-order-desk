using Microsoft.Extensions.Hosting;

namespace PersonalOrderDesk.Services;

public sealed class DailyBackupScheduler(IServiceScopeFactory scopes, IBusinessClock clock, ILogger<DailyBackupScheduler> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateOnly? completedDay;
    private string? lastError;
    public string? LastError => Volatile.Read(ref lastError);
    public void ClearLastError() => Volatile.Write(ref lastError, null);

    public async Task EnsureTodayBackupAsync(CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        if (completedDay == today) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (completedDay == today) return;
            await using var scope = scopes.CreateAsyncScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<IMaintenanceService>();
            var backups = await maintenance.ListBackupsAsync(cancellationToken);
            var todayBackupExists = backups.Any(x => x.IsValid && !Path.GetFileName(x.Path).Contains("protection", StringComparison.OrdinalIgnoreCase)
                && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(new DateTimeOffset(x.LastWriteTime), ResolveShanghai()).DateTime) == today);
            if (!todayBackupExists)
            {
                var result = await maintenance.CreateBackupAsync(cancellationToken);
                if (!result.Success) throw new IOException(result.Error ?? "自动备份失败。");
                logger.LogInformation("Daily database backup created at {Path}", result.Path);
            }
            completedDay = today;
            Volatile.Write(ref lastError, null);
        }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await EnsureTodayBackupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Volatile.Write(ref lastError, ex.Message);
                logger.LogError(ex, "Daily database backup check failed; write requests will retry the check.");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private static TimeZoneInfo ResolveShanghai()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }
}
