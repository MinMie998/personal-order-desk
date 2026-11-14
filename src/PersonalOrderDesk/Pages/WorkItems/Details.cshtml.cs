using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.WorkItems;

public sealed class DetailsModel(IWorkbenchService workbench, IBusinessClock clock) : PageModel
{
    [BindProperty] public ProgressEditorInput ProgressInput { get; set; } = new();
    public bool ProgressEditorOpen { get; private set; }
    public int ReminderLeadDays { get; private set; }
    public DateOnly BusinessToday => clock.Today;
    public WorkItemDetailDto? Detail { get; private set; }
    public string? Message { get; private set; }
    public string? ErrorMessage { get; private set; }
    [BindProperty] public long ExpectedVersion { get; set; }
    [BindProperty] public WorkStatus TargetStatus { get; set; }
    [BindProperty] public WorkStatus? RestoreChoice { get; set; }
    [BindProperty] public string? Reason { get; set; }
    [BindProperty] public bool ContinueFulfillment { get; set; }
    [BindProperty] public bool IsArchived { get; set; }
    [BindProperty] public bool ConfirmCancel { get; set; }
    [BindProperty] public DateOnly? HistoricalCompletionDate { get; set; }

    public async Task<IActionResult> OnPostSaveProgressAsync(Guid id, CancellationToken ct)
    {
        ProgressEditorOpen = true;
        if (ModelState.Any(x => x.Key.StartsWith("ProgressInput.") && x.Value?.Errors.Count > 0))
            ErrorMessage = "进度填写不符合格式或超出长度限制，请检查后再保存。";
        else
        {
            try
            {
                await workbench.UpdateProgress(id, new(ProgressInput.ExpectedVersion, ProgressInput.CurrentWork, ProgressInput.NextStep, ProgressInput.ProjectPath), ct);
                TempData["Notice"] = "进度已保存，可以接着处理下一单。";
                return RedirectToPage(new { id });
            }
            catch (DbUpdateConcurrencyException) { ErrorMessage = "项目已在其他页面更新，请刷新核对后再保存进度。"; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { ErrorMessage = ex.Message; }
            catch (KeyNotFoundException) { return NotFound(); }
        }
        return await OnGetAsync(id, ct);
    }

    public async Task<IActionResult> OnPostBackfillCompletionDateAsync(Guid id, CancellationToken cancellationToken)
    {
        if (HistoricalCompletionDate is null || ModelState.Any(entry => entry.Value?.Errors.Count > 0
            && (entry.Key == nameof(HistoricalCompletionDate) || entry.Key == nameof(ExpectedVersion))))
        {
            ErrorMessage = "请填写已核实的完整完成日期。";
            return await OnGetAsync(id, cancellationToken);
        }
        try
        {
            await workbench.BackfillCompletionDate(id, HistoricalCompletionDate.Value, ExpectedVersion, cancellationToken);
            TempData["Notice"] = "历史完成日期已保存，具体时间仍为未知。";
            return RedirectToPage(new { id });
        }
        catch (DbUpdateConcurrencyException) { ErrorMessage = "此工作项已在其他页面更新，请刷新后核对。"; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { ErrorMessage = ex.Message; }
        return await OnGetAsync(id, cancellationToken);
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Detail = await workbench.GetDetail(id, cancellationToken);
        if (Detail is null) return NotFound();
        ExpectedVersion = Detail.WorkItem.Version;
        if (HttpMethods.IsGet(Request.Method)) ProgressInput = new ProgressEditorInput {
            ExpectedVersion = Detail.WorkItem.Version, CurrentWork = Detail.WorkItem.CurrentWork,
            NextStep = Detail.WorkItem.NextStep, ProjectPath = Detail.WorkItem.ProjectPath };

        if (Detail.WorkItem.Status is not WorkStatus.Completed and not WorkStatus.Cancelled)
        {
            var settings = await workbench.GetSettings(cancellationToken);
            ReminderLeadDays = Detail.WorkItem.ScheduleCategory == ScheduleCategory.Physical
                ? settings.PhysicalReminderLeadDays : settings.DigitalReminderLeadDays;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostChangeStatusAsync(Guid id, CancellationToken cancellationToken)
    {
        if (TargetStatus == WorkStatus.Cancelled && !ConfirmCancel)
        {
            ErrorMessage = "取消会保留收支和物流记录，请先勾选确认后再执行。";
            return await OnGetAsync(id, cancellationToken);
        }
        try
        {
            await workbench.ChangeStatus(id, new WorkStatusChangeRequestDto(TargetStatus, ExpectedVersion, Reason, RestoreChoice, ContinueFulfillment), cancellationToken);
            TempData["Notice"] = "工作状态已更新。";
            return RedirectToPage(new { id });
        }
        catch (DbUpdateConcurrencyException) { ErrorMessage = "此工作项已在其他标签页中更新。请刷新后确认当前状态，再重新操作。"; }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        return await OnGetAsync(id, cancellationToken);
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await workbench.Archive(id, new ArchiveRequestDto(true, ExpectedVersion), cancellationToken);
            TempData["Notice"] = "工作项已归档；仍有应收或履约事项时会继续出现在对应待办中。";
            return RedirectToPage(new { id });
        }
        catch (DbUpdateConcurrencyException) { ErrorMessage = "此工作项已在其他标签页中更新，请刷新后再归档。"; }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        return await OnGetAsync(id, cancellationToken);
    }

    public async Task<IActionResult> OnPostUnarchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await workbench.Archive(id, new ArchiveRequestDto(false, ExpectedVersion), cancellationToken);
            TempData["Notice"] = "工作项已取消归档。";
            return RedirectToPage(new { id });
        }
        catch (DbUpdateConcurrencyException) { ErrorMessage = "此工作项已在其他标签页中更新，请刷新后再操作。"; }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
        return await OnGetAsync(id, cancellationToken);
    }

    public string? TakeNotice() { var value = TempData["Notice"] as string; return value; }
    public IActionResult OnGetCheckPath(string? path) => new JsonResult(workbench.CheckProjectPath(path));
}

public sealed class ProgressEditorInput
{
    public long ExpectedVersion { get; set; }
    [StringLength(4000)] public string? CurrentWork { get; set; }
    [StringLength(2000)] public string? NextStep { get; set; }
    [StringLength(1000)] public string? ProjectPath { get; set; }
}
