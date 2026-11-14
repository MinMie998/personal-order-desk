using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ClosedXML.Excel;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;

public sealed class WorkItemDeletionTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private DeskDbContext db = null!;
    private WorkbenchService workbench = null!;
    private FinanceService finance = null!;
    private readonly Clock clock = new();
    public async Task InitializeAsync()
    {
        connection = new("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        workbench = new(db,clock,new WorkItemService(db),NullLogger<WorkbenchService>.Instance);
        finance = new(db,clock);
    }
    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    private WorkItemSaveRequestDto Request(string name, ProjectType type = ProjectType.Simulation) => new(null,"DEL-001",name,type,null,null,clock.Today,DueDateMode.Auto,null,"当前工作","下一步",70000,"D:\test-project","保留备注");
    private async Task<WorkItemDetailDto> Add(string name = "删除验收", ProjectType type = ProjectType.Simulation) => await workbench.Create(Request(name,type));

    [Fact]
    public async Task Delete_and_restore_keep_identity_state_notes_and_original_files_path()
    {
        var item = await Add(); var id = item.WorkItem.Id!.Value;
        await workbench.Delete(id,item.WorkItem.Version);
        Assert.Null(await workbench.GetDetail(id));
        Assert.Empty((await workbench.GetList(new(IncludeArchived:true))).Items);
        Assert.Empty((await workbench.GetDashboard()).Sections.SelectMany(x=>x.Items));
        var recycled = Assert.Single(await workbench.GetRecycleBin());
        Assert.Equal(id,recycled.Id); Assert.Equal(clock.Now,recycled.DeletedAt);
        await Assert.ThrowsAsync<KeyNotFoundException>(()=>workbench.GetEditor(id));
        await workbench.RestoreDeleted(id,recycled.Version);
        var restored = (await workbench.GetDetail(id))!.WorkItem;
        Assert.Equal(item.WorkItem.Status,restored.Status); Assert.Equal("保留备注",restored.Notes);
        Assert.Equal(item.WorkItem.ProjectPath,restored.ProjectPath); Assert.Empty(await workbench.GetRecycleBin());
        Assert.Equal(2,await db.ActivityLogs.CountAsync(x=>x.ActionType=="WorkItemDeleted"||x.ActionType=="WorkItemRestored"));
    }

    [Fact]
    public async Task Funded_order_leaves_statistics_but_all_money_returns_after_restore()
    {
        var item=await Add(); var id=item.WorkItem.Id!.Value;
        await finance.CreateTransaction(new(TransactionType.Receipt,70000,null,DateKnowledge.Unknown,"微信",null,null,"delete-money",[new(id,70000)]));
        await finance.SaveExpenseGroup(new(id,null,item.WorkItem.Version,"材料","材料",null,[new(null,35000,null,DateKnowledge.Unknown,ExpensePaymentStatus.Paid,null,DateKnowledge.Unknown,null)]));
        var version=(await workbench.GetEditor(id)).Version;
        var preview=(await workbench.GetDeletePreview(id))!;
        Assert.Equal(70000,preview.NetReceivedCents); Assert.Equal(35000,preview.TotalCostCents);
        await workbench.Delete(id,version);
        var hidden=await finance.GetOverview(new()); Assert.Empty(hidden.WorkItems); Assert.Equal(0,hidden.UnknownDateNetReceivedCents); Assert.Equal(0,hidden.UnknownDatePaidCostCents);
        Assert.Null(await finance.GetWorkItemFinance(id)); Assert.Single(await db.PaymentTransactions.ToListAsync()); Assert.Single(await db.Expenses.ToListAsync());
        await workbench.RestoreDeleted(id,(await workbench.GetRecycleBin()).Single().Version);
        var restored=(await finance.GetWorkItemFinance(id))!; Assert.Equal(35000,restored.NetReceivedCents-restored.TotalCostCents);
    }

    [Fact]
    public async Task Shared_receipt_and_additional_relation_survive_deleting_one_order()
    {
        var parent=await Add("原单"); var id=parent.WorkItem.Id!.Value;
        var child=await workbench.CreateAdditionalWork(id,Request("追加单")); var childId=child.WorkItem.Id!.Value;
        await finance.CreateTransaction(new(TransactionType.Receipt,70000,clock.Today,DateKnowledge.Known,"微信",null,null,"shared-delete",[new(id,50000),new(childId,20000)]));
        Assert.True((await workbench.GetDeletePreview(id))!.HasSharedTransaction);
        await workbench.Delete(id,(await workbench.GetEditor(id)).Version);
        var childMoney=(await finance.GetWorkItemFinance(childId))!; Assert.Equal(20000,childMoney.NetReceivedCents);
        Assert.Equal(2,childMoney.Transactions.Single().Allocations.Count);
        Assert.True(childMoney.Transactions.Single().Allocations.Single(x=>x.WorkItemId==id).IsDeleted);
        Assert.Single(await db.WorkItemRelations.ToListAsync()); Assert.Empty((await workbench.GetDetail(childId))!.Relations);
        await workbench.RestoreDeleted(id,(await workbench.GetRecycleBin()).Single().Version);
        Assert.Single((await workbench.GetDetail(childId))!.Relations);
        Assert.Equal(70000,(await finance.GetOverview(new())).TotalNetReceivedCents);
    }

    [Fact]
    public async Task Deleted_shipping_queue_is_hidden_and_stale_versions_cannot_delete_or_restore()
    {
        var item=await Add(type:ProjectType.PhysicalProduction); var id=item.WorkItem.Id!.Value;
        var shipping=new ShippingService(db,clock); Assert.Single(await shipping.GetQueue());
        var changed=await workbench.ChangeStatus(id,new(WorkStatus.InProgress,item.WorkItem.Version));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>workbench.Delete(id,item.WorkItem.Version));
        await workbench.Delete(id,changed.WorkItem.Version); Assert.Empty(await shipping.GetQueue());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>workbench.RestoreDeleted(id,changed.WorkItem.Version));
        await workbench.RestoreDeleted(id,(await workbench.GetRecycleBin()).Single().Version); Assert.Single(await shipping.GetQueue());
    }

    [Fact]
    public async Task Deleted_orders_reject_new_money_and_hidden_transaction_changes()
    {
        var item=await Add(); var id=item.WorkItem.Id!.Value;
        var payment=await finance.CreateTransaction(new(TransactionType.Receipt,1000,null,DateKnowledge.Unknown,"现金",null,null,"old-delete",[new(id,1000)]));
        await workbench.Delete(id,item.WorkItem.Version);
        await Assert.ThrowsAsync<KeyNotFoundException>(()=>finance.CreateTransaction(new(TransactionType.Receipt,1000,null,DateKnowledge.Unknown,"现金",null,null,"new-delete",[new(id,1000)])));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>finance.ReverseTransaction(payment.Id,new("旧标签页")));
        Assert.Equal(RecordStatus.Active,(await db.PaymentTransactions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Detailed_report_keeps_recycled_records_explicitly_marked_for_audit()
    {
        var item=await Add(); var id=item.WorkItem.Id!.Value;
        await workbench.Delete(id,item.WorkItem.Version);
        var bytes=await new TransferService(db,null!).ExportReportAsync();
        using var workbook=new XLWorkbook(new MemoryStream(bytes)); var sheet=workbook.Worksheet("工作项");
        Assert.Contains("含回收站",sheet.Cell(1,2).GetString());
        var header=sheet.Row(2).CellsUsed().Single(x=>x.GetString()=="回收站");
        Assert.Equal("已删除",sheet.Cell(3,header.Address.ColumnNumber).GetString());
        Assert.Empty((await workbench.GetList(new())).Items);
    }

    [Fact]
    public async Task Quick_progress_update_preserves_price_dates_notes_and_type_defaults()
    {
        var item = await Add(); var original = item.WorkItem;
        var changed = await workbench.UpdateProgress(original.Id!.Value, new(original.Version,"已联调","准备交付","D:\\new-project"));
        Assert.Equal("已联调", changed.WorkItem.CurrentWork);
        Assert.Equal(original.OrderAmountCents, changed.WorkItem.OrderAmountCents);
        Assert.Equal(original.EffectiveDueDate, changed.WorkItem.EffectiveDueDate);
        Assert.Equal(original.Notes, changed.WorkItem.Notes);
        Assert.Equal(original.ScheduleCategorySource, changed.WorkItem.ScheduleCategorySource);
        Assert.Equal(original.ShippingRequirementSource, changed.WorkItem.ShippingRequirementSource);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => workbench.UpdateProgress(original.Id.Value,new(original.Version,"旧标签页",null,null)));
    }

    private sealed class Clock : IBusinessClock { public DateTimeOffset Now => new(2026,11,14,15,0,0,TimeSpan.FromHours(8)); public DateOnly Today => new(2026,11,14); }
}
