using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;

namespace PersonalOrderDesk.Tests;

public sealed class Stage3MaintenanceTests
{
    [Theory]
    [InlineData("/Settings")]
    [InlineData("/Settings/Index")]
    public void Restore_handlers_on_both_settings_routes_exclude_only_the_calling_restore_request(string route)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = route;
        context.Request.QueryString = new QueryString("?handler=Restore");
        Assert.True(MaintenanceRequestPolicy.IsRestoreHandler(context));
        Assert.False(MaintenanceRequestPolicy.RequiresDailyBackup(context));

        context.Request.Path = "/Maintenance/Anything";
        Assert.False(MaintenanceRequestPolicy.IsRestoreHandler(context));
        context.Request.QueryString = QueryString.Empty;
        context.Request.Method = HttpMethods.Post;
        Assert.True(MaintenanceRequestPolicy.RequiresDailyBackup(context));
    }

    [Fact]
    public async Task Coordinator_drains_active_requests_and_rejects_new_requests_until_released()
    {
        var coordinator = new MaintenanceCoordinator();
        Assert.True(await coordinator.EnterRequestAsync(CancellationToken.None));
        var entered = coordinator.BeginMaintenanceAsync(CancellationToken.None);
        Assert.False(entered.IsCompleted);
        await using (await coordinator.BeginBackupOperationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))) { }
        coordinator.LeaveRequest();
        await using (await entered)
        {
            Assert.True(coordinator.IsMaintenance);
            Assert.False(await coordinator.EnterRequestAsync(CancellationToken.None));
        }
        Assert.False(coordinator.IsMaintenance);
        Assert.True(await coordinator.EnterRequestAsync(CancellationToken.None));
        coordinator.LeaveRequest();
    }

    [Fact]
    public async Task Failed_daily_backup_blocks_business_writes_but_preserves_settings_backup_restore_and_exit_rescue_handlers()
    {
        using var services = new ServiceCollection()
            .AddScoped<IMaintenanceService>(_ => new FailingMaintenance())
            .BuildServiceProvider();
        using var scheduler = new DailyBackupScheduler(services.GetRequiredService<IServiceScopeFactory>(),
            new SystemBusinessClock(), NullLogger<DailyBackupScheduler>.Instance);
        var nextCalled = false;
        var middleware = new MaintenanceRequestMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var coordinator = new MaintenanceCoordinator();

        var businessWrite = new DefaultHttpContext();
        businessWrite.Request.Method = HttpMethods.Post;
        businessWrite.Request.Path = "/Items/Create";
        await middleware.InvokeAsync(businessWrite, coordinator, scheduler);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, businessWrite.Response.StatusCode);
        Assert.False(nextCalled);

        foreach (var route in new[] { "/Settings", "/Settings/Index" })
        foreach (var handler in new[] { "Save", "CreateBackup", "PreviewRestore", "Restore", "Exit" })
        {
            nextCalled = false;
            var recoveryWrite = new DefaultHttpContext();
            recoveryWrite.Request.Method = HttpMethods.Post;
            recoveryWrite.Request.Path = route;
            recoveryWrite.Request.QueryString = new QueryString($"?handler={handler}");
            await middleware.InvokeAsync(recoveryWrite, coordinator, scheduler);
            Assert.True(nextCalled, $"{route}?handler={handler} should remain available after daily backup fails.");
            Assert.Equal(StatusCodes.Status200OK, recoveryWrite.Response.StatusCode);
        }
    }

    [Fact]
    public async Task Backup_preview_and_confirmed_restore_restore_a_valid_database_and_keep_protection_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "pod-maintenance-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "app.db");
        var backupDirectory = Path.Combine(root, "Backups");
        try
        {
            var fault = new PostSwapFaultInterceptor(dbPath);
            var options = new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(new SqliteConnectionStringBuilder
                { DataSource = dbPath, ForeignKeys = true, Pooling = false }.ToString())
                .AddInterceptors(fault).Options;
            await using var db = new DeskDbContext(options);
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
            db.Settings.Add(new Setting { Key = "test-marker", Value = "before" });
            await db.SaveChangesAsync();

            using var lifetime = new TestApplicationLifetime();
            using var provider = new ServiceCollection().BuildServiceProvider();
            var dailyBackups = new DailyBackupScheduler(provider.GetRequiredService<IServiceScopeFactory>(), new SystemBusinessClock(), NullLogger<DailyBackupScheduler>.Instance);
            var service = new MaintenanceService(db, new TestWorkbench(backupDirectory), new MaintenanceCoordinator(), dailyBackups,
                lifetime, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["PersonalOrderDesk:DataDirectory"] = root }).Build(), NullLogger<MaintenanceService>.Instance);
            var backup = await service.CreateBackupAsync();
            Assert.True(backup.Success, backup.Error);
            Assert.True(File.Exists(backup.Path));
            var importProtection = await service.CreateImportProtectionBackupAsync();
            Assert.True(importProtection.Success, importProtection.Error);
            Assert.Contains("import-protection", importProtection.Path);
            File.SetLastWriteTimeUtc(importProtection.Path!, DateTime.UtcNow.AddDays(-365));
            Assert.True((await service.CreateBackupAsync()).Success);
            Assert.True(File.Exists(importProtection.Path), "Import protection copies must survive normal backup retention.");

            var previewPage = new PersonalOrderDesk.Pages.Settings.IndexModel(new TestWorkbench(backupDirectory), service)
            {
                PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext
                {
                    HttpContext = new DefaultHttpContext()
                },
                RestoreBackupPath = backup.Path
            };
            previewPage.Request.QueryString = new QueryString("?handler=PreviewRestore");
            previewPage.ModelState.AddModelError("Settings.BackupDirectory", "Unrelated settings form was not submitted.");
            await previewPage.OnPostPreviewRestoreAsync(CancellationToken.None);
            Assert.True(previewPage.RestorePreview?.CanRestore, previewPage.ErrorMessage);
            Assert.Equal(7, previewPage.Settings.PhysicalReminderLeadDays);
            Assert.False(previewPage.ModelState.ContainsKey("Settings.BackupDirectory"));

            await db.Database.ExecuteSqlRawAsync("DELETE FROM Settings WHERE Key='test-marker'");
            var preview = await service.PreviewRestoreAsync(backup.Path!);
            Assert.True(preview.CanRestore, preview.Message);
            var heldDatabase = backup.Path + ".held";
            File.Copy(backup.Path!, heldDatabase, true);
            File.Copy(backup.Path! + ".manifest.json", heldDatabase + ".manifest.json", true);
            var alternate = await service.CreateBackupAsync();
            Assert.True(alternate.Success, alternate.Error);
            File.Copy(alternate.Path!, backup.Path!, true);
            File.Copy(alternate.Path! + ".manifest.json", backup.Path! + ".manifest.json", true);
            var substituted = await service.RestoreAsync(backup.Path!, preview.ConfirmationToken!);
            Assert.False(substituted.Success);
            Assert.Contains("内容已变化", substituted.Error);
            File.Copy(heldDatabase, backup.Path!, true);
            File.Copy(heldDatabase + ".manifest.json", backup.Path! + ".manifest.json", true);
            File.Delete(heldDatabase);
            File.Delete(heldDatabase + ".manifest.json");

            preview = await service.PreviewRestoreAsync(backup.Path!);
            Assert.True(preview.CanRestore, preview.Message);
            var missingConfirmation = await service.RestoreAsync(backup.Path!, "wrong-token");
            Assert.False(missingConfirmation.Success);

            preview = await service.PreviewRestoreAsync(backup.Path!);
            var restored = await service.RestoreAsync(backup.Path!, preview.ConfirmationToken!);
            Assert.True(restored.Success, restored.Error);
            Assert.NotNull(restored.ProtectionBackupPath);
            Assert.True(File.Exists(restored.ProtectionBackupPath));

            await using var verification = new DeskDbContext(options);
            Assert.Equal("before", await verification.Settings.Where(x => x.Key == "test-marker").Select(x => x.Value).SingleAsync());

            await db.Database.ExecuteSqlRawAsync("UPDATE Settings SET Value='alternate' WHERE Key='test-marker'");
            var failureSource = await service.CreateBackupAsync();
            Assert.True(failureSource.Success, failureSource.Error);
            await db.Database.ExecuteSqlRawAsync("UPDATE Settings SET Value='current' WHERE Key='test-marker'");
            var failurePreview = await service.PreviewRestoreAsync(failureSource.Path!);
            Assert.True(failurePreview.CanRestore, failurePreview.Message);
            fault.TargetHash = await FileHashAsync(failureSource.Path!);
            fault.Enabled = true;
            var failedRestore = await service.RestoreAsync(failureSource.Path!, failurePreview.ConfirmationToken!);
            fault.Enabled = false;

            Assert.False(failedRestore.Success);
            Assert.True(fault.Injected, "The failure must be injected after the target database was replaced by the previewed restore source.");
            Assert.Contains("Injected post-swap", failedRestore.Error);
            Assert.NotNull(failedRestore.ProtectionBackupPath);
            Assert.True(File.Exists(failedRestore.ProtectionBackupPath));
            await using var rollbackVerification = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = dbPath, ForeignKeys = true, Pooling = false }.ToString()).Options);
            Assert.Equal("current", await rollbackVerification.Settings.Where(x => x.Key == "test-marker").Select(x => x.Value).SingleAsync());
            await AssertDatabaseIntegrityAsync(rollbackVerification);

            var auditPath = Path.Combine(root, "Logs", "maintenance.jsonl");
            var failures = File.ReadLines(auditPath).Select(line => JsonDocument.Parse(line)).Where(json =>
                json.RootElement.TryGetProperty("operation", out var operation) && operation.GetString() == "restore"
                && json.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean()).ToArray();
            Assert.NotEmpty(failures);
            foreach (var failure in failures) failure.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class TestWorkbench(string backupDirectory) : IWorkbenchService
    {
        public Task<WorkItemDetailDto> UpdateProgress(Guid id, UpdateProgressRequestDto request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkItemDeletePreviewDto?> GetDeletePreview(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeletedWorkItemDto>> GetRecycleBin(CancellationToken ct = default) => throw new NotSupportedException();
        public Task Delete(Guid id, long version, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreDeleted(Guid id, long version, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkbenchSettingsDto> GetSettings(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkbenchSettingsDto(0, 1, 7, 1, backupDirectory, 30));
        public Task<DashboardDto> GetDashboard(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WorkItemListResultDto> GetList(WorkItemListQueryDto query, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WorkItemDetailDto?> GetDetail(Guid workItemId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WorkItemEditorDto> GetEditor(Guid? workItemId = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WorkItemDetailDto> Create(WorkItemSaveRequestDto request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WorkItemDetailDto> Update(Guid workItemId, WorkItemSaveRequestDto request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WorkItemDetailDto> CreateAdditionalWork(Guid sourceWorkItemId, WorkItemSaveRequestDto request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WorkItemDetailDto> ChangeStatus(Guid workItemId, WorkStatusChangeRequestDto request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WorkItemDetailDto> Archive(Guid workItemId, ArchiveRequestDto request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WorkbenchSettingsDto> SaveSettings(WorkbenchSettingsDto settings, long expectedVersion, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BackupResultDto> CreateBackup(string? destinationPath = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ProjectPathCheckDto CheckProjectPath(string? projectPath) => throw new NotImplementedException();
    }

    private sealed class FailingMaintenance : IMaintenanceService
    {
        public Task<MaintenanceStatusDto> GetMaintenanceStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MaintenanceStatusDto("", "", true, false, null));
        public Task<IReadOnlyList<BackupFileDto>> ListBackupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BackupFileDto>>([]);
        public Task<MaintenanceBackupResultDto> CreateBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MaintenanceBackupResultDto(false, null, null, "受控测试中的目录不可用"));
        public Task<RestorePreviewDto> PreviewRestoreAsync(string backupPath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RestoreResultDto> RestoreAsync(string backupPath, string confirmationToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RequestExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PostSwapFaultInterceptor(string databasePath) : DbCommandInterceptor
    {
        public string TargetHash { get; set; } = "";
        public bool Enabled { get; set; }
        public bool Injected { get; private set; }

        private void MaybeInject(DbCommand command)
        {
            if (!Enabled || Injected || command.Connection is not SqliteConnection connection
                || !string.Equals(Path.GetFullPath(connection.DataSource), Path.GetFullPath(databasePath), StringComparison.OrdinalIgnoreCase)) return;
            using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, TargetHash, StringComparison.OrdinalIgnoreCase)) return;
            Injected = true;
            throw new InvalidOperationException("Injected post-swap database migration failure.");
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { MaybeInject(command); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { MaybeInject(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { MaybeInject(command); return result; }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { MaybeInject(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { MaybeInject(command); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { MaybeInject(command); return ValueTask.FromResult(result); }
    }

    private static async Task<string> FileHashAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task AssertDatabaseIntegrityAsync(DeskDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var integrity = context.Database.GetDbConnection().CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", Convert.ToString(await integrity.ExecuteScalarAsync()));
            await using var foreignKeys = context.Database.GetDbConnection().CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            await using var rows = await foreignKeys.ExecuteReaderAsync();
            Assert.False(await rows.ReadAsync());
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource started = new();
        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopping.Token;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() { stopping.Dispose(); started.Dispose(); }
    }
}
