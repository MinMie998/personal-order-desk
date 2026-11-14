using Microsoft.AspNetCore.Mvc.RazorPages;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages;

public sealed class IndexModel(IWorkbenchService workbench) : PageModel
{
    public DashboardDto? Dashboard { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try { Dashboard = await workbench.GetDashboard(cancellationToken); }
        catch (Exception) { ErrorMessage = "暂时无法读取工作台数据，请刷新页面；已保存的数据不会因此改变。"; }
    }
}
