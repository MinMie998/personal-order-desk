using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public interface IBusinessClock { DateTimeOffset Now { get; } DateOnly Today { get; } }
public sealed class SystemBusinessClock : IBusinessClock
{
    private readonly TimeZoneInfo zone = ResolveZone();
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, zone).DateTime);
    private static TimeZoneInfo ResolveZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }
}

public sealed partial class WorkbenchService : IWorkbenchService
{
    private const string SettingsKey = "WorkbenchSettings";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DeskDbContext db;
    private readonly IBusinessClock clock;
    private readonly WorkItemService workItems;
    private readonly ILogger<WorkbenchService> logger;

    public WorkbenchService(DeskDbContext db, IBusinessClock clock, WorkItemService workItems, ILogger<WorkbenchService> logger)
    { this.db = db; this.clock = clock; this.workItems = workItems; this.logger = logger; }

    public async Task<WorkItemEditorDto> GetEditor(Guid? workItemId = null, CancellationToken cancellationToken = default)
    {
        if (workItemId.HasValue)
        {
            var found = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workItemId.Value, cancellationToken);
            if (found is null) throw new KeyNotFoundException("找不到该工作项。");
            return ToEditor(found);
        }
        return new WorkItemEditorDto(null, null, string.Empty, ProjectType.MixedOrUnknown, ScheduleCategory.Pending,
            ValueSource.Default, ShippingRequirement.Pending, ValueSource.Default, clock.Today, DueDateMode.Auto, null, null,
            WorkStatus.NotStarted, null, null, null, null, null, 0, false);
    }

    public async Task<WorkItemDetailDto?> GetDetail(Guid workItemId, CancellationToken cancellationToken = default)
    {
        var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workItemId, cancellationToken);
        if (item is null) return null;
        var settings = await ReadSettings(cancellationToken);
        var finance = await LoadFinance([workItemId], cancellationToken);
        var shipment = await db.Shipments.AsNoTracking().SingleOrDefaultAsync(x => x.WorkItemId == workItemId, cancellationToken);
        var row = ToRow(item, clock.Today, settings, finance, shipment);
        var costRows = await (from expenseGroup in db.ExpenseGroups.AsNoTracking()
                              join expense in db.Expenses.AsNoTracking() on expenseGroup.Id equals expense.ExpenseGroupId
                              where expenseGroup.WorkItemId == workItemId && expense.Status == ExpenseStatus.Active
                              select expense.AmountCents).ToListAsync(cancellationToken);
        long? confirmedCost = item.CostConfirmationStatus switch
        {
            CostConfirmationStatus.ConfirmedNoCost => 0,
            CostConfirmationStatus.ConfirmedComplete => costRows.Sum(),
            _ => null
        };
        var histories = (await db.WorkStatusHistories.AsNoTracking().Where(x => x.WorkItemId == workItemId).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.RecordedAt)
            .Select(x => new WorkStatusHistoryDto(x.OldStatus, x.NewStatus, x.EventType, x.OccurredDate, x.OccurredAt,
                x.RecordedAt, x.PreviousCompletedDate, x.PreviousCompletedDateKnowledge, x.Reason, x.IsBackfilled)).ToArray();
        var activities = (await db.ActivityLogs.AsNoTracking().Where(x => x.WorkItemId == workItemId).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.RecordedAt)
            .Select(x => new ActivityLogDto(x.ActionType, x.RecordedAt, x.Summary, x.BeforeData, x.AfterData)).ToArray();
        var relations = await LoadRelations(workItemId, cancellationToken);
        return new WorkItemDetailDto(ToEditor(item), row.PaymentState, row.NetReceivedCents, row.OutstandingCents,
            CostLabel(item.CostConfirmationStatus), confirmedCost, row.FulfillmentState, shipment?.ShipmentStatus,
            shipment?.FulfillmentPaused ?? false, histories, activities, relations);
    }

    public async Task<WorkItemDetailDto> Create(WorkItemSaveRequestDto request, CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettings(cancellationToken);
        var item = NewWorkItem(request, settings.NonPhysicalDefaultDurationDays);
        await SaveCreated(item, null, null, cancellationToken);
        return (await GetDetail(item.Id, cancellationToken))!;
    }

    public async Task<WorkItemDetailDto> CreateAdditionalWork(Guid sourceWorkItemId, WorkItemSaveRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!await db.WorkItems.AnyAsync(x => x.Id == sourceWorkItemId, cancellationToken)) throw new KeyNotFoundException("找不到原工作项。");
        var settings = await ReadSettings(cancellationToken);
        var item = NewWorkItem(request, settings.NonPhysicalDefaultDurationDays);
        await SaveCreated(item, sourceWorkItemId, request.Notes, cancellationToken);
        return (await GetDetail(item.Id, cancellationToken))!;
    }

    public async Task<WorkItemDetailDto> BackfillCompletionDate(Guid workItemId, DateOnly completedDate, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (completedDate > clock.Today) throw new ArgumentOutOfRangeException(nameof(completedDate), "完成日期不能晚于今天。");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await db.WorkItems.SingleOrDefaultAsync(item => item.Id == workItemId, cancellationToken)
            ?? throw new KeyNotFoundException("工作项不存在。");
        if (item.Version != expectedVersion) throw new DbUpdateConcurrencyException("工作项已在其他页面更新，请刷新后重试。");
        if (item.SourceKind != SourceKind.HistoricalImport || item.WorkStatus != WorkStatus.Completed
            || item.CompletedDateKnowledge != CompletionDateKnowledge.Unknown)
            throw new InvalidOperationException("此操作仅用于补录已完成历史项目的未知完成日期。");
        item.CompletedDate = completedDate;
        item.CompletedDateKnowledge = CompletionDateKnowledge.Known;
        item.CompletedAt = null;
        item.Version++;
        item.UpdatedAt = clock.Now;
        db.WorkStatusHistories.Add(new WorkStatusHistory
        {
            WorkItemId = item.Id, OldStatus = WorkStatus.Completed, NewStatus = WorkStatus.Completed,
            EventType = HistoryEventType.Completed, OccurredDate = completedDate, OccurredAt = null,
            RecordedAt = clock.Now, IsBackfilled = true, SourceKind = SourceKind.Manual,
            PreviousCompletedDateKnowledge = CompletionDateKnowledge.Unknown, Reason = "补录历史完成日期，具体时间未知"
        });
        db.ActivityLogs.Add(new ActivityLog
        {
            EntityType = nameof(WorkItem), EntityId = item.Id, WorkItemId = item.Id,
            ActionType = "CompletionDateBackfilled", Summary = "已补录历史完成日期，工作状态及财务记录不变",
            RecordedAt = clock.Now, SourceKind = LogSourceKind.Manual,
            BeforeData = "{\"completedDateKnowledge\":\"Unknown\"}",
            AfterData = JsonSerializer.Serialize(new { completedDate, CompletedAt = (DateTimeOffset?)null }, JsonOptions)
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetDetail(item.Id, cancellationToken))!;
    }

    public async Task<WorkItemDetailDto> LinkExistingAdditionalWork(Guid originalId, Guid additionalId, string reason,
        CancellationToken cancellationToken = default)
    {
        if (originalId == additionalId) throw new ArgumentException("追加工作不能关联自己。");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("请说明追加关联的依据。");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.WorkItems.CountAsync(item => item.Id == originalId || item.Id == additionalId, cancellationToken) != 2)
            throw new KeyNotFoundException("原工作项或追加工作项不存在。");
        var existing = await db.WorkItemRelations.AnyAsync(edge => edge.SourceWorkItemId == originalId
            && edge.TargetWorkItemId == additionalId && edge.RelationType == RelationType.AdditionalWork, cancellationToken);
        if (!existing)
        {
            var relation = new WorkItemRelation
            {
                SourceWorkItemId = originalId, TargetWorkItemId = additionalId,
                RelationType = RelationType.AdditionalWork, CreatedAt = clock.Now, Notes = reason.Trim()
            };
            db.WorkItemRelations.Add(relation);
            db.ActivityLogs.Add(new ActivityLog
            {
                EntityType = nameof(WorkItemRelation), EntityId = relation.Id, ActionType = "ExistingAdditionalWorkLinked",
                Summary = "关联原单与已存在的追加工作，金额和进度分别保留", RecordedAt = clock.Now,
                SourceKind = LogSourceKind.Manual,
                AfterData = JsonSerializer.Serialize(new { originalId, additionalId, Reason = reason.Trim() })
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return (await GetDetail(additionalId, cancellationToken))!;
    }

    public async Task<WorkItemDetailDto> Update(Guid workItemId, WorkItemSaveRequestDto request, CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettings(cancellationToken);
        var item = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == workItemId, cancellationToken)
            ?? throw new KeyNotFoundException("找不到该工作项。");
        if (request.ExpectedVersion != item.Version) throw new DbUpdateConcurrencyException("工作项已被其他页面修改，请刷新后重试。");
        ValidateRequest(request, settings.NonPhysicalDefaultDurationDays, false, item.SourceKind);
        if (request.ProjectType != item.ProjectType && request.ApplyProjectTypeDefaults is null)
            throw new InvalidOperationException("项目类型已变化，请明确选择应用新默认值或保留当前值。");
        var beforeAmount = item.OrderAmountCents;
        if (beforeAmount != request.OrderAmountCents && string.IsNullOrWhiteSpace(request.OrderAmountChangeReason))
            throw new ArgumentException("调整订单金额时必须填写原因。", nameof(request.OrderAmountChangeReason));
        var projectPathChanged = item.ProjectPath != Clean(request.ProjectPath);
        var before = new { item.BusinessOrderNo, item.ProjectName, item.ProjectType, item.ReceivedDate, item.DueDateMode,
            item.ScheduleCategory, item.ScheduleCategorySource, item.ShippingRequirement, item.ShippingRequirementSource,
            item.SpecifiedDueDate, item.EffectiveDueDate, item.CurrentWork, item.NextStep, item.OrderAmountCents };

        if (request.ProjectType != item.ProjectType && request.ApplyProjectTypeDefaults == false)
        {
            item.ScheduleCategorySource = ValueSource.Manual;
            item.ShippingRequirementSource = ValueSource.Manual;
        }
        item.ProjectType = request.ProjectType;
        if (request.ApplyProjectTypeDefaults == true)
        {
            var defaults = WorkItemService.MapDefaults(request.ProjectType);
            item.ScheduleCategory = defaults.Schedule; item.ScheduleCategorySource = ValueSource.Default;
            item.ShippingRequirement = defaults.Shipping; item.ShippingRequirementSource = ValueSource.Default;
        }
        if (request.ScheduleCategory.HasValue) { item.ScheduleCategory = request.ScheduleCategory.Value; item.ScheduleCategorySource = ValueSource.Manual; }
        if (request.ShippingRequirement.HasValue) { item.ShippingRequirement = request.ShippingRequirement.Value; item.ShippingRequirementSource = ValueSource.Manual; }
        item.BusinessOrderNo = Clean(request.BusinessOrderNo);
        item.ProjectName = request.ProjectName.Trim();
        item.ReceivedDate = request.ReceivedDate;
        item.DueDateMode = request.DueDateMode;
        item.SpecifiedDueDate = request.SpecifiedDueDate;
        item.EffectiveDueDate = WorkItemService.CalculateDue(item, settings.NonPhysicalDefaultDurationDays);
        item.CurrentWork = Clean(request.CurrentWork); item.NextStep = Clean(request.NextStep);
        item.OrderAmountCents = request.OrderAmountCents; item.ProjectPath = Clean(request.ProjectPath); item.Notes = Clean(request.Notes);
        item.Version++; item.UpdatedAt = clock.Now;
        db.ActivityLogs.Add(new ActivityLog { EntityType = "WorkItem", EntityId = item.Id, WorkItemId = item.Id,
            ActionType = "WorkItemUpdated", RecordedAt = clock.Now, Summary = "工作项资料已更新",
            BeforeData = JsonSerializer.Serialize(before, JsonOptions),
            AfterData = JsonSerializer.Serialize(new { item.BusinessOrderNo, item.ProjectName, item.ProjectType, item.ReceivedDate,
                item.ScheduleCategory, item.ScheduleCategorySource, item.ShippingRequirement, item.ShippingRequirementSource,
                item.DueDateMode, item.SpecifiedDueDate, item.EffectiveDueDate, item.CurrentWork, item.NextStep, item.OrderAmountCents }, JsonOptions) });
        if (projectPathChanged)
            db.ActivityLogs.Add(new ActivityLog { EntityType = "WorkItem", EntityId = item.Id, WorkItemId = item.Id,
                ActionType = "ProjectPathChanged", RecordedAt = clock.Now, Summary = "工程路径已修改" });
        if (beforeAmount != item.OrderAmountCents)
            db.ActivityLogs.Add(new ActivityLog { EntityType = "WorkItem", EntityId = item.Id, WorkItemId = item.Id,
                ActionType = "OrderAmountChanged", RecordedAt = clock.Now,
                Summary = $"订单金额调整：{request.OrderAmountChangeReason!.Trim()}",
                BeforeData = JsonSerializer.Serialize(new { AmountCents = beforeAmount }, JsonOptions),
                AfterData = JsonSerializer.Serialize(new { AmountCents = item.OrderAmountCents, Reason = request.OrderAmountChangeReason }, JsonOptions) });
        if (item.ShippingRequirement == ShippingRequirement.Required
            && !await db.Shipments.AnyAsync(x => x.WorkItemId == item.Id, cancellationToken)) AddDefaultShipment(item, clock.Now);
        await db.SaveChangesAsync(cancellationToken);
        return (await GetDetail(item.Id, cancellationToken))!;
    }

    public async Task<WorkItemDetailDto> ChangeStatus(Guid workItemId, WorkStatusChangeRequestDto request, CancellationToken cancellationToken = default)
    {
        await workItems.SetStatusAsync(workItemId, request.TargetStatus, clock.Today, request.ExpectedVersion,
            request.Reason, request.RestoreChoice, request.ContinueFulfillment, cancellationToken);
        return (await GetDetail(workItemId, cancellationToken))!;
    }

    public async Task<WorkItemDetailDto> Archive(Guid workItemId, ArchiveRequestDto request, CancellationToken cancellationToken = default)
    {
        await workItems.SetArchivedAsync(workItemId, request.IsArchived, request.ExpectedVersion, cancellationToken);
        return (await GetDetail(workItemId, cancellationToken))!;
    }

    private async Task SaveCreated(WorkItem item, Guid? sourceId, string? relationNotes, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.WorkItems.Add(item);
        if (item.ShippingRequirement == ShippingRequirement.Required) AddDefaultShipment(item, clock.Now);
        db.ActivityLogs.Add(new ActivityLog { EntityType = "WorkItem", EntityId = item.Id, WorkItemId = item.Id,
            ActionType = "WorkItemCreated", RecordedAt = clock.Now, Summary = "已创建工作项" });
        if (sourceId.HasValue) db.WorkItemRelations.Add(new WorkItemRelation { SourceWorkItemId = sourceId.Value,
            TargetWorkItemId = item.Id, RelationType = RelationType.AdditionalWork, CreatedAt = clock.Now, Notes = Clean(relationNotes) });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private WorkItem NewWorkItem(WorkItemSaveRequestDto request, int defaultDays)
    {
        ValidateRequest(request, defaultDays, true, SourceKind.Manual);
        var defaults = WorkItemService.MapDefaults(request.ProjectType);
        var item = new WorkItem
        {
            BusinessOrderNo = Clean(request.BusinessOrderNo), ProjectName = request.ProjectName.Trim(), ProjectType = request.ProjectType,
            ScheduleCategory = request.ScheduleCategory ?? defaults.Schedule,
            ScheduleCategorySource = request.ScheduleCategory.HasValue ? ValueSource.Manual : ValueSource.Default,
            ShippingRequirement = request.ShippingRequirement ?? defaults.Shipping,
            ShippingRequirementSource = request.ShippingRequirement.HasValue ? ValueSource.Manual : ValueSource.Default,
            SourceKind = SourceKind.Manual, ReceivedDate = request.ReceivedDate, DueDateMode = request.DueDateMode,
            SpecifiedDueDate = request.SpecifiedDueDate, WorkStatus = WorkStatus.NotStarted,
            CurrentWork = Clean(request.CurrentWork), NextStep = Clean(request.NextStep), OrderAmountCents = request.OrderAmountCents,
            CostConfirmationStatus = CostConfirmationStatus.Pending, ProjectPath = Clean(request.ProjectPath), Notes = Clean(request.Notes),
            CompletedDateKnowledge = CompletionDateKnowledge.NotApplicable, Version = 1, CreatedAt = clock.Now, UpdatedAt = clock.Now
        };
        item.EffectiveDueDate = WorkItemService.CalculateDue(item, defaultDays);
        return item;
    }

    private static void ValidateRequest(WorkItemSaveRequestDto request, int defaultDays, bool isCreate, SourceKind sourceKind)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName)) throw new ArgumentException("项目名称不能为空。");
        if (request.ProjectName.Trim().Length > 200) throw new ArgumentException("项目名称不能超过200个字符。");
        if (request.BusinessOrderNo?.Length > 100) throw new ArgumentException("业务订单编号不能超过100个字符。");
        if (request.OrderAmountCents is < 0) throw new ArgumentOutOfRangeException(nameof(request.OrderAmountCents), "订单金额不能为负数。");
        if (defaultDays < 1) throw new ArgumentOutOfRangeException(nameof(defaultDays));
        if (sourceKind == SourceKind.Manual && !request.ReceivedDate.HasValue) throw new ArgumentException("手工工作项必须填写接单日期。");
        if (request.DueDateMode == DueDateMode.Manual && !request.SpecifiedDueDate.HasValue) throw new ArgumentException("手动截止日期必须指定日期。");
        if (request.DueDateMode != DueDateMode.Manual && request.SpecifiedDueDate.HasValue) throw new ArgumentException("只有手动模式可以指定截止日期。");
        if (request.DueDateMode == DueDateMode.HistoricalUnknown && sourceKind != SourceKind.HistoricalImport) throw new ArgumentException("HistoricalUnknown 仅用于历史导入记录。");
        if (!isCreate && request.ExpectedVersion is null) throw new ArgumentException("编辑保存必须提供 Version。", nameof(request.ExpectedVersion));
    }

    private void AddDefaultShipment(WorkItem item, DateTimeOffset now) => db.Shipments.Add(new Shipment
    {
        WorkItemId = item.Id,
        ShipmentStatus = item.SourceKind == SourceKind.HistoricalImport ? ShipmentStatus.Unknown : ShipmentStatus.NotShipped,
        ShippedDateKnowledge = item.SourceKind == SourceKind.HistoricalImport ? DateKnowledge.Unknown : DateKnowledge.NotApplicable,
        DeliveredDateKnowledge = item.SourceKind == SourceKind.HistoricalImport ? DateKnowledge.Unknown : DateKnowledge.NotApplicable,
        SourceKind = item.SourceKind, ImportBatchId = item.ImportBatchId,
        FulfillmentPaused = item.WorkStatus == WorkStatus.Cancelled, CreatedAt = now, UpdatedAt = now
    });

    private async Task<IReadOnlyList<WorkItemRelationDto>> LoadRelations(Guid id, CancellationToken ct)
    {
        var edges = await db.WorkItemRelations.AsNoTracking().Where(x => x.SourceWorkItemId == id || x.TargetWorkItemId == id).ToListAsync(ct);
        var relatedIds = edges.Select(x => x.SourceWorkItemId == id ? x.TargetWorkItemId : x.SourceWorkItemId).Distinct().ToArray();
        var related = await db.WorkItems.AsNoTracking().Where(x => relatedIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return edges.Where(x => related.ContainsKey(x.SourceWorkItemId == id ? x.TargetWorkItemId : x.SourceWorkItemId))
            .Select(x => { var otherId = x.SourceWorkItemId == id ? x.TargetWorkItemId : x.SourceWorkItemId; var other = related[otherId];
                return new WorkItemRelationDto(otherId, x.RelationType.ToString(), other.BusinessOrderNo, other.ProjectName, other.WorkStatus, x.Notes); }).ToArray();
    }

    private static WorkItemEditorDto ToEditor(WorkItem x) => new(x.Id, x.BusinessOrderNo, x.ProjectName, x.ProjectType,
        x.ScheduleCategory, x.ScheduleCategorySource, x.ShippingRequirement, x.ShippingRequirementSource, x.ReceivedDate,
        x.DueDateMode, x.SpecifiedDueDate, x.EffectiveDueDate, x.WorkStatus, x.CurrentWork, x.NextStep, x.OrderAmountCents,
        x.ProjectPath, x.Notes, x.Version, x.IsArchived, x.CompletedDate, x.CompletedDateKnowledge, x.CompletedAt,
        x.CancelledFromStatus, x.SourceKind);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string CostLabel(CostConfirmationStatus status) => status switch
    { CostConfirmationStatus.Pending => "待确认", CostConfirmationStatus.ConfirmedNoCost => "已确认无成本", _ => "已确认完整" };
}
