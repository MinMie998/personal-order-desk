using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public sealed partial class FinanceService
{
    private sealed record CashRow(Guid WorkItemId, ProjectType ProjectType, WorkStatus WorkStatus, TransactionType Type, DateOnly? TransactionDate, DateKnowledge DateKnowledge, long AmountCents);
    private sealed record CostRow(Guid WorkItemId, string Category, ProjectType ProjectType, WorkStatus WorkStatus, DateOnly? PaidDate, DateKnowledge PaidDateKnowledge, long AmountCents);

    public async Task<FinanceOverviewDto> GetOverview(FinanceQueryDto query, CancellationToken cancellationToken = default)
    {
        ValidatePeriod(query.ReceivedYear, query.ReceivedMonth);
        ValidatePeriod(query.CashYear, query.CashMonth);
        var scopedItems = db.WorkItems.AsNoTracking().AsQueryable();
        if (query.ProjectType.HasValue) scopedItems = scopedItems.Where(x => x.ProjectType == query.ProjectType.Value);
        if (query.WorkStatus.HasValue) scopedItems = scopedItems.Where(x => x.WorkStatus == query.WorkStatus.Value);
        if (query.ReceivedYear.HasValue)
        {
            var start = new DateOnly(query.ReceivedYear.Value, query.ReceivedMonth ?? 1, 1);
            var end = query.ReceivedMonth.HasValue ? start.AddMonths(1) : new DateOnly(query.ReceivedYear.Value + 1, 1, 1);
            scopedItems = scopedItems.Where(x => x.ReceivedDate >= start && x.ReceivedDate < end);
        }
        var orderItems = await scopedItems.ToListAsync(cancellationToken);
        var unknownReceivedQuery = db.WorkItems.AsNoTracking();
        if (query.ProjectType.HasValue) unknownReceivedQuery = unknownReceivedQuery.Where(x => x.ProjectType == query.ProjectType.Value);
        if (query.WorkStatus.HasValue) unknownReceivedQuery = unknownReceivedQuery.Where(x => x.WorkStatus == query.WorkStatus.Value);
        var unknownReceived = await unknownReceivedQuery.CountAsync(x => x.ReceivedDate == null, cancellationToken);

        var financeRows = new List<WorkItemFinanceDto>();
        foreach (var item in orderItems)
            financeRows.Add(await GetWorkItemFinance(item.Id, cancellationToken) ?? throw new InvalidOperationException("财务工作项在查询期间消失。"));

        var cashAllocations = await (from allocation in db.PaymentAllocations.AsNoTracking()
                                     join payment in db.PaymentTransactions.AsNoTracking() on allocation.PaymentTransactionId equals payment.Id
                                     join item in db.WorkItems.AsNoTracking() on allocation.WorkItemId equals item.Id
                                     where payment.Status == RecordStatus.Active
                                     select new CashRow(allocation.WorkItemId, item.ProjectType, item.WorkStatus, payment.Type, payment.TransactionDate, payment.DateKnowledge, allocation.AmountCents)).ToListAsync(cancellationToken);
        if (query.ProjectType.HasValue) cashAllocations = cashAllocations.Where(x => x.ProjectType == query.ProjectType).ToList();
        if (query.WorkStatus.HasValue) cashAllocations = cashAllocations.Where(x => x.WorkStatus == query.WorkStatus).ToList();
        var paidCosts = await (from e in db.Expenses.AsNoTracking()
                               join g in db.ExpenseGroups.AsNoTracking() on e.ExpenseGroupId equals g.Id
                               join w in db.WorkItems.AsNoTracking() on g.WorkItemId equals w.Id
                               where e.Status == ExpenseStatus.Active && e.PaymentStatus == ExpensePaymentStatus.Paid
                               select new CostRow(g.WorkItemId, g.Category, w.ProjectType, w.WorkStatus, e.PaidDate, e.PaidDateKnowledge, e.AmountCents)).ToListAsync(cancellationToken);
        if (query.ProjectType.HasValue) paidCosts = paidCosts.Where(x => x.ProjectType == query.ProjectType).ToList();
        if (query.WorkStatus.HasValue) paidCosts = paidCosts.Where(x => x.WorkStatus == query.WorkStatus).ToList();

        var unknownReceivedCash = cashAllocations.Where(x => x.DateKnowledge == DateKnowledge.Unknown).Sum(x => x.Type == TransactionType.Receipt ? x.AmountCents : -x.AmountCents);
        var unknownPaidCosts = paidCosts.Where(x => x.PaidDateKnowledge == DateKnowledge.Unknown).Sum(x => x.AmountCents);
        var selectedCash = cashAllocations.Where(x => IsInPeriod(x.TransactionDate, query.CashYear, query.CashMonth)).ToList();
        var selectedCosts = paidCosts.Where(x => IsInPeriod(x.PaidDate, query.CashYear, query.CashMonth)).ToList();
        var totalNetReceived = selectedCash.Sum(x => x.Type == TransactionType.Receipt ? x.AmountCents : -x.AmountCents);
        var totalPaidCost = selectedCosts.Sum(x => x.AmountCents);
        var eligible = financeRows.Where(x => x.ExpectedGrossProfitCents.HasValue).ToList();
        var missing = financeRows.Count - eligible.Count;
        var categories = BuildCategoryRows(query, orderItems, financeRows, cashAllocations, paidCosts);
        var months = BuildMonthRows(cashAllocations, paidCosts);
        return new FinanceOverviewDto(
            totalNetReceived,
            financeRows.Where(x => x.OutstandingCents.HasValue).Sum(x => x.OutstandingCents!.Value),
            financeRows.Count(x => x.CostConfirmationStatus == CostConfirmationStatus.Pending),
            eligible.Count == 0 ? null : eligible.Sum(x => x.ExpectedGrossProfitCents!.Value),
            eligible.Count,
            missing,
            totalPaidCost,
            totalNetReceived - totalPaidCost,
            unknownReceivedCash,
            unknownPaidCosts,
            unknownReceived,
            categories,
            months,
            financeRows);
    }

    public async Task<WorkItemFinanceDto?> GetWorkItemFinance(Guid workItemId, CancellationToken cancellationToken = default)
    {
        var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workItemId, cancellationToken);
        if (item is null) return null;
        var payments = await (from allocation in db.PaymentAllocations.AsNoTracking()
                              join payment in db.PaymentTransactions.AsNoTracking() on allocation.PaymentTransactionId equals payment.Id
                              where allocation.WorkItemId == workItemId
                              select new { payment, allocation.AmountCents }).ToListAsync(cancellationToken);
        var net = payments.Where(x => x.payment.Status == RecordStatus.Active).Sum(x => x.payment.Type == TransactionType.Receipt ? x.AmountCents : -x.AmountCents);
        var groups = (await db.ExpenseGroups.AsNoTracking().Where(x => x.WorkItemId == workItemId).ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt)
            .ToList();
        var groupDtos = new List<ExpenseGroupDto>();
        var allExpenses = new List<Expense>();
        foreach (var group in groups)
        {
            var expenses = await db.Expenses.AsNoTracking().Where(x => x.ExpenseGroupId == group.Id).ToListAsync(cancellationToken);
            allExpenses.AddRange(expenses);
            var expenseDtos = expenses.Select(ToExpenseDto).ToArray();
            groupDtos.Add(new ExpenseGroupDto(group.Id, group.WorkItemId, group.Title, group.Category, group.Notes,
                expenses.Where(x => x.Status == ExpenseStatus.Active).Sum(x => x.AmountCents), expenseDtos));
        }
        var activeExpenses = allExpenses.Where(x => x.Status == ExpenseStatus.Active).ToList();
        var costTotal = activeExpenses.Sum(x => x.AmountCents);
        var paidCost = activeExpenses.Where(x => x.PaymentStatus == ExpensePaymentStatus.Paid).Sum(x => x.AmountCents);
        long? outstanding = item.OrderAmountCents.HasValue ? Math.Max(item.OrderAmountCents.Value - net, 0) : null;
        long? overpaid = item.OrderAmountCents.HasValue ? Math.Max(net - item.OrderAmountCents.Value, 0) : null;
        long? gross = item.OrderAmountCents.HasValue && item.CostConfirmationStatus == CostConfirmationStatus.ConfirmedComplete
            ? item.OrderAmountCents.Value - costTotal
            : item.OrderAmountCents.HasValue && item.CostConfirmationStatus == CostConfirmationStatus.ConfirmedNoCost
                ? item.OrderAmountCents.Value
                : null;
        var transactionDtos = new List<PaymentTransactionDto>();
        foreach (var payment in payments.Select(x => x.payment).DistinctBy(x => x.Id))
            transactionDtos.Add(await ToTransactionDto(payment, cancellationToken));
        return new WorkItemFinanceDto(item.Id, item.BusinessOrderNo, item.ProjectName, item.OrderAmountCents, net, outstanding,
            overpaid, costTotal, paidCost, item.CostConfirmationStatus, gross, net - paidCost, transactionDtos, groupDtos);
    }

    private static IReadOnlyList<FinanceCategoryRowDto> BuildCategoryRows(
        FinanceQueryDto query,
        IReadOnlyList<WorkItem> operatingItems,
        IReadOnlyList<WorkItemFinanceDto> financeRows,
        IReadOnlyList<CashRow> allCash,
        IReadOnlyList<CostRow> allCosts)
    {
        var cash = allCash.Where(x => IsInPeriod(x.TransactionDate, query.CashYear, query.CashMonth)).ToList();
        var costs = allCosts.Where(x => IsInPeriod(x.PaidDate, query.CashYear, query.CashMonth)).ToList();
        var typeByWorkItem = operatingItems.ToDictionary(x => x.Id, x => x.ProjectType);
        var operatingByType = financeRows
            .Where(x => typeByWorkItem.ContainsKey(x.WorkItemId))
            .GroupBy(x => typeByWorkItem[x.WorkItemId])
            .ToDictionary(x => x.Key, x => x.ToList());
        var groupedCash = cash.GroupBy(x => x.ProjectType).ToDictionary(x => x.Key, x => x.ToList());
        var groupedCosts = costs.GroupBy(x => x.ProjectType).ToDictionary(x => x.Key, x => x.ToList());
        var types = operatingByType.Keys.Union(groupedCash.Keys).Union(groupedCosts.Keys).OrderBy(x => x).ToArray();
        var result = new List<FinanceCategoryRowDto>();
        foreach (var type in types)
        {
            var typeCash = groupedCash.GetValueOrDefault(type) ?? [];
            var typeCosts = groupedCosts.GetValueOrDefault(type) ?? [];
            var eligibleProfit = (operatingByType.GetValueOrDefault(type) ?? [])
                .Where(x => x.ExpectedGrossProfitCents.HasValue)
                .Sum(x => x.ExpectedGrossProfitCents!.Value);
            var profitCount = (operatingByType.GetValueOrDefault(type) ?? []).Count(x => x.ExpectedGrossProfitCents.HasValue);
            var netCents = typeCash.Sum(x => x.Type == TransactionType.Receipt ? x.AmountCents : -x.AmountCents);
            var paidCents = typeCosts.Sum(x => x.AmountCents);
            result.Add(new FinanceCategoryRowDto(ProjectTypeName(type), netCents, paidCents,
                profitCount == 0 ? null : eligibleProfit, netCents - paidCents));
        }
        return result;
    }

    private static string ProjectTypeName(ProjectType type) => type switch
    {
        ProjectType.Simulation => "仿真",
        ProjectType.SchematicPcbDesign => "原理图与PCB设计",
        ProjectType.CodeReport => "代码与报告",
        ProjectType.PhysicalProduction => "实物制作",
        ProjectType.Other => "其他",
        ProjectType.MixedOrUnknown => "综合或未知",
        _ => type.ToString()
    };

    private static IReadOnlyList<FinanceMonthRowDto> BuildMonthRows(IReadOnlyList<CashRow> cash, IReadOnlyList<CostRow> costs)
    {
        var datedCash = cash.Where(x => x.TransactionDate.HasValue).GroupBy(x => (x.TransactionDate!.Value.Year, x.TransactionDate.Value.Month)).ToDictionary(
            x => x.Key,
            x => x.Sum(row => row.Type == TransactionType.Receipt ? row.AmountCents : -row.AmountCents));
        var datedCosts = costs.Where(x => x.PaidDate.HasValue).GroupBy(x => (x.PaidDate!.Value.Year, x.PaidDate.Value.Month)).ToDictionary(
            x => x.Key,
            x => x.Sum(row => row.AmountCents));
        return datedCash.Keys.Union(datedCosts.Keys).OrderBy(x => x.Year).ThenBy(x => x.Month)
            .Select(key => new FinanceMonthRowDto(key.Year, key.Month, datedCash.GetValueOrDefault(key), datedCosts.GetValueOrDefault(key), null,
                datedCash.GetValueOrDefault(key) - datedCosts.GetValueOrDefault(key))).ToArray();
    }

    private static bool IsInPeriod(DateOnly? date, int? year, int? month)
    {
        if (!year.HasValue) return true;
        if (!date.HasValue) return false;
        if (month.HasValue) return date.Value.Year == year.Value && date.Value.Month == month.Value;
        return date.Value.Year == year.Value;
    }

    private static void ValidatePeriod(int? year, int? month)
    {
        if (month.HasValue && !year.HasValue) throw new InvalidOperationException("按月份查询时必须同时指定年份。");
        if (year.HasValue && (year is < 1900 or > 9998 || month is < 1 or > 12)) throw new InvalidOperationException("月份查询范围无效。");
    }
}
