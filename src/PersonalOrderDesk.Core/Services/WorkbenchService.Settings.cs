using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public sealed partial class WorkbenchService
{
    public async Task<WorkbenchSettingsDto> GetSettings(CancellationToken cancellationToken = default)
    {
        var current = await ReadSettings(cancellationToken);
        var affected = await db.WorkItems.CountAsync(x => x.DueDateMode == DueDateMode.Auto
            && x.ScheduleCategory == ScheduleCategory.Digital && x.ReceivedDate != null, cancellationToken);
        return current with { AffectedAutomaticDueDateCount = affected };
    }

    public async Task<WorkbenchSettingsDto> SaveSettings(WorkbenchSettingsDto proposed, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ValidateSettings(proposed);
        var backupDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(proposed.BackupDirectory.Trim()));
        var current = await ReadSettings(cancellationToken);
        if (current.Version != expectedVersion) throw new DbUpdateConcurrencyException("设置已被其他页面修改，请刷新后重试。");
        EnsureWritableDirectory(backupDirectory);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.Settings.SingleOrDefaultAsync(x => x.Key == SettingsKey, cancellationToken);
        var newVersion = current.Version + 1;
        var affectedItems = current.NonPhysicalDefaultDurationDays == proposed.NonPhysicalDefaultDurationDays
            ? new List<WorkItem>()
            : await db.WorkItems.Where(x => x.DueDateMode == DueDateMode.Auto
                && x.ScheduleCategory == ScheduleCategory.Digital && x.ReceivedDate != null).ToListAsync(cancellationToken);
        var newSettings = proposed with { Version = newVersion, BackupDirectory = backupDirectory,
            AffectedAutomaticDueDateCount = affectedItems.Count };
        var serialized = SerializeSettings(newSettings);
        if (row is null)
        {
            if (current.Version != 0) throw new DbUpdateConcurrencyException("设置已被其他页面修改，请刷新后重试。");
            db.Settings.Add(new Setting { Key = SettingsKey, Value = serialized, UpdatedAt = clock.Now });
        }
        else
        {
            var oldValue = row.Value;
            var changed = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Settings SET Value={serialized}, UpdatedAt={clock.Now} WHERE Key={SettingsKey} AND Value={oldValue}", cancellationToken);
            if (changed != 1) throw new DbUpdateConcurrencyException("设置已被其他页面修改，请刷新后重试。");
            var entry = db.Entry(row);
            entry.Property(x => x.Value).CurrentValue = serialized;
            entry.Property(x => x.Value).OriginalValue = serialized;
            entry.Property(x => x.Value).IsModified = false;
            entry.Property(x => x.UpdatedAt).CurrentValue = clock.Now;
            entry.Property(x => x.UpdatedAt).OriginalValue = clock.Now;
            entry.Property(x => x.UpdatedAt).IsModified = false;
        }
        foreach (var item in affectedItems)
        {
            item.EffectiveDueDate = WorkItemService.CalculateDue(item, proposed.NonPhysicalDefaultDurationDays);
            item.Version++;
            item.UpdatedAt = clock.Now;
        }
        db.ActivityLogs.Add(new ActivityLog { EntityType = "Settings", EntityId = Guid.Empty,
            ActionType = "SettingsChanged", RecordedAt = clock.Now, Summary = "工作台设置已更新",
            BeforeData = System.Text.Json.JsonSerializer.Serialize(current, SettingsJsonOptions),
            AfterData = System.Text.Json.JsonSerializer.Serialize(newSettings, SettingsJsonOptions), SourceKind = LogSourceKind.System });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return newSettings;
    }

    public async Task<BackupResultDto> CreateBackup(string? destinationPath = null, CancellationToken cancellationToken = default)
    {
        var destination = string.Empty;
        try
        {
            var settings = await ReadSettings(cancellationToken);
            destination = Path.GetFullPath(string.IsNullOrWhiteSpace(destinationPath)
                ? Path.Combine(settings.BackupDirectory, $"workbench_{clock.Now:yyyyMMdd_HHmmss}.db")
                : Environment.ExpandEnvironmentVariables(destinationPath.Trim()));
            var directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("备份目标路径无效。");
            EnsureWritableDirectory(directory);
            var source = db.Database.GetDbConnection() as SqliteConnection
                ?? throw new InvalidOperationException("当前数据库提供程序不是 SQLite。");
            var sourcePath = source.DataSource;
            if (!string.IsNullOrWhiteSpace(sourcePath) && sourcePath != ":memory:"
                && string.Equals(Path.GetFullPath(sourcePath), destination, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("备份目标不能与当前数据库相同。");
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, Pooling = false }.ToString());
                await target.OpenAsync(cancellationToken);
                source.BackupDatabase(target);
            }
            finally { await db.Database.CloseConnectionAsync(); }
            db.ActivityLogs.Add(new ActivityLog { EntityType = "Database", EntityId = Guid.Empty,
                ActionType = "BackupCreated", RecordedAt = clock.Now, Summary = "已创建数据库备份", SourceKind = LogSourceKind.System });
            await db.SaveChangesAsync(cancellationToken);
            return new BackupResultDto(true, destination, clock.Now, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException)
        {
            logger.LogError(ex, "Database backup failed");
            return new BackupResultDto(false, null, null, $"备份失败：{ex.Message}");
        }
    }

    public ProjectPathCheckDto CheckProjectPath(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return new ProjectPathCheckDto(projectPath, false, false, false, "未填写工程目录。");
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(projectPath.Trim()));
            var exists = Directory.Exists(fullPath);
            return new ProjectPathCheckDto(projectPath, true, exists, exists,
                exists ? "目录可访问。" : "工程目录不存在或不可访问；不影响工作项保存。");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ProjectPathCheckDto(projectPath, true, false, false, "工程目录路径无效或不可访问；不影响工作项保存。");
        }
    }

    private static string SerializeSettings(WorkbenchSettingsDto settings) => System.Text.Json.JsonSerializer.Serialize(
        settings with { AffectedAutomaticDueDateCount = 0 }, SettingsJsonOptions);

    private static void ValidateSettings(WorkbenchSettingsDto settings)
    {
        if (settings.NonPhysicalDefaultDurationDays < 1) throw new ArgumentOutOfRangeException(nameof(settings.NonPhysicalDefaultDurationDays));
        if (settings.PhysicalReminderLeadDays < 0 || settings.DigitalReminderLeadDays < 0)
            throw new ArgumentOutOfRangeException(nameof(settings.PhysicalReminderLeadDays));
        if (settings.BackupRetentionDays < 1) throw new ArgumentOutOfRangeException(nameof(settings.BackupRetentionDays));
        if (string.IsNullOrWhiteSpace(settings.BackupDirectory)) throw new ArgumentException("备份目录不能为空。");
        if (!Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(settings.BackupDirectory.Trim())))
            throw new ArgumentException("备份目录必须是绝对路径。");
    }

    private static void EnsureWritableDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}.tmp");
        using (File.Create(probe)) { }
        File.Delete(probe);
    }
}
