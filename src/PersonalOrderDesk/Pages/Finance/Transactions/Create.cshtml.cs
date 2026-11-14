using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.Finance.Transactions;

public sealed class CreateModel(IFinanceService finance, IWorkbenchService workbench, IBusinessClock clock) : PageModel
{
    [BindProperty] public TransactionCreateInput Input { get; set; } = new();
    public IReadOnlyList<WorkItemRowDto> WorkItems { get; private set; } = [];
    public DateOnly BusinessToday => clock.Today;
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(Guid? workItemId, CancellationToken ct)
    {
        await Load(ct);
        Input.TransactionDate = BusinessToday;
        Input.IdempotencyKey = Guid.NewGuid().ToString("N");
        if (workItemId.HasValue) Input.Allocations[0].WorkItemId = workItemId;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = "有字段无法识别或不符合输入格式，请检查后再保存；系统没有采用默认金额或状态覆盖记录。";
            await Load(ct);
            return Page();
        }
        if (Input.Allocations.Count == 0 || Input.Allocations.Any(x => !x.WorkItemId.HasValue))
        {
            ErrorMessage = "请为每笔分配选择工作项并填写金额。";
            await Load(ct);
            return Page();
        }
        try
        {
            var allocations = Input.Allocations.Select(x => new PaymentAllocationInputDto(x.WorkItemId!.Value, ToCents(x.AmountYuan))).ToArray();
            await finance.CreateTransaction(new CreatePaymentRequestDto(Input.Type, ToCents(Input.AmountYuan), Input.TransactionDate,
                Input.TransactionDate.HasValue ? DateKnowledge.Known : DateKnowledge.Unknown, FinanceFormChoices.PaymentMethod(Input.PaymentMethod, Input.CustomPaymentMethod), Input.ExternalReference, Input.Notes,
                string.IsNullOrWhiteSpace(Input.IdempotencyKey) ? Guid.NewGuid().ToString("N") : Input.IdempotencyKey, allocations), ct);
            if (Input.Type == TransactionType.Receipt) TempData["ReceiptAmountCents"] = ToCents(Input.AmountYuan).ToString(System.Globalization.CultureInfo.InvariantCulture);
            TempData["Notice"] = Input.Type == TransactionType.Receipt ? "收款及其分配已保存。" : "退款及其分配已保存。";
            var firstId = allocations[0].WorkItemId;
            return RedirectToPage("/Finance/WorkItem", new { id = firstId });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            ErrorMessage = ex.Message;
            await Load(ct);
            return Page();
        }
    }

    private async Task Load(CancellationToken ct)
    {
        var items = new List<WorkItemRowDto>();
        var firstPage = await workbench.GetList(new WorkItemListQueryDto(IncludeArchived: true, Page: 1, PageSize: 200), ct);
        items.AddRange(firstPage.Items);
        for (var page = 2; items.Count < firstPage.TotalCount; page++)
        {
            var next = await workbench.GetList(new WorkItemListQueryDto(IncludeArchived: true, Page: page, PageSize: 200), ct);
            if (next.Items.Count == 0) break;
            items.AddRange(next.Items);
        }
        WorkItems = items;
        if (Input.Allocations.Count == 0) Input.Allocations = [new()];
        Input.TransactionDate ??= BusinessToday;
        if (string.IsNullOrWhiteSpace(Input.IdempotencyKey)) Input.IdempotencyKey = Guid.NewGuid().ToString("N");
    }

    private static long ToCents(decimal amountYuan)
    {
        if (amountYuan <= 0 || decimal.Round(amountYuan, 2) != amountYuan) throw new ArgumentException("金额须大于 0，且最多保留两位小数。");
        return decimal.ToInt64(amountYuan * 100m);
    }
}
