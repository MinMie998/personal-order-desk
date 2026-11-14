using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.Finance;

public sealed class WorkItemModel(IFinanceService finance, IWorkbenchService workbench, IBusinessClock clock) : PageModel
{
    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (HttpMethods.IsPost(Request.Method) && Guid.TryParse(RouteData.Values["id"]?.ToString(), out var id)
            && await workbench.GetDetail(id, HttpContext.RequestAborted) is null)
        { context.Result = NotFound(); return; }
        await next();
    }

    public WorkItemFinanceDto? Finance { get; private set; }
    public WorkItemDetailDto? WorkItem { get; private set; }
    public DateOnly BusinessToday => clock.Today;
    public string? ErrorMessage { get; private set; }
    [BindProperty] public WorkItemPaymentInput PaymentInput { get; set; } = new();
    [BindProperty] public ExpenseGroupInput GroupInput { get; set; } = new();
    [BindProperty] public SupplementTransactionInput SupplementInput { get; set; } = new();
    [BindProperty] public ReverseTransactionInput ReverseInput { get; set; } = new();
    [BindProperty] public ExpensePaymentInput ExpensePaymentInput { get; set; } = new();
    [BindProperty] public ReverseExpenseInput ReverseExpenseInput { get; set; } = new();
    [BindProperty] public CostConfirmationStatus ConfirmStatus { get; set; }
    [BindProperty] public long ExpectedVersion { get; set; }
    [BindProperty] public QuickExpenseInput QuickExpense { get; set; } = new();

    public async Task<IActionResult> OnPostQuickExpenseAsync(Guid id, CancellationToken ct)
    {
        if (!await EnsureValidModel(id, ct, nameof(QuickExpense))) return Page();
        try
        {
            var knowledge = QuickExpense.Date.HasValue ? DateKnowledge.Known : DateKnowledge.Unknown;
            await finance.SaveExpenseGroup(new SaveExpenseGroupRequestDto(id, null, QuickExpense.ExpectedVersion,
                string.IsNullOrWhiteSpace(QuickExpense.Note) ? "项目支出" : QuickExpense.Note.Trim(), QuickExpense.Category, null,
                [new ExpenseInputDto(null, ToCents(QuickExpense.Amount), QuickExpense.Date, knowledge,
                    ExpensePaymentStatus.Paid, QuickExpense.Date, knowledge, null)], false), ct);
            TempData["Notice"] = "支出已记录，利润已更新。";
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, ct); return Page(); }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, Guid? groupId, CancellationToken cancellationToken)
    {
        await Load(id, cancellationToken);
        if (!groupId.HasValue && Finance is not null)
            GroupInput = new ExpenseGroupInput { ExpectedWorkItemVersion = WorkItem!.WorkItem.Version, Expenses = [NewExpenseLine()] };
        if (groupId.HasValue && Finance is not null)
        {
            var group = Finance.ExpenseGroups.FirstOrDefault(x => x.Id == groupId.Value);
            if (group is null) return NotFound();
            GroupInput = new ExpenseGroupInput
            {
                ExpenseGroupId = group.Id,
                ExpectedWorkItemVersion = WorkItem!.WorkItem.Version,
                Title = group.Title,
                Category = group.Category,
                Notes = group.Notes,
                Expenses = group.Expenses.Where(x => x.Status == ExpenseStatus.Active).Select(x => new ExpenseLineInput
                {
                    Id = x.Id,
                    AmountYuan = x.AmountCents / 100m,
                    ExpenseDate = x.ExpenseDate,
                    ExpenseDateKnowledge = x.ExpenseDateKnowledge,
                    ExpenseDateIsHistoricalUnknown = x.ExpenseDateKnowledge == DateKnowledge.Unknown,
                    PaymentStatus = x.PaymentStatus,
                    PaidDate = x.PaidDate,
                    PaidDateKnowledge = x.PaidDateKnowledge,
                    PaidDateIsHistoricalUnknown = x.PaidDateKnowledge == DateKnowledge.Unknown,
                    Notes = x.Notes
                }).ToList()
            };
        }
        return Finance is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostCreateTransactionAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await EnsureValidModel(id, cancellationToken, "PaymentInput")) return Page();
        try
        {
            var cents = ToCents(PaymentInput.AmountYuan);
            await finance.CreateTransaction(new CreatePaymentRequestDto(PaymentInput.Type, cents,
                PaymentInput.TransactionDate, PaymentInput.TransactionDate.HasValue ? DateKnowledge.Known : DateKnowledge.Unknown, FinanceFormChoices.PaymentMethod(PaymentInput.PaymentMethod, PaymentInput.CustomPaymentMethod), PaymentInput.ExternalReference,
                PaymentInput.Notes, PaymentInput.IdempotencyKey, [new PaymentAllocationInputDto(id, cents)]), cancellationToken);
            if (PaymentInput.Type == TransactionType.Receipt) TempData["ReceiptAmountCents"] = cents.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (PaymentInput.Type == TransactionType.Refund) TempData["RefundRecorded"] = "true";
            TempData["Notice"] = PaymentInput.Type == TransactionType.Receipt ? "收款已记录。" : "退款已记录。";
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostSupplementTransactionAsync(Guid id, Guid transactionId, CancellationToken cancellationToken)
    {
        if (!await EnsureValidModel(id, cancellationToken, "SupplementInput")) return Page();
        try
        {
            await finance.SupplementTransaction(transactionId, new SupplementTransactionRequestDto(SupplementInput.TransactionDate,
                SupplementInput.DateKnowledge, SupplementInput.PaymentMethod, SupplementInput.ExternalReference,
                SupplementInput.Notes, SupplementInput.ExpectedVersion), cancellationToken);
            TempData["Notice"] = "交易日期或说明已补充。";
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostReverseTransactionAsync(Guid id, Guid transactionId, CancellationToken cancellationToken)
    {
        if (!await EnsureValidModel(id, cancellationToken, "ReverseInput")) return Page();
        if (!ReverseInput.Confirm) { ErrorMessage = "请明确确认撤销整笔交易及其所有分配。"; await Load(id, cancellationToken); return Page(); }
        try
        {
            await finance.ReverseTransaction(transactionId, new ReverseTransactionRequestDto(ReverseInput.Reason), cancellationToken);
            TempData["Notice"] = "交易已撤销，所有关联分配一并撤销。";
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostSaveExpenseGroupAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await EnsureValidModel(id, cancellationToken, "GroupInput")) return Page();
        try
        {
            var lines = GroupInput.Expenses.Select(x => new ExpenseInputDto(x.Id, ToCents(x.AmountYuan),
                x.ExpenseDateIsHistoricalUnknown ? null : x.ExpenseDate,
                x.ExpenseDateIsHistoricalUnknown ? DateKnowledge.Unknown : x.ExpenseDate.HasValue ? DateKnowledge.Known : DateKnowledge.Unknown,
                x.PaymentStatus,
                x.PaymentStatus == ExpensePaymentStatus.Unpaid || x.PaidDateIsHistoricalUnknown ? null : x.PaidDate,
                x.PaymentStatus == ExpensePaymentStatus.Unpaid ? DateKnowledge.NotApplicable : x.PaidDateIsHistoricalUnknown ? DateKnowledge.Unknown : x.PaidDate.HasValue ? DateKnowledge.Known : DateKnowledge.Unknown, x.Notes)).ToArray();
            await finance.SaveExpenseGroup(new SaveExpenseGroupRequestDto(id, GroupInput.ExpenseGroupId,
                GroupInput.ExpectedWorkItemVersion, GroupInput.Title, GroupInput.Category, GroupInput.Notes,
                lines, GroupInput.ConfirmComplete), cancellationToken);
            TempData["Notice"] = GroupInput.ConfirmComplete ? "成本已保存并确认完整。" : "成本事项及分项已保存，成本状态需重新确认。";
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostSetCostConfirmationAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await EnsureValidModel(id, cancellationToken, "ConfirmStatus", "ExpectedVersion")) return Page();
        try
        {
            await finance.SetCostConfirmation(id, new SetCostConfirmationRequestDto(ConfirmStatus, ExpectedVersion), cancellationToken);
            TempData["Notice"] = ConfirmStatus == CostConfirmationStatus.ConfirmedNoCost ? "已明确确认无成本。" : ConfirmStatus == CostConfirmationStatus.ConfirmedComplete ? "成本已确认完整。" : "成本状态已设为待确认。";
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostSetExpensePaymentAsync(Guid id, Guid expenseId, CancellationToken cancellationToken)
    {
        if (!await EnsureValidModel(id, cancellationToken, "ExpensePaymentInput")) return Page();
        try
        {
            var paidKnowledge = ExpensePaymentInput.PaymentStatus == ExpensePaymentStatus.Unpaid ? DateKnowledge.NotApplicable : ExpensePaymentInput.PaidDateIsHistoricalUnknown ? DateKnowledge.Unknown : ExpensePaymentInput.PaidDate.HasValue ? DateKnowledge.Known : DateKnowledge.Unknown;
            await finance.SetExpensePayment(expenseId, new SetExpensePaymentRequestDto(ExpensePaymentInput.PaymentStatus,
                ExpensePaymentInput.PaymentStatus == ExpensePaymentStatus.Unpaid || ExpensePaymentInput.PaidDateIsHistoricalUnknown ? null : ExpensePaymentInput.PaidDate, paidKnowledge, ExpensePaymentInput.ExpectedWorkItemVersion,
                ExpensePaymentInput.IsCorrection, ExpensePaymentInput.CorrectionReason), cancellationToken);
            TempData["Notice"] = "成本支付状态已更新。";
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostReverseExpenseAsync(Guid id, Guid expenseId, CancellationToken cancellationToken)
    {
        if (!await EnsureValidModel(id, cancellationToken, "ReverseExpenseInput")) return Page();
        try
        {
            await finance.ReverseExpense(expenseId, new ReverseExpenseRequestDto(ReverseExpenseInput.Reason,
                ReverseExpenseInput.ExpectedWorkItemVersion), cancellationToken);
            TempData["Notice"] = "成本分项已撤销，成本状态回到待确认。";
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    private async Task Load(Guid id, CancellationToken ct)
    {
        WorkItem = await workbench.GetDetail(id, ct);
        Finance = await finance.GetWorkItemFinance(id, ct);
        if (Finance is not null)
        {
            PaymentInput.TransactionDate ??= BusinessToday;
            PaymentInput.IdempotencyKey = string.IsNullOrWhiteSpace(PaymentInput.IdempotencyKey) ? Guid.NewGuid().ToString("N") : PaymentInput.IdempotencyKey;
            if (HttpMethods.IsGet(Request.Method)) GroupInput.ExpectedWorkItemVersion = WorkItem!.WorkItem.Version;
        }
    }

    private async Task<bool> EnsureValidModel(Guid id, CancellationToken ct, params string[] prefixes)
    {
        // Each independent form validates only its own fields; other editors are not being submitted.
        foreach (var key in ModelState.Keys.Where(key => !prefixes.Any(prefix => key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)) && ModelState[key]?.Errors.Count > 0).ToArray())
            ModelState.Remove(key);
        var hasErrors = ModelState.Any(entry => prefixes.Any(prefix =>
            entry.Key.Equals(prefix, StringComparison.OrdinalIgnoreCase) || entry.Key.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            && entry.Value?.Errors.Count > 0);
        if (!hasErrors) return true;
        ErrorMessage = "有字段无法识别或不符合输入格式，请检查后再保存；系统没有采用默认状态覆盖记录。";
        await Load(id, ct);
        return false;
    }

    private ExpenseLineInput NewExpenseLine() => new() { ExpenseDate = BusinessToday, ExpenseDateKnowledge = DateKnowledge.Known, PaymentStatus = ExpensePaymentStatus.Unpaid, PaidDateKnowledge = DateKnowledge.NotApplicable };
    private static long ToCents(decimal value)
    {
        if (value <= 0 || decimal.Round(value, 2) != value) throw new ArgumentException("金额须大于 0，且最多保留两位小数。");
        return decimal.ToInt64(value * 100m);
    }
    private static bool Showable(Exception ex) => ex is DbUpdateConcurrencyException or InvalidOperationException or ArgumentException or KeyNotFoundException;
    private static string Explain(Exception ex) => ex is DbUpdateConcurrencyException ? "工作项已在其他页面更新，请刷新后核对版本，再重新保存。" : ex.Message;
}
