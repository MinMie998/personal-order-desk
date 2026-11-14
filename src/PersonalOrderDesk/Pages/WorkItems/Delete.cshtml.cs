using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.WorkItems;

public sealed class DeleteModel(IWorkbenchService workbench) : PageModel
{
    public WorkItemDeletePreviewDto? Preview { get; private set; }
    public string? ErrorMessage { get; private set; }
    [BindProperty] public long ExpectedVersion { get; set; }
    [BindProperty] public bool ConfirmDelete { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Preview = await workbench.GetDeletePreview(id, ct);
        if (Preview is null) return NotFound();
        ExpectedVersion = Preview.WorkItem.Version;
        return Page();
    }
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        if (!ConfirmDelete || !ModelState.IsValid) ErrorMessage = "请先核对项目并勾选删除确认。";
        else
        {
            try
            {
                await workbench.Delete(id, ExpectedVersion, ct);
                TempData["Notice"] = "工作项已移入回收站，需要时可恢复。";
                return RedirectToPage("/WorkItems/Index");
            }
            catch (DbUpdateConcurrencyException) { ErrorMessage = "项目已在其他页面更新，请返回项目重新核对后删除。"; }
            catch (KeyNotFoundException) { return RedirectToPage("/WorkItems/RecycleBin"); }
        }
        Preview = await workbench.GetDeletePreview(id, ct);
        return Preview is null ? RedirectToPage("/WorkItems/RecycleBin") : Page();
    }
}
