using Microsoft.AspNetCore.Mvc;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.WorkItems;

public sealed class EditModel(IWorkbenchService workbench) : WorkItemFormPageModel(workbench)
{
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if ((await Workbench.GetDetail(id, cancellationToken)) is null) return NotFound();
        await LoadEditor(id, cancellationToken);
        return Page();
    }
    public Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken) => SubmitAsync(id, false, cancellationToken);
}
