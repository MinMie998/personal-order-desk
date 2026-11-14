using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Data;

namespace PersonalOrderDesk.Services;

public sealed class MaintenanceService(
    DeskDbContext db,
    IWorkbenchService workbench,
    MaintenanceCoordinator coordinator,
    DailyBackupScheduler dailyBackups,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    ILogger<MaintenanceService> logger) : IMaintenanceService
{
    private const string ProductId = "PersonalOrderDesk";
    private static readonly ConcurrentDictionary<string, PreviewEntry> previews = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private string DataDirectory => Path.GetFullPath(configuration["PersonalOrderDesk:DataDirectory"]
        ?? Environment.GetEnvironmentVariable("PERSONAL_ORDER_DESK_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductId));
    private string DatabasePath => Path.GetFullPath(db.Database.GetDbConnection().DataSource);
    private string AuditPath => Path.Combine(DataDirectory, "Logs", "maintenance.jsonl");

    public Task<MaintenanceStatusDto> GetMaintenanceStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new MaintenanceStatusDto(DataDirectory, DatabasePath, File.Exists(DatabasePath), coordinator.IsMaintenance,
            coordinator.IsMaintenance ? "维护操作正在执行。" : dailyBackups.LastError is null ? null : $"今日自动备份尚未成功，普通写操作暂时暂停：{dailyBackups.LastError}"));

    public async Task<IReadOnlyList<BackupFileDto>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await workbench.GetSettings(cancellationToken);
        var path = Path.GetFullPath(settings.BackupDirectory);
        if (!Directory.Exists(path)) return [];
        var files = new List<BackupFileDto>();
        foreach (var file in Directory.EnumerateFiles(path, "*.db").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            (bool Valid, bool Integrity, bool ForeignKeys, string? ProductId, string? Schema, string? Message) validation;
            try { validation = await ValidateDatabaseAsync(file, requireManifest: true, cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or JsonException)
            { validation = (false, false, false, null, null, $"备份无法读取：{ex.Message}"); }
            var retentionDays = Math.Max(1, settings.BackupRetentionDays);
            // Key protection backups are never removed by ordinary retention.
            var isProtection = Path.GetFileName(file).Contains("protection", StringComparison.OrdinalIgnoreCase);
            if (!isProtection && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-retentionDays))
                continue;
            var info = new FileInfo(file);
            files.Add(new BackupFileDto(file, info.LastWriteTime, info.Length, validation.Valid, validation.Message));
        }
        return files;
    }

    public async Task<MaintenanceBackupResultDto> CreateBackupAsync(CancellationToken cancellationToken = default)
        => await CreateBackupCoreAsync(false, cancellationToken);

    public async Task<MaintenanceBackupResultDto> CreateImportProtectionBackupAsync(CancellationToken cancellationToken = default)
        => await CreateBackupCoreAsync(true, cancellationToken);

    private async Task<MaintenanceBackupResultDto> CreateBackupCoreAsync(bool importProtection, CancellationToken cancellationToken)
    {
        await using var operation = await coordinator.BeginBackupOperationAsync(cancellationToken);
        try
        {
            var settings = await workbench.GetSettings(cancellationToken);
            var prefix = importProtection ? "import-protection" : "workbench";
            var path = Path.GetFullPath(Path.Combine(settings.BackupDirectory, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await BackupDatabaseAsync(path, cancellationToken);
            await WriteManifestAsync(path, cancellationToken);
            var validation = await ValidateDatabaseAsync(path, true, cancellationToken);
            if (!validation.Valid) throw new InvalidDataException(validation.Message);
            await PruneBackupsAsync(settings.BackupDirectory, settings.BackupRetentionDays, cancellationToken);
            dailyBackups.ClearLastError();
            await AuditAsync("backup", true, path, null, cancellationToken);
            return new(true, path, DateTime.Now, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException or DbUpdateException)
        {
            logger.LogError(ex, "Database backup failed");
            await AuditAsync("backup", false, null, ex.Message, CancellationToken.None);
            return new(false, null, null, ex.Message);
        }
    }

    public async Task<RestorePreviewDto> PreviewRestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(backupPath.Trim()));
            if (string.Equals(fullPath, DatabasePath, StringComparison.OrdinalIgnoreCase))
                return new(false, null, fullPath, ProductId, null, false, false, "不能将当前数据库作为恢复来源。");
            var result = await ValidateDatabaseAsync(fullPath, true, cancellationToken);
            if (!result.Valid) return new(false, null, fullPath, result.ProductId, result.Schema, result.Integrity, result.ForeignKeys, result.Message);
            var upgradeError = await CheckUpgradeAsync(fullPath, cancellationToken);
            if (upgradeError is not null) return new(false, null, fullPath, result.ProductId, result.Schema, result.Integrity, result.ForeignKeys,
                $"备份无法升级到当前程序数据库版本：{upgradeError}");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var contentHash = Convert.ToHexString(await HashFileAsync(fullPath, cancellationToken));
            previews[fullPath] = new(token, contentHash, DateTimeOffset.UtcNow.AddMinutes(5));
            return new(true, token, fullPath, result.ProductId, result.Schema, result.Integrity, result.ForeignKeys,
                "备份属于本工作台，完整性与外键检查通过。确认后将先生成当前数据库保护备份，再恢复。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException or JsonException)
        { return new(false, null, backupPath, null, null, false, false, $"无法预览备份：{ex.Message}"); }
    }

    public async Task<RestoreResultDto> RestoreAsync(string backupPath, string confirmationToken, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(backupPath.Trim()));
        if (!previews.TryRemove(fullPath, out var entry) || entry.Expiry < DateTimeOffset.UtcNow
            || !IsValidConfirmation(entry.Token, confirmationToken))
            return new(false, null, "恢复预览已失效或确认令牌无效，请重新预览并确认。");

        string? protectionPath = null;
        try
        {
            var validated = await ValidateDatabaseAsync(fullPath, true, cancellationToken);
            if (!validated.Valid) throw new InvalidDataException($"恢复来源验证失败：{validated.Message}");
            var currentHash = Convert.ToHexString(await HashFileAsync(fullPath, cancellationToken));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(entry.ContentHash), Convert.FromHexString(currentHash)))
                throw new InvalidDataException("预览后备份内容已变化，请重新预览并确认。");
            await AuditCriticalAsync("restore-started", fullPath, null, cancellationToken);
            await using var maintenance = await coordinator.BeginMaintenanceAsync(cancellationToken);
            if (!db.Database.GetDbConnection().State.Equals(System.Data.ConnectionState.Closed)) await db.Database.CloseConnectionAsync();
            SqliteConnection.ClearAllPools();
            protectionPath = Path.Combine(DataDirectory, "Backups", $"restore-protection_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");
            Directory.CreateDirectory(Path.GetDirectoryName(protectionPath)!);
            await BackupFileAsync(DatabasePath, protectionPath, cancellationToken);
            await WriteManifestAsync(protectionPath, cancellationToken);
            var protectedState = await ValidateDatabaseAsync(protectionPath, true, cancellationToken);
            if (!protectedState.Valid) throw new InvalidDataException($"当前数据库保护备份验证失败：{protectedState.Message}");
            await AuditCriticalAsync("restore-protected", fullPath, protectionPath, cancellationToken);
            var stagePath = DatabasePath + ".restore-stage";
            try
            {
                File.Copy(fullPath, stagePath, true);
                File.Copy(fullPath + ".manifest.json", stagePath + ".manifest.json", true);
                var stagedHash = Convert.ToHexString(await HashFileAsync(stagePath, cancellationToken));
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(entry.ContentHash), Convert.FromHexString(stagedHash)))
                    throw new InvalidDataException("恢复来源在准备恢复期间发生变化，当前数据保持不变。");
                var stagedValidation = await ValidateDatabaseAsync(stagePath, true, cancellationToken);
                if (!stagedValidation.Valid) throw new InvalidDataException(stagedValidation.Message);
                SqliteConnection.ClearAllPools();
                DeleteSqliteSidecars(DatabasePath);
                if (File.Exists(DatabasePath + ".manifest.json")) File.Delete(DatabasePath + ".manifest.json");
                File.Replace(stagePath, DatabasePath, DatabasePath + ".pre-restore.db", true);
                await db.Database.MigrateAsync(cancellationToken);
                var after = await ValidateDatabaseAsync(DatabasePath, false, cancellationToken);
                if (!after.Valid) throw new InvalidDataException($"恢复后检查失败：{after.Message}");
            }
            catch
            {
                try { await db.Database.CloseConnectionAsync(); } catch { }
                SqliteConnection.ClearAllPools();
                DeleteSqliteSidecars(DatabasePath);
                if (File.Exists(DatabasePath + ".manifest.json")) File.Delete(DatabasePath + ".manifest.json");
                File.Copy(protectionPath, DatabasePath, true);
                throw;
            }
            finally
            {
                foreach (var path in new[] { stagePath, stagePath + ".manifest.json", stagePath + "-wal", stagePath + "-shm" })
                    if (File.Exists(path)) File.Delete(path);
            }
            if (File.Exists(DatabasePath + ".pre-restore.db")) File.Delete(DatabasePath + ".pre-restore.db");
            await AuditAsync("restore", true, fullPath, $"Protection backup: {protectionPath}", CancellationToken.None);
            return new(true, protectionPath, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException or DbUpdateException or InvalidDataException or JsonException)
        {
            logger.LogError(ex, "Database restore failed");
            await AuditAsync("restore", false, fullPath, ex.Message, CancellationToken.None);
            return new(false, protectionPath, ex.Message);
        }
    }

    public Task RequestExitAsync(CancellationToken cancellationToken = default)
    {
        _ = Task.Run(async () => { await Task.Delay(1500); lifetime.StopApplication(); });
        return Task.CompletedTask;
    }

    private async Task BackupDatabaseAsync(string destination, CancellationToken ct)
    {
        var source = db.Database.GetDbConnection() as SqliteConnection ?? throw new InvalidOperationException("当前数据库不是 SQLite。");
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, Pooling = false }.ToString());
            await target.OpenAsync(ct);
            source.BackupDatabase(target);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task BackupFileAsync(string sourcePath, string destination, CancellationToken ct)
    {
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, ForeignKeys = true, Pooling = false }.ToString());
        await source.OpenAsync(ct);
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, Pooling = false }.ToString());
        await target.OpenAsync(ct);
        source.BackupDatabase(target);
    }

    private async Task WriteManifestAsync(string databasePath, CancellationToken ct)
    {
        var schema = db.Database.GetMigrations();
        var manifest = new BackupManifest(ProductId, schema.LastOrDefault() ?? "", DateTimeOffset.UtcNow);
        var text = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(databasePath + ".manifest.json", text, ct);
    }

    private async Task<(bool Valid, bool Integrity, bool ForeignKeys, string? ProductId, string? Schema, string? Message)> ValidateDatabaseAsync(string path, bool requireManifest, CancellationToken ct)
    {
        if (!File.Exists(path)) return (false, false, false, null, null, "备份文件不存在。");
        BackupManifest? manifest = null;
        if (File.Exists(path + ".manifest.json"))
            manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(path + ".manifest.json", ct), JsonOptions);
        if (requireManifest && manifest is null) return (false, false, false, null, null, "缺少应用身份/数据库版本清单，不能安全恢复该文件。");
        if (manifest is not null && manifest.ProductId != ProductId) return (false, false, false, manifest.ProductId, manifest.Schema, "备份来自其他应用。");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, ForeignKeys = true }.ToString());
        await connection.OpenAsync(ct);
        string? integrity;
        await using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA integrity_check;"; integrity = Convert.ToString(await command.ExecuteScalarAsync(ct)); }
        var integrityOk = string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase);
        var foreignOk = true;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            foreignOk = !await reader.ReadAsync(ct);
        }
        var hasHistory = false;
        await using (var command = connection.CreateCommand()) { command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';"; hasHistory = Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0; }
        if (!hasHistory) return (false, integrityOk, foreignOk, manifest?.ProductId, manifest?.Schema, "数据库缺少本应用 EF 迁移历史表。");
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        string? actualSchema = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                actualSchema = reader.GetString(0);
                if (!known.Contains(actualSchema)) return (false, integrityOk, foreignOk, manifest?.ProductId, manifest?.Schema, "数据库包含当前程序不认识的更新版本，拒绝恢复。");
            }
        }
        if (manifest is not null && !string.Equals(manifest.Schema, actualSchema ?? "", StringComparison.Ordinal))
            return (false, integrityOk, foreignOk, manifest.ProductId, manifest.Schema, "备份版本清单与数据库迁移记录不一致。");
        if (!integrityOk || !foreignOk) return (false, integrityOk, foreignOk, manifest?.ProductId, manifest?.Schema, "数据库完整性或外键检查未通过。");
        return (true, true, true, manifest?.ProductId ?? ProductId, manifest?.Schema, null);
    }

    private async Task<string?> CheckUpgradeAsync(string sourcePath, CancellationToken ct)
    {
        var tempRoot = Path.Combine(DataDirectory, "tmp");
        Directory.CreateDirectory(tempRoot);
        var tempPath = Path.Combine(tempRoot, $"restore-preview-{Guid.NewGuid():N}.db");
        try
        {
            await BackupFileAsync(sourcePath, tempPath, ct);
            var options = new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(new SqliteConnectionStringBuilder
                { DataSource = tempPath, ForeignKeys = true, Pooling = false, DefaultTimeout = 5 }.ToString()).Options;
            await using var probe = new DeskDbContext(options);
            await probe.Database.MigrateAsync(ct);
            await using var connection = probe.Database.GetDbConnection() as SqliteConnection;
            if (connection is not null && connection.State == System.Data.ConnectionState.Closed) await connection.OpenAsync(ct);
            await using var command = connection!.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(Convert.ToString(await command.ExecuteScalarAsync(ct)), "ok", StringComparison.OrdinalIgnoreCase))
                return "升级后完整性检查未通过。";
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) return "升级后外键检查未通过。";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException or DbUpdateException)
        { return ex.Message; }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { tempPath, tempPath + "-wal", tempPath + "-shm" })
                try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private async Task PruneBackupsAsync(string directory, int retentionDays, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return;
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
        foreach (var file in Directory.EnumerateFiles(directory, "workbench_*.db"))
        {
            ct.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                File.Delete(file);
                File.Delete(file + ".manifest.json");
            }
        }
    }

    private static void DeleteSqliteSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = path + suffix;
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }

    private static bool IsValidConfirmation(string expected, string? supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length != expected.Length) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(supplied)); }
        catch (FormatException) { return false; }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, ct);
    }

    private async Task AuditAsync(string operation, bool success, string? path, string? detail, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AuditPath)!);
            var entry = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, operation, success, path, detail }, JsonOptions);
            await File.AppendAllTextAsync(AuditPath, entry + Environment.NewLine, ct);
        }
        catch (Exception ex) { logger.LogError(ex, "Could not write maintenance audit log"); }
    }

    private async Task AuditCriticalAsync(string operation, string? path, string? detail, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuditPath)!);
        var entry = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, operation, path, detail }, JsonOptions);
        await File.AppendAllTextAsync(AuditPath, entry + Environment.NewLine, ct);
    }

    private sealed record BackupManifest(string ProductId, string Schema, DateTimeOffset CreatedAt);
    private sealed record PreviewEntry(string Token, string ContentHash, DateTimeOffset Expiry);
}
