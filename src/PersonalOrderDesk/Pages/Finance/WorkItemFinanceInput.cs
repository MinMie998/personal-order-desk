using System.ComponentModel.DataAnnotations;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Pages.Finance;

public sealed class QuickExpenseInput
{
    [Range(typeof(decimal), "0.01", "999999999")] public decimal Amount { get; set; }
    public DateOnly? Date { get; set; }
    public string Category { get; set; } = "其他";
    public string? Note { get; set; }
    public long ExpectedVersion { get; set; }
}

public sealed class WorkItemPaymentInput
{
    public TransactionType Type { get; set; } = TransactionType.Receipt;
    [Range(typeof(decimal), "0.01", "999999999")]
    public decimal AmountYuan { get; set; }
    public DateOnly? TransactionDate { get; set; }
    public string? PaymentMethod { get; set; }
    public string? CustomPaymentMethod { get; set; }
    public string? ExternalReference { get; set; }
    public string? Notes { get; set; }
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
}

public sealed class ExpenseGroupInput
{
    public Guid? ExpenseGroupId { get; set; }
    public long ExpectedWorkItemVersion { get; set; }
    [Required, StringLength(200)] public string Title { get; set; } = "";
    [Required, StringLength(100)] public string Category { get; set; } = "其他";
    public string? Notes { get; set; }
    public List<ExpenseLineInput> Expenses { get; set; } = [new()];
    public bool ConfirmComplete { get; set; }
}

public sealed class ExpenseLineInput
{
    public Guid? Id { get; set; }
    [Range(typeof(decimal), "0.01", "999999999")] public decimal AmountYuan { get; set; }
    public DateOnly? ExpenseDate { get; set; }
    public DateKnowledge ExpenseDateKnowledge { get; set; } = DateKnowledge.Known;
    public bool ExpenseDateIsHistoricalUnknown { get; set; }
    public ExpensePaymentStatus PaymentStatus { get; set; } = ExpensePaymentStatus.Unpaid;
    public DateOnly? PaidDate { get; set; }
    public DateKnowledge PaidDateKnowledge { get; set; } = DateKnowledge.NotApplicable;
    public bool PaidDateIsHistoricalUnknown { get; set; }
    public string? Notes { get; set; }
}

public sealed class SupplementTransactionInput
{
    public DateOnly? TransactionDate { get; set; }
    public DateKnowledge DateKnowledge { get; set; } = DateKnowledge.Known;
    public string? PaymentMethod { get; set; }
    public string? CustomPaymentMethod { get; set; }
    public string? ExternalReference { get; set; }
    public string? Notes { get; set; }
    public long ExpectedVersion { get; set; }
}

public sealed class ReverseTransactionInput { public string Reason { get; set; } = ""; public bool Confirm { get; set; } }
public sealed class ExpensePaymentInput
{
    public long ExpectedWorkItemVersion { get; set; }
    public ExpensePaymentStatus PaymentStatus { get; set; }
    public DateOnly? PaidDate { get; set; }
    public DateKnowledge PaidDateKnowledge { get; set; } = DateKnowledge.NotApplicable;
    public bool PaidDateIsHistoricalUnknown { get; set; }
    public bool IsCorrection { get; set; }
    public string? CorrectionReason { get; set; }
}
public sealed class ReverseExpenseInput { public long ExpectedWorkItemVersion { get; set; } public string Reason { get; set; } = ""; }
