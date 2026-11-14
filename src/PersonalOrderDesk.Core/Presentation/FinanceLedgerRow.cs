using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Presentation;

public sealed record FinanceLedgerRow(Guid Id, string Label, long AmountCents, DateOnly? Date,
    bool IsActive, PaymentTransactionDto? Transaction, ExpenseGroupDto? Group, ExpenseDto? Expense)
{
    public static IReadOnlyList<FinanceLedgerRow> From(WorkItemFinanceDto finance)
    {
        var rows = finance.Transactions.Select(t => new FinanceLedgerRow(t.Id,
            t.Type == TransactionType.Receipt ? "收款" : "退款",
            t.Allocations.Where(a => a.WorkItemId == finance.WorkItemId).Sum(a => a.AmountCents),
            t.TransactionDate, t.Status == RecordStatus.Active, t, null, null)).ToList();
        rows.AddRange(finance.ExpenseGroups.SelectMany(g => g.Expenses.Select(e => new FinanceLedgerRow(
            e.Id, "支出", e.AmountCents, e.ExpenseDate, e.Status == ExpenseStatus.Active, null, g, e))));
        return rows.OrderByDescending(r => r.Date.HasValue).ThenByDescending(r => r.Date).ThenBy(r => r.Id).ToArray();
    }
}
