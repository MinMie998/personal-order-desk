using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.Settings;

public sealed class IndexModel(IWorkbenchService workbench, IMaintenanceService maintenance) : PageModel
{
    [BindProperty] public WorkbenchSettingsDto Settings { get; set; } = new(0, 1, 7, 1, "", 30);
    [BindProperty] public bool ConfirmAutomaticDateRecalculation { get; set; }
    [BindProperty] public string? RestoreBackupPath { get; set; }
    [BindProperty] public string? RestoreConfirmationToken { get; set; }
    [BindProperty] public bool ConfirmRestore { get; set; }
    public MaintenanceStatusDto? MaintenanceStatus { get; private set; }
    public IReadOnlyList<BackupFileDto> Backups { get; private set; } = [];
    public RestorePreviewDto? RestorePreview { get; private set; }
    public RestoreResultDto? RestoreResult { get; private set; }
    public MaintenanceBackupResultDto? MaintenanceBackupResult { get; private set; }
    public string? Notice { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) { Settings = await workbench.GetSettings(cancellationToken); await LoadMaintenance(cancellationToken); }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (HasBindingErrors(nameof(Settings), nameof(ConfirmAutomaticDateRecalculation)))
        {
            ErrorMessage = "设置输入格式无法识别，请检查后重试；没有保存任何更改。";
            await LoadMaintenance(cancellationToken);
            return Page();
        }
        if (Settings.NonPhysicalDefaultDurationDays < 1 || Settings.PhysicalReminderLeadDays < 0 || Settings.DigitalReminderLeadDays < 0 || Settings.BackupRetentionDays < 1)
        {
            ModelState.AddModelError(string.Empty, "默认工期必须至少 1 天；提醒天数可为 0；备份保留天数必须至少 1 天。");
            await LoadMaintenance(cancellationToken);
            return Page();
        }
        try
        {
            var current = await workbench.GetSettings(cancellationToken);
            if (Settings.NonPhysicalDefaultDurationDays != current.NonPhysicalDefaultDurationDays && current.AffectedAutomaticDueDateCount > 0 && !ConfirmAutomaticDateRecalculation)
            {
                Settings = Settings with { AffectedAutomaticDueDateCount = current.AffectedAutomaticDueDateCount };
                ModelState.AddModelError(string.Empty, $"更改默认工期会重算 {current.AffectedAutomaticDueDateCount} 项 Auto 截止日期。请先确认影响后再保存。");
                await LoadMaintenance(cancellationToken);
                return Page();
            }
            Settings = await workbench.SaveSettings(Settings, Settings.Version, cancellationToken);
            Notice = "设置已保存；Auto 截止日期将在下次读取时依新默认工期重算。";
        }
        catch (DbUpdateConcurrencyException)
        {
            ErrorMessage = "设置已在其他页面中更新。请刷新后确认当前值，再重新保存。";
            Settings = await workbench.GetSettings(cancellationToken);
        }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        catch (ArgumentException ex) { ErrorMessage = ex.Message; }
        catch (UnauthorizedAccessException) { ErrorMessage = "备份目录不可写，请选择可访问的目录。"; }
        await LoadMaintenance(cancellationToken); return Page();
    }

    public async Task<IActionResult> OnPostPreviewRestoreAsync(CancellationToken ct)
    {
        RestoreBackupPath = RestoreBackupPath?.Trim();
        if (string.IsNullOrWhiteSpace(RestoreBackupPath) || HasBindingErrors(nameof(RestoreBackupPath))) ErrorMessage = "请选择一个备份文件，并检查输入格式。";
        else
        {
            try { RestorePreview = await maintenance.PreviewRestoreAsync(RestoreBackupPath, ct); RestoreBackupPath = RestorePreview.BackupPath ?? RestoreBackupPath; RestoreConfirmationToken = RestorePreview.ConfirmationToken; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException) { ErrorMessage = $"无法预览此备份：{ex.Message}"; }
        }
        await LoadMaintenance(ct); return Page();
    }

    public async Task<IActionResult> OnPostRestoreAsync(CancellationToken ct)
    {
        RestoreBackupPath = RestoreBackupPath?.Trim();
        if (string.IsNullOrWhiteSpace(RestoreBackupPath) || string.IsNullOrWhiteSpace(RestoreConfirmationToken) || !ConfirmRestore || HasBindingErrors(nameof(RestoreBackupPath), nameof(RestoreConfirmationToken), nameof(ConfirmRestore)))
            ErrorMessage = "请先预览备份，并在预览确认可恢复后明确确认操作。";
        else
        {
            try { RestoreResult = await maintenance.RestoreAsync(RestoreBackupPath, RestoreConfirmationToken, ct); ErrorMessage = RestoreResult.Success ? null : $"恢复未完成：{RestoreResult.Error ?? "请检查备份文件后重试。"}"; Notice = RestoreResult.Success ? $"恢复完成。恢复前保护备份：{RestoreResult.ProtectionBackupPath}" : null; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException) { ErrorMessage = $"恢复未完成，当前数据仍可使用：{ex.Message}"; }
        }
        RestorePreview = null; RestoreConfirmationToken = null; ConfirmRestore = false; Settings = await workbench.GetSettings(ct); await LoadMaintenance(ct); return Page();
    }

    public async Task<IActionResult> OnPostCreateBackupAsync(CancellationToken ct)
    {
        try { MaintenanceBackupResult = await maintenance.CreateBackupAsync(ct); if (MaintenanceBackupResult.Success) Notice = $"备份已创建：{MaintenanceBackupResult.Path}"; else ErrorMessage = $"备份失败：{MaintenanceBackupResult.Error ?? "请检查备份目录后重试。"}"; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { ErrorMessage = $"备份失败：{ex.Message}"; }
        Settings = await workbench.GetSettings(ct); await LoadMaintenance(ct); return Page();
    }

    public async Task<IActionResult> OnPostExitAsync(CancellationToken ct)
    {
        Settings = await workbench.GetSettings(ct);
        try { await maintenance.RequestExitAsync(ct); Notice = "已收到退出请求，数据写入完成后应用将关闭。"; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { ErrorMessage = $"退出请求未能提交：{ex.Message}"; await LoadMaintenance(ct); }
        return Page();
    }

    private async Task LoadMaintenance(CancellationToken ct)
    {
        if (!string.Equals(Request.Query["handler"], "Save", StringComparison.OrdinalIgnoreCase))
        {
            Settings = await workbench.GetSettings(ct);
            foreach (var key in ModelState.Keys.Where(key => key == nameof(Settings) || key.StartsWith(nameof(Settings) + ".", StringComparison.Ordinal)).ToArray())
                ModelState.Remove(key);
        }
        MaintenanceStatus = await maintenance.GetMaintenanceStatusAsync(ct);
        Backups = await maintenance.ListBackupsAsync(ct);
    }

    private bool HasBindingErrors(params string[] fields) => ModelState.Any(entry => entry.Value?.Errors.Count > 0
        && fields.Any(field => entry.Key == field || entry.Key.StartsWith(field + ".", StringComparison.Ordinal)));
}
