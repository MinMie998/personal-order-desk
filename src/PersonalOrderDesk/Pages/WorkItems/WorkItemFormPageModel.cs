using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;
using PersonalOrderDesk.Presentation;

namespace PersonalOrderDesk.Pages.WorkItems;

public abstract class WorkItemFormPageModel(IWorkbenchService workbench) : PageModel
{
    [BindProperty] public WorkItemFormInput Input { get; set; } = new();
    public string? ImportedNoteHints { get; private set; }
    public string? ConflictMessage { get; protected set; }
    public ProjectPathCheckDto? PathCheck { get; protected set; }
    public int DefaultDurationDays { get; private set; } = 1;
    protected IWorkbenchService Workbench { get; } = workbench;
    protected async Task LoadEditor(Guid? id, CancellationToken ct)
    {
        DefaultDurationDays = (await Workbench.GetSettings(ct)).NonPhysicalDefaultDurationDays;
        ViewData["DefaultDurationDays"] = DefaultDurationDays;
        var editor = await Workbench.GetEditor(id, ct);
        var noteSections = WorkItemNoteSections.From(editor.Notes, editor.SourceKind);
        ImportedNoteHints = noteSections.ImportHints;
        Input = new WorkItemFormInput
        {
            ExpectedVersion = id.HasValue ? editor.Version : null,
            IsHistoricalImport = editor.SourceKind == SourceKind.HistoricalImport,
            BusinessOrderNo = editor.BusinessOrderNo, ProjectName = editor.ProjectName,
            ProjectType = editor.ProjectType,
            ScheduleCategory = editor.ScheduleCategorySource == ValueSource.Default ? null : editor.ScheduleCategory,
            ShippingRequirement = editor.ShippingRequirementSource == ValueSource.Default ? null : editor.ShippingRequirement,
            ReceivedDate = editor.ReceivedDate, DueDateMode = editor.DueDateMode,
            SpecifiedDueDate = editor.SpecifiedDueDate, CurrentWork = editor.CurrentWork,
            NextStep = editor.NextStep, OrderAmount = editor.OrderAmountCents is long cents ? cents / 100m : null,
            ProjectPath = editor.ProjectPath, Notes = noteSections.CurrentNotes
        };
    }
    protected WorkItemSaveRequestDto ToRequest()
    {
        var dueMode = Input.DueDateMode == DueDateMode.Manual && !Input.SpecifiedDueDate.HasValue
            ? DueDateMode.Auto : Input.DueDateMode;
        var specifiedDate = dueMode == DueDateMode.Manual ? Input.SpecifiedDueDate : null;
        return new WorkItemSaveRequestDto(
            Input.ExpectedVersion, Input.BusinessOrderNo, Input.ProjectName, Input.ProjectType,
            Input.ScheduleCategory, Input.ShippingRequirement, Input.ReceivedDate, dueMode,
            specifiedDate, Input.CurrentWork, Input.NextStep,
            Input.OrderAmount is decimal amount ? decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero)) : null,
            Input.ProjectPath, Input.Notes, Input.ApplyProjectTypeDefaults, Input.OrderAmountChangeReason);
    }

    protected async Task<IActionResult> SubmitAsync(Guid? id, bool additional, CancellationToken ct)
    {
        DefaultDurationDays = (await Workbench.GetSettings(ct)).NonPhysicalDefaultDurationDays;
        ViewData["DefaultDurationDays"] = DefaultDurationDays;
        try
        {
            var request = ToRequest();
            if (id.HasValue && !additional)
            {
                var existing = await Workbench.GetEditor(id, ct);
                ImportedNoteHints = WorkItemNoteSections.From(existing.Notes, existing.SourceKind).ImportHints;
                request = request with { Notes = WorkItemNoteSections.MergeCurrentNotes(Input.Notes, existing.Notes, existing.SourceKind) };
            }
            var result = id is null ? await Workbench.Create(request, ct)
                : additional ? await Workbench.CreateAdditionalWork(id.Value, request, ct)
                : await Workbench.Update(id.Value, request, ct);
            return RedirectToPage("/WorkItems/Details", new { id = result.WorkItem.Id });
        }
        catch (DbUpdateConcurrencyException)
        {
            ConflictMessage = "此工作项已在其他标签页中更新。你的修改尚未保存，请重新打开最新版本后再编辑。";
            ModelState.AddModelError(string.Empty, ConflictMessage);
        }
        catch (InvalidOperationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        return Page();
    }
    public IActionResult OnGetCheckPath(string? path)
    {
        PathCheck = Workbench.CheckProjectPath(path);
        return new JsonResult(PathCheck);
    }
}
