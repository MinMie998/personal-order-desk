using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public sealed partial class WorkbenchService
{
    public async Task<WorkItemDetailDto> UpdateProgress(Guid id, UpdateProgressRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request.CurrentWork?.Length > 4000 || request.NextStep?.Length > 2000 || request.ProjectPath?.Length > 1000)
            throw new ArgumentException("进度、下一步或工程路径过长，请缩短后保存。");
        var item = await GetEditor(id, cancellationToken);
        return await Update(id, new WorkItemSaveRequestDto(request.ExpectedVersion, item.BusinessOrderNo,
            item.ProjectName, item.ProjectType, null, null, item.ReceivedDate, item.DueDateMode, item.SpecifiedDueDate,
            request.CurrentWork, request.NextStep, item.OrderAmountCents, request.ProjectPath, item.Notes), cancellationToken);
    }

    public async Task<WorkItemDeletePreviewDto?> GetDeletePreview(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return null;
        var payments = await (from a in db.PaymentAllocations.AsNoTracking()
            join p in db.PaymentTransactions.AsNoTracking() on a.PaymentTransactionId equals p.Id
            where a.WorkItemId == id select new { p.Id, p.Type, p.Status, a.AmountCents }).ToListAsync(cancellationToken);
        var costs = await (from g in db.ExpenseGroups.AsNoTracking()
            join e in db.Expenses.AsNoTracking() on g.Id equals e.ExpenseGroupId
            where g.WorkItemId == id select new { e.Status, e.AmountCents }).ToListAsync(cancellationToken);
        var paymentIds = payments.Select(x => x.Id).ToArray();
        return new(ToEditor(item), payments.Count,
            payments.Where(x => x.Status == RecordStatus.Active).Sum(x => x.Type == TransactionType.Receipt ? x.AmountCents : -x.AmountCents),
            costs.Count, costs.Where(x => x.Status == ExpenseStatus.Active).Sum(x => x.AmountCents),
            await db.Shipments.Where(x => x.WorkItemId == id).Select(x => (ShipmentStatus?)x.ShipmentStatus).SingleOrDefaultAsync(cancellationToken),
            await db.WorkItemRelations.CountAsync(x => x.SourceWorkItemId == id || x.TargetWorkItemId == id, cancellationToken),
            await db.PaymentAllocations.AnyAsync(x => paymentIds.Contains(x.PaymentTransactionId) && x.WorkItemId != id, cancellationToken));
    }

    public async Task<IReadOnlyList<DeletedWorkItemDto>> GetRecycleBin(CancellationToken cancellationToken = default)
    {
        var items = await db.WorkItems.IgnoreQueryFilters().AsNoTracking().Where(x => x.IsDeleted).ToListAsync(cancellationToken);
        return items.OrderByDescending(x => x.DeletedAt).Select(x => new DeletedWorkItemDto(x.Id, x.BusinessOrderNo, x.ProjectName, x.DeletedAt, x.Version)).ToArray();
    }

    public async Task Delete(Guid id, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("工作项不存在或已在回收站。");
        if (item.Version != expectedVersion) throw new DbUpdateConcurrencyException("工作项已更新，请重新核对后删除。");
        item.IsDeleted = true;
        item.DeletedAt = clock.Now;
        item.UpdatedAt = clock.Now;
        item.Version++;
        db.ActivityLogs.Add(new ActivityLog { EntityType = "WorkItem", EntityId = id, WorkItemId = id,
            ActionType = "WorkItemDeleted", Summary = "工作项移入回收站，关联记录保留", RecordedAt = clock.Now,
            AfterData = JsonSerializer.Serialize(new { item.IsDeleted, item.DeletedAt }, JsonOptions) });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RestoreDeleted(Guid id, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await db.WorkItems.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id && x.IsDeleted, cancellationToken)
            ?? throw new KeyNotFoundException("该工作项不在回收站。");
        if (item.Version != expectedVersion) throw new DbUpdateConcurrencyException("记录已更新，请刷新后再恢复。");
        item.IsDeleted = false;
        item.DeletedAt = null;
        item.UpdatedAt = clock.Now;
        item.Version++;
        db.ActivityLogs.Add(new ActivityLog { EntityType = "WorkItem", EntityId = id, WorkItemId = id,
            ActionType = "WorkItemRestored", Summary = "工作项从回收站恢复，保留原状态和关联记录", RecordedAt = clock.Now });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
