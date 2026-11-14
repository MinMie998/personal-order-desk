using Microsoft.AspNetCore.Mvc;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.WorkItems;

public sealed class CreateModel(IWorkbenchService workbench) : WorkItemFormPageModel(workbench)
{
    [BindProperty(SupportsGet = true)] public Guid? SourceId { get; set; }
    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadEditor(null, cancellationToken);
    public Task<IActionResult> OnPostAsync(CancellationToken cancellationToken) => SubmitAsync(SourceId, SourceId.HasValue, cancellationToken);
}
