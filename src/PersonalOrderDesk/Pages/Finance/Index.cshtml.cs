using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.Finance;

public sealed class IndexModel(IFinanceService finance, IBusinessClock clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? ReceivedYear { get; set; }
    [BindProperty(SupportsGet = true)] public int? ReceivedMonth { get; set; }
    [BindProperty(SupportsGet = true)] public int? CashYear { get; set; }
    [BindProperty(SupportsGet = true)] public int? CashMonth { get; set; }
    [BindProperty(SupportsGet = true)] public ProjectType? ProjectType { get; set; }
    [BindProperty(SupportsGet = true)] public WorkStatus? WorkStatus { get; set; }
    public FinanceOverviewDto? Overview { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        CashYear ??= clock.Today.Year;
        CashMonth ??= clock.Today.Month;
        try { Overview = await finance.GetOverview(new FinanceQueryDto(ReceivedYear, ReceivedMonth, CashYear, CashMonth, ProjectType: ProjectType, WorkStatus: WorkStatus), cancellationToken); }
        catch (ArgumentException ex) { ErrorMessage = ex.Message; }
        catch (InvalidOperationException ex) { ErrorMessage = ex.Message; }
    }
}
