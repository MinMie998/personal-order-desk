using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;

public sealed class Stage2FinanceTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private DeskDbContext db = null!;
    private FinanceService finance = null!;
    private static readonly DateOnly Today = new(2026, 11, 13);

    [Fact]
    public async Task Personal_bookkeeping_accepts_blank_dates_and_exposes_700_minus_350_without_cost_confirmation()
    {
        var work = await AddWork(70_000, Today);
        var receipt = await finance.CreateTransaction(new(TransactionType.Receipt, 70_000, null,
            DateKnowledge.Unknown, "微信", null, null, "personal-700", [new(work.Id, 70_000)]));
        await finance.SaveExpenseGroup(new(work.Id, null, work.Version, "项目支出", "材料", null,
            [new(null, 35_000, null, DateKnowledge.Unknown, ExpensePaymentStatus.Paid, null, DateKnowledge.Unknown, null)], false));
        var result = (await finance.GetWorkItemFinance(work.Id))!;
        Assert.Equal(35_000, result.NetReceivedCents - result.TotalCostCents);
        Assert.Equal(CostConfirmationStatus.Pending, result.CostConfirmationStatus);
        Assert.Null(receipt.TransactionDate);
        Assert.Equal(DateKnowledge.Unknown, receipt.DateKnowledge);
        Assert.Equal("微信", receipt.PaymentMethod);
        Assert.Null(result.ExpenseGroups.Single().Expenses.Single().PaidDate);
        Assert.Equal(35_000, result.PaidCostCents);
    }

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        db = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        finance = new FinanceService(db, new FixedClock());
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Receipt_is_idempotent_and_allocation_must_conserve_total()
    {
        var work = await AddWork(10_000);
        var request = Receipt("pay-1", 2_000, (work.Id, 1_000), (work.Id, 1_000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => finance.CreateTransaction(request));
        var valid = Receipt("pay-1", 2_000, (work.Id, 2_000));
        var first = await finance.CreateTransaction(valid);
        var retry = await finance.CreateTransaction(valid);
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(1, await db.PaymentTransactions.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => finance.CreateTransaction(Receipt("pay-1", 1_000, (work.Id, 1_000))));
    }

    [Fact]
    public async Task Combined_receipt_allocates_once_and_invalid_cross_item_refund_rolls_back_everything()
    {
        var first = await AddWork(10_000);
        var second = await AddWork(10_000);
        await finance.CreateTransaction(Receipt("combined", 10_000, (first.Id, 4_000), (second.Id, 6_000)));
        Assert.Equal(4_000, (await finance.GetWorkItemFinance(first.Id))!.NetReceivedCents);
        Assert.Equal(6_000, (await finance.GetWorkItemFinance(second.Id))!.NetReceivedCents);
        var paymentCount = await db.PaymentTransactions.CountAsync();
        var allocationCount = await db.PaymentAllocations.CountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => finance.CreateTransaction(Refund("bad-combined", 8_000,
            (first.Id, 1_000), (second.Id, 7_000))));
        Assert.Equal(paymentCount, await db.PaymentTransactions.CountAsync());
        Assert.Equal(allocationCount, await db.PaymentAllocations.CountAsync());
    }

    [Fact]
    public async Task Refund_balance_is_checked_inside_serialized_write_and_reversal_protects_dependency()
    {
        var work = await AddWork(9_000);
        var receipt = await finance.CreateTransaction(Receipt("pay-2", 10_000, (work.Id, 10_000)));
        await finance.CreateTransaction(Refund("refund-1", 6_000, (work.Id, 6_000)));
        await finance.CreateTransaction(Refund("refund-2", 4_000, (work.Id, 4_000)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => finance.CreateTransaction(Refund("refund-3", 1, (work.Id, 1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => finance.ReverseTransaction(receipt.Id, new ReverseTransactionRequestDto("误录")));
        var report = await finance.GetWorkItemFinance(work.Id);
        Assert.Equal(0, report!.NetReceivedCents);
        Assert.Equal(9_000, report.OutstandingCents);
    }

    [Fact]
    public async Task Expense_groups_separate_confirmed_cost_from_cash_and_preserve_void_history()
    {
        var work = await AddWork(50_000);
        var group = await finance.SaveExpenseGroup(new SaveExpenseGroupRequestDto(work.Id, null, work.Version,
            "PCB采购", "材料", null,
            [new(null, 4_000, Today, DateKnowledge.Known, ExpensePaymentStatus.Paid, Today, DateKnowledge.Known, null),
             new(null, 6_000, Today, DateKnowledge.Known, ExpensePaymentStatus.Unpaid, null, DateKnowledge.NotApplicable, null)],
            ConfirmComplete: true));
        var state = await finance.GetWorkItemFinance(work.Id);
        Assert.Equal(10_000, state!.TotalCostCents);
        Assert.Equal(4_000, state.PaidCostCents);
        Assert.Equal(40_000, state.ExpectedGrossProfitCents);
        Assert.Equal(-4_000, state.ActualCashNetCents);
        Assert.Equal(CostConfirmationStatus.ConfirmedComplete, state.CostConfirmationStatus);

        var refreshed = await db.WorkItems.SingleAsync(x => x.Id == work.Id);
        var paidExpense = Assert.Single(group.Expenses, x => x.AmountCents == 4_000);
        await finance.ReverseExpense(paidExpense.Id, new ReverseExpenseRequestDto("金额录错", refreshed.Version));
        Assert.Equal(CostConfirmationStatus.Pending, (await db.WorkItems.SingleAsync(x => x.Id == work.Id)).CostConfirmationStatus);
        Assert.Single(await db.Expenses.Where(x => x.Status == ExpenseStatus.Voided).ToListAsync());
        Assert.Equal(6_000, (await finance.GetWorkItemFinance(work.Id))!.TotalCostCents);
    }

    [Fact]
    public async Task Confirmed_no_cost_requires_no_active_items_and_unknown_amount_does_not_become_zero_profit()
    {
        var work = await AddWork(null);
        await finance.CreateTransaction(Receipt("unknown-order-receipt", 500, (work.Id, 500)));
        await finance.SetCostConfirmation(work.Id, new SetCostConfirmationRequestDto(CostConfirmationStatus.ConfirmedNoCost, work.Version));
        var state = await finance.GetWorkItemFinance(work.Id);
        Assert.Equal(500, state!.NetReceivedCents);
        Assert.Null(state.OutstandingCents);
        Assert.Null(state.ExpectedGrossProfitCents);
        Assert.Equal(CostConfirmationStatus.ConfirmedNoCost, state.CostConfirmationStatus);
    }

    [Fact]
    public async Task Supplementing_cash_date_changes_period_without_creating_transaction()
    {
        var work = await AddWork(2_000);
        var transaction = await finance.CreateTransaction(Receipt("pay-date", 1_000, (work.Id, 1_000)));
        await finance.SupplementTransaction(transaction.Id, new SupplementTransactionRequestDto(new DateOnly(2026, 10, 31), DateKnowledge.Known, "转账", null, "", transaction.Version));
        var after = await finance.GetOverview(new FinanceQueryDto(CashYear: 2026, CashMonth: 10));
        Assert.Equal(1_000, after.TotalNetReceivedCents);
        Assert.Equal(1, await db.PaymentTransactions.CountAsync());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => finance.SupplementTransaction(transaction.Id,
            new SupplementTransactionRequestDto(new DateOnly(2026, 10, 30), DateKnowledge.Known, null, null, null, transaction.Version)));
    }

    [Fact]
    public async Task Historical_unknown_cash_date_is_kept_out_of_months_but_in_cumulative_net()
    {
        var work = await AddWork(null);
        var batchId = await AddImportBatch();
        var payment = new PaymentTransaction
        {
            Type = TransactionType.Receipt,
            AmountCents = 700,
            DateKnowledge = DateKnowledge.Unknown,
            TransactionDate = null,
            SourceKind = SourceKind.HistoricalImport,
            ImportBatchId = batchId,
            IdempotencyKey = "legacy-unknown-date",
            Status = RecordStatus.Active
        };
        db.PaymentTransactions.Add(payment);
        db.PaymentAllocations.Add(new PaymentAllocation { PaymentTransactionId = payment.Id, WorkItemId = work.Id, AmountCents = 700 });
        var group = new ExpenseGroup { WorkItemId = work.Id, Title = "历史材料", Category = "材料" };
        db.ExpenseGroups.Add(group);
        db.Expenses.Add(new Expense
        {
            ExpenseGroupId = group.Id,
            AmountCents = 300,
            ExpenseDate = Today,
            ExpenseDateKnowledge = DateKnowledge.Known,
            PaymentStatus = ExpensePaymentStatus.Paid,
            PaidDate = null,
            PaidDateKnowledge = DateKnowledge.Unknown,
            SourceKind = SourceKind.HistoricalImport,
            ImportBatchId = batchId,
            Status = ExpenseStatus.Active
        });
        await db.SaveChangesAsync();
        var month = await finance.GetOverview(new FinanceQueryDto(CashYear: 2026, CashMonth: 11));
        Assert.Equal(0, month.TotalNetReceivedCents);
        Assert.Equal(700, month.UnknownDateNetReceivedCents);
        Assert.Equal(300, month.UnknownDatePaidCostCents);
        var cumulative = await finance.GetOverview(new FinanceQueryDto());
        Assert.Equal(700, cumulative.TotalNetReceivedCents);
    }

    [Fact]
    public async Task New_migration_preserves_stage1_transactions_and_allocations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"desk-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<DeskDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False")
                .Options;
            await using var upgrade = new DeskDbContext(options);
            var migrator = upgrade.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261113043344_ConstraintAndReferenceCorrections");
            await upgrade.Database.OpenConnectionAsync();
            var work = new WorkItem
            {
                ProjectName = "升级保留项目",
                ProjectType = ProjectType.Simulation,
                ScheduleCategory = ScheduleCategory.Digital,
                ScheduleCategorySource = ValueSource.Default,
                ShippingRequirement = ShippingRequirement.None,
                ShippingRequirementSource = ValueSource.Default,
                SourceKind = SourceKind.Manual,
                ReceivedDate = Today,
                DueDateMode = DueDateMode.Auto,
                WorkStatus = WorkStatus.NotStarted,
                CompletedDateKnowledge = CompletionDateKnowledge.NotApplicable,
                OrderAmountCents = 3_000,
                CostConfirmationStatus = CostConfirmationStatus.Pending
            };
            // Populate the old schema using only columns that existed in that release.
            await upgrade.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WorkItems (Id,ProjectName,ProjectType,ScheduleCategory,ScheduleCategorySource,ShippingRequirement,ShippingRequirementSource,SourceKind,ReceivedDate,DueDateMode,WorkStatus,CompletedDateKnowledge,OrderAmountCents,CostConfirmationStatus,IsArchived,Version,CreatedAt,UpdatedAt) VALUES ({work.Id},{work.ProjectName},'Simulation','Digital','Default','None','Default','Manual',{Today},'Auto','NotStarted','NotApplicable',3000,'Pending',0,1,{work.CreatedAt},{work.UpdatedAt})");
            var paymentId = Guid.NewGuid();
            await using (var command = upgrade.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "INSERT INTO PaymentTransactions (Id, Type, AmountCents, TransactionDate, PaymentMethod, ExternalReference, Notes, IdempotencyKey, Status, ReversalReason, CreatedAt, UpdatedAt, SourceKind, ImportBatchId, DateKnowledge) VALUES ($id,'Receipt',1250,'2026-11-13',NULL,NULL,'旧收款','legacy-upgrade-key','Active',NULL,'2026-11-13T12:00:00+08:00','2026-11-13T12:00:00+08:00','Manual',NULL,'Known');";
                command.Parameters.Add(new SqliteParameter("$id", paymentId.ToString()));
                await command.ExecuteNonQueryAsync();
            }
            async Task<string> StoredId(string sql)
            {
                await using var command = upgrade.Database.GetDbConnection().CreateCommand();
                command.CommandText = sql;
                var value = await command.ExecuteScalarAsync();
                if (value is null || value == DBNull.Value) throw new InvalidOperationException("旧阶段记录未写入数据库。");
                return Convert.ToString(value)!;
            }
            var storedPaymentId = await StoredId("SELECT Id FROM PaymentTransactions WHERE IdempotencyKey='legacy-upgrade-key'");
            var storedWorkId = await StoredId("SELECT Id FROM WorkItems WHERE ProjectName='升级保留项目'");
            await using (var command = upgrade.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM PaymentTransactions p CROSS JOIN WorkItems w WHERE p.Id=$payment AND w.Id=$work;";
                command.Parameters.Add(new SqliteParameter("$payment", storedPaymentId));
                command.Parameters.Add(new SqliteParameter("$work", storedWorkId));
                Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }
            await using (var command = upgrade.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "INSERT INTO PaymentAllocations (Id, PaymentTransactionId, WorkItemId, AmountCents) SELECT $id, p.Id, w.Id, 1250 FROM PaymentTransactions p CROSS JOIN WorkItems w WHERE p.Id=$payment AND w.Id=$work;";
                command.Parameters.Add(new SqliteParameter("$id", Guid.NewGuid().ToString()));
                command.Parameters.Add(new SqliteParameter("$payment", storedPaymentId));
                command.Parameters.Add(new SqliteParameter("$work", storedWorkId));
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await upgrade.Database.MigrateAsync();
            var migratedWork = await upgrade.WorkItems.SingleAsync(x => x.ProjectName == "升级保留项目");
            Assert.False(migratedWork.IsDeleted);
            Assert.Null(migratedWork.DeletedAt);
            var preserved = await upgrade.PaymentTransactions.SingleAsync(x => x.IdempotencyKey == "legacy-upgrade-key");
            Assert.Equal(1, preserved.Version);
            Assert.Null(preserved.RequestPayloadHash);
            Assert.Equal("旧收款", preserved.Notes);
            Assert.Equal(1_250, await (from allocation in upgrade.PaymentAllocations
                                      join transaction in upgrade.PaymentTransactions on allocation.PaymentTransactionId equals transaction.Id
                                      where transaction.IdempotencyKey == "legacy-upgrade-key"
                                      select allocation.AmountCents).SumAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Mixed_collection_month_and_order_month_use_independent_dates_and_unknown_date_is_separate()
    {
        var oldOrder = await AddWork(20_000, new DateOnly(2026, 10, 1));
        var noDate = await AddWork(5_000, null);
        await finance.CreateTransaction(Receipt("nov-cash", 3_000, (oldOrder.Id, 3_000)));
        var overview = await finance.GetOverview(new FinanceQueryDto(ReceivedYear: 2026, ReceivedMonth: 10, CashYear: 2026, CashMonth: 11));
        Assert.Contains(overview.WorkItems, x => x.WorkItemId == oldOrder.Id);
        Assert.DoesNotContain(overview.WorkItems, x => x.WorkItemId == noDate.Id);
        Assert.Equal(3_000, overview.TotalNetReceivedCents);
        Assert.Equal(1, overview.UnknownReceivedDateWorkItemCount);
    }

    [Fact]
    public async Task Type_report_counts_mixed_allocations_once_and_uses_independent_order_year_filters()
    {
        var simulation = await AddWork(10_000, new DateOnly(2026, 10, 5));
        var noCost = await AddWork(7_000, new DateOnly(2025, 9, 3));
        noCost.ProjectType = ProjectType.Other;
        await db.SaveChangesAsync();
        var oldOrderWithCurrentCash = await AddWork(8_000, new DateOnly(2025, 4, 8));
        oldOrderWithCurrentCash.ProjectType = ProjectType.CodeReport;
        await db.SaveChangesAsync();

        await finance.SaveExpenseGroup(new SaveExpenseGroupRequestDto(simulation.Id, null, simulation.Version,
            "元件采购", "元件", null,
            [new(null, 2_000, Today, DateKnowledge.Known, ExpensePaymentStatus.Paid, Today, DateKnowledge.Known, null)], true));
        var simulationVersion = await db.WorkItems.Where(x => x.Id == simulation.Id).Select(x => x.Version).SingleAsync();
        await finance.SaveExpenseGroup(new SaveExpenseGroupRequestDto(simulation.Id, null, simulationVersion,
            "加工服务", "加工", null,
            [new(null, 3_000, Today, DateKnowledge.Known, ExpensePaymentStatus.Unpaid, null, DateKnowledge.NotApplicable, null)]));
        simulationVersion = await db.WorkItems.Where(x => x.Id == simulation.Id).Select(x => x.Version).SingleAsync();
        await finance.SetCostConfirmation(simulation.Id, new SetCostConfirmationRequestDto(CostConfirmationStatus.ConfirmedComplete, simulationVersion));

        var oldCostVersion = oldOrderWithCurrentCash.Version;
        await finance.SaveExpenseGroup(new SaveExpenseGroupRequestDto(oldOrderWithCurrentCash.Id, null, oldCostVersion,
            "历史制作成本", "加工", null,
            [new(null, 1_000, new DateOnly(2025, 4, 9), DateKnowledge.Known, ExpensePaymentStatus.Paid,
                new DateOnly(2025, 4, 9), DateKnowledge.Known, null)], true));
        var noCostVersion = noCost.Version;
        await finance.SetCostConfirmation(noCost.Id, new SetCostConfirmationRequestDto(CostConfirmationStatus.ConfirmedNoCost, noCostVersion));

        await finance.CreateTransaction(Receipt("mixed-type-alloc", 10_000, (simulation.Id, 4_000), (noCost.Id, 6_000)));
        await finance.CreateTransaction(Receipt("old-order-current-cash", 2_000, (oldOrderWithCurrentCash.Id, 2_000)));

        var allTypes = await finance.GetOverview(new FinanceQueryDto(CashYear: 2026, CashMonth: 11));
        var simulationType = Assert.Single(allTypes.Categories, x => x.Category == "仿真");
        Assert.Equal(4_000, simulationType.NetReceivedCents);
        Assert.Equal(2_000, simulationType.PaidCostCents);
        Assert.Equal(5_000, simulationType.ExpectedGrossProfitCents);
        var noCostType = Assert.Single(allTypes.Categories, x => x.Category == "其他");
        Assert.Equal(6_000, noCostType.NetReceivedCents);
        Assert.Equal(7_000, noCostType.ExpectedGrossProfitCents);
        var codeType = Assert.Single(allTypes.Categories, x => x.Category == "代码与报告");
        Assert.Equal(2_000, codeType.NetReceivedCents);
        Assert.Equal(7_000, codeType.ExpectedGrossProfitCents);

        var selectedYear = await finance.GetOverview(new FinanceQueryDto(ReceivedYear: 2026, CashYear: 2026, CashMonth: 11));
        Assert.Single(selectedYear.WorkItems, x => x.WorkItemId == simulation.Id);
        Assert.DoesNotContain(selectedYear.WorkItems, x => x.WorkItemId == noCost.Id || x.WorkItemId == oldOrderWithCurrentCash.Id);
        Assert.Equal(5_000, selectedYear.ExpectedGrossProfitCents);
        Assert.Equal(12_000, selectedYear.TotalNetReceivedCents);
        Assert.Null(Assert.Single(selectedYear.Categories, x => x.Category == "代码与报告").ExpectedGrossProfitCents);

        var selectedType = await finance.GetOverview(new FinanceQueryDto(CashYear: 2026, CashMonth: 11, ProjectType: ProjectType.Simulation));
        Assert.Equal(4_000, selectedType.TotalNetReceivedCents);
        Assert.Equal(5_000, selectedType.ExpectedGrossProfitCents);
        Assert.Single(selectedType.Categories, x => x.Category == "仿真");
        Assert.DoesNotContain(selectedType.Categories, x => x.Category == "其他" || x.Category == "代码与报告");
    }

    [Fact]
    public async Task Concurrent_refunds_cannot_both_spend_the_same_balance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"desk-finance-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<DeskDbContext>()
                .UseSqlite($"Data Source={path};Foreign Keys=True;Default Timeout=15;Pooling=False")
                .Options;
            Guid workId;
            await using (var setup = new DeskDbContext(options))
            {
                await setup.Database.MigrateAsync();
                var work = new WorkItem
                {
                    ProjectName = "并发退款",
                    ProjectType = ProjectType.Simulation,
                    ScheduleCategory = ScheduleCategory.Digital,
                    ScheduleCategorySource = ValueSource.Default,
                    ShippingRequirement = ShippingRequirement.None,
                    ShippingRequirementSource = ValueSource.Default,
                    SourceKind = SourceKind.Manual,
                    ReceivedDate = Today,
                    DueDateMode = DueDateMode.Auto,
                    WorkStatus = WorkStatus.NotStarted,
                    CompletedDateKnowledge = CompletionDateKnowledge.NotApplicable,
                    OrderAmountCents = 10_000,
                    CostConfirmationStatus = CostConfirmationStatus.Pending
                };
                setup.WorkItems.Add(work);
                await setup.SaveChangesAsync();
                workId = work.Id;
                var service = new FinanceService(setup, new FixedClock());
                await service.CreateTransaction(Receipt("concurrent-receipt", 10_000, (workId, 10_000)));
            }

            async Task<bool> TryRefund(string key)
            {
                await using var context = new DeskDbContext(options);
                var service = new FinanceService(context, new FixedClock());
                try
                {
                    await service.CreateTransaction(Refund(key, 6_000, (workId, 6_000)));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }

            var results = await Task.WhenAll(Task.Run(() => TryRefund("parallel-a")), Task.Run(() => TryRefund("parallel-b")));
            Assert.Single(results, x => x);
            await using var verify = new DeskDbContext(options);
            var refunded = await (from a in verify.PaymentAllocations
                                  join p in verify.PaymentTransactions on a.PaymentTransactionId equals p.Id
                                  where p.Type == TransactionType.Refund && p.Status == RecordStatus.Active
                                  select (long?)a.AmountCents).SumAsync() ?? 0;
            Assert.Equal(6_000, refunded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task<WorkItem> AddWork(long? amount, DateOnly? receivedDate = null)
    {
        var item = new WorkItem
        {
            ProjectName = "财务测试项目",
            ProjectType = ProjectType.Simulation,
            ScheduleCategory = ScheduleCategory.Digital,
            ScheduleCategorySource = ValueSource.Default,
            ShippingRequirement = ShippingRequirement.None,
            ShippingRequirementSource = ValueSource.Default,
            SourceKind = receivedDate.HasValue ? SourceKind.Manual : SourceKind.HistoricalImport,
            ImportBatchId = receivedDate.HasValue ? null : await AddImportBatch(),
            ReceivedDate = receivedDate,
            DueDateMode = receivedDate.HasValue ? DueDateMode.Auto : DueDateMode.HistoricalUnknown,
            WorkStatus = WorkStatus.NotStarted,
            CompletedDateKnowledge = CompletionDateKnowledge.NotApplicable,
            OrderAmountCents = amount,
            CostConfirmationStatus = CostConfirmationStatus.Pending
        };
        db.WorkItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private async Task<Guid> AddImportBatch()
    {
        var batch = new ImportBatch { SourceFileName = "history.xlsx", FileFingerprint = Guid.NewGuid().ToString(), Status = ImportStatus.Imported };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync();
        return batch.Id;
    }

    private static CreatePaymentRequestDto Receipt(string key, long amount, params (Guid Id, long Amount)[] allocation) =>
        new(TransactionType.Receipt, amount, Today, DateKnowledge.Known, "转账", null, null, key,
            allocation.Select(x => new PaymentAllocationInputDto(x.Id, x.Amount)).ToArray());

    private static CreatePaymentRequestDto Refund(string key, long amount, params (Guid Id, long Amount)[] allocation) =>
        new(TransactionType.Refund, amount, Today, DateKnowledge.Known, "转账", null, null, key,
            allocation.Select(x => new PaymentAllocationInputDto(x.Id, x.Amount)).ToArray());

    private sealed class FixedClock : IBusinessClock
    {
        public DateTimeOffset Now => new(2026, 11, 13, 12, 0, 0, TimeSpan.FromHours(8));
        public DateOnly Today => Stage2FinanceTests.Today;
    }
}
