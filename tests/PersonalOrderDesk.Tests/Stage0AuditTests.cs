using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;

/// <summary>Targeted SQLite regression cases not already exercised by BaselineTests.</summary>
public sealed class Stage0AuditTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<DeskDbContext> _options = null!;
    private DeskDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options;
        _db = new DeskDbContext(_options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static WorkItem NewWork(string name, string orderNo, DueDateMode mode = DueDateMode.Auto) => new()
    {
        ProjectName = name, BusinessOrderNo = orderNo, ProjectType = ProjectType.Simulation,
        ScheduleCategory = ScheduleCategory.Digital, ScheduleCategorySource = ValueSource.Default,
        ShippingRequirement = ShippingRequirement.None, ShippingRequirementSource = ValueSource.Default,
        SourceKind = SourceKind.Manual, ReceivedDate = new DateOnly(2026, 11, 12),
        DueDateMode = mode, SpecifiedDueDate = mode == DueDateMode.Manual ? new DateOnly(2026, 11, 30) : null,
        EffectiveDueDate = mode == DueDateMode.Manual ? new DateOnly(2026, 11, 30) : null,
        WorkStatus = WorkStatus.NotStarted, CompletedDateKnowledge = CompletionDateKnowledge.NotApplicable
    };

    [Fact]
    public async Task Received_date_change_recalculates_auto_and_leaves_manual_deadline_untouched()
    {
        var auto = NewWork("自动期限", "R-101");
        var manual = NewWork("手工期限", "R-102", DueDateMode.Manual);
        _db.WorkItems.AddRange(auto, manual);
        await _db.SaveChangesAsync();
        var service = new WorkItemService(_db);

        await service.ChangeReceivedDateAsync(auto.Id, new DateOnly(2026, 11, 30), 1, auto.Version);
        await service.ChangeReceivedDateAsync(manual.Id, new DateOnly(2026, 12, 1), 1, manual.Version);

        Assert.Equal(new DateOnly(2026, 12, 1), auto.EffectiveDueDate);
        Assert.Equal(new DateOnly(2026, 11, 30), manual.EffectiveDueDate);
    }

    [Fact]
    public async Task Default_change_touches_only_auto_rows_and_type_defaults_respect_imported_values()
    {
        var automatic = NewWork("Auto", "R-201");
        var fixedDate = NewWork("Manual", "R-202", DueDateMode.Manual);
        var imported = NewWork("Imported defaults", "R-203");
        imported.ScheduleCategory = ScheduleCategory.Digital;
        imported.ScheduleCategorySource = ValueSource.Imported;
        imported.ShippingRequirement = ShippingRequirement.None;
        imported.ShippingRequirementSource = ValueSource.Imported;
        _db.WorkItems.AddRange(automatic, fixedDate, imported);
        await _db.SaveChangesAsync();
        var service = new WorkItemService(_db);

        Assert.Equal(2, await service.RecalculateAutomaticDueDatesAsync(3));
        Assert.Equal(new DateOnly(2026, 11, 15), automatic.EffectiveDueDate);
        Assert.Equal(new DateOnly(2026, 11, 30), fixedDate.EffectiveDueDate);
        await service.ChangeProjectTypeAsync(imported.Id, ProjectType.PhysicalProduction, true, 3, imported.Version);
        Assert.Equal(ScheduleCategory.Physical, imported.ScheduleCategory);
        Assert.Equal(ShippingRequirement.Required, imported.ShippingRequirement);
        Assert.Equal(ValueSource.Default, imported.ScheduleCategorySource);
        Assert.Equal(ValueSource.Default, imported.ShippingRequirementSource);
    }

    [Fact]
    public async Task Same_business_order_number_keeps_work_items_independent()
    {
        var first = NewWork("订单甲", "SAME");
        var second = NewWork("订单乙", "SAME");
        var service = new WorkItemService(_db);
        await service.CreateAsync(first, 1);
        await service.CreateAsync(second, 1);
        var secondReceivedDate = second.ReceivedDate;
        var secondDueDate = second.EffectiveDueDate;
        await service.ChangeReceivedDateAsync(first.Id, new DateOnly(2026, 11, 20), 2, first.Version);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("订单乙", (await _db.WorkItems.SingleAsync(x => x.Id == second.Id)).ProjectName);
        Assert.Equal(secondReceivedDate, second.ReceivedDate);
        Assert.Equal(secondDueDate, second.EffectiveDueDate);
        Assert.Equal(new DateOnly(2026, 11, 13), second.EffectiveDueDate);
    }

    [Fact]
    public async Task Concurrent_context_update_rejects_stale_version_without_overwriting_first_writer()
    {
        var work = NewWork("并发订单", "C-301");
        _db.WorkItems.Add(work);
        await _db.SaveChangesAsync();
        await using var secondContext = new DeskDbContext(_options);
        var stale = await secondContext.WorkItems.SingleAsync(x => x.Id == work.Id);
        var firstService = new WorkItemService(_db);
        var secondService = new WorkItemService(secondContext);

        await firstService.ChangeReceivedDateAsync(work.Id, new DateOnly(2026, 11, 20), 1, work.Version);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            secondService.ChangeReceivedDateAsync(stale.Id, new DateOnly(2026, 11, 21), 1, stale.Version));

        _db.ChangeTracker.Clear();
        var persisted = await _db.WorkItems.SingleAsync(x => x.Id == work.Id);
        Assert.Equal(new DateOnly(2026, 11, 20), persisted.ReceivedDate);
    }

    [Fact]
    public async Task Negative_order_amount_is_rejected_by_sqlite_constraint()
    {
        var work = NewWork("负金额", "N-401");
        work.OrderAmountCents = -1;
        _db.WorkItems.Add(work);

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(ShipmentStatus.Delivered, null, null, DateKnowledge.NotApplicable, "DeliveredDate needs a ship date")]
    [InlineData(ShipmentStatus.Shipped, "2026-11-12", "2026-11-13", DateKnowledge.Known, "Shipped cannot carry delivered date")]
    public async Task Manual_shipment_rejects_inconsistent_milestone_dates(ShipmentStatus state, string? shipDate, string? deliveredDate, DateKnowledge deliveredKnowledge, string _)
    {
        var work = NewWork("物流日期", Guid.NewGuid().ToString());
        _db.WorkItems.Add(work);
        await _db.SaveChangesAsync();
        _db.Shipments.Add(new Shipment
        {
            WorkItemId = work.Id, ShipmentStatus = state, SourceKind = SourceKind.Manual,
            NoTrackingNumber = true,
            ShippedDate = shipDate is null ? null : DateOnly.Parse(shipDate),
            ShippedDateKnowledge = shipDate is null ? DateKnowledge.NotApplicable : DateKnowledge.Known,
            DeliveredDate = deliveredDate is null ? null : DateOnly.Parse(deliveredDate),
            DeliveredDateKnowledge = deliveredKnowledge
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Historical_delivered_shipment_can_preserve_known_status_with_unknown_ship_date()
    {
        var batch = new ImportBatch { SourceFileName = "history.xlsx", FileFingerprint = "delivered-unknown-ship" };
        _db.ImportBatches.Add(batch);
        await _db.SaveChangesAsync();
        var work = NewWork("历史签收", "H-501");
        work.SourceKind = SourceKind.HistoricalImport;
        work.ImportBatchId = batch.Id;
        _db.WorkItems.Add(work);
        await _db.SaveChangesAsync();
        _db.Shipments.Add(new Shipment
        {
            WorkItemId = work.Id, ShipmentStatus = ShipmentStatus.Delivered, SourceKind = SourceKind.HistoricalImport,
            ImportBatchId = batch.Id, ShippedDateKnowledge = DateKnowledge.Unknown,
            DeliveredDate = new DateOnly(2026, 11, 10), DeliveredDateKnowledge = DateKnowledge.Known
        });

        await _db.SaveChangesAsync();

        var saved = await _db.Shipments.SingleAsync(x => x.WorkItemId == work.Id);
        Assert.Equal(ShipmentStatus.Delivered, saved.ShipmentStatus);
        Assert.Null(saved.ShippedDate);
        Assert.Equal(DateKnowledge.Unknown, saved.ShippedDateKnowledge);
    }
}
