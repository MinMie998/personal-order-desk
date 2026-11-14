using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;

public sealed class Stage2ShippingTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private DeskDbContext db = null!;
    private IShippingService service = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        service = new ShippingService(db, new TestClock());
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Incomplete_work_is_not_overdue_and_sending_clears_reminder()
    {
        var (work, _) = await Add(WorkStatus.InProgress, ShippingRequirement.Required,
            ShipmentStatus.NotShipped, new DateOnly(2026, 11, 12));
        var row = Assert.Single(await service.GetQueue(ShippingQueueFilter.All));
        Assert.Equal("未到发货阶段", row.ShippingUrgency);

        await new WorkItemService(db).SetStatusAsync(work.Id, WorkStatus.Completed,
            new DateOnly(2026, 11, 13), work.Version);
        row = Assert.Single(await service.GetQueue(ShippingQueueFilter.ToShip));
        Assert.Equal("寄出已逾期", row.ShippingUrgency);
        var sent = await service.MarkShipped(work.Id,
            new(work.Version, new DateOnly(2026, 11, 13), null, "ABC", false));
        Assert.Equal(ShipmentStatus.Shipped, sent.ShipmentStatus);
        Assert.Empty(await service.GetQueue(ShippingQueueFilter.ToShip));
    }

    [Fact]
    public async Task Missing_tracking_number_requires_explicit_no_tracking_confirmation()
    {
        var (work, _) = await Add(WorkStatus.Completed, ShippingRequirement.Required,
            ShipmentStatus.NotShipped, null);
        await Assert.ThrowsAsync<ArgumentException>(() => service.MarkShipped(work.Id,
            new(work.Version, new DateOnly(2026, 11, 13), null, null, null)));
        var result = await service.MarkShipped(work.Id,
            new(work.Version, new DateOnly(2026, 11, 13), null, null, true));
        Assert.True(result.NoTrackingNumber);
    }

    [Fact]
    public async Task Manual_dates_cannot_be_future_and_delivery_cannot_precede_shipment()
    {
        var (work, _) = await Add(WorkStatus.Completed, ShippingRequirement.Required,
            ShipmentStatus.NotShipped, null);
        await Assert.ThrowsAsync<ArgumentException>(() => service.MarkShipped(work.Id,
            new(work.Version, new DateOnly(2026, 11, 14), null, "ABC", false)));
        var sent = await service.MarkShipped(work.Id,
            new(work.Version, new DateOnly(2026, 11, 13), null, "ABC", false));
        await Assert.ThrowsAsync<ArgumentException>(() => service.MarkDelivered(work.Id,
            new(sent.Version, new DateOnly(2026, 11, 12))));
    }

    [Fact]
    public async Task Historical_delivery_can_have_unknown_ship_date_and_unknown_tracking_number()
    {
        var (work, _) = await AddHistoricalShipment();
        var result = await service.ReconcileHistory(work.Id, new(work.Version, ShipmentStatus.Delivered,
            null, DateKnowledge.Unknown, new DateOnly(2026, 11, 10), DateKnowledge.Known,
            null, null, null, "核对平台记录"));
        Assert.Equal(ShipmentStatus.Delivered, result.ShipmentStatus);
        Assert.Equal(DateKnowledge.Unknown, result.ShippedDateKnowledge);
        Assert.Equal(DateKnowledge.Known, result.DeliveredDateKnowledge);
        Assert.Null(result.NoTrackingNumber);
        Assert.Contains(await service.GetQueue(), row => row.WorkItemId == work.Id
            && row.ShippingUrgency == "已签收（寄出日期未知）");
    }

    [Fact]
    public async Task Missing_historical_shipment_is_listed_and_explicitly_prepared_for_review()
    {
        var work = await AddHistoricalWorkWithoutShipment();
        var row = Assert.Single(await service.GetQueue(ShippingQueueFilter.History));
        Assert.Null(row.ShipmentStatus);
        Assert.Equal("历史物流待核对", row.ShippingUrgency);

        var oldVersion = work.Version;
        var prepared = await service.PrepareHistoryReview(work.Id, oldVersion);
        Assert.Equal(ShipmentStatus.Unknown, prepared.ShipmentStatus);
        Assert.Null(prepared.NoTrackingNumber);
        Assert.Equal(DateKnowledge.Unknown, prepared.ShippedDateKnowledge);
        Assert.Equal(oldVersion + 1, prepared.Version);
        Assert.Single(await service.GetQueue(ShippingQueueFilter.History));
    }

    [Fact]
    public async Task Missing_pending_shipment_is_returned_as_confirmation_not_unsent()
    {
        await Add(WorkStatus.NotStarted, ShippingRequirement.Pending, null, null);
        var item = Assert.Single(await service.GetQueue(ShippingQueueFilter.NeedsConfirmation));
        Assert.Null(item.ShipmentStatus);
        Assert.Equal("需求待确认", item.ShippingUrgency);
        Assert.Empty(await service.GetQueue(ShippingQueueFilter.ToShip));
    }

    [Fact]
    public async Task Cancelled_work_continues_when_unpaused_and_pause_edit_is_explicit_and_audited()
    {
        var (work, shipment) = await Add(WorkStatus.Cancelled, ShippingRequirement.Required,
            ShipmentStatus.NotShipped, new DateOnly(2026, 11, 13));
        work.CancelledFromStatus = CancelledStatus.InProgress;
        Assert.NotNull(shipment);
        shipment.FulfillmentPaused = true;
        await db.SaveChangesAsync();
        Assert.Empty(await service.GetQueue(ShippingQueueFilter.ToShip));

        var pausedVersion = work.Version;
        var saved = await service.SaveDraft(work.Id, new(work.Version, null, "ABC", false,
            shipment.LatestShipDate, true));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SaveDraft(work.Id,
            new(pausedVersion, null, "ABC", false, shipment.LatestShipDate, true)));
        Assert.Empty(await service.GetQueue(ShippingQueueFilter.ToShip));

        await service.SaveDraft(work.Id, new(saved.Version, null, "ABC", false,
            shipment.LatestShipDate, false));
        Assert.Single(await service.GetQueue(ShippingQueueFilter.ToShip));
        Assert.Contains(await db.ActivityLogs.Where(x => x.WorkItemId == work.Id).Select(x => x.ActionType).ToListAsync(),
            action => action == "ShipmentFulfillmentPauseChanged");
    }

    private async Task<(WorkItem Work, Shipment? Shipment)> Add(
        WorkStatus status, ShippingRequirement requirement, ShipmentStatus? shipmentStatus, DateOnly? latest)
    {
        var work = new WorkItem
        {
            ProjectName = "测试项目",
            ProjectType = ProjectType.PhysicalProduction,
            ScheduleCategory = ScheduleCategory.Physical,
            ScheduleCategorySource = ValueSource.Default,
            ShippingRequirement = requirement,
            ShippingRequirementSource = ValueSource.Manual,
            SourceKind = SourceKind.Manual,
            ReceivedDate = new DateOnly(2026, 11, 1),
            DueDateMode = DueDateMode.Auto,
            WorkStatus = status,
            CancelledFromStatus = status == WorkStatus.Cancelled ? CancelledStatus.Completed : null,
            CompletedDate = status == WorkStatus.Completed ? new DateOnly(2026, 11, 12) : null,
            CompletedDateKnowledge = status == WorkStatus.Completed
                ? CompletionDateKnowledge.Known : CompletionDateKnowledge.NotApplicable,
            CostConfirmationStatus = CostConfirmationStatus.Pending
        };
        db.WorkItems.Add(work);
        Shipment? shipment = null;
        if (shipmentStatus.HasValue)
        {
            shipment = new Shipment
            {
                WorkItemId = work.Id,
                ShipmentStatus = shipmentStatus.Value,
                LatestShipDate = latest,
                ShippedDateKnowledge = DateKnowledge.NotApplicable,
                DeliveredDateKnowledge = DateKnowledge.NotApplicable,
                SourceKind = SourceKind.Manual
            };
            db.Shipments.Add(shipment);
        }
        await db.SaveChangesAsync();
        return (work, shipment);
    }

    private async Task<WorkItem> AddHistoricalWorkWithoutShipment()
    {
        var batch = new ImportBatch
        {
            SourceFileName = "legacy.xlsx",
            FileFingerprint = Guid.NewGuid().ToString("N")
        };
        db.ImportBatches.Add(batch);
        var work = new WorkItem
        {
            ProjectName = "历史无物流事实",
            ProjectType = ProjectType.Other,
            ScheduleCategory = ScheduleCategory.Pending,
            ScheduleCategorySource = ValueSource.Imported,
            ShippingRequirement = ShippingRequirement.Required,
            ShippingRequirementSource = ValueSource.Imported,
            SourceKind = SourceKind.HistoricalImport,
            ImportBatchId = batch.Id,
            DueDateMode = DueDateMode.HistoricalUnknown,
            WorkStatus = WorkStatus.Completed,
            CompletedDateKnowledge = CompletionDateKnowledge.Unknown,
            CostConfirmationStatus = CostConfirmationStatus.Pending
        };
        db.WorkItems.Add(work);
        await db.SaveChangesAsync();
        return work;
    }

    private async Task<(WorkItem Work, Shipment Shipment)> AddHistoricalShipment()
    {
        var work = await AddHistoricalWorkWithoutShipment();
        var shipment = new Shipment
        {
            WorkItemId = work.Id,
            ShipmentStatus = ShipmentStatus.Unknown,
            ShippedDateKnowledge = DateKnowledge.Unknown,
            DeliveredDateKnowledge = DateKnowledge.Unknown,
            SourceKind = SourceKind.HistoricalImport,
            ImportBatchId = work.ImportBatchId
        };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        return (work, shipment);
    }

    private sealed class TestClock : IBusinessClock
    {
        public DateTimeOffset Now => new(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(8));
        public DateOnly Today => new(2026, 11, 13);
    }
}
