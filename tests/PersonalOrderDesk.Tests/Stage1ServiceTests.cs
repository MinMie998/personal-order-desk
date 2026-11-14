using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;

public sealed class Stage1ServiceTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private DeskDbContext db = null!;
    private TestBusinessClock clock = null!;
    private WorkbenchService service = null!;
    private readonly List<string> tempDirectories = [];

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        clock = new TestBusinessClock(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(8)));
        service = new WorkbenchService(db, clock, new WorkItemService(db), NullLogger<WorkbenchService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
        foreach (var path in tempDirectories)
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    [Fact]
    public async Task Default_backup_directory_follows_the_actual_database_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pod-backup-location-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        tempDirectories.Add(directory);
        await using var isolated = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "app.db")};Pooling=False").Options);
        await isolated.Database.MigrateAsync();
        var local = new WorkbenchService(isolated, clock, new WorkItemService(isolated), NullLogger<WorkbenchService>.Instance);
        Assert.Equal(Path.Combine(directory, "Backups"), (await local.GetSettings()).BackupDirectory);
    }

    [Fact]
    public async Task Settings_duration_recalculates_only_auto_rows_and_keeps_history_unknown()
    {
        var automatic = await service.Create(Request("auto", "自动日期", ProjectType.Simulation, new DateOnly(2026, 11, 13)));
        var manual = await service.Create(Request("manual", "手动日期", ProjectType.Simulation, new DateOnly(2026, 11, 13), DueDateMode.Manual, new DateOnly(2026, 11, 20)));
        var batch = new ImportBatch { SourceFileName = "history.xlsx", FileFingerprint = Guid.NewGuid().ToString("N") };
        db.ImportBatches.Add(batch);
        var historical = new WorkItem
        {
            ProjectName = "历史未知", ProjectType = ProjectType.Simulation, ScheduleCategory = ScheduleCategory.Digital,
            ScheduleCategorySource = ValueSource.Imported, ShippingRequirement = ShippingRequirement.None,
            ShippingRequirementSource = ValueSource.Imported, SourceKind = SourceKind.HistoricalImport, ImportBatchId = batch.Id,
            ReceivedDate = new DateOnly(2026, 11, 10), DueDateMode = DueDateMode.HistoricalUnknown,
            WorkStatus = WorkStatus.NotStarted, CompletedDateKnowledge = CompletionDateKnowledge.NotApplicable,
            CostConfirmationStatus = CostConfirmationStatus.Pending, Version = 1
        };
        db.WorkItems.Add(historical);
        await db.SaveChangesAsync();

        var folder = TempDirectory();
        var saved = await service.SaveSettings(Settings(folder, duration: 3), expectedVersion: 0);

        Assert.Equal(new DateOnly(2026, 11, 16), (await service.GetDetail(automatic.WorkItem.Id!.Value))!.WorkItem.EffectiveDueDate);
        Assert.Equal(new DateOnly(2026, 11, 20), manual.WorkItem.EffectiveDueDate);
        Assert.Null((await db.WorkItems.SingleAsync(x => x.Id == historical.Id)).EffectiveDueDate);
        Assert.Equal(1, saved.AffectedAutomaticDueDateCount);
        Assert.Equal(2, (await db.WorkItems.SingleAsync(x => x.Id == automatic.WorkItem.Id!.Value)).Version);
        Assert.Equal(1, (await db.WorkItems.SingleAsync(x => x.Id == manual.WorkItem.Id!.Value)).Version);
    }

    [Fact]
    public async Task Reminder_windows_use_business_date_and_exact_boundaries()
    {
        var today = clock.Today;
        var withinPhysical = await service.Create(Request("p7", "Physical 7", ProjectType.PhysicalProduction, today, DueDateMode.Manual, today.AddDays(7)));
        var outsidePhysical = await service.Create(Request("p8", "Physical 8", ProjectType.PhysicalProduction, today, DueDateMode.Manual, today.AddDays(8)));
        var withinDigital = await service.Create(Request("d1", "Digital 1", ProjectType.Simulation, today, DueDateMode.Manual, today.AddDays(1)));
        var outsideDigital = await service.Create(Request("d2", "Digital 2", ProjectType.Simulation, today, DueDateMode.Manual, today.AddDays(2)));
        var dashboard = await service.GetDashboard();
        var upcoming = dashboard.Sections.Single(x => x.Kind == WorkSectionKind.Upcoming);
        Assert.Contains(withinPhysical.WorkItem.Id!.Value, upcoming.Items.Select(x => x.Id));
        Assert.Contains(withinDigital.WorkItem.Id!.Value, upcoming.Items.Select(x => x.Id));
        Assert.DoesNotContain(outsidePhysical.WorkItem.Id!.Value, upcoming.Items.Select(x => x.Id));
        Assert.DoesNotContain(outsideDigital.WorkItem.Id!.Value, upcoming.Items.Select(x => x.Id));

        clock.AdvanceBusinessDays(1);
        dashboard = await service.GetDashboard();
        upcoming = dashboard.Sections.Single(x => x.Kind == WorkSectionKind.Upcoming);
        Assert.Equal(new DateOnly(2026, 11, 14), dashboard.BusinessDate);
        Assert.Contains(outsidePhysical.WorkItem.Id!.Value, upcoming.Items.Select(x => x.Id));
        Assert.Contains(outsideDigital.WorkItem.Id!.Value, upcoming.Items.Select(x => x.Id));
        Assert.Contains(withinDigital.WorkItem.Id!.Value, dashboard.Sections.Single(x => x.Kind == WorkSectionKind.DueToday).Items.Select(x => x.Id));
    }

    [Fact]
    public async Task Configured_zero_physical_and_three_day_digital_windows_keep_due_today_and_overdue_active()
    {
        var today = clock.Today;
        var physicalFuture = await service.Create(Request("p-future", "实物明天到期", ProjectType.PhysicalProduction, today, DueDateMode.Manual, today.AddDays(1)));
        var physicalToday = await service.Create(Request("p-today", "实物今天到期", ProjectType.PhysicalProduction, today, DueDateMode.Manual, today));
        var physicalPast = await service.Create(Request("p-past", "实物已逾期", ProjectType.PhysicalProduction, today, DueDateMode.Manual, today.AddDays(-1)));
        var digital2 = await service.Create(Request("d-2", "仿真2天", ProjectType.Simulation, today, DueDateMode.Manual, today.AddDays(2)));
        var digital3 = await service.Create(Request("d-3", "仿真3天", ProjectType.Simulation, today, DueDateMode.Manual, today.AddDays(3)));
        var digital4 = await service.Create(Request("d-4", "仿真4天", ProjectType.Simulation, today, DueDateMode.Manual, today.AddDays(4)));
        var settings = (await service.GetSettings()) with
            { PhysicalReminderLeadDays = 0, DigitalReminderLeadDays = 3, BackupDirectory = TempDirectory() };
        await service.SaveSettings(settings, settings.Version);
        var dashboard = await service.GetDashboard();
        var upcoming = dashboard.Sections.Single(section => section.Kind == WorkSectionKind.Upcoming).Items;
        Assert.Contains(upcoming, row => row.Id == digital2.WorkItem.Id);
        Assert.Contains(upcoming, row => row.Id == digital3.WorkItem.Id);
        Assert.DoesNotContain(upcoming, row => row.Id == digital4.WorkItem.Id || row.Id == physicalFuture.WorkItem.Id);
        Assert.Contains(dashboard.Sections.Single(section => section.Kind == WorkSectionKind.DueToday).Items, row => row.Id == physicalToday.WorkItem.Id);
        Assert.Contains(dashboard.Sections.Single(section => section.Kind == WorkSectionKind.Overdue).Items, row => row.Id == physicalPast.WorkItem.Id);
    }

    [Fact]
    public async Task Update_requires_amount_reason_is_atomic_and_rejects_stale_version()
    {
        var created = await service.Create(Request("amt", "原名称", ProjectType.Simulation, clock.Today, amount: 10000));
        var invalid = Request("amt", "不应保存", ProjectType.Simulation, clock.Today, amount: 20000) with { ExpectedVersion = created.WorkItem.Version };
        await Assert.ThrowsAsync<ArgumentException>(() => service.Update(created.WorkItem.Id!.Value, invalid));
        var unchanged = await service.GetDetail(created.WorkItem.Id!.Value);
        Assert.Equal("原名称", unchanged!.WorkItem.ProjectName);
        Assert.Equal(10000, unchanged.WorkItem.OrderAmountCents);
        Assert.DoesNotContain(unchanged.Activities, x => x.ActionType == "OrderAmountChanged");

        var valid = invalid with { OrderAmountChangeReason = "追加工作范围" };
        var updated = await service.Update(created.WorkItem.Id!.Value, valid);
        Assert.Equal("不应保存", updated.WorkItem.ProjectName);
        Assert.Equal(20000, updated.WorkItem.OrderAmountCents);
        var amountLog = Assert.Single(updated.Activities, x => x.ActionType == "OrderAmountChanged");
        Assert.Contains("追加工作范围", amountLog.Summary);
        Assert.Contains("10000", amountLog.BeforeData);
        Assert.Contains("20000", amountLog.AfterData);

        var stale = valid with { ExpectedVersion = created.WorkItem.Version, ProjectName = "旧标签覆盖" };
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.Update(created.WorkItem.Id!.Value, stale));
        Assert.Equal("不应保存", (await service.GetDetail(created.WorkItem.Id!.Value))!.WorkItem.ProjectName);
    }

    [Fact]
    public async Task Completed_archived_item_remains_in_payment_todo_with_nonnegative_balance()
    {
        var created = await service.Create(Request("owed", "已完成欠款", ProjectType.Simulation, clock.Today, amount: 12500));
        var completed = await service.ChangeStatus(created.WorkItem.Id!.Value,
            new WorkStatusChangeRequestDto(WorkStatus.Completed, created.WorkItem.Version));
        await service.Archive(created.WorkItem.Id!.Value, new ArchiveRequestDto(true, completed.WorkItem.Version));
        var dashboard = await service.GetDashboard();
        var awaiting = Assert.Single(dashboard.Sections.Single(x => x.Kind == WorkSectionKind.AwaitingPayment).Items);
        Assert.Equal(created.WorkItem.Id!.Value, awaiting.Id);
        Assert.Equal(12500, awaiting.OutstandingCents);
        Assert.True(awaiting.IsArchived);
        Assert.Equal(WorkStatus.Completed, awaiting.Status);
    }

    [Fact]
    public async Task Online_backup_is_readable_and_contains_database_schema_and_work()
    {
        var created = await service.Create(Request("backup", "备份样本", ProjectType.Simulation, clock.Today));
        Assert.Equal(1, await db.WorkItems.CountAsync());
        var folder = TempDirectory();
        var destination = Path.Combine(folder, "snapshot.db");
        var result = await service.CreateBackup(destination);
        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(destination));
        await using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, ForeignKeys = true, Pooling = false }.ToString());
        await copy.OpenAsync();
        await using var check = copy.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", (string?)await check.ExecuteScalarAsync());
        await using var count = copy.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM WorkItems";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        await copy.CloseAsync();
        await copy.DisposeAsync();
    }

    [Fact]
    public async Task Additional_work_is_a_separate_financial_and_work_item_unit()
    {
        var primary = await service.Create(Request("repeat", "主工作", ProjectType.Simulation, clock.Today, amount: 5000));
        var additionRequest = Request("repeat", "收费追加工作", ProjectType.CodeReport, clock.Today, amount: 2000);
        var addition = await service.CreateAdditionalWork(primary.WorkItem.Id!.Value, additionRequest);
        var primaryDetail = await service.GetDetail(primary.WorkItem.Id!.Value);
        Assert.NotEqual(primary.WorkItem.Id!.Value, addition.WorkItem.Id!.Value);
        Assert.Equal(5000, primaryDetail!.WorkItem.OrderAmountCents);
        Assert.Equal(2000, addition.WorkItem.OrderAmountCents);
        Assert.Contains(primaryDetail.Relations, x => x.RelatedWorkItemId == addition.WorkItem.Id!.Value && x.RelationType == nameof(RelationType.AdditionalWork));
    }

    [Fact]
    public async Task Confirming_shipping_requirement_for_history_does_not_invent_an_unsent_parcel()
    {
        var batch = new ImportBatch { SourceFileName = "shipping-history.xlsx", FileFingerprint = Guid.NewGuid().ToString("N") };
        var item = new WorkItem
        {
            ProjectName = "历史实物", BusinessOrderNo = "HISTORY-SHIPPING", SourceKind = SourceKind.HistoricalImport,
            ImportBatchId = batch.Id, ProjectType = ProjectType.PhysicalProduction, ScheduleCategory = ScheduleCategory.Physical,
            ScheduleCategorySource = ValueSource.Imported, ShippingRequirement = ShippingRequirement.Pending,
            ShippingRequirementSource = ValueSource.Imported, DueDateMode = DueDateMode.HistoricalUnknown,
            WorkStatus = WorkStatus.Completed, CompletedDateKnowledge = CompletionDateKnowledge.Unknown,
            CostConfirmationStatus = CostConfirmationStatus.Pending, OrderAmountCents = 10000
        };
        db.ImportBatches.Add(batch); db.WorkItems.Add(item); await db.SaveChangesAsync();
        var result = await service.Update(item.Id, new(1, item.BusinessOrderNo, item.ProjectName, item.ProjectType,
            null, ShippingRequirement.Required, null, DueDateMode.HistoricalUnknown, null, null, null, 10000, null, null));
        var shipment = await db.Shipments.SingleAsync();
        Assert.Equal(ShipmentStatus.Unknown, shipment.ShipmentStatus);
        Assert.Equal(SourceKind.HistoricalImport, shipment.SourceKind);
        Assert.Equal(batch.Id, shipment.ImportBatchId);
        Assert.Null(shipment.NoTrackingNumber);
        Assert.Null(shipment.ShippedDate);
        Assert.Equal("历史物流待核对", result.FulfillmentState);
    }

    [Fact]
    public async Task Backfilling_unknown_completion_preserves_history_precision_and_financial_facts()
    {
        var batch = new ImportBatch { SourceFileName = "history.xlsx", FileFingerprint = Guid.NewGuid().ToString("N") };
        var item = new WorkItem
        {
            ProjectName = "历史补日期样本", SourceKind = SourceKind.HistoricalImport, ImportBatchId = batch.Id,
            ProjectType = ProjectType.Simulation, ScheduleCategory = ScheduleCategory.Digital,
            ScheduleCategorySource = ValueSource.Imported, ShippingRequirement = ShippingRequirement.None,
            ShippingRequirementSource = ValueSource.Imported, DueDateMode = DueDateMode.HistoricalUnknown,
            WorkStatus = WorkStatus.Completed, CompletedDateKnowledge = CompletionDateKnowledge.Unknown,
            CostConfirmationStatus = CostConfirmationStatus.Pending, OrderAmountCents = 10000
        };
        db.ImportBatches.Add(batch); db.WorkItems.Add(item);
        db.WorkStatusHistories.Add(new WorkStatusHistory
        {
            WorkItemId = item.Id, OldStatus = WorkStatus.Completed, NewStatus = WorkStatus.Completed,
            EventType = HistoryEventType.Completed, SourceKind = SourceKind.HistoricalImport, ImportBatchId = batch.Id,
            IsBackfilled = true, Reason = "来源仅明确已完成，日期未知"
        });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.BackfillCompletionDate(item.Id, clock.Today.AddDays(1), 1));
        var date = clock.Today.AddDays(-2);
        var detail = await service.BackfillCompletionDate(item.Id, date, 1);
        Assert.Equal(WorkStatus.Completed, detail.WorkItem.Status);
        Assert.Equal(date, detail.WorkItem.CompletedDate);
        Assert.Null(detail.WorkItem.CompletedAt);
        Assert.Equal(10000, detail.WorkItem.OrderAmountCents);
        Assert.Equal(2, detail.History.Count);
        Assert.Contains(detail.History, entry => entry.OccurredDate is null && entry.IsBackfilled);
        Assert.Contains(detail.History, entry => entry.OccurredDate == date && entry.OccurredAt is null && entry.RecordedAt == clock.Now);
        Assert.Empty(await db.PaymentTransactions.ToListAsync());
        Assert.Empty(await db.Expenses.ToListAsync());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.BackfillCompletionDate(item.Id, date, 1));
    }

    [Fact]
    public async Task Existing_imported_items_can_be_linked_as_additional_work_without_creating_or_merging_orders()
    {
        var parent = await service.Create(Request("same-number", "原单", ProjectType.Simulation, clock.Today, amount: 5000));
        var child = await service.Create(Request("same-number", "追加单", ProjectType.CodeReport, clock.Today, amount: 2000));
        var parentId = parent.WorkItem.Id!.Value;
        var childId = child.WorkItem.Id!.Value;
        await service.LinkExistingAdditionalWork(parentId, childId, "用户核对历史追加关系");
        await service.LinkExistingAdditionalWork(parentId, childId, "重复确认同一关联");
        Assert.Equal(2, await db.WorkItems.CountAsync());
        Assert.Single(await db.WorkItemRelations.ToListAsync());
        Assert.Equal(5000, (await service.GetDetail(parentId))!.WorkItem.OrderAmountCents);
        Assert.Equal(2000, (await service.GetDetail(childId))!.WorkItem.OrderAmountCents);
        Assert.Contains((await service.GetDetail(parentId))!.Relations, relation => relation.RelatedWorkItemId == childId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.LinkExistingAdditionalWork(parentId, parentId, "不允许自关联"));
    }

    [Fact]
    public async Task Status_history_keeps_rework_and_cancelled_completion_snapshots()
    {
        var created = await service.Create(Request("state", "状态样本", ProjectType.Simulation, clock.Today));
        var completed = await service.ChangeStatus(created.WorkItem.Id!.Value,
            new WorkStatusChangeRequestDto(WorkStatus.Completed, created.WorkItem.Version));
        var completedDate = completed.WorkItem.CompletedDate;
        var rework = await service.ChangeStatus(created.WorkItem.Id!.Value,
            new WorkStatusChangeRequestDto(WorkStatus.NeedsRework, completed.WorkItem.Version, "客户要求调整"));
        Assert.Null(rework.WorkItem.CompletedDate);
        var reworkEvent = Assert.Single(rework.History, x => x.NewStatus == WorkStatus.NeedsRework);
        Assert.Equal(completedDate, reworkEvent.PreviousCompletedDate);
        var completedAgain = await service.ChangeStatus(created.WorkItem.Id!.Value,
            new WorkStatusChangeRequestDto(WorkStatus.Completed, rework.WorkItem.Version));
        var cancelled = await service.ChangeStatus(created.WorkItem.Id!.Value,
            new WorkStatusChangeRequestDto(WorkStatus.Cancelled, completedAgain.WorkItem.Version, "取消"));
        Assert.Null(cancelled.WorkItem.CompletedDate);
        var restored = await service.ChangeStatus(created.WorkItem.Id!.Value,
            new WorkStatusChangeRequestDto(WorkStatus.Completed, cancelled.WorkItem.Version));
        Assert.Equal(completedAgain.WorkItem.CompletedDate, restored.WorkItem.CompletedDate);
        Assert.Equal(CompletionDateKnowledge.Known, restored.WorkItem.CompletedDateKnowledge);
    }

    private WorkItemSaveRequestDto Request(string orderNo, string name, ProjectType type, DateOnly received,
        DueDateMode dueMode = DueDateMode.Auto, DateOnly? specifiedDue = null, long? amount = null) =>
        new(null, orderNo, name, type, null, null, received, dueMode, specifiedDue,
            null, null, amount, null, null);

    private WorkbenchSettingsDto Settings(string backupDirectory, int duration) => new(0, duration, 7, 1, backupDirectory, 30);

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stage1-tests-{Guid.NewGuid():N}");
        tempDirectories.Add(path);
        return path;
    }

    private sealed class TestBusinessClock(DateTimeOffset now) : IBusinessClock
    {
        public DateTimeOffset Now { get; private set; } = now;
        public DateOnly Today => DateOnly.FromDateTime(Now.ToOffset(TimeSpan.FromHours(8)).DateTime);
        public void AdvanceBusinessDays(int days) => Now = Now.AddDays(days);
    }
}

