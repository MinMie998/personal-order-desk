using Microsoft.AspNetCore.Mvc.RazorPages;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;
namespace PersonalOrderDesk.Pages;
public sealed class OverviewModel(IFinanceService finance, IWorkbenchService workbench) : PageModel
{
    public IReadOnlyList<WorkItemFinanceDto> Money { get; private set; } = [];
    public List<WorkItemRowDto> Orders { get; } = [];
    public int Completed => Orders.Count(x => x.Status == WorkStatus.Completed);
    public int Working => Orders.Count(x => x.Status == WorkStatus.InProgress);
    public int NotStarted => Orders.Count(x => x.Status == WorkStatus.NotStarted);
    public long Received => Money.Sum(x => x.NetReceivedCents);
    public long Expense => Money.Sum(x => x.TotalCostCents);
    public long Profit => Received - Expense;
    public async Task OnGetAsync(CancellationToken ct)
    {
        Money = (await finance.GetOverview(new FinanceQueryDto(), ct)).WorkItems;
        for (var page = 1; ; page++)
        {
            var result = await workbench.GetList(new(IncludeArchived: true, Page: page, PageSize: 200), ct);
            Orders.AddRange(result.Items);
            if (Orders.Count >= result.TotalCount || result.Items.Count == 0) break;
        }
    }
}
