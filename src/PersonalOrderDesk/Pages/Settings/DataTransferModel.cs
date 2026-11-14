using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.Settings;

public sealed class DataTransferModel(ITransferService transfer, IMaintenanceService maintenance) : PageModel
{
    private const long MaxUploadBytes = 25 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, CachedPreview> Previews = new();
    [BindProperty] public IFormFile? Workbook { get; set; }
    [BindProperty] public string? PreviewKey { get; set; }
    [BindProperty] public List<DecisionInput> Rows { get; set; } = [];
    [BindProperty] public bool ConfirmWholeBatch { get; set; }
    public TransferPreview? Preview { get; private set; }
    public IReadOnlyList<TransferDifference> Differences { get; private set; } = [];
    public IReadOnlyList<string> IdentityWarnings { get; private set; } = [];
    public TransferImportResult? ImportResult { get; private set; }
    public string? Notice { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) { _ = await maintenance.GetMaintenanceStatusAsync(ct); }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        if (Workbook is null || Workbook.Length == 0 || Workbook.Length > MaxUploadBytes || !Path.GetExtension(Workbook.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        { Error = "请选择不超过 25 MB 的 .xlsx 原始工作簿。导出报告不支持重新导入。"; return Page(); }
        string? privateDirectory = null;
        try
        {
            var status = await maintenance.GetMaintenanceStatusAsync(ct);
            var directory = Path.Combine(status.DataDirectory, "Temp", "Imports"); Directory.CreateDirectory(directory);
            foreach (var old in Directory.EnumerateDirectories(directory).Where(path => Directory.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-3)))
                try { Directory.Delete(old, recursive: true); } catch (IOException) { }
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var safeName = new string(Path.GetFileName(Workbook.FileName).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
            if (!Path.GetExtension(safeName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) { Error = "文件名无效，请重新选择原始 .xlsx 工作簿。"; return Page(); }
            privateDirectory = Path.Combine(directory, key); Directory.CreateDirectory(privateDirectory);
            var path = Path.Combine(privateDirectory, safeName);
            await using (var target = System.IO.File.Create(path)) await Workbook.CopyToAsync(target, ct);
            await using var source = System.IO.File.OpenRead(path);
            var preview = await transfer.PreviewAsync(source, safeName, System.IO.File.GetLastWriteTimeUtc(path), ct);
            Preview = preview;
            PreviewKey = key;
            Previews[key] = new CachedPreview(path, preview, DateTimeOffset.UtcNow.AddHours(2));
            Rows = preview.Decisions.Select(DecisionInput.From).ToList();
            IdentityWarnings = FindIdentityWarnings(preview);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException) { if (privateDirectory is not null) TryDeleteDirectory(privateDirectory); Error = $"预览失败：{ex.Message}"; }
        return Page();
    }

    public async Task<IActionResult> OnPostDifferencesAsync(CancellationToken ct)
    {
        var cached = GetCached(); if (cached is null) return BadPreview();
        if (!ModelState.IsValid) { Preview = cached.Preview; IdentityWarnings = FindIdentityWarnings(cached.Preview); Error = "日期输入格式无效，请使用日期选择器修正；未调用差异核对。"; return Page(); }
        try
        {
            var decisions = DecisionsFrom(cached);
            await using var file = System.IO.File.OpenRead(cached.Path);
            var preview = await transfer.PreviewDifferencesAsync(file, cached.Preview.FileName, decisions, ct);
            Preview = preview;
            Differences = preview.Differences;
            Previews[PreviewKey!] = cached with { Preview = preview, DifferencesReviewed = true };
            Rows = preview.Decisions.Select(DecisionInput.From).ToList();
            IdentityWarnings = FindIdentityWarnings(preview);
            Notice = "已根据当前核对选择刷新差异预览；正式数据尚未改变。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException) { Error = $"差异预览失败：{ex.Message}"; Preview = cached.Preview; }
        return Page();
    }

    public Task<IActionResult> OnPostAsync(string? action, CancellationToken ct) => string.Equals(action, "Confirm", StringComparison.Ordinal)
        ? OnPostConfirmAsync(ct) : OnPostDifferencesAsync(ct);

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken ct)
    {
        var cached = GetCached(); if (cached is null) return BadPreview();
        Preview = cached.Preview;
        IdentityWarnings = FindIdentityWarnings(cached.Preview);
        foreach (var row in Rows)
            row.SuggestedWorkItemGroupKey = cached.Preview.Decisions.FirstOrDefault(x => x.SheetName == row.SheetName && x.RowNumber == row.RowNumber)?.SuggestedWorkItemGroupKey;
        if (!cached.DifferencesReviewed) { Error = "请先按当前核对选择刷新差异预览；正式导入前必须查看一次差异。"; return Page(); }
        if (!ModelState.IsValid) { Error = "日期输入格式无效，请使用日期选择器修正；未写入数据。"; return Page(); }
        if (!ConfirmWholeBatch) { Error = "请明确勾选整批确认；未写入数据。"; return Page(); }
        try
        {
            var decisions = DecisionsFrom(cached);
            await using var input = System.IO.File.OpenRead(cached.Path);
            var current = await transfer.PreviewAsync(input, cached.Preview.FileName, cancellationToken: ct);
            if (!StringComparer.Ordinal.Equals(current.FileFingerprint, cached.Preview.FileFingerprint)) { Error = "临时工作簿内容与预览不一致，请重新上传并核对。"; return Page(); }
            var result = await transfer.ConfirmImportAsync(new TransferImportRequest(cached.Path, decisions, true, cached.Preview.FileFingerprint), ct);
            ImportResult = result;
            Notice = result.WasDuplicate ? "此工作簿已导入；没有重复写入。" : $"导入操作已完成：处理 {result.ImportedRows} 行，跳过 {result.SkippedRows} 行。";
            Previews.TryRemove(PreviewKey!, out _); TryDeleteDirectory(Path.GetDirectoryName(cached.Path)!);
            Preview = null; PreviewKey = null; Rows = []; ConfirmWholeBatch = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException) { Error = $"整批导入未完成：{ex.Message}。服务会整体回滚失败批次，请检查提示后重新预览。"; }
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        try
        {
            var bytes = await transfer.ExportReportAsync(ct);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"个人接单工作台-只读报告-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { Error = $"报告生成失败：{ex.Message}"; return Page(); }
    }

    private CachedPreview? GetCached()
    {
        if (PreviewKey is null || !Previews.TryGetValue(PreviewKey, out var value)) return null;
        if (value.ExpiresAt > DateTimeOffset.UtcNow) return value;
        if (Previews.TryRemove(PreviewKey, out var expired)) TryDeleteDirectory(Path.GetDirectoryName(expired.Path)!);
        return null;
    }
    private TransferRowDecision[] DecisionsFrom(CachedPreview cached) => Rows.Select(row =>
    {
        var decision = row.ToDecision();
        var trusted = cached.Preview.Decisions.FirstOrDefault(x => x.SheetName == row.SheetName && x.RowNumber == row.RowNumber);
        return trusted is null ? decision : decision with { SuggestedWorkItemGroupKey = trusted.SuggestedWorkItemGroupKey };
    }).ToArray();
    private IActionResult BadPreview() { Error = "预览已过期或找不到对应文件，请重新上传。"; return Page(); }
    private static IReadOnlyList<string> FindIdentityWarnings(TransferPreview preview) => preview.Sheets.SelectMany(x => x.Rows)
        .Select(r => (Order: r.Values.GetValueOrDefault("BusinessOrderNo")?.Trim(), Name: r.Values.GetValueOrDefault("ProjectName")?.Trim()))
        .Where(x => !string.IsNullOrWhiteSpace(x.Order) && !string.IsNullOrWhiteSpace(x.Name))
        .GroupBy(x => x.Order!, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        .Select(g => $"业务编号 {g.Key} 对应多个项目名称：{string.Join("、", g.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase))}。默认分别保留；请明确合并为统一名称、分别建组或留待核对。")
        .ToArray();
    public static bool IsReceiptCandidate(TransferSourceRow? source) => source is not null
        && source.Values.GetValueOrDefault("PaymentStatus")?.Contains("已结款", StringComparison.Ordinal) == true
        && decimal.TryParse(source.Values.GetValueOrDefault("QuotedAmount"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount)
        && amount > 0 && amount * 100m == decimal.Truncate(amount * 100m);
    public static string CnStatus<T>(T value) where T : struct, Enum => value.ToString() switch
    {
        "NotStarted" => "未开始", "InProgress" => "进行中", "Completed" => "已完成", "NeedsRework" => "需返工", "Cancelled" => "已取消", "Unknown" => "未知",
        "Paid" => "已支付", "Unpaid" => "未支付", "None" => "无需寄送", "Required" => "需要寄送", "Pending" => "待确认", _ => value.ToString()
    };
    public static string CnField(string field)
    {
        if (field.StartsWith("Source:", StringComparison.Ordinal)) return "来源变化：" + CnField(field[7..]);
        return field switch
    {
        "BusinessOrderNo" => "业务编号", "ProjectName" => "项目名称", "ProjectType" => "项目类型", "ReceivedDate" => "接单日期", "EffectiveDueDate" => "截止日期",
        "WorkStatus" => "工作状态", "CompletedDate" => "完成日期", "CurrentWork" => "当前工作", "NextStep" => "下一步", "ProjectPath" => "工程目录", "Notes" => "备注",
        "QuotedAmount" => "报价金额", "ExpenseAmount" => "支出", "ExpenseStatus" => "支出记录状态", "PaymentStatus" => "结款状态", "PaidDate" => "结款日期",
        "ShipmentStatus" => "物流状态", "TrackingNumber" => "快递单号", "ShippedDate" => "寄出日期", "DeliveredDate" => "收货日期", "CustomerPlatform" => "客户平台",
        "ReminderDays" => "提醒提前天数", "Profit" => "利润（来源值）", "CurrentStage" => "当前阶段", "CompletionPercent" => "完成比例",
        "LastUpdated" => "最近更新", "ReworkStatus" => "返工状态", "OrderStatus" => "订单状态", _ => field
        };
    }
    public string DateConflictKey(TransferDateIssue issue)
    {
        var row = Rows.FirstOrDefault(x => x.SheetName == issue.SheetName && x.RowNumber == issue.RowNumber);
        var key = row?.NewWorkItemGroupKey is { Length: > 0 } newKey ? newKey : row?.SuggestedWorkItemGroupKey ?? $"{issue.SheetName}:{issue.RowNumber}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private sealed record CachedPreview(string Path, TransferPreview Preview, DateTimeOffset ExpiresAt, bool DifferencesReviewed = false);
    private static void TryDeleteDirectory(string path) { try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

public sealed class DecisionInput
{
    public string SheetName { get; set; } = ""; public int RowNumber { get; set; } public string EntityType { get; set; } = "";
    public string? TargetEntityId { get; set; } public bool IsKnown { get; set; } public string? Note { get; set; }
    public string? SuggestedWorkItemGroupKey { get; set; } public string? NewWorkItemGroupKey { get; set; } public string? NewWorkItemProjectName { get; set; }
    public bool? ConfirmReceipt { get; set; } public ExpensePaymentStatus? ExpensePaymentDecision { get; set; }
    public bool? CostCompletenessConfirmed { get; set; } public bool? NoTrackingNumber { get; set; }
    public ShippingRequirement? ShippingRequirementDecision { get; set; } public WorkStatus? WorkStatusDecision { get; set; }
    public CancelledStatus? CancelledFromStatusDecision { get; set; }
    public DateOnly? ReceivedDateOverride { get; set; } public DateOnly? DueDateOverride { get; set; } public DateOnly? CompletedDateOverride { get; set; }
    public DateOnly? ShippedDateOverride { get; set; } public DateOnly? DeliveredDateOverride { get; set; } public DateOnly? PaidDateOverride { get; set; }
    public static DecisionInput From(TransferRowDecision x) => new() { SheetName=x.SheetName, RowNumber=x.RowNumber, EntityType=x.EntityType, TargetEntityId=x.TargetEntityId, IsKnown=x.IsKnown, Note=x.Note, ConfirmReceipt=x.ConfirmReceipt, ExpensePaymentDecision=x.ExpensePaymentDecision, CostCompletenessConfirmed=x.CostCompletenessConfirmed, NoTrackingNumber=x.NoTrackingNumber, ShippingRequirementDecision=x.ShippingRequirementDecision, WorkStatusDecision=x.WorkStatusDecision, CancelledFromStatusDecision=x.CancelledFromStatusDecision, SuggestedWorkItemGroupKey=x.SuggestedWorkItemGroupKey, NewWorkItemGroupKey=x.NewWorkItemGroupKey, NewWorkItemProjectName=x.NewWorkItemProjectName, ReceivedDateOverride=x.ReceivedDateOverride, DueDateOverride=x.DueDateOverride, CompletedDateOverride=x.CompletedDateOverride, ShippedDateOverride=x.ShippedDateOverride, DeliveredDateOverride=x.DeliveredDateOverride, PaidDateOverride=x.PaidDateOverride };
    public TransferRowDecision ToDecision()
    {
        var d = new TransferRowDecision(SheetName, RowNumber, EntityType, TargetEntityId, IsKnown, Note, ConfirmReceipt, ExpensePaymentDecision, CostCompletenessConfirmed, NoTrackingNumber, ShippingRequirementDecision, WorkStatusDecision, CancelledFromStatusDecision);
        return d with { SuggestedWorkItemGroupKey = SuggestedWorkItemGroupKey, NewWorkItemGroupKey = NewWorkItemGroupKey, NewWorkItemProjectName = NewWorkItemProjectName, ReceivedDateOverride=ReceivedDateOverride, DueDateOverride=DueDateOverride, CompletedDateOverride=CompletedDateOverride, ShippedDateOverride=ShippedDateOverride, DeliveredDateOverride=DeliveredDateOverride, PaidDateOverride=PaidDateOverride };
    }
}
