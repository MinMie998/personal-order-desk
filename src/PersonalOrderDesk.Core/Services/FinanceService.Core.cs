using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public sealed partial class FinanceService : IFinanceService
{
    private sealed record NormalizedPaymentPayload(TransactionType Type, long AmountCents, DateOnly? TransactionDate, DateKnowledge DateKnowledge, string? PaymentMethod, string? ExternalReference, string? Notes, IReadOnlyList<PaymentAllocationInputDto> Allocations);
    private readonly DeskDbContext db;
    private readonly IBusinessClock clock;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FinanceService(DeskDbContext db, IBusinessClock clock)
    {
        this.db = db;
        this.clock = clock;
    }

    public async Task<PaymentTransactionDto> CreateTransaction(CreatePaymentRequestDto request, CancellationToken cancellationToken = default)
    {
        ValidateTransactionRequest(request);
        request = request with { IdempotencyKey = request.IdempotencyKey.Trim() };
        ValidateNotFuture(request.TransactionDate, "交易日期");
        var normalized = Normalize(request);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized, JsonOptions))));
        await using var tx = await BeginImmediateAsync(cancellationToken);
        var existing = await db.PaymentTransactions.SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestPayloadHash, hash, StringComparison.Ordinal)) throw new InvalidOperationException("幂等键已用于不同内容的交易。");
            await tx.CommitAsync(cancellationToken);
            return await ToTransactionDto(existing, cancellationToken);
        }

        var ids = normalized.Allocations.Select(x => x.WorkItemId).ToArray();
        if (await db.WorkItems.CountAsync(x => ids.Contains(x.Id), cancellationToken) != ids.Length)
            throw new KeyNotFoundException("一个或多个工作项不存在。");
        if (request.Type == TransactionType.Refund)
        {
            foreach (var allocation in normalized.Allocations)
            {
                var receipts = await (from a in db.PaymentAllocations
                                      join p in db.PaymentTransactions on a.PaymentTransactionId equals p.Id
                                      where a.WorkItemId == allocation.WorkItemId && p.Status == RecordStatus.Active
                                      select new { p.Type, a.AmountCents }).ToListAsync(cancellationToken);
                var balance = receipts.Sum(x => x.Type == TransactionType.Receipt ? x.AmountCents : -x.AmountCents);
                if (allocation.AmountCents > balance) throw new InvalidOperationException("退款分配超过该工作项当前有效净收款。");
            }
        }

        var transaction = new PaymentTransaction
        {
            Type = request.Type,
            AmountCents = request.AmountCents,
            TransactionDate = request.TransactionDate,
            DateKnowledge = request.DateKnowledge,
            PaymentMethod = Clean(request.PaymentMethod),
            ExternalReference = Clean(request.ExternalReference),
            Notes = Clean(request.Notes),
            RequestPayloadHash = hash,
            IdempotencyKey = request.IdempotencyKey.Trim(),
            Status = RecordStatus.Active,
            SourceKind = SourceKind.Manual,
            CreatedAt = clock.Now,
            UpdatedAt = clock.Now
        };
        db.PaymentTransactions.Add(transaction);
        foreach (var allocation in normalized.Allocations)
            db.PaymentAllocations.Add(new PaymentAllocation { PaymentTransactionId = transaction.Id, WorkItemId = allocation.WorkItemId, AmountCents = allocation.AmountCents });
        AddLog("PaymentTransaction", transaction.Id, null, "TransactionCreated", $"{(request.Type == TransactionType.Receipt ? "收款" : "退款")} {request.AmountCents} 分", null, JsonSerializer.Serialize(normalized, JsonOptions));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return await ToTransactionDto(transaction, cancellationToken);
    }

    public async Task<PaymentTransactionDto> ReverseTransaction(Guid transactionId, ReverseTransactionRequestDto request, CancellationToken cancellationToken = default)
    {
        var reason = Required(request.Reason, "撤销交易必须填写原因。");
        await using var tx = await BeginImmediateAsync(cancellationToken);
        var transaction = await db.PaymentTransactions.SingleOrDefaultAsync(x => x.Id == transactionId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到该交易。");
        await RequireVisibleTransaction(transactionId, cancellationToken);
        if (transaction.Status != RecordStatus.Active) throw new InvalidOperationException("交易已撤销。");
        var allocations = await db.PaymentAllocations.Where(x => x.PaymentTransactionId == transactionId).ToListAsync(cancellationToken);
        if (transaction.Type == TransactionType.Receipt)
        {
            foreach (var allocation in allocations)
            {
                var refunded = await (from a in db.PaymentAllocations
                                      join p in db.PaymentTransactions on a.PaymentTransactionId equals p.Id
                                      where a.WorkItemId == allocation.WorkItemId && p.Type == TransactionType.Refund && p.Status == RecordStatus.Active
                                      select (long?)a.AmountCents).SumAsync(cancellationToken) ?? 0;
                var received = await (from a in db.PaymentAllocations
                                      join p in db.PaymentTransactions on a.PaymentTransactionId equals p.Id
                                      where a.WorkItemId == allocation.WorkItemId && p.Type == TransactionType.Receipt && p.Status == RecordStatus.Active
                                      select (long?)a.AmountCents).SumAsync(cancellationToken) ?? 0;
                if (refunded > received - allocation.AmountCents)
                    throw new InvalidOperationException("该收款已有有效退款依赖，请先处理退款。");
            }
        }
        transaction.Status = RecordStatus.Reversed;
        transaction.ReversalReason = reason;
        transaction.UpdatedAt = clock.Now;
        transaction.Version++;
        AddLog("PaymentTransaction", transaction.Id, null, "TransactionReversed", "交易已撤销", null, JsonSerializer.Serialize(new { reason }, JsonOptions));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return await ToTransactionDto(transaction, cancellationToken);
    }

    public async Task<PaymentTransactionDto> SupplementTransaction(Guid transactionId, SupplementTransactionRequestDto request, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginImmediateAsync(cancellationToken);
        var transaction = await db.PaymentTransactions.SingleOrDefaultAsync(x => x.Id == transactionId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到该交易。");
        await RequireVisibleTransaction(transactionId, cancellationToken);
        if (transaction.Version != request.ExpectedVersion) throw new DbUpdateConcurrencyException("交易已被其他页面修改，请刷新后重试。");
        ValidateDate(request.TransactionDate, request.DateKnowledge, transaction.SourceKind, "交易日期");
        ValidateNotFuture(request.TransactionDate, "交易日期");
        var before = new { transaction.TransactionDate, transaction.DateKnowledge, transaction.PaymentMethod, transaction.ExternalReference, transaction.Notes };
        transaction.TransactionDate = request.TransactionDate;
        transaction.DateKnowledge = request.DateKnowledge;
        transaction.PaymentMethod = Clean(request.PaymentMethod);
        transaction.ExternalReference = Clean(request.ExternalReference);
        transaction.Notes = Clean(request.Notes);
        transaction.UpdatedAt = clock.Now;
        transaction.Version++;
        AddLog("PaymentTransaction", transaction.Id, null, "TransactionSupplemented", "已补充交易日期或说明", JsonSerializer.Serialize(before, JsonOptions), JsonSerializer.Serialize(request, JsonOptions));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return await ToTransactionDto(transaction, cancellationToken);
    }

    public async Task<ExpenseGroupDto> SaveExpenseGroup(SaveExpenseGroupRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request.Expenses.Count == 0) throw new InvalidOperationException("成本组至少需要一条有效分项。");
        if (request.Expenses.Any(x => x.AmountCents <= 0)) throw new InvalidOperationException("成本金额必须为正整数分。");
        await using var tx = await BeginImmediateAsync(cancellationToken);
        var workItem = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == request.WorkItemId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到该工作项。");
        CheckVersion(workItem, request.ExpectedWorkItemVersion);
        var group = request.ExpenseGroupId.HasValue
            ? await db.ExpenseGroups.SingleOrDefaultAsync(x => x.Id == request.ExpenseGroupId.Value && x.WorkItemId == workItem.Id, cancellationToken)
                ?? throw new KeyNotFoundException("找不到该成本组。")
            : new ExpenseGroup { WorkItemId = workItem.Id, Title = Required(request.Title, "成本事项名称不能为空。"), Category = Required(request.Category, "成本类别不能为空。") };
        if (!request.ExpenseGroupId.HasValue) db.ExpenseGroups.Add(group);
        var before = request.ExpenseGroupId.HasValue ? JsonSerializer.Serialize(new { group.Title, group.Category, group.Notes }, JsonOptions) : null;
        group.Title = Required(request.Title, "成本事项名称不能为空。");
        group.Category = Required(request.Category, "成本类别不能为空。");
        group.Notes = Clean(request.Notes);
        group.UpdatedAt = clock.Now;
        foreach (var input in request.Expenses)
        {
            if (input.Id.HasValue)
            {
                var expense = await db.Expenses.SingleOrDefaultAsync(x => x.Id == input.Id.Value && x.ExpenseGroupId == group.Id, cancellationToken)
                    ?? throw new KeyNotFoundException("成本分项不存在或不属于该成本组。");
                if (expense.Status != ExpenseStatus.Active) throw new InvalidOperationException("已撤销的成本分项不能编辑。");
                if (expense.PaymentStatus != input.PaymentStatus) throw new InvalidOperationException("请使用单项支付状态操作更新支付状态，避免绕过更正原因记录。");
                ValidateExpenseDates(input.ExpenseDate, input.ExpenseDateKnowledge, input.PaymentStatus, input.PaidDate, input.PaidDateKnowledge, expense.SourceKind);
                var changedAmount = expense.AmountCents != input.AmountCents;
                expense.AmountCents = input.AmountCents;
                expense.ExpenseDate = input.ExpenseDate;
                expense.ExpenseDateKnowledge = input.ExpenseDateKnowledge;
                expense.PaymentStatus = input.PaymentStatus;
                expense.PaidDate = input.PaidDate;
                expense.PaidDateKnowledge = input.PaidDateKnowledge;
                expense.Notes = Clean(input.Notes);
                expense.UpdatedAt = clock.Now;
                if (changedAmount) workItem.CostConfirmationStatus = CostConfirmationStatus.Pending;
            }
            else
            {
                ValidateExpenseDates(input.ExpenseDate, input.ExpenseDateKnowledge, input.PaymentStatus, input.PaidDate, input.PaidDateKnowledge, SourceKind.Manual);
                db.Expenses.Add(new Expense
                {
                    ExpenseGroupId = group.Id,
                    AmountCents = input.AmountCents,
                    ExpenseDate = input.ExpenseDate,
                    ExpenseDateKnowledge = input.ExpenseDateKnowledge,
                    PaymentStatus = input.PaymentStatus,
                    PaidDate = input.PaidDate,
                    PaidDateKnowledge = input.PaidDateKnowledge,
                    SourceKind = SourceKind.Manual,
                    Status = ExpenseStatus.Active,
                    Notes = Clean(input.Notes),
                    CreatedAt = clock.Now,
                    UpdatedAt = clock.Now
                });
                workItem.CostConfirmationStatus = CostConfirmationStatus.Pending;
            }
        }
        if (request.ConfirmComplete) workItem.CostConfirmationStatus = CostConfirmationStatus.ConfirmedComplete;
        workItem.Version++;
        workItem.UpdatedAt = clock.Now;
        AddLog("ExpenseGroup", group.Id, workItem.Id, "ExpenseGroupSaved", "成本事项及分项已保存", before, JsonSerializer.Serialize(request, JsonOptions));
        await db.SaveChangesAsync(cancellationToken);
        await ValidateCostConfirmation(workItem.Id, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return await ToExpenseGroupDto(group, cancellationToken);
    }

    public async Task<ExpenseDto> SetExpensePayment(Guid expenseId, SetExpensePaymentRequestDto request, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginImmediateAsync(cancellationToken);
        var expense = await db.Expenses.SingleOrDefaultAsync(x => x.Id == expenseId, cancellationToken) ?? throw new KeyNotFoundException("找不到该成本分项。");
        var group = await db.ExpenseGroups.SingleAsync(x => x.Id == expense.ExpenseGroupId, cancellationToken);
        var workItem = await db.WorkItems.SingleAsync(x => x.Id == group.WorkItemId, cancellationToken);
        CheckVersion(workItem, request.ExpectedWorkItemVersion);
        if (expense.Status != ExpenseStatus.Active) throw new InvalidOperationException("已撤销的成本分项不能修改。");
        ValidateExpenseDates(expense.ExpenseDate, expense.ExpenseDateKnowledge, request.PaymentStatus, request.PaidDate, request.PaidDateKnowledge, expense.SourceKind);
        if (request.IsCorrection && string.IsNullOrWhiteSpace(request.CorrectionReason))
            throw new InvalidOperationException("更正支付状态必须说明原因。");
        var before = JsonSerializer.Serialize(new { expense.PaymentStatus, expense.PaidDate, expense.PaidDateKnowledge }, JsonOptions);
        expense.PaymentStatus = request.PaymentStatus;
        expense.PaidDate = request.PaidDate;
        expense.PaidDateKnowledge = request.PaidDateKnowledge;
        expense.UpdatedAt = clock.Now;
        workItem.Version++;
        workItem.UpdatedAt = clock.Now;
        AddLog("Expense", expense.Id, workItem.Id, "ExpensePaymentChanged", request.IsCorrection ? $"成本支付状态更正：{Required(request.CorrectionReason, "更正支付状态必须说明原因。")}" : "成本支付状态已更新", before, JsonSerializer.Serialize(request, JsonOptions));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return ToExpenseDto(expense);
    }

    public async Task<ExpenseDto> ReverseExpense(Guid expenseId, ReverseExpenseRequestDto request, CancellationToken cancellationToken = default)
    {
        var reason = Required(request.Reason, "撤销成本必须填写原因。");
        await using var tx = await BeginImmediateAsync(cancellationToken);
        var expense = await db.Expenses.SingleOrDefaultAsync(x => x.Id == expenseId, cancellationToken) ?? throw new KeyNotFoundException("找不到该成本分项。");
        var group = await db.ExpenseGroups.SingleAsync(x => x.Id == expense.ExpenseGroupId, cancellationToken);
        var workItem = await db.WorkItems.SingleAsync(x => x.Id == group.WorkItemId, cancellationToken);
        CheckVersion(workItem, request.ExpectedWorkItemVersion);
        if (expense.Status != ExpenseStatus.Active) throw new InvalidOperationException("成本分项已撤销。");
        expense.Status = ExpenseStatus.Voided;
        expense.ReversalReason = reason;
        expense.UpdatedAt = clock.Now;
        workItem.CostConfirmationStatus = CostConfirmationStatus.Pending;
        workItem.Version++;
        workItem.UpdatedAt = clock.Now;
        AddLog("Expense", expense.Id, workItem.Id, "ExpenseReversed", $"成本分项已撤销：{reason}", null, null);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return ToExpenseDto(expense);
    }

    public async Task SetCostConfirmation(Guid workItemId, SetCostConfirmationRequestDto request, CancellationToken cancellationToken = default)
    {
        await using var tx = await BeginImmediateAsync(cancellationToken);
        var workItem = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == workItemId, cancellationToken) ?? throw new KeyNotFoundException("找不到该工作项。");
        CheckVersion(workItem, request.ExpectedWorkItemVersion);
        var activeCount = await (from e in db.Expenses join g in db.ExpenseGroups on e.ExpenseGroupId equals g.Id where g.WorkItemId == workItemId && e.Status == ExpenseStatus.Active select e.Id).CountAsync(cancellationToken);
        if (request.Status == CostConfirmationStatus.ConfirmedNoCost && activeCount > 0) throw new InvalidOperationException("存在有效成本分项，不能确认无成本。");
        if (request.Status == CostConfirmationStatus.ConfirmedComplete && activeCount == 0) throw new InvalidOperationException("确认成本完整至少需要一条有效成本分项。");
        workItem.CostConfirmationStatus = request.Status;
        workItem.Version++;
        workItem.UpdatedAt = clock.Now;
        AddLog("WorkItem", workItemId, workItemId, "CostConfirmed", $"成本确认状态：{request.Status}", null, null);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private async Task<FinanceWriteTransaction> BeginImmediateAsync(CancellationToken cancellationToken)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        var raw = connection.BeginTransaction(deferred: false);
        var contextTransaction = await db.Database.UseTransactionAsync(raw, cancellationToken)
            ?? throw new InvalidOperationException("无法将SQLite写事务关联到数据上下文。");
        return new FinanceWriteTransaction(contextTransaction, raw);
    }

    private sealed class FinanceWriteTransaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction contextTransaction, SqliteTransaction raw) : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken cancellationToken) => contextTransaction.CommitAsync(cancellationToken);
        public async ValueTask DisposeAsync()
        {
            await contextTransaction.DisposeAsync();
            await raw.DisposeAsync();
        }
    }

    private static void ValidateTransactionRequest(CreatePaymentRequestDto request)
    {
        if (request.AmountCents <= 0) throw new InvalidOperationException("交易金额必须为正整数分。");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Trim().Length > 200) throw new InvalidOperationException("幂等键不能为空且不得超过200个字符。");
        ValidateDate(request.TransactionDate, request.DateKnowledge, SourceKind.Manual, "交易日期");
        if (request.Allocations.Count == 0 || request.Allocations.Any(x => x.AmountCents <= 0)) throw new InvalidOperationException("每项分配必须为正整数分。");
        if (request.Allocations.GroupBy(x => x.WorkItemId).Any(x => x.Count() > 1)) throw new InvalidOperationException("同一工作项只能分配一次，请先合并分配行。");
        if (request.Allocations.Sum(x => x.AmountCents) != request.AmountCents) throw new InvalidOperationException("分配金额之和必须等于交易总额。");
    }

    private static NormalizedPaymentPayload Normalize(CreatePaymentRequestDto request) => new(
        request.Type, request.AmountCents, request.TransactionDate, request.DateKnowledge,
        Clean(request.PaymentMethod), Clean(request.ExternalReference), Clean(request.Notes),
        request.Allocations.OrderBy(x => x.WorkItemId).Select(x => new PaymentAllocationInputDto(x.WorkItemId, x.AmountCents)).ToArray());

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(error) : value.Trim();
    private static void CheckVersion(WorkItem item, long expected) { if (item.Version != expected) throw new DbUpdateConcurrencyException("工作项已被其他页面修改，请刷新后重试。"); }

    private static void ValidateDate(DateOnly? date, DateKnowledge knowledge, SourceKind source, string name)
    {
        if (knowledge == DateKnowledge.Known && !date.HasValue) throw new InvalidOperationException($"{name}标记已知时必须填写日期。");
        if (knowledge == DateKnowledge.Unknown && date.HasValue) throw new InvalidOperationException($"{name}留空时不能同时填写日期。");
        if (knowledge == DateKnowledge.NotApplicable || (knowledge != DateKnowledge.Known && knowledge != DateKnowledge.Unknown)) throw new InvalidOperationException($"{name}知识状态无效。");
    }

    private void ValidateExpenseDates(DateOnly? expenseDate, DateKnowledge expenseKnowledge, ExpensePaymentStatus status, DateOnly? paidDate, DateKnowledge paidKnowledge, SourceKind sourceKind)
    {
        ValidateDate(expenseDate, expenseKnowledge, sourceKind, "成本发生日期");
        ValidateNotFuture(expenseDate, "成本发生日期");
        if (status == ExpensePaymentStatus.Unpaid)
        {
            if (paidDate.HasValue || paidKnowledge != DateKnowledge.NotApplicable) throw new InvalidOperationException("未支付成本不能填写支付日期。");
        }
        else
        {
            ValidateDate(paidDate, paidKnowledge, sourceKind, "成本支付日期");
            ValidateNotFuture(paidDate, "成本支付日期");
        }
    }

    private void ValidateNotFuture(DateOnly? date, string field)
    {
        if (date.HasValue && date.Value > clock.Today) throw new InvalidOperationException($"实际{field}不能晚于今天。");
    }

    private async Task ValidateCostConfirmation(Guid workItemId, CancellationToken cancellationToken)
    {
        var item = await db.WorkItems.SingleAsync(x => x.Id == workItemId, cancellationToken);
        var active = await (from e in db.Expenses join g in db.ExpenseGroups on e.ExpenseGroupId equals g.Id where g.WorkItemId == workItemId && e.Status == ExpenseStatus.Active select e.Id).CountAsync(cancellationToken);
        if ((item.CostConfirmationStatus == CostConfirmationStatus.ConfirmedNoCost && active != 0) || (item.CostConfirmationStatus == CostConfirmationStatus.ConfirmedComplete && active == 0))
            throw new InvalidOperationException("成本确认状态与有效分项不一致。");
    }

    private void AddLog(string entityType, Guid entityId, Guid? workItemId, string action, string summary, string? before, string? after) =>
        db.ActivityLogs.Add(new ActivityLog { EntityType = entityType, EntityId = entityId, WorkItemId = workItemId, ActionType = action, Summary = summary, BeforeData = before, AfterData = after, RecordedAt = clock.Now });

    private async Task RequireVisibleTransaction(Guid id, CancellationToken ct)
    {
        if (!await (from a in db.PaymentAllocations join w in db.WorkItems on a.WorkItemId equals w.Id
            where a.PaymentTransactionId == id select a.Id).AnyAsync(ct))
            throw new InvalidOperationException("该交易所属工作项已在回收站，请先恢复工作项。");
    }

    private async Task<PaymentTransactionDto> ToTransactionDto(PaymentTransaction payment, CancellationToken cancellationToken)
    {
        var rows = await (from a in db.PaymentAllocations join w in db.WorkItems.IgnoreQueryFilters() on a.WorkItemId equals w.Id where a.PaymentTransactionId == payment.Id select new PaymentAllocationDto(w.Id, w.BusinessOrderNo, w.ProjectName, a.AmountCents, w.IsDeleted)).ToListAsync(cancellationToken);
        return new(payment.Id, payment.Type, payment.AmountCents, payment.TransactionDate, payment.DateKnowledge, payment.PaymentMethod, payment.ExternalReference, UserNotes(payment.Notes), payment.IdempotencyKey, payment.Status, payment.ReversalReason, payment.UpdatedAt, payment.Version, rows);
    }

    private static string? UserNotes(string? notes)
    {
        if (notes is null) return null;
        return notes;
    }

    private async Task<ExpenseGroupDto> ToExpenseGroupDto(ExpenseGroup group, CancellationToken cancellationToken)
    {
        var expenses = (await db.Expenses.Where(x => x.ExpenseGroupId == group.Id).ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt)
            .Select(ToExpenseDto)
            .ToList();
        return new(group.Id, group.WorkItemId, group.Title, group.Category, group.Notes, expenses.Where(x => x.Status == ExpenseStatus.Active).Sum(x => x.AmountCents), expenses);
    }

    private static ExpenseDto ToExpenseDto(Expense expense) => new(expense.Id, expense.ExpenseGroupId, expense.AmountCents, expense.ExpenseDate, expense.ExpenseDateKnowledge, expense.PaymentStatus, expense.PaidDate, expense.PaidDateKnowledge, expense.Status, expense.ReversalReason, expense.Notes);
}
