using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public sealed partial class WorkbenchService
{
    public async Task<DashboardDto> GetDashboard(CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var settings = await ReadSettings(cancellationToken);
        var items = await db.WorkItems.AsNoTracking().ToListAsync(cancellationToken);
        var ids = items.Select(x => x.Id).ToArray();
        var finance = await LoadFinance(ids, cancellationToken);
        var shipmentRows = await db.Shipments.AsNoTracking().ToListAsync(cancellationToken);
        var shipments = shipmentRows.ToDictionary(x => x.WorkItemId);
        var rows = items.ToDictionary(x => x.Id, x => ToRow(x, today, settings, finance, shipments.GetValueOrDefault(x.Id)));
        var activeRows = rows.Values.Where(x => IsActive(x.Status)).ToList();
        var overdue = activeRows.Where(x => x.EffectiveDueDate < today).ToList();
        var dueToday = activeRows.Where(x => x.EffectiveDueDate == today).ToList();
        var upcoming = activeRows.Where(x => IsUpcoming(x, today, settings)).ToList();
        var inProgress = rows.Values.Where(x => x.Status == WorkStatus.InProgress).ToList();
        var needsInformation = items.Where(x => NeedsInformation(x)).Select(x => rows[x.Id]).ToList();
        var readyToShip = rows.Values.Where(x => x.FulfillmentState == "待寄出").ToList();
        var awaitingPayment = rows.Values.Where(x => x.OutstandingCents is > 0).ToList();
        var completionReview = items.Where(x => x.WorkStatus == WorkStatus.Completed && x.CompletedDateKnowledge == CompletionDateKnowledge.Unknown)
            .Select(x => rows[x.Id]).ToList();
        var shipmentReview = items.Where(x => IsHistoryShipmentReview(x, shipments.GetValueOrDefault(x.Id)))
            .Select(x => rows[x.Id]).ToList();

        return new DashboardDto(today,
        [
            Section(WorkSectionKind.Overdue, "已逾期", overdue),
            Section(WorkSectionKind.DueToday, "今日到期", dueToday),
            Section(WorkSectionKind.Upcoming, "即将到期", upcoming),
            Section(WorkSectionKind.InProgress, "进行中", inProgress),
            Section(WorkSectionKind.NeedsInformation, "待补信息", needsInformation),
            Section(WorkSectionKind.ReadyToShip, "待寄出", readyToShip),
            Section(WorkSectionKind.AwaitingPayment, "待收款", awaitingPayment),
            Section(WorkSectionKind.HistoricalCompletionReview, "历史资料完善", completionReview),
            Section(WorkSectionKind.HistoricalShipmentReview, "历史物流核对", shipmentReview)
        ]);
    }

    public async Task<WorkItemListResultDto> GetList(WorkItemListQueryDto query, CancellationToken cancellationToken = default)
    {
        var source = db.WorkItems.AsNoTracking().AsQueryable();
        var settings = await ReadSettings(cancellationToken);
        if (!query.IncludeArchived) source = source.Where(x => !x.IsArchived);
        if (query.Status.HasValue) source = source.Where(x => x.WorkStatus == query.Status.Value);
        if (query.ProjectType.HasValue) source = source.Where(x => x.ProjectType == query.ProjectType.Value);
        if (query.ScheduleCategory.HasValue) source = source.Where(x => x.ScheduleCategory == query.ScheduleCategory.Value);
        if (query.ReceivedFrom.HasValue) source = source.Where(x => x.ReceivedDate >= query.ReceivedFrom.Value);
        if (query.ReceivedTo.HasValue) source = source.Where(x => x.ReceivedDate <= query.ReceivedTo.Value);
        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search)) source = source.Where(x =>
            (x.BusinessOrderNo != null && x.BusinessOrderNo.Contains(search)) || x.ProjectName.Contains(search)
            || (x.CurrentWork != null && x.CurrentWork.Contains(search)) || (x.NextStep != null && x.NextStep.Contains(search))
            || (x.Notes != null && x.Notes.Contains(search)) || (x.ProjectPath != null && x.ProjectPath.Contains(search)));
        var items = await source.ToListAsync(cancellationToken);
        var today = clock.Today;
        var finance = await LoadFinance(items.Select(x => x.Id).ToArray(), cancellationToken);
        var itemIds = items.Select(x => x.Id).ToArray();
        var shipments = await db.Shipments.AsNoTracking().Where(x => itemIds.Contains(x.WorkItemId)).ToListAsync(cancellationToken);
        var shipmentMap = shipments.ToDictionary(x => x.WorkItemId);
        var rows = items.Select(x => ToRow(x, today, settings, finance, shipmentMap.GetValueOrDefault(x.Id)))
            .OrderBy(x => UrgencyOrder(x.Urgency)).ThenBy(x => x.EffectiveDueDate ?? DateOnly.MaxValue)
            .ThenBy(x => x.ProjectName, StringComparer.CurrentCulture).ToList();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        return new WorkItemListResultDto(rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), rows.Count, page, pageSize);
    }

    private async Task<WorkbenchSettingsDto> ReadSettings(CancellationToken cancellationToken)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == SettingsKey, cancellationToken);
        if (row is null) return DefaultSettings();
        try { return System.Text.Json.JsonSerializer.Deserialize<WorkbenchSettingsDto>(row.Value, SettingsJsonOptions) ?? DefaultSettings(); }
        catch (System.Text.Json.JsonException) { throw new InvalidOperationException("工作台设置无法解析，请检查设置数据。"); }
    }

    private static readonly System.Text.Json.JsonSerializerOptions SettingsJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    private WorkbenchSettingsDto DefaultSettings()
    {
        var source = db.Database.GetDbConnection().DataSource;
        var directory = !string.IsNullOrWhiteSpace(source) && source != ":memory:"
            ? Path.GetDirectoryName(Path.GetFullPath(source))!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalOrderDesk");
        return new(0, 1, 7, 1, Path.Combine(directory, "Backups"), 30);
    }

    private async Task<FinanceSnapshot> LoadFinance(Guid[] workItemIds, CancellationToken cancellationToken)
    {
        if (workItemIds.Length == 0) return new FinanceSnapshot(new Dictionary<Guid, long>());
        var active = await db.PaymentTransactions.AsNoTracking().Where(x => x.Status == RecordStatus.Active).ToListAsync(cancellationToken);
        var transactionType = active.ToDictionary(x => x.Id, x => x.Type);
        var activeIds = transactionType.Keys.ToArray();
        var allocations = await db.PaymentAllocations.AsNoTracking()
            .Where(x => activeIds.Contains(x.PaymentTransactionId) && workItemIds.Contains(x.WorkItemId)).ToListAsync(cancellationToken);
        var sums = new Dictionary<Guid, long>();
        foreach (var allocation in allocations)
        {
            var signed = transactionType[allocation.PaymentTransactionId] == TransactionType.Receipt ? allocation.AmountCents : -allocation.AmountCents;
            sums[allocation.WorkItemId] = sums.GetValueOrDefault(allocation.WorkItemId) + signed;
        }
        return new FinanceSnapshot(sums);
    }

    private static WorkItemRowDto ToRow(WorkItem item, DateOnly today, WorkbenchSettingsDto settings, FinanceSnapshot finance, Shipment? shipment)
    {
        var net = finance.NetByWorkItem.GetValueOrDefault(item.Id);
        long? outstanding = item.OrderAmountCents.HasValue ? Math.Max(0, item.OrderAmountCents.Value - net) : null;
        int? days = item.EffectiveDueDate.HasValue ? item.EffectiveDueDate.Value.DayNumber - today.DayNumber : null;
        var urgency = !IsActive(item.WorkStatus) ? WorkUrgency.NoDueDate
            : item.EffectiveDueDate < today ? WorkUrgency.Overdue
            : item.EffectiveDueDate == today ? WorkUrgency.DueToday
            : item.ScheduleCategory == ScheduleCategory.Physical && days > 0 && days <= settings.PhysicalReminderLeadDays ? WorkUrgency.PhysicalUpcoming
            : item.ScheduleCategory == ScheduleCategory.Digital && days > 0 && days <= settings.DigitalReminderLeadDays ? WorkUrgency.DigitalUpcoming
            : item.WorkStatus == WorkStatus.NeedsRework ? WorkUrgency.NeedsRework
            : item.WorkStatus == WorkStatus.InProgress ? WorkUrgency.InProgress
            : item.EffectiveDueDate.HasValue ? WorkUrgency.NotStarted : WorkUrgency.NoDueDate;
        var payment = PaymentLabel(item.OrderAmountCents, net);
        return new WorkItemRowDto(item.Id, item.BusinessOrderNo, item.ProjectName, item.ProjectType, item.ScheduleCategory,
            item.WorkStatus, item.ReceivedDate, item.DueDateMode, item.EffectiveDueDate, urgency, days, payment, net,
            outstanding, FulfillmentLabel(item, shipment), item.CurrentWork, item.NextStep, item.Version, item.IsArchived);
    }

    private static bool IsActive(WorkStatus status) => status is WorkStatus.NotStarted or WorkStatus.InProgress or WorkStatus.NeedsRework;
    private static bool IsUpcoming(WorkItemRowDto row, DateOnly today, WorkbenchSettingsDto settings)
    {
        if (!IsActive(row.Status) || !row.EffectiveDueDate.HasValue) return false;
        var days = row.EffectiveDueDate.Value.DayNumber - today.DayNumber;
        var reminder = row.ScheduleCategory == ScheduleCategory.Physical ? settings.PhysicalReminderLeadDays
            : row.ScheduleCategory == ScheduleCategory.Digital ? settings.DigitalReminderLeadDays : 0;
        return days is > 0 && days <= reminder;
    }

    private static bool NeedsInformation(WorkItem item)
    {
        if (IsActive(item.WorkStatus) && (!item.EffectiveDueDate.HasValue || item.ScheduleCategory == ScheduleCategory.Pending)) return true;
        if (item.WorkStatus != WorkStatus.Cancelled && item.ShippingRequirement == ShippingRequirement.Pending) return true;
        if (item.OrderAmountCents is null) return true;
        return item.WorkStatus is WorkStatus.Completed or WorkStatus.Cancelled && item.CostConfirmationStatus == CostConfirmationStatus.Pending;
    }

    private static bool IsHistoryShipmentReview(WorkItem item, Shipment? shipment) => shipment?.ShipmentStatus == ShipmentStatus.Unknown
        || item.SourceKind == SourceKind.HistoricalImport && item.ShippingRequirement == ShippingRequirement.Required && shipment is null;

    private static WorkSectionDto Section(WorkSectionKind kind, string title, IReadOnlyCollection<WorkItemRowDto> items)
    {
        var sorted = items.OrderBy(x => UrgencyOrder(x.Urgency)).ThenBy(x => x.EffectiveDueDate ?? DateOnly.MaxValue)
            .ThenBy(x => x.ProjectName, StringComparer.CurrentCulture).ToArray();
        return new WorkSectionDto(kind, title, sorted.Length, sorted);
    }

    private static int UrgencyOrder(WorkUrgency urgency) => urgency switch
    {
        WorkUrgency.Overdue => 0, WorkUrgency.DueToday => 1, WorkUrgency.PhysicalUpcoming => 2,
        WorkUrgency.DigitalUpcoming => 3, WorkUrgency.NeedsRework => 4, WorkUrgency.InProgress => 5,
        WorkUrgency.NotStarted => 6, _ => 7
    };

    private static string PaymentLabel(long? amount, long net) => amount switch
    {
        null => "待补金额", 0 when net == 0 => "无需收款", 0 => "超收待核对", _ when net < 0 => "待核对",
        _ when net == amount => "已收齐", _ when net > amount => "超收待核对", _ when net == 0 => "未收款", _ => "部分收款"
    };

    private static string FulfillmentLabel(WorkItem item, Shipment? shipment)
    {
        if (item.ShippingRequirement == ShippingRequirement.None) return "无需快递";
        if (item.ShippingRequirement == ShippingRequirement.Pending) return item.WorkStatus == WorkStatus.Cancelled ? "待确认" : "物流待确认";
        if (item.SourceKind == SourceKind.HistoricalImport && shipment is null) return "历史物流待核对";
        if (shipment?.ShipmentStatus == ShipmentStatus.Unknown) return "历史物流待核对";
        if (shipment?.ShipmentStatus == ShipmentStatus.Delivered)
        {
            if (shipment.ShippedDateKnowledge == DateKnowledge.Unknown)
                return shipment.DeliveredDateKnowledge == DateKnowledge.Unknown ? "已签收（寄出和签收日期未知）" : "已签收（寄出日期未知）";
            return shipment.DeliveredDateKnowledge == DateKnowledge.Unknown ? "已签收（签收日期未知）" : "已签收";
        }
        if (shipment?.ShipmentStatus == ShipmentStatus.Shipped) return shipment.ShippedDateKnowledge == DateKnowledge.Unknown ? "已寄出（日期未知）" : "已寄出";
        if (shipment?.FulfillmentPaused == true) return "已暂停";
        return item.WorkStatus is WorkStatus.Completed or WorkStatus.Cancelled ? "待寄出" : "未到发货阶段";
    }

    private sealed record FinanceSnapshot(IReadOnlyDictionary<Guid, long> NetByWorkItem);
}
