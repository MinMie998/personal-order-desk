namespace PersonalOrderDesk.Services;

/// <summary>Stable application maintenance contract used by the UI.</summary>
public interface IMaintenanceService
{
    Task<MaintenanceStatusDto> GetMaintenanceStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackupFileDto>> ListBackupsAsync(CancellationToken cancellationToken = default);
    Task<MaintenanceBackupResultDto> CreateBackupAsync(CancellationToken cancellationToken = default);
    Task<MaintenanceBackupResultDto> CreateImportProtectionBackupAsync(CancellationToken cancellationToken = default)
        => CreateBackupAsync(cancellationToken);
    Task<RestorePreviewDto> PreviewRestoreAsync(string backupPath, CancellationToken cancellationToken = default);
    Task<RestoreResultDto> RestoreAsync(string backupPath, string confirmationToken, CancellationToken cancellationToken = default);
    Task RequestExitAsync(CancellationToken cancellationToken = default);
}

public sealed record MaintenanceStatusDto(string DataDirectory, string DatabasePath, bool IsReady, bool IsMaintenanceInProgress, string? Message);
public sealed record BackupFileDto(string Path, DateTime LastWriteTime, long SizeBytes, bool IsValid, string? Message);
public sealed record MaintenanceBackupResultDto(bool Success, string? Path, DateTime? CreatedAt, string? Error);
public sealed record RestorePreviewDto(bool CanRestore, string? ConfirmationToken, string? BackupPath, string? BackupApplicationId,
    string? BackupSchemaVersion, bool IntegrityCheckPassed, bool ForeignKeyCheckPassed, string? Message);
public sealed record RestoreResultDto(bool Success, string? ProtectionBackupPath, string? Error);
