using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using Microsoft.EntityFrameworkCore;
namespace PersonalOrderDesk.Services;

public sealed class WorkItemService(DeskDbContext db)
{
    public static DateOnly? CalculateDue(WorkItem w, int nonPhysicalDays) => w.DueDateMode switch { DueDateMode.Manual => w.SpecifiedDueDate, DueDateMode.HistoricalUnknown => null, _ when w.ScheduleCategory == ScheduleCategory.Digital && w.ReceivedDate.HasValue => w.ReceivedDate.Value.AddDays(nonPhysicalDays), _ => null };

    public async Task ChangeDueDateAsync(Guid id, DueDateMode mode, DateOnly? specifiedDate, int defaultDays, long expectedVersion, CancellationToken ct = default)
    {
        if (defaultDays < 1) throw new ArgumentOutOfRangeException(nameof(defaultDays));
        var item = await db.WorkItems.SingleAsync(x => x.Id == id, ct);
        if (item.Version != expectedVersion) throw new DbUpdateConcurrencyException("工作项已被其他页面修改，请刷新后重试。");
        if (mode == DueDateMode.Manual && !specifiedDate.HasValue) throw new InvalidOperationException("手动截止日期必须指定日期。");
        if (mode != DueDateMode.Manual && specifiedDate.HasValue) throw new InvalidOperationException("只有手动模式可以指定截止日期。");
        if (mode == DueDateMode.HistoricalUnknown && item.SourceKind != SourceKind.HistoricalImport) throw new InvalidOperationException("历史未知截止日期仅用于历史导入记录。");
        item.DueDateMode = mode;
        item.SpecifiedDueDate = specifiedDate;
        item.EffectiveDueDate = CalculateDue(item, defaultDays);
        item.Version++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        db.ActivityLogs.Add(new ActivityLog { EntityType = "WorkItem", EntityId = id, WorkItemId = id, ActionType = "DueDateChanged", Summary = $"截止日期模式更改为 {mode}" });
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangeReceivedDateAsync(Guid id, DateOnly? receivedDate, int nonPhysicalDefaultDays, long expectedVersion, CancellationToken ct = default)
    {
        if (nonPhysicalDefaultDays < 1) throw new ArgumentOutOfRangeException(nameof(nonPhysicalDefaultDays));
        var item = await db.WorkItems.SingleAsync(x => x.Id == id, ct);
        if (item.Version != expectedVersion) throw new DbUpdateConcurrencyException("工作项已被其他页面修改，请刷新后重试。");
        if (item.SourceKind == SourceKind.Manual && !receivedDate.HasValue) throw new InvalidOperationException("手工工作项必须填写接单日期。");
        item.ReceivedDate = receivedDate;
        if (item.DueDateMode == DueDateMode.Auto) item.EffectiveDueDate = CalculateDue(item, nonPhysicalDefaultDays);
        item.Version++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> RecalculateAutomaticDueDatesAsync(int newDefaultDays, CancellationToken ct = default)
    {
        if (newDefaultDays < 1) throw new ArgumentOutOfRangeException(nameof(newDefaultDays));
        var items = await db.WorkItems.Where(x => x.DueDateMode == DueDateMode.Auto).ToListAsync(ct);
        foreach (var item in items)
        {
            item.EffectiveDueDate = CalculateDue(item, newDefaultDays);
            item.Version++;
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    public async Task SetArchivedAsync(Guid id, bool archived, long expectedVersion, CancellationToken ct = default)
    {
        var item = await db.WorkItems.SingleAsync(x => x.Id == id, ct);
        if (item.Version != expectedVersion) throw new DbUpdateConcurrencyException("工作项已被其他页面修改，请刷新后重试。");
        if (archived && item.WorkStatus is not (WorkStatus.Completed or WorkStatus.Cancelled)) throw new InvalidOperationException("只有已完成或已取消的工作项可以归档。");
        var beforeArchived = item.IsArchived;
        item.IsArchived = archived;
        item.Version++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        db.ActivityLogs.Add(new ActivityLog { EntityType = "WorkItem", EntityId = id, WorkItemId = id,
            ActionType = archived ? "Archived" : "Unarchived", Summary = archived ? "工作项已归档" : "工作项已取消归档",
            BeforeData = $"{{\"IsArchived\":{beforeArchived.ToString().ToLowerInvariant()}}}",
            AfterData = $"{{\"IsArchived\":{archived.ToString().ToLowerInvariant()}}}" });
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangeProjectTypeAsync(Guid id, ProjectType projectType, bool applyNewDefaults, int nonPhysicalDefaultDays, long expectedVersion, CancellationToken ct = default)
    {
        var item = await db.WorkItems.SingleAsync(x => x.Id == id, ct);
        if (item.Version != expectedVersion) throw new DbUpdateConcurrencyException("工作项已被其他页面修改，请刷新后重试。");
        var (schedule, shipping) = MapDefaults(projectType);
        item.ProjectType = projectType;
        if (applyNewDefaults)
        {
            item.ScheduleCategory = schedule; item.ScheduleCategorySource = ValueSource.Default;
            item.ShippingRequirement = shipping; item.ShippingRequirementSource = ValueSource.Default;
        }
        else
        {
            item.ScheduleCategorySource = ValueSource.Manual;
            item.ShippingRequirementSource = ValueSource.Manual;
        }
        item.EffectiveDueDate = CalculateDue(item, nonPhysicalDefaultDays);
        item.Version++; item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public static (ScheduleCategory Schedule, ShippingRequirement Shipping) MapDefaults(ProjectType type) => type switch
    {
        ProjectType.Simulation or ProjectType.SchematicPcbDesign or ProjectType.CodeReport or ProjectType.Other => (ScheduleCategory.Digital, ShippingRequirement.None),
        ProjectType.PhysicalProduction => (ScheduleCategory.Physical, ShippingRequirement.Required),
        _ => (ScheduleCategory.Pending, ShippingRequirement.Pending)
    };

    public async Task CreateAsync(WorkItem item, int defaultDays, CancellationToken ct = default) { if (item.SourceKind == SourceKind.Manual && !item.ReceivedDate.HasValue) throw new InvalidOperationException("手工工作项必须填写接单日期。"); if (item.DueDateMode == DueDateMode.HistoricalUnknown && item.SourceKind != SourceKind.HistoricalImport) throw new InvalidOperationException("历史未知截止日期仅用于导入。"); item.EffectiveDueDate = CalculateDue(item, defaultDays); item.CreatedAt = item.UpdatedAt = DateTimeOffset.UtcNow; db.WorkItems.Add(item); await db.SaveChangesAsync(ct); }
    public async Task SetStatusAsync(Guid id, WorkStatus target, DateOnly today, long expectedVersion, string? reason = null, WorkStatus? restoreChoice = null, bool continueFulfillment = false, CancellationToken ct = default)
    {
        var w = await db.WorkItems.SingleAsync(x => x.Id == id, ct); if (w.Version != expectedVersion) throw new DbUpdateConcurrencyException("工作项已被其他页面修改，请刷新后重试。"); var actionAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)); var old = w.WorkStatus; var priorCompletedDate = w.CompletedDate; var priorCompletionKnowledge = w.CompletedDateKnowledge; var priorCompletedAt = w.CompletedAt; if (old == WorkStatus.Cancelled)
        {
            if (target == WorkStatus.Cancelled) throw new InvalidOperationException(); var was = w.CancelledFromStatus;
            if ((!was.HasValue || was == CancelledStatus.Unknown) && !restoreChoice.HasValue) throw new InvalidOperationException("取消前状态未知，恢复时必须选择状态。");
            if (restoreChoice.HasValue && restoreChoice.Value is not (WorkStatus.NotStarted or WorkStatus.InProgress or WorkStatus.NeedsRework or WorkStatus.Completed)) throw new InvalidOperationException("恢复状态无效。");
            var restored = restoreChoice ?? Enum.Parse<WorkStatus>(was!.Value.ToString());
            if (was != CancelledStatus.Completed && restored == WorkStatus.Completed) throw new InvalidOperationException("取消前未完成的工作不能直接恢复为已完成。");
            if (restored != target) throw new InvalidOperationException("恢复目标与所选状态不一致。");
            if (was == CancelledStatus.Completed && target != WorkStatus.Completed) throw new InvalidOperationException("取消前已完成时，恢复必须保留已完成状态。");
            if (was == CancelledStatus.Completed) { var h = (await db.WorkStatusHistories.Where(x => x.WorkItemId == id && x.NewStatus == WorkStatus.Cancelled && x.OldStatus == WorkStatus.Completed).ToListAsync(ct)).OrderByDescending(x => x.RecordedAt).First(); w.CompletedDate = h.PreviousCompletedDate; w.CompletedDateKnowledge = h.PreviousCompletedDateKnowledge; w.CompletedAt = h.PreviousCompletedAt; } else { w.CompletedDate = null; w.CompletedAt = null; w.CompletedDateKnowledge = CompletionDateKnowledge.NotApplicable; }
            w.CancelledFromStatus = null; w.IsArchived = false; var ship = await db.Shipments.SingleOrDefaultAsync(x => x.WorkItemId == id, ct); if (ship is not null && continueFulfillment) ship.FulfillmentPaused = false;
        }
        else { if (!Allowed(old, target)) throw new InvalidOperationException($"不允许从 {old} 转为 {target}。"); if (target == WorkStatus.Cancelled) { w.CancelledFromStatus = Enum.Parse<CancelledStatus>(old.ToString()); if (old == WorkStatus.Completed) { var hx = new WorkStatusHistory { WorkItemId = id, OldStatus = old, NewStatus = target, EventType = HistoryEventType.Cancelled, OccurredDate = today, OccurredAt = actionAt, PreviousCompletedDate = w.CompletedDate, PreviousCompletedDateKnowledge = w.CompletedDateKnowledge, PreviousCompletedAt = w.CompletedAt, Reason = reason }; db.WorkStatusHistories.Add(hx); } } if (old == WorkStatus.Completed || target != WorkStatus.Completed) { w.CompletedDate = null; w.CompletedAt = null; w.CompletedDateKnowledge = CompletionDateKnowledge.NotApplicable; } if (target == WorkStatus.Completed) { w.CompletedDate = today; w.CompletedAt = actionAt; w.CompletedDateKnowledge = CompletionDateKnowledge.Known; } if (target == WorkStatus.Cancelled) { var ship = await db.Shipments.SingleOrDefaultAsync(x => x.WorkItemId == id, ct); if (ship?.ShipmentStatus == ShipmentStatus.NotShipped) ship.FulfillmentPaused = !continueFulfillment; } }
        w.WorkStatus = target; w.Version++; w.UpdatedAt = DateTimeOffset.UtcNow; if (!(old == WorkStatus.Completed && target == WorkStatus.Cancelled)) db.WorkStatusHistories.Add(new WorkStatusHistory { WorkItemId = id, OldStatus = old, NewStatus = target, EventType = target == WorkStatus.Cancelled ? HistoryEventType.Cancelled : target == WorkStatus.Completed ? HistoryEventType.Completed : old == WorkStatus.Cancelled ? HistoryEventType.Restored : old == WorkStatus.Completed ? HistoryEventType.Reopened : HistoryEventType.StatusChanged, OccurredDate = today, OccurredAt = actionAt, RecordedAt = DateTimeOffset.UtcNow, Reason = reason, PreviousCompletedDate = old == WorkStatus.Completed ? priorCompletedDate : null, PreviousCompletedDateKnowledge = old == WorkStatus.Completed ? priorCompletionKnowledge : CompletionDateKnowledge.NotApplicable, PreviousCompletedAt = old == WorkStatus.Completed ? priorCompletedAt : null }); db.ActivityLogs.Add(new ActivityLog { EntityType = "WorkItem", EntityId = id, WorkItemId = id, ActionType = "StatusChanged", Summary = $"工作状态：{StatusName(old)} → {StatusName(target)}" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"；原因：{reason}") + (old == WorkStatus.Cancelled ? $"；恢复履约：{(continueFulfillment ? "是" : "否")}" : target == WorkStatus.Cancelled ? $"；继续履约：{(continueFulfillment ? "是" : "否")}" : string.Empty) }); await db.SaveChangesAsync(ct);
    }
    static bool Allowed(WorkStatus a, WorkStatus b) => a switch { WorkStatus.NotStarted => b is WorkStatus.InProgress or WorkStatus.Completed or WorkStatus.Cancelled, WorkStatus.InProgress => b is WorkStatus.Completed or WorkStatus.NeedsRework or WorkStatus.Cancelled, WorkStatus.NeedsRework => b is WorkStatus.InProgress or WorkStatus.Completed or WorkStatus.Cancelled, WorkStatus.Completed => b is WorkStatus.InProgress or WorkStatus.NeedsRework or WorkStatus.Cancelled, _ => false };
    static string StatusName(WorkStatus status) => status switch
    {
        WorkStatus.NotStarted => "未开始",
        WorkStatus.InProgress => "进行中",
        WorkStatus.Completed => "已完成",
        WorkStatus.NeedsRework => "需返工",
        WorkStatus.Cancelled => "已取消",
        _ => status.ToString()
    };
}
