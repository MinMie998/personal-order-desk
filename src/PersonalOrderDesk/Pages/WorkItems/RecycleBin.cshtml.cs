using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.WorkItems;

public sealed class RecycleBinModel(IWorkbenchService workbench) : PageModel
{
    public IReadOnlyList<DeletedWorkItemDto> Items { get; private set; } = [];
    public string? ErrorMessage { get; private set; }
    [BindProperty] public long ExpectedVersion { get; set; }
    public async Task OnGetAsync(CancellationToken ct) => Items = await workbench.GetRecycleBin(ct);
    public async Task<IActionResult> OnPostRestoreAsync(Guid id, CancellationToken ct)
    {
        try
        {
            if (!ModelState.IsValid) throw new InvalidOperationException("记录版本无效，请刷新后再恢复。");
            await workbench.RestoreDeleted(id, ExpectedVersion, ct);
            TempData["Notice"] = "工作项已恢复，原状态、收支、物流和关联记录均保留。";
            return RedirectToPage("/WorkItems/Details", new { id });
        }
        catch (DbUpdateConcurrencyException) { ErrorMessage = "记录已更新，请刷新后再恢复。"; }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { ErrorMessage = ex.Message; }
        await OnGetAsync(ct);
        return Page();
    }
}
