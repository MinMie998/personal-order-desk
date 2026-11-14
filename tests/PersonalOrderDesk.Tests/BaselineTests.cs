using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;

public sealed class BaselineTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DeskDbContext _db = null!;
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await _connection.OpenAsync();
        _db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(_connection).Options);
        await _db.Database.MigrateAsync();
    }
    public async Task DisposeAsync() { await _db.DisposeAsync(); await _connection.DisposeAsync(); }

    static WorkItem NewWork(WorkStatus status = WorkStatus.NotStarted) => new() { ProjectName = "测试项目", ProjectType = ProjectType.Simulation, ScheduleCategory = ScheduleCategory.Digital, ScheduleCategorySource = ValueSource.Default, ShippingRequirement = ShippingRequirement.None, ShippingRequirementSource = ValueSource.Default, SourceKind = SourceKind.Manual, ReceivedDate = new DateOnly(2026, 11, 13), DueDateMode = DueDateMode.Auto, WorkStatus = status, CompletedDateKnowledge = status == WorkStatus.Completed ? CompletionDateKnowledge.Known : CompletionDateKnowledge.NotApplicable, CompletedDate = status == WorkStatus.Completed ? new DateOnly(2026, 11, 10) : null };

    [Fact]
    public async Task Migration_and_sqlite_constraints_work()
    {
        await using (var cmd = _connection.CreateCommand()) { cmd.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", (string?)await cmd.ExecuteScalarAsync()); }
        var wi = NewWork(); wi.BusinessOrderNo = "重复编号"; _db.WorkItems.Add(wi); await _db.SaveChangesAsync();
        var second = NewWork(); second.BusinessOrderNo = "重复编号"; _db.WorkItems.Add(second); await _db.SaveChangesAsync();
        _db.PaymentTransactions.Add(new PaymentTransaction { Type = TransactionType.Receipt, AmountCents = 0, IdempotencyKey = "bad", Status = RecordStatus.Active, SourceKind = SourceKind.Manual, DateKnowledge = DateKnowledge.Known, TransactionDate = new DateOnly(2026, 11, 13) });
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        await using var fkCmd = _connection.CreateCommand(); fkCmd.CommandText = "PRAGMA foreign_keys"; Assert.Equal(1L, (long)(await fkCmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Foreign_key_and_shipment_date_constraints_reject_invalid_rows()
    {
        _db.PaymentAllocations.Add(new PaymentAllocation { PaymentTransactionId = Guid.NewGuid(), WorkItemId = Guid.NewGuid(), AmountCents = 100 });
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        _db.ChangeTracker.Clear();
        var wi = NewWork(); _db.WorkItems.Add(wi); await _db.SaveChangesAsync();
        _db.Shipments.Add(new Shipment { WorkItemId = wi.Id, ShipmentStatus = ShipmentStatus.Delivered, SourceKind = SourceKind.Manual, NoTrackingNumber = true, ShippedDate = new DateOnly(2026, 11, 12), ShippedDateKnowledge = DateKnowledge.Known, DeliveredDate = null, DeliveredDateKnowledge = DateKnowledge.NotApplicable });
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Due_modes_and_cancel_restore_keep_original_completion_date()
    {
        var service = new WorkItemService(_db);
        var wi = NewWork(); await service.CreateAsync(wi, 1);
        Assert.Equal(new DateOnly(2026, 11, 14), wi.EffectiveDueDate);
        var completed = NewWork(WorkStatus.Completed); completed.Id = Guid.NewGuid(); completed.DueDateMode = DueDateMode.Manual; completed.SpecifiedDueDate = new DateOnly(2026, 11, 20); completed.EffectiveDueDate = completed.SpecifiedDueDate;
        _db.WorkItems.Add(completed); await _db.SaveChangesAsync();
        await service.SetStatusAsync(completed.Id, WorkStatus.Cancelled, new DateOnly(2026, 11, 13), completed.Version);
        var cancelled = await _db.WorkItems.SingleAsync(x => x.Id == completed.Id);
        await service.SetStatusAsync(completed.Id, WorkStatus.Completed, new DateOnly(2026, 11, 14), cancelled.Version);
        var restored = await _db.WorkItems.SingleAsync(x => x.Id == completed.Id);
        Assert.Equal(new DateOnly(2026, 11, 10), restored.CompletedDate);
        Assert.Equal(2, await _db.WorkStatusHistories.CountAsync(x => x.WorkItemId == completed.Id));
    }

    [Fact]
    public async Task Due_date_mode_changes_recalculate_only_explicitly_and_preserve_unknown_history()
    {
        var service = new WorkItemService(_db);
        var wi = NewWork(); await service.CreateAsync(wi, 1);
        Assert.Equal(new DateOnly(2026, 11, 14), wi.EffectiveDueDate);
        await service.ChangeDueDateAsync(wi.Id, DueDateMode.Manual, new DateOnly(2026, 12, 1), 1, wi.Version);
        Assert.Equal(new DateOnly(2026, 12, 1), wi.EffectiveDueDate);
        await service.ChangeDueDateAsync(wi.Id, DueDateMode.Auto, null, 3, wi.Version);
        Assert.Equal(new DateOnly(2026, 11, 16), wi.EffectiveDueDate);
        Assert.Equal(1, await service.RecalculateAutomaticDueDatesAsync(4));
        Assert.Equal(new DateOnly(2026, 11, 17), wi.EffectiveDueDate);
        Assert.Equal(new DateOnly(2027, 3, 1), WorkItemService.CalculateDue(new WorkItem { ProjectName = "跨月", ProjectType = ProjectType.Simulation, ScheduleCategory = ScheduleCategory.Digital, SourceKind = SourceKind.Manual, ReceivedDate = new DateOnly(2027, 2, 28), DueDateMode = DueDateMode.Auto }, 1));
        var batch = new ImportBatch { SourceFileName = "history.xlsx", FileFingerprint = "unknown-due" }; _db.ImportBatches.Add(batch); await _db.SaveChangesAsync();
        var historic = NewWork(); historic.Id = Guid.NewGuid(); historic.SourceKind = SourceKind.HistoricalImport; historic.ImportBatchId = batch.Id; historic.DueDateMode = DueDateMode.HistoricalUnknown; historic.ReceivedDate = null; historic.EffectiveDueDate = null;
        _db.WorkItems.Add(historic); await _db.SaveChangesAsync();
        Assert.Equal(1, await service.RecalculateAutomaticDueDatesAsync(5));
        Assert.Null(historic.EffectiveDueDate);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ChangeDueDateAsync(wi.Id, DueDateMode.Auto, null, 1, expectedVersion: 1));
    }

    [Fact]
    public async Task Rework_preserves_completion_snapshot_and_version_is_checked()
    {
        var wi = NewWork(WorkStatus.Completed); wi.Version = 8; _db.WorkItems.Add(wi); await _db.SaveChangesAsync();
        var service = new WorkItemService(_db);
        await service.SetStatusAsync(wi.Id, WorkStatus.NeedsRework, new DateOnly(2026, 11, 13), expectedVersion: 8, reason: "返工");
        Assert.Null(wi.CompletedDate);
        var history = await _db.WorkStatusHistories.SingleAsync(x => x.WorkItemId == wi.Id);
        Assert.Equal(new DateOnly(2026, 11, 10), history.PreviousCompletedDate);
        Assert.Equal(CompletionDateKnowledge.Known, history.PreviousCompletedDateKnowledge);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SetStatusAsync(wi.Id, WorkStatus.InProgress, new DateOnly(2026, 11, 14), expectedVersion: 8));
        Assert.Equal(WorkStatus.NeedsRework, wi.WorkStatus);
    }

    [Fact]
    public async Task Unknown_cancelled_state_requires_explicit_noncompleted_restore()
    {
        var batch = new ImportBatch { SourceFileName = "cancel.xlsx", FileFingerprint = "unknown-cancel" }; _db.ImportBatches.Add(batch); await _db.SaveChangesAsync();
        var wi = NewWork(WorkStatus.Cancelled); wi.Id = Guid.NewGuid(); wi.SourceKind = SourceKind.HistoricalImport; wi.ImportBatchId = batch.Id; wi.ReceivedDate = null; wi.CancelledFromStatus = CancelledStatus.Unknown; wi.DueDateMode = DueDateMode.HistoricalUnknown;
        _db.WorkItems.Add(wi); await _db.SaveChangesAsync();
        var service = new WorkItemService(_db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetStatusAsync(wi.Id, WorkStatus.InProgress, new DateOnly(2026, 11, 13), expectedVersion: wi.Version));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetStatusAsync(wi.Id, WorkStatus.Completed, new DateOnly(2026, 11, 13), expectedVersion: wi.Version, restoreChoice: WorkStatus.Completed));
        await service.SetStatusAsync(wi.Id, WorkStatus.InProgress, new DateOnly(2026, 11, 13), expectedVersion: wi.Version, restoreChoice: WorkStatus.InProgress);
        Assert.Equal(WorkStatus.InProgress, wi.WorkStatus);
    }

    [Fact]
    public async Task Type_defaults_and_override_sources_follow_the_frozen_mapping()
    {
        Assert.Equal((ScheduleCategory.Physical, ShippingRequirement.Required), WorkItemService.MapDefaults(ProjectType.PhysicalProduction));
        Assert.Equal((ScheduleCategory.Pending, ShippingRequirement.Pending), WorkItemService.MapDefaults(ProjectType.MixedOrUnknown));
        var wi = NewWork(); wi.ScheduleCategorySource = ValueSource.Manual; wi.ShippingRequirementSource = ValueSource.Imported;
        _db.WorkItems.Add(wi); await _db.SaveChangesAsync();
        var service = new WorkItemService(_db);
        await service.ChangeProjectTypeAsync(wi.Id, ProjectType.PhysicalProduction, applyNewDefaults: false, nonPhysicalDefaultDays: 2, expectedVersion: wi.Version);
        Assert.Equal(ScheduleCategory.Digital, wi.ScheduleCategory);
        Assert.Equal(ValueSource.Manual, wi.ScheduleCategorySource);
        Assert.Equal(ValueSource.Manual, wi.ShippingRequirementSource);
        await service.ChangeProjectTypeAsync(wi.Id, ProjectType.PhysicalProduction, applyNewDefaults: true, nonPhysicalDefaultDays: 2, expectedVersion: wi.Version);
        Assert.Equal(ScheduleCategory.Physical, wi.ScheduleCategory);
        Assert.Equal(ValueSource.Default, wi.ScheduleCategorySource);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArchivedAsync(wi.Id, true, wi.Version));
        await service.SetStatusAsync(wi.Id, WorkStatus.Completed, new DateOnly(2026, 11, 13), wi.Version);
        await service.SetArchivedAsync(wi.Id, true, wi.Version);
        Assert.True(wi.IsArchived);
    }
}


