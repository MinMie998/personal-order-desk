using System.ComponentModel.DataAnnotations;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Pages.Finance.Transactions;

public sealed class TransactionCreateInput
{
    public TransactionType Type { get; set; } = TransactionType.Receipt;
    [Range(typeof(decimal), "0.01", "999999999")] public decimal AmountYuan { get; set; }
    public DateOnly? TransactionDate { get; set; }
    public string? PaymentMethod { get; set; }
    public string? CustomPaymentMethod { get; set; }
    public string? ExternalReference { get; set; }
    public string? Notes { get; set; }
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public List<AllocationCreateInput> Allocations { get; set; } = [new()];
}

public sealed class AllocationCreateInput
{
    public Guid? WorkItemId { get; set; }
    [Range(typeof(decimal), "0.01", "999999999")] public decimal AmountYuan { get; set; }
}
