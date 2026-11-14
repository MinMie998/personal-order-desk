using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public sealed class ShippingService(DeskDbContext db, IBusinessClock clock) : IShippingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ShippingQueueItemDto>> GetQueue(
        ShippingQueueFilter filter = ShippingQueueFilter.All, CancellationToken ct = default)
    {
        var rows = await (
            from work in db.WorkItems.AsNoTracking()
            join shipment in db.Shipments.AsNoTracking()
                on work.Id equals shipment.WorkItemId into shipmentRows
            from shipment in shipmentRows.DefaultIfEmpty()
            select new { Work = work, Shipment = shipment }).ToListAsync(ct);

        return rows
            .Where(x => x.Work.ShippingRequirement != ShippingRequirement.None || x.Shipment != null)
            .Where(x => MatchFilter(x.Shipment, x.Work, filter))
            .Select(x => new ShippingQueueItemDto(
                x.Work.Id, x.Work.ProjectName, x.Work.BusinessOrderNo, x.Work.WorkStatus,
                x.Work.ShippingRequirement, x.Work.SourceKind, x.Shipment?.ShipmentStatus, x.Shipment?.FulfillmentPaused ?? false,
                x.Shipment?.LatestShipDate, Urgency(x.Shipment, x.Work), x.Shipment?.Carrier,
                x.Shipment?.TrackingNumber, x.Shipment?.NoTrackingNumber, x.Work.Version, x.Work.IsArchived))
            .OrderBy(x => UrgencyRank(x.ShippingUrgency))
            .ThenBy(x => x.LatestShipDate)
            .ToArray();
    }

    public async Task<ShippingDraftDto> GetDraft(Guid workItemId, CancellationToken ct = default)
    {
        var (work, shipment) = await Load(workItemId, ct);
        return ToDto(work, shipment);
    }

    public async Task<ShippingDraftDto> PrepareHistoryReview(
        Guid workItemId, long expectedVersion, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var work = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == workItemId, ct)
            ?? throw new KeyNotFoundException("找不到工作项。");
        CheckVersion(work, expectedVersion);
        if (work.SourceKind != SourceKind.HistoricalImport || work.ShippingRequirement != ShippingRequirement.Required)
            throw new InvalidOperationException("仅历史导入且确认需要快递的工作项可准备历史核对。");
        var shipment = await db.Shipments.SingleOrDefaultAsync(x => x.WorkItemId == workItemId, ct);
        if (shipment is null)
        {
            shipment = new Shipment
            {
                WorkItemId = work.Id,
                ShipmentStatus = ShipmentStatus.Unknown,
                ShippedDateKnowledge = DateKnowledge.Unknown,
                DeliveredDateKnowledge = DateKnowledge.Unknown,
                SourceKind = SourceKind.HistoricalImport,
                ImportBatchId = work.ImportBatchId,
                CreatedAt = clock.Now,
                UpdatedAt = clock.Now
            };
            db.Shipments.Add(shipment);
            work.Version++;
            work.UpdatedAt = clock.Now;
            AddActivity(work, shipment, "ShipmentHistoryReviewPrepared", "已准备历史物流核对（尚无物流事实）", null, Snapshot(shipment));
            await db.SaveChangesAsync(ct);
        }
        else if (shipment.SourceKind != SourceKind.HistoricalImport || shipment.ShipmentStatus != ShipmentStatus.Unknown)
        {
            throw new InvalidOperationException("该物流记录已有已确认状态，不能重复准备历史核对。");
        }
        await transaction.CommitAsync(ct);
        return ToDto(work, shipment);
    }

    public async Task<ShippingDraftDto> SaveDraft(Guid id, ShippingDraftRequestDto request, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (work, shipment) = await Load(id, ct);
        CheckVersion(work, request.ExpectedVersion);
        if (shipment.ShipmentStatus != ShipmentStatus.NotShipped)
            throw new InvalidOperationException("已寄出或历史状态需通过历史核对操作修改。");
        if (request.FulfillmentPaused && shipment.ShipmentStatus != ShipmentStatus.NotShipped)
            throw new InvalidOperationException("仅待寄出记录可以暂停履约。");
        ValidateTracking(request.TrackingNumber, request.NoTrackingNumber, shipped: false);
        var before = Snapshot(shipment);
        var wasPaused = shipment.FulfillmentPaused;
        shipment.Carrier = Clean(request.Carrier);
        shipment.TrackingNumber = Clean(request.TrackingNumber);
        shipment.NoTrackingNumber = Clean(request.TrackingNumber) is null ? request.NoTrackingNumber : false;
        shipment.LatestShipDate = request.LatestShipDate;
        shipment.FulfillmentPaused = request.FulfillmentPaused;
        var action = wasPaused == shipment.FulfillmentPaused ? "ShipmentDraftSaved" : "ShipmentFulfillmentPauseChanged";
        var summary = wasPaused == shipment.FulfillmentPaused
            ? "物流草稿已保存"
            : shipment.FulfillmentPaused ? "物流履约已暂停" : "用户明确继续物流履约";
        await Save(work, shipment, action, summary, before, ct);
        await transaction.CommitAsync(ct);
        return ToDto(work, shipment);
    }

    public async Task<ShippingDraftDto> MarkShipped(Guid id, MarkShippedRequestDto request, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (work, shipment) = await Load(id, ct);
        CheckVersion(work, request.ExpectedVersion);
        if (shipment.ShipmentStatus != ShipmentStatus.NotShipped)
            throw new InvalidOperationException("仅待寄出记录可标记寄出。");
        ValidateTracking(request.TrackingNumber, request.NoTrackingNumber, shipped: true);
        ValidateDate(request.ShippedDate, "寄出日期");
        if (work.ShippingRequirement != ShippingRequirement.Required)
            throw new InvalidOperationException("请先确认需要快递。");
        if (!Eligible(shipment, work))
            throw new InvalidOperationException("工作未完成、履约已暂停或尚未明确继续，不能登记寄出。");

        var before = Snapshot(shipment);
        shipment.ShipmentStatus = ShipmentStatus.Shipped;
        shipment.Carrier = Clean(request.Carrier);
        shipment.TrackingNumber = Clean(request.TrackingNumber);
        shipment.NoTrackingNumber = request.NoTrackingNumber == true;
        shipment.FulfillmentPaused = false;
        shipment.ShippedDate = request.ShippedDate;
        shipment.ShippedDateKnowledge = DateKnowledge.Known;
        await Save(work, shipment, "ShipmentShipped", "已记录寄出", before, ct);
        await transaction.CommitAsync(ct);
        return ToDto(work, shipment);
    }

    public async Task<ShippingDraftDto> MarkDelivered(Guid id, MarkDeliveredRequestDto request, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (work, shipment) = await Load(id, ct);
        CheckVersion(work, request.ExpectedVersion);
        if (shipment.ShipmentStatus != ShipmentStatus.Shipped || !shipment.ShippedDate.HasValue)
            throw new InvalidOperationException("仅有已知寄出日期的包裹可登记签收。");
        ValidateDate(request.DeliveredDate, "签收日期");
        if (request.DeliveredDate < shipment.ShippedDate)
            throw new ArgumentException("签收日期不能早于寄出日期。");

        var before = Snapshot(shipment);
        shipment.ShipmentStatus = ShipmentStatus.Delivered;
        shipment.DeliveredDate = request.DeliveredDate;
        shipment.DeliveredDateKnowledge = DateKnowledge.Known;
        await Save(work, shipment, "ShipmentDelivered", "已记录签收", before, ct);
        await transaction.CommitAsync(ct);
        return ToDto(work, shipment);
    }

    public async Task<ShippingDraftDto> ReconcileHistory(Guid id, ReconcileShipmentRequestDto request, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (work, shipment) = await Load(id, ct);
        CheckVersion(work, request.ExpectedVersion);
        if (shipment.SourceKind != SourceKind.HistoricalImport || shipment.ShipmentStatus != ShipmentStatus.Unknown)
            throw new InvalidOperationException("仅历史状态未知记录可通过历史核对处理。");
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("历史核对必须填写依据或原因。");
        if (request.Status == ShipmentStatus.Unknown)
            throw new ArgumentException("请选择已确认的物流状态。");

        ValidateHistoricalDates(request.Status, request.ShippedDate, request.ShippedDateKnowledge,
            request.DeliveredDate, request.DeliveredDateKnowledge);
        ValidateHistoricalTracking(request.Status, request.TrackingNumber, request.NoTrackingNumber);
        var before = Snapshot(shipment);
        shipment.ShipmentStatus = request.Status;
        shipment.Carrier = Clean(request.Carrier);
        shipment.TrackingNumber = Clean(request.TrackingNumber);
        shipment.NoTrackingNumber = request.NoTrackingNumber;
        shipment.FulfillmentPaused = false;
        shipment.ShippedDate = request.ShippedDate;
        shipment.ShippedDateKnowledge = request.ShippedDateKnowledge;
        shipment.DeliveredDate = request.DeliveredDate;
        shipment.DeliveredDateKnowledge = request.DeliveredDateKnowledge;
        await Save(work, shipment, "ShipmentHistoryReconciled", "已核对历史物流：" + request.Reason.Trim(), before, ct);
        await transaction.CommitAsync(ct);
        return ToDto(work, shipment);
    }

    public async Task<ShippingDraftDto> BackfillDates(Guid id, BackfillShipmentDatesRequestDto request, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (work, shipment) = await Load(id, ct);
        CheckVersion(work, request.ExpectedVersion);
        if (shipment.ShipmentStatus is not (ShipmentStatus.Shipped or ShipmentStatus.Delivered))
            throw new InvalidOperationException("仅已寄出或已签收记录可补日期。");
        var shippedDate = request.ShippedDate ?? shipment.ShippedDate;
        var deliveredDate = request.DeliveredDate ?? shipment.DeliveredDate;
        if (shippedDate.HasValue) ValidateDate(shippedDate.Value, "寄出日期");
        if (deliveredDate.HasValue) ValidateDate(deliveredDate.Value, "签收日期");
        if (shippedDate.HasValue && deliveredDate.HasValue && deliveredDate < shippedDate)
            throw new ArgumentException("签收日期不能早于寄出日期。");

        var before = Snapshot(shipment);
        if (request.ShippedDate.HasValue)
        {
            shipment.ShippedDate = request.ShippedDate;
            shipment.ShippedDateKnowledge = DateKnowledge.Known;
        }
        if (request.DeliveredDate.HasValue)
        {
            if (shipment.ShipmentStatus != ShipmentStatus.Delivered)
                throw new InvalidOperationException("只有已签收状态可补签收日期。");
            shipment.DeliveredDate = request.DeliveredDate;
            shipment.DeliveredDateKnowledge = DateKnowledge.Known;
        }
        await Save(work, shipment, "ShipmentDatesBackfilled", "已补录物流日期", before, ct);
        await transaction.CommitAsync(ct);
        return ToDto(work, shipment);
    }

    private static bool MatchFilter(Shipment? shipment, WorkItem work, ShippingQueueFilter filter) => filter switch
    {
        ShippingQueueFilter.ToShip => shipment is not null && Eligible(shipment, work),
        ShippingQueueFilter.NeedsConfirmation => work.ShippingRequirement == ShippingRequirement.Pending,
        ShippingQueueFilter.History => shipment?.ShipmentStatus == ShipmentStatus.Unknown
            || (shipment is null && work.SourceKind == SourceKind.HistoricalImport && work.ShippingRequirement == ShippingRequirement.Required),
        _ => true
    };

    private static bool Eligible(Shipment shipment, WorkItem work) =>
        work.ShippingRequirement == ShippingRequirement.Required
        && shipment.ShipmentStatus == ShipmentStatus.NotShipped
        && !shipment.FulfillmentPaused
        && (work.WorkStatus == WorkStatus.Completed || work.WorkStatus == WorkStatus.Cancelled);

    private string Urgency(Shipment? shipment, WorkItem work)
    {
        if (work.ShippingRequirement == ShippingRequirement.Pending) return "需求待确认";
        if (shipment is null)
            return work.SourceKind == SourceKind.HistoricalImport ? "历史物流待核对" : "物流记录缺失";
        if (shipment.ShipmentStatus == ShipmentStatus.Unknown) return "历史物流待核对";
        if (shipment.ShipmentStatus == ShipmentStatus.Delivered)
        {
            if (shipment.ShippedDateKnowledge == DateKnowledge.Unknown)
                return shipment.DeliveredDateKnowledge == DateKnowledge.Unknown ? "已签收（寄出和签收日期未知）" : "已签收（寄出日期未知）";
            return shipment.DeliveredDateKnowledge == DateKnowledge.Unknown ? "已签收（签收日期未知）" : "已签收";
        }
        if (shipment.ShipmentStatus == ShipmentStatus.Shipped) return "已寄出";
        if (!Eligible(shipment, work)) return shipment.FulfillmentPaused ? "已暂停" : "未到发货阶段";
        if (!shipment.LatestShipDate.HasValue) return "待寄出";
        if (shipment.LatestShipDate < clock.Today) return "寄出已逾期";
        return shipment.LatestShipDate == clock.Today ? "今日应寄出" : "待寄出";
    }

    private static int UrgencyRank(string urgency) => urgency switch
    {
        "寄出已逾期" => 0,
        "今日应寄出" => 1,
        "历史物流待核对" => 2,
        "需求待确认" => 3,
        _ => 4
    };

    private async Task<(WorkItem Work, Shipment Shipment)> Load(Guid id, CancellationToken ct)
    {
        var work = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException("找不到工作项。");
        var shipment = await db.Shipments.SingleOrDefaultAsync(x => x.WorkItemId == id, ct)
            ?? throw new InvalidOperationException("该工作项没有物流记录，请从历史物流核对入口准备记录。");
        return (work, shipment);
    }

    private static void CheckVersion(WorkItem work, long version)
    {
        if (work.Version != version)
            throw new DbUpdateConcurrencyException("工作项已被其他页面修改，请刷新后重试。");
    }

    private void ValidateDate(DateOnly date, string label)
    {
        if (date > clock.Today) throw new ArgumentException($"{label}不能晚于业务今天。");
    }

    private static void ValidateTracking(string? number, bool? noTrackingNumber, bool shipped)
    {
        number = Clean(number);
        if (shipped)
        {
            if (noTrackingNumber == true && number is not null)
                throw new ArgumentException("无单号与单号不能同时填写。");
            if (noTrackingNumber != true && number is null)
                throw new ArgumentException("请填写单号或明确勾选无单号。");
        }
        else if (noTrackingNumber == true)
            throw new ArgumentException("待寄出草稿尚不能记录无单号事实。");
        else if (noTrackingNumber == false && number is null)
            throw new ArgumentException("有单号时必须填写单号。");
    }

    private static void ValidateHistoricalTracking(ShipmentStatus status, string? number, bool? noTrackingNumber)
    {
        number = Clean(number);
        if (status == ShipmentStatus.NotShipped && noTrackingNumber == true)
            throw new ArgumentException("尚未寄出时不能记录无单号事实。");
        if (noTrackingNumber == true && number is not null)
            throw new ArgumentException("无单号事实不能同时填写单号。");
        if (noTrackingNumber == false && number is null)
            throw new ArgumentException("已确认有单号时必须填写单号。");
        if (number is not null && noTrackingNumber != false)
            throw new ArgumentException("历史记录有单号时必须标记单号状态为有单号。");
    }

    private void ValidateHistoricalDates(ShipmentStatus status, DateOnly? shippedDate, DateKnowledge shippedKnowledge,
        DateOnly? deliveredDate, DateKnowledge deliveredKnowledge)
    {
        if (status == ShipmentStatus.NotShipped)
        {
            if (shippedDate is not null || shippedKnowledge != DateKnowledge.NotApplicable
                || deliveredDate is not null || deliveredKnowledge != DateKnowledge.NotApplicable)
                throw new ArgumentException("未寄出状态不能有寄出/签收日期。");
            return;
        }
        if (shippedKnowledge == DateKnowledge.Known)
        {
            if (!shippedDate.HasValue) throw new ArgumentException("已知寄出日期必须填写。");
            ValidateDate(shippedDate.Value, "寄出日期");
        }
        else if (shippedKnowledge != DateKnowledge.Unknown || shippedDate.HasValue)
            throw new ArgumentException("寄出日期知识状态不一致。");
        if (status == ShipmentStatus.Shipped)
        {
            if (deliveredDate is not null || deliveredKnowledge != DateKnowledge.NotApplicable)
                throw new ArgumentException("仅签收状态可有签收日期。");
            return;
        }
        if (status != ShipmentStatus.Delivered) throw new ArgumentException("物流状态无效。");
        if (deliveredKnowledge == DateKnowledge.Known)
        {
            if (!deliveredDate.HasValue) throw new ArgumentException("已知签收日期必须填写。");
            ValidateDate(deliveredDate.Value, "签收日期");
        }
        else if (deliveredKnowledge != DateKnowledge.Unknown || deliveredDate.HasValue)
            throw new ArgumentException("签收日期知识状态不一致。");
        if (shippedDate.HasValue && deliveredDate.HasValue && deliveredDate < shippedDate)
            throw new ArgumentException("签收日期不能早于寄出日期。");
    }

    private async Task Save(WorkItem work, Shipment shipment, string action, string summary, object before, CancellationToken ct)
    {
        shipment.UpdatedAt = clock.Now;
        work.Version++;
        work.UpdatedAt = clock.Now;
        AddActivity(work, shipment, action, summary, before, Snapshot(shipment));
        await db.SaveChangesAsync(ct);
    }

    private void AddActivity(WorkItem work, Shipment shipment, string action, string summary, object? before, object after) =>
        db.ActivityLogs.Add(new ActivityLog
        {
            EntityType = "Shipment", EntityId = shipment.Id, WorkItemId = work.Id, ActionType = action,
            RecordedAt = clock.Now, Summary = summary,
            BeforeData = before is null ? null : JsonSerializer.Serialize(before, JsonOptions),
            AfterData = JsonSerializer.Serialize(after, JsonOptions)
        });

    private static object Snapshot(Shipment shipment) => new
    {
        shipment.ShipmentStatus, shipment.Carrier, shipment.TrackingNumber, shipment.NoTrackingNumber,
        shipment.LatestShipDate, shipment.FulfillmentPaused, shipment.ShippedDate,
        shipment.ShippedDateKnowledge, shipment.DeliveredDate, shipment.DeliveredDateKnowledge
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ShippingDraftDto ToDto(WorkItem work, Shipment shipment) => new(
        work.Id, shipment.ShipmentStatus, shipment.Carrier, shipment.TrackingNumber, shipment.NoTrackingNumber,
        shipment.LatestShipDate, shipment.FulfillmentPaused, shipment.ShippedDate, shipment.ShippedDateKnowledge,
        shipment.DeliveredDate, shipment.DeliveredDateKnowledge, work.Version, work.IsArchived,
        work.ShippingRequirement, work.SourceKind, work.WorkStatus);
}


