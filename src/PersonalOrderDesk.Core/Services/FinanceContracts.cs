using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public interface IFinanceService
{
    Task<FinanceOverviewDto> GetOverview(FinanceQueryDto query, CancellationToken cancellationToken = default);
    Task<WorkItemFinanceDto?> GetWorkItemFinance(Guid workItemId, CancellationToken cancellationToken = default);
    Task<PaymentTransactionDto> CreateTransaction(CreatePaymentRequestDto request, CancellationToken cancellationToken = default);
    Task<PaymentTransactionDto> ReverseTransaction(Guid transactionId, ReverseTransactionRequestDto request, CancellationToken cancellationToken = default);
    Task<PaymentTransactionDto> SupplementTransaction(Guid transactionId, SupplementTransactionRequestDto request, CancellationToken cancellationToken = default);
    Task<ExpenseGroupDto> SaveExpenseGroup(SaveExpenseGroupRequestDto request, CancellationToken cancellationToken = default);
    Task<ExpenseDto> SetExpensePayment(Guid expenseId, SetExpensePaymentRequestDto request, CancellationToken cancellationToken = default);
    Task<ExpenseDto> ReverseExpense(Guid expenseId, ReverseExpenseRequestDto request, CancellationToken cancellationToken = default);
    Task SetCostConfirmation(Guid workItemId, SetCostConfirmationRequestDto request, CancellationToken cancellationToken = default);
}

public sealed record FinanceQueryDto(int? ReceivedYear = null, int? ReceivedMonth = null, int? CashYear = null, int? CashMonth = null, ProjectType? ProjectType = null, WorkStatus? WorkStatus = null);
public sealed record FinanceOverviewDto(long TotalNetReceivedCents, long TotalOutstandingCents, int CostPendingWorkItemCount, long? ExpectedGrossProfitCents, int GrossProfitCoveredWorkItemCount, int GrossProfitPendingWorkItemCount, long TotalPaidCostCents, long ActualCashNetCents, long UnknownDateNetReceivedCents, long UnknownDatePaidCostCents, int UnknownReceivedDateWorkItemCount, IReadOnlyList<FinanceCategoryRowDto> Categories, IReadOnlyList<FinanceMonthRowDto> Months, IReadOnlyList<WorkItemFinanceDto> WorkItems);
public sealed record FinanceCategoryRowDto(string Category, long NetReceivedCents, long PaidCostCents, long? ExpectedGrossProfitCents, long ActualCashNetCents);
public sealed record FinanceMonthRowDto(int Year, int Month, long NetReceivedCents, long PaidCostCents, long? ExpectedGrossProfitCents, long ActualCashNetCents);
public sealed record WorkItemFinanceDto(Guid WorkItemId, string? BusinessOrderNo, string ProjectName, long? OrderAmountCents, long NetReceivedCents, long? OutstandingCents, long? OverpaidCents, long TotalCostCents, long PaidCostCents, CostConfirmationStatus CostConfirmationStatus, long? ExpectedGrossProfitCents, long ActualCashNetCents, IReadOnlyList<PaymentTransactionDto> Transactions, IReadOnlyList<ExpenseGroupDto> ExpenseGroups);
public sealed record PaymentTransactionDto(Guid Id, TransactionType Type, long AmountCents, DateOnly? TransactionDate, DateKnowledge DateKnowledge, string? PaymentMethod, string? ExternalReference, string? Notes, string IdempotencyKey, RecordStatus Status, string? ReversalReason, DateTimeOffset UpdatedAt, long Version, IReadOnlyList<PaymentAllocationDto> Allocations);
public sealed record PaymentAllocationDto(Guid WorkItemId, string? BusinessOrderNo, string ProjectName, long AmountCents, bool IsDeleted = false);
public sealed record CreatePaymentRequestDto(TransactionType Type, long AmountCents, DateOnly? TransactionDate, DateKnowledge DateKnowledge, string? PaymentMethod, string? ExternalReference, string? Notes, string IdempotencyKey, IReadOnlyList<PaymentAllocationInputDto> Allocations);
public sealed record PaymentAllocationInputDto(Guid WorkItemId, long AmountCents);
public sealed record ReverseTransactionRequestDto(string Reason);
public sealed record SupplementTransactionRequestDto(DateOnly? TransactionDate, DateKnowledge DateKnowledge, string? PaymentMethod, string? ExternalReference, string? Notes, long ExpectedVersion);
public sealed record ExpenseGroupDto(Guid Id, Guid WorkItemId, string Title, string Category, string? Notes, long TotalAmountCents, IReadOnlyList<ExpenseDto> Expenses);
public sealed record ExpenseDto(Guid Id, Guid ExpenseGroupId, long AmountCents, DateOnly? ExpenseDate, DateKnowledge ExpenseDateKnowledge, ExpensePaymentStatus PaymentStatus, DateOnly? PaidDate, DateKnowledge PaidDateKnowledge, ExpenseStatus Status, string? ReversalReason, string? Notes);
public sealed record ExpenseInputDto(Guid? Id, long AmountCents, DateOnly? ExpenseDate, DateKnowledge ExpenseDateKnowledge, ExpensePaymentStatus PaymentStatus, DateOnly? PaidDate, DateKnowledge PaidDateKnowledge, string? Notes);
public sealed record SaveExpenseGroupRequestDto(Guid WorkItemId, Guid? ExpenseGroupId, long ExpectedWorkItemVersion, string Title, string Category, string? Notes, IReadOnlyList<ExpenseInputDto> Expenses, bool ConfirmComplete = false);
public sealed record SetExpensePaymentRequestDto(ExpensePaymentStatus PaymentStatus, DateOnly? PaidDate, DateKnowledge PaidDateKnowledge, long ExpectedWorkItemVersion, bool IsCorrection = false, string? CorrectionReason = null);
public sealed record ReverseExpenseRequestDto(string Reason, long ExpectedWorkItemVersion);
public sealed record SetCostConfirmationRequestDto(CostConfirmationStatus Status, long ExpectedWorkItemVersion);
