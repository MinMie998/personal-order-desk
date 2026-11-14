using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.WorkItems;

public sealed class IndexModel(IWorkbenchService workbench) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public WorkStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public ProjectType? ProjectType { get; set; }
    [BindProperty(SupportsGet = true)] public ScheduleCategory? ScheduleCategory { get; set; }
    [BindProperty(SupportsGet = true)] public bool IncludeArchived { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? ReceivedFrom { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? ReceivedTo { get; set; }
    [BindProperty(SupportsGet = true)] public WorkSectionKind? Filter { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;
    public IReadOnlyList<WorkItemRowDto> Items { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int PageSize { get; private set; } = 50;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (Filter.HasValue && string.IsNullOrWhiteSpace(Search) && Status is null && ProjectType is null && ScheduleCategory is null && !IncludeArchived && ReceivedFrom is null && ReceivedTo is null)
        {
            var section = (await workbench.GetDashboard(cancellationToken)).Sections.FirstOrDefault(x => x.Kind == Filter);
            Items = section?.Items ?? [];
            TotalCount = section?.Count ?? 0;
            PageSize = Math.Max(Items.Count, 1);
            return;
        }
        var result = await workbench.GetList(new WorkItemListQueryDto(Search, Status, ProjectType, ScheduleCategory, IncludeArchived, ReceivedFrom, ReceivedTo, Math.Max(PageNumber, 1), PageSize), cancellationToken);
        Items = result.Items;
        TotalCount = result.TotalCount;
        PageNumber = result.Page;
        PageSize = result.PageSize;
    }
}
