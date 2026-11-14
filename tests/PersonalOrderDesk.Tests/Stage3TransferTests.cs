using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using PersonalOrderDesk.Data;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;

public sealed class Stage3TransferTests : IAsyncLifetime
{
    [Fact]
    public async Task Reassigning_a_source_row_exposes_due_conflicts_in_the_new_group_before_confirmation()
    {
        var path = SaveWorkbook("regroup-dates.xlsx");
        try
        {
            using (var book = new XLWorkbook(path))
            {
                book.Worksheet("基本信息").Cell(7, 5).Value = new DateTime(2026, 12, 30);
                book.Save();
            }
            var preview = await service.PreviewFileAsync(path);
            Assert.Empty(preview.DateIssues!);
            var decisions = preview.Decisions.Select(decision =>
                decision.SheetName == "完成进度" || decision.SheetName == "基本信息" && decision.RowNumber == 7
                    ? decision with { NewWorkItemGroupKey = "reassigned", NewWorkItemProjectName = "测试项目-另一项目" }
                    : decision).ToArray();
            await using var stream = File.OpenRead(path);
            var regrouped = await service.PreviewDifferencesAsync(stream, Path.GetFileName(path), decisions);
            Assert.Equal(2, regrouped.DateIssues!.Count(issue => issue.IsCrossRowConflict));
            Assert.Contains(regrouped.DateIssues!, issue => issue.SheetName == "基本信息" && issue.RowNumber == 7);
            Assert.Contains(regrouped.DateIssues!, issue => issue.SheetName == "完成进度" && issue.RowNumber == 6);
            Assert.Empty(await db.WorkItems.ToListAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Failure_after_batch_insert_rolls_back_business_facts_and_provenance_together()
    {
        var path = SaveWorkbook("mid-transaction-failure.xlsx");
        try
        {
            var fault = new ImportWriteFault();
            await using var failingDb = new DeskDbContext(new DbContextOptionsBuilder<DeskDbContext>()
                .UseSqlite(connection).AddInterceptors(fault).Options);
            var importer = new TransferService(failingDb, new SuccessfulMaintenance());
            var preview = await importer.PreviewFileAsync(path);
            var decisions = preview.Decisions.Select(row => row with
            {
                IsKnown = true,
                WorkStatusDecision = row.SheetName == "基本信息" && row.RowNumber == 7 ? WorkStatus.InProgress : null,
                ConfirmReceipt = row.SheetName == "收益记录",
                CostCompletenessConfirmed = row.SheetName == "收益记录",
                ExpensePaymentDecision = row.SheetName == "收益记录" ? ExpensePaymentStatus.Paid : null
            }).ToArray();
            await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ConfirmImportAsync(new(path, decisions, true, preview.FileFingerprint)));
            Assert.True(fault.BatchWasInserted, "Failure must happen after the batch was inserted inside the live transaction.");
            Assert.Empty(await db.ImportBatches.ToListAsync());
            Assert.Empty(await db.ImportRowMappings.ToListAsync());
            Assert.Empty(await db.WorkItems.ToListAsync());
            Assert.Empty(await db.PaymentTransactions.ToListAsync());
            Assert.Empty(await db.Expenses.ToListAsync());
            Assert.Empty(await db.ActivityLogs.ToListAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Historical_completion_preserves_known_date_without_inventing_a_time()
    {
        var path = SaveWorkbook("known-completion-date.xlsx");
        try
        {
            using (var book = new XLWorkbook(path))
            {
                book.Worksheet("完成进度").Cell(5, 11).Value = "完成日期";
                book.Worksheet("完成进度").Cell(6, 11).Value = new DateTime(2026, 10, 2);
                book.Save();
            }
            var preview = await service.PreviewFileAsync(path);
            var decisions = preview.Decisions.Select(row => row with
            {
                IsKnown = true,
                WorkStatusDecision = row.SheetName == "基本信息" && row.RowNumber == 7 ? WorkStatus.InProgress : null
            }).ToArray();
            await service.ConfirmImportAsync(new(path, decisions, true, preview.FileFingerprint));
            var item = await db.WorkItems.SingleAsync(item => item.ProjectName == "测试项目");
            var history = await db.WorkStatusHistories.SingleAsync(entry => entry.WorkItemId == item.Id);
            Assert.Equal(new DateOnly(2026, 10, 2), item.CompletedDate);
            Assert.Equal(CompletionDateKnowledge.Known, item.CompletedDateKnowledge);
            Assert.Null(item.CompletedAt);
            Assert.Equal(item.CompletedDate, history.OccurredDate);
            Assert.Null(history.OccurredAt);
            Assert.True(history.RecordedAt > new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        }
        finally { File.Delete(path); }
    }

    private sealed class ImportWriteFault : DbCommandInterceptor
    {
        public bool BatchWasInserted { get; private set; }
        private void Fail(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO \"WorkItems\"", StringComparison.OrdinalIgnoreCase)) return;
            using var probe = command.Connection!.CreateCommand();
            probe.Transaction = command.Transaction;
            probe.CommandText = "SELECT count(*) FROM ImportBatches";
            BatchWasInserted = Convert.ToInt64(probe.ExecuteScalar()) == 1;
            throw new InvalidOperationException("Controlled failure while writing imported business facts.");
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Fail(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Fail(command); return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task Repeated_source_names_with_different_quotes_require_explicit_row_assignment()
    {
        var path = SaveWorkbook("repeated-names.xlsx");
        try
        {
            using (var book = new XLWorkbook(path))
            {
                var income = book.Worksheet("收益记录");
                for (var column = 1; column <= 8; column++) income.Cell(7, column).Value = income.Cell(6, column).Value;
                income.Cell(7, 3).Value = 20;
                income.Cell(7, 4).Clear();
                book.Save();
            }
            var preview = await service.PreviewFileAsync(path);
            Assert.Contains(preview.Warnings, warning => warning.Contains("不同报价不能自动取第一笔"));
            var decisions = preview.Decisions.Select(row => row with
            {
                IsKnown = true,
                WorkStatusDecision = row.SheetName == "基本信息" && row.RowNumber == 7 ? WorkStatus.InProgress : null
            }).ToArray();
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ConfirmImportAsync(new(path, decisions, true, preview.FileFingerprint)));
            Assert.Empty(await db.WorkItems.ToListAsync());
            var assigned = decisions.Select(decision =>
            {
                var source = preview.Sheets.Single(sheet => sheet.SheetName == decision.SheetName).Rows.Single(row => row.RowNumber == decision.RowNumber);
                var name = decision.SheetName == "收益记录" && decision.RowNumber == 7
                    ? "测试项目-另一项目" : source.Values["ProjectName"]!;
                return decision with { NewWorkItemGroupKey = name, NewWorkItemProjectName = name };
            }).ToArray();
            await service.ConfirmImportAsync(new(path, assigned, true, preview.FileFingerprint));
            Assert.Equal(135500, (await db.WorkItems.SingleAsync(item => item.ProjectName == "测试项目")).OrderAmountCents);
            Assert.Equal(2000, (await db.WorkItems.SingleAsync(item => item.ProjectName == "测试项目-另一项目")).OrderAmountCents);
        }
        finally { File.Delete(path); }
    }

    private SqliteConnection connection=null!; private DeskDbContext db=null!; private TransferService service=null!;
    public async Task InitializeAsync(){connection=new("Data Source=:memory:;Foreign Keys=True");await connection.OpenAsync();db=new(new DbContextOptionsBuilder<DeskDbContext>().UseSqlite(connection).Options);await db.Database.MigrateAsync();service=new(db,new SuccessfulMaintenance());}
    public async Task DisposeAsync(){await db.DisposeAsync();await connection.DisposeAsync();}
    private string SaveWorkbook(string name,string project="测试项目",int quote=1355,int expense=350){var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+name);using var b=new XLWorkbook();var info=b.AddWorksheet("基本信息");for(var r=1;r<=4;r++)info.Cell(r,1).Value="标题/说明";string[] h=["订单编号","项目名称","项目类型","接单日期","截止日期","客户平台","工程目录","提醒提前天数","备注","项目类型","提醒提前天数"];for(var c=0;c<h.Length;c++)info.Cell(5,c+1).Value=h[c];info.Cell(6,1).Value="重复业务号";info.Cell(6,2).Value=project;info.Cell(6,3).Value="代码报告";info.Cell(6,10).Value="实物制作";info.Cell(6,11).Value=99;info.Cell(6,4).Value="2026.9.17";info.Cell(7,1).Value="重复业务号";info.Cell(7,2).Value=project+"-另一项目";var prog=b.AddWorksheet("完成进度");for(var r=1;r<=4;r++)prog.Cell(r,1).Value="标题/说明";prog.Cell(5,1).Value="订单编号";prog.Cell(5,2).Value="项目名称";prog.Cell(5,4).Value="截止日期";prog.Cell(5,5).Value="订单状态";prog.Cell(5,15).Value="物流状态";prog.Cell(5,16).Value="快递单号";prog.Cell(6,1).Value="重复业务号";prog.Cell(6,2).Value=project;prog.Cell(6,4).Value=new DateTime(2026,12,31);prog.Cell(6,5).Value="已完成";prog.Cell(6,15).Value="待确认";var earn=b.AddWorksheet("收益记录");for(var r=1;r<=4;r++)earn.Cell(r,1).Value="标题/说明";earn.Cell(5,1).Value="订单编号";earn.Cell(5,2).Value="项目名称";earn.Cell(5,3).Value="收入（原表金额）";earn.Cell(5,4).Value="支出";earn.Cell(5,6).Value="支出记录状态";earn.Cell(5,7).Value="结款状态";earn.Cell(5,8).Value="结款日期";earn.Cell(6,1).Value="重复业务号";earn.Cell(6,2).Value=project;earn.Cell(6,3).Value=quote;earn.Cell(6,4).Value=expense;earn.Cell(6,6).Value="已记录";earn.Cell(6,7).Value="已结款";b.SaveAs(path);return path;}
    [Fact] public async Task Preview_confirm_duplicate_and_report_are_one_way(){var path=SaveWorkbook("fixture.xlsx");try{await using var stream=File.OpenRead(path);var p=await service.PreviewAsync(stream,path);Assert.Equal(2,p.Sheets[0].RowCount);Assert.False(p.AlreadyImported);Assert.Contains(p.Warnings,x=>x.Contains("报价不视为实收"));var decisions=p.Decisions.Select(x=>x with{IsKnown=true,WorkStatusDecision=x.SheetName=="基本信息"&&x.RowNumber==7?WorkStatus.InProgress:WorkStatus.Completed,ConfirmReceipt=x.SheetName=="收益记录",ExpensePaymentDecision=x.SheetName=="收益记录"?ExpensePaymentStatus.Paid:null,CostCompletenessConfirmed=x.SheetName=="收益记录",ShippingRequirementDecision=x.SheetName=="完成进度"?ShippingRequirement.Required:null}).ToArray();var result=await service.ConfirmImportAsync(new(path,decisions,true,p.FileFingerprint));Assert.Equal(2,result.ImportedRows);Assert.Equal(2,await db.WorkItems.CountAsync());Assert.Equal(ProjectType.CodeReport,(await db.WorkItems.SingleAsync(x=>x.ProjectName=="测试项目")).ProjectType);Assert.Equal(DueDateMode.Manual,(await db.WorkItems.SingleAsync(x=>x.ProjectName=="测试项目")).DueDateMode);Assert.Equal(new DateOnly(2026,12,31),(await db.WorkItems.SingleAsync(x=>x.ProjectName=="测试项目")).EffectiveDueDate);Assert.Equal(ScheduleCategory.Pending,(await db.WorkItems.SingleAsync(x=>x.ProjectName=="测试项目-另一项目")).ScheduleCategory);Assert.Contains(await db.WorkItems.ToListAsync(),x=>x.WorkStatus==PersonalOrderDesk.Domain.WorkStatus.Completed&&x.CompletedDateKnowledge==CompletionDateKnowledge.Unknown&&x.CompletedDate is null);Assert.Single(await db.PaymentTransactions.ToListAsync());Assert.Single(await db.PaymentAllocations.ToListAsync());Assert.Single(await db.Expenses.ToListAsync());Assert.Single(await db.Shipments.ToListAsync());Assert.Single(await db.WorkStatusHistories.ToListAsync());var payment=await db.PaymentTransactions.SingleAsync();Assert.Equal(DateKnowledge.Unknown,payment.DateKnowledge);Assert.Equal(135500,payment.AmountCents);Assert.Equal(35000,(await db.Expenses.SingleAsync()).AmountCents);Assert.Equal(ShipmentStatus.Unknown,(await db.Shipments.SingleAsync()).ShipmentStatus);Assert.True(await db.ActivityLogs.AnyAsync(x=>x.ActionType=="HistoricalImportSourceSnapshot"&&x.AfterData!.Contains("ExpenseAmount")));await using var again=File.OpenRead(path);var duplicate=await service.PreviewAsync(again,path);Assert.True(duplicate.AlreadyImported);var receiptForReport=await db.PaymentTransactions.SingleAsync();receiptForReport.Status=RecordStatus.Reversed;receiptForReport.ReversalReason="重复收款撤销";var expenseForReport=await db.Expenses.SingleAsync();expenseForReport.Status=ExpenseStatus.Voided;expenseForReport.ReversalReason="来源支出更正";var secondItem=await db.WorkItems.SingleAsync(x=>x.ProjectName=="测试项目-另一项目");secondItem.ProjectName="=1+1";var secondGroup=new ExpenseGroup{WorkItemId=secondItem.Id,Title="第二项目材料",Category="材料",Notes="第二项成本"};db.ExpenseGroups.Add(secondGroup);db.Expenses.Add(new Expense{ExpenseGroupId=secondGroup.Id,AmountCents=9900,ExpenseDateKnowledge=DateKnowledge.Unknown,PaymentStatus=ExpensePaymentStatus.Unpaid,PaidDateKnowledge=DateKnowledge.NotApplicable,SourceKind=SourceKind.HistoricalImport,ImportBatchId=result.BatchId,Status=ExpenseStatus.Active});db.Shipments.Add(new Shipment{WorkItemId=secondItem.Id,ShipmentStatus=ShipmentStatus.Unknown,NoTrackingNumber=true,ShippedDateKnowledge=DateKnowledge.Unknown,DeliveredDateKnowledge=DateKnowledge.Unknown,SourceKind=SourceKind.HistoricalImport,ImportBatchId=result.BatchId});var thirdItem=new WorkItem{BusinessOrderNo="T-3",ProjectName="第三项目",SourceKind=SourceKind.HistoricalImport,ImportBatchId=result.BatchId,ProjectType=ProjectType.Other,ScheduleCategory=ScheduleCategory.Pending,ScheduleCategorySource=ValueSource.Imported,ShippingRequirement=ShippingRequirement.Required,ShippingRequirementSource=ValueSource.Imported,DueDateMode=DueDateMode.HistoricalUnknown,WorkStatus=WorkStatus.InProgress,CompletedDateKnowledge=CompletionDateKnowledge.NotApplicable,CostConfirmationStatus=CostConfirmationStatus.Pending};db.WorkItems.Add(thirdItem);db.Shipments.Add(new Shipment{WorkItemId=thirdItem.Id,ShipmentStatus=ShipmentStatus.Unknown,TrackingNumber="YT123456",NoTrackingNumber=false,ShippedDateKnowledge=DateKnowledge.Unknown,DeliveredDateKnowledge=DateKnowledge.Unknown,SourceKind=SourceKind.HistoricalImport,ImportBatchId=result.BatchId});await db.SaveChangesAsync();var bytes=await service.ExportReportAsync();using var report=new XLWorkbook(new MemoryStream(bytes));Assert.Equal("工作项",report.Worksheet(1).Name);Assert.Equal("财务交易",report.Worksheet("财务交易").Name);Assert.Equal(1355m,report.Worksheet("财务交易").Cell(3,3).GetValue<decimal>());Assert.Equal("已撤销",report.Worksheet("财务交易").Cell(3,6).GetString());Assert.Equal("重复收款撤销",report.Worksheet("财务交易").Cell(3,7).GetString());Assert.Equal(1355m,report.Worksheet("工作项").Column(3).CellsUsed().Single(x=>x.GetString()=="测试项目").WorksheetRow().Cell(12).GetValue<decimal>());Assert.Contains(report.Worksheet("成本明细").Column(5).CellsUsed().Select(x=>x.GetString()),x=>x=="测试项目");Assert.Contains(report.Worksheet("成本明细").Column(5).CellsUsed().Select(x=>x.GetString()),x=>x=="=1+1");Assert.Contains(report.Worksheet("成本明细").Column(15).CellsUsed().Select(x=>x.GetString()),x=>x=="已作废");Assert.Contains(report.Worksheet("成本明细").Column(15).CellsUsed().Select(x=>x.GetString()),x=>x=="有效");Assert.Contains(report.Worksheet("物流").Column(7).CellsUsed().Select(x=>x.GetString()),x=>x=="未知");Assert.Contains(report.Worksheet("物流").Column(7).CellsUsed().Select(x=>x.GetString()),x=>x=="明确无单号");Assert.Contains(report.Worksheet("物流").Column(7).CellsUsed().Select(x=>x.GetString()),x=>x=="有单号");var formulaLike=report.Worksheet("工作项").Column(3).CellsUsed().Single(x=>x.GetString()=="=1+1");Assert.Equal(XLDataType.Text,formulaLike.DataType);Assert.Throws<InvalidDataException>(()=>service.PreviewAsync(new MemoryStream(bytes),"report.xlsx").GetAwaiter().GetResult());}finally{File.Delete(path);}}
    [Fact] public async Task Explicit_group_decision_merges_same_order_different_names_and_keeps_each_source_mapping(){var path=SaveWorkbook("group.xlsx");try{await using var s=File.OpenRead(path);var preview=await service.PreviewAsync(s,path);var basic=preview.Decisions.Where(x=>x.SheetName=="基本信息").ToArray();Assert.NotEqual(basic[0].SuggestedWorkItemGroupKey,basic[1].SuggestedWorkItemGroupKey);var decisions=preview.Decisions.Select(x=>x with{IsKnown=true,NewWorkItemGroupKey="explicit-group-1",NewWorkItemProjectName="统一项目"}).ToArray();var result=await service.ConfirmImportAsync(new(path,decisions,true,preview.FileFingerprint));Assert.Equal(1,result.ImportedRows);var item=await db.WorkItems.SingleAsync();Assert.Equal("统一项目",item.ProjectName);Assert.Equal(4,await db.ImportRowMappings.CountAsync(x=>x.EntityType=="WorkItem"));Assert.All(await db.ImportRowMappings.Where(x=>x.EntityType=="WorkItem").ToListAsync(),x=>Assert.Equal(item.Id.ToString(),x.EntityId));}finally{File.Delete(path);}}
    [Fact] public async Task Explicit_zero_quote_and_zero_cost_are_not_treated_as_missing(){var path=SaveWorkbook("zero.xlsx");try{using(var book=new XLWorkbook(path)){book.Worksheet("收益记录").Cell(6,3).Value="0.00";book.Worksheet("收益记录").Cell(6,4).Value="0.00";book.Save();}await using var s=File.OpenRead(path);var p=await service.PreviewAsync(s,path);var decisions=p.Decisions.Select(x=>x with{IsKnown=true,WorkStatusDecision=x.SheetName=="基本信息"&&x.RowNumber==7?WorkStatus.InProgress:null,ConfirmReceipt=x.SheetName=="收益记录",CostCompletenessConfirmed=x.SheetName=="收益记录"}).ToArray();await service.ConfirmImportAsync(new(path,decisions,true,p.FileFingerprint));var target=await db.WorkItems.SingleAsync(x=>x.ProjectName=="测试项目");Assert.Equal(0,target.OrderAmountCents);Assert.Equal(CostConfirmationStatus.ConfirmedNoCost,target.CostConfirmationStatus);Assert.Empty(await db.PaymentTransactions.ToListAsync());Assert.Empty(await db.Expenses.ToListAsync());}finally{File.Delete(path);}}
    [Fact] public async Task Invalid_date_without_year_is_reported_and_requires_an_explicit_override(){var path=SaveWorkbook("invalid-date.xlsx");try{using(var book=new XLWorkbook(path)){book.Worksheet("基本信息").Cell(6,4).Value="9/17";book.Save();}await using var s=File.OpenRead(path);var preview=await service.PreviewAsync(s,path);var issue=Assert.Single(preview.DateIssues!,x=>x.SheetName=="基本信息"&&x.RowNumber==6&&x.Field=="ReceivedDate");Assert.Equal("9/17",issue.RawValue);Assert.Contains("无法解析",issue.Problem);var decisions=preview.Decisions.Select(x=>x with{IsKnown=true,WorkStatusDecision=x.SheetName=="基本信息"&&x.RowNumber==7?WorkStatus.InProgress:null}).ToArray();await Assert.ThrowsAsync<InvalidDataException>(()=>service.ConfirmImportAsync(new(path,decisions,true,preview.FileFingerprint)));var corrected=decisions.Select(x=>x.SheetName=="基本信息"&&x.RowNumber==6?x with{ReceivedDateOverride=new DateOnly(2026,9,17)}:x).ToArray();await service.ConfirmImportAsync(new(path,corrected,true,preview.FileFingerprint));Assert.Equal(new DateOnly(2026,9,17),(await db.WorkItems.SingleAsync(x=>x.ProjectName=="测试项目")).ReceivedDate);}finally{File.Delete(path);}}
    [Fact] public async Task Cross_sheet_due_conflict_is_previewed_and_requires_same_explicit_date(){var path=SaveWorkbook("due-conflict.xlsx");try{using(var book=new XLWorkbook(path)){book.Worksheet("基本信息").Cell(6,5).Value=new DateTime(2026,12,30);book.Save();}await using var s=File.OpenRead(path);var preview=await service.PreviewAsync(s,path);var conflicts=preview.DateIssues!.Where(x=>x.IsCrossRowConflict&&x.Field=="EffectiveDueDate").ToArray();Assert.Equal(2,conflicts.Length);var decisions=preview.Decisions.Select(x=>x with{IsKnown=true,WorkStatusDecision=x.SheetName=="基本信息"&&x.RowNumber==7?WorkStatus.InProgress:null}).ToArray();await Assert.ThrowsAsync<InvalidDataException>(()=>service.ConfirmImportAsync(new(path,decisions,true,preview.FileFingerprint)));var resolved=decisions.Select(x=>x.SheetName=="基本信息"&&x.RowNumber==6||x.SheetName=="完成进度"&&x.RowNumber==6?x with{DueDateOverride=new DateOnly(2026,12,30)}:x).ToArray();await service.ConfirmImportAsync(new(path,resolved,true,preview.FileFingerprint));var item=await db.WorkItems.SingleAsync(x=>x.ProjectName=="测试项目");Assert.Equal(DueDateMode.Manual,item.DueDateMode);Assert.Equal(new DateOnly(2026,12,30),item.EffectiveDueDate);}finally{File.Delete(path);}}
    [Fact] public async Task Confirmation_required_and_invalid_row_rolls_back(){var path=SaveWorkbook("x.xlsx");try{await using var s=File.OpenRead(path);var p=await service.PreviewAsync(s,path);await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ConfirmImportAsync(new(path,p.Decisions,false)));await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ConfirmImportAsync(new(path,p.Decisions,true,p.FileFingerprint)));Assert.Empty(await db.ImportBatches.ToListAsync());var bad=p.Decisions.Append(new TransferRowDecision("missing",999,"WorkItem",null,true,null)).ToArray();await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ConfirmImportAsync(new(path,bad,true,p.FileFingerprint)));Assert.Empty(await db.WorkItems.ToListAsync());Assert.Empty(await db.ImportBatches.ToListAsync());}finally{File.Delete(path);}}
    [Fact] public async Task Changed_source_shows_real_differences_and_never_overwrites_or_duplicates(){var pth=SaveWorkbook("old.xlsx");var changedPath=SaveWorkbook("new.xlsx","修改后的内容",1400,400);try{await using(var s=File.OpenRead(pth)){var p=await service.PreviewAsync(s,pth);var decisions=p.Decisions.Select(x=>x with{IsKnown=true,WorkStatusDecision=WorkStatus.Completed,ConfirmReceipt=x.SheetName=="收益记录",ExpensePaymentDecision=x.SheetName=="收益记录"?ExpensePaymentStatus.Paid:null,CostCompletenessConfirmed=x.SheetName=="收益记录"}).ToArray();await service.ConfirmImportAsync(new(pth,decisions,true,p.FileFingerprint));}var unchanged=await service.PreviewFileAsync(pth);Assert.Empty(unchanged.Differences);var original=(await db.WorkItems.SingleAsync(x=>x.ProjectName=="测试项目")).Id;File.Copy(changedPath,pth,true);await using var changed=File.OpenRead(pth);var p2=await service.PreviewAsync(changed,pth);Assert.NotEqual(p2.FileFingerprint,(await db.ImportBatches.SingleAsync()).FileFingerprint);Assert.Contains(p2.Differences,x=>x.TargetEntityId==original.ToString()&&x.Field=="ProjectName"&&x.ExistingValue=="测试项目"&&x.IncomingValue=="修改后的内容");Assert.Contains(p2.Differences,x=>x.TargetEntityId==original.ToString()&&x.Field=="Source:QuotedAmount"&&x.ExistingValue=="1355"&&x.IncomingValue=="1400");Assert.Contains(p2.Differences,x=>x.TargetEntityId==original.ToString()&&x.Field=="Source:ExpenseAmount"&&x.ExistingValue=="350"&&x.IncomingValue=="400");Assert.All(p2.Decisions,x=>Assert.False(string.IsNullOrWhiteSpace(x.TargetEntityId)));var noMapping=p2.Decisions.Select(x=>x with{IsKnown=true,TargetEntityId=null}).ToArray();await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ConfirmImportAsync(new(pth,noMapping,true,p2.FileFingerprint)));var decisions2=p2.Decisions.Select(x=>x with{IsKnown=true}).ToArray();await using var changedAgain=File.OpenRead(pth);var diff=await service.PreviewDifferencesAsync(changedAgain,pth,decisions2);Assert.Contains(diff.Differences,x=>x.TargetEntityId==original.ToString()&&x.Field=="ProjectName"&&x.ExistingValue=="测试项目"&&x.IncomingValue=="修改后的内容");Assert.Contains(diff.Differences,x=>x.TargetEntityId==original.ToString()&&x.Field=="Source:QuotedAmount"&&x.ExistingValue=="1355"&&x.IncomingValue=="1400");Assert.Contains(diff.Differences,x=>x.TargetEntityId==original.ToString()&&x.Field=="Source:ExpenseAmount"&&x.ExistingValue=="350"&&x.IncomingValue=="400");var result=await service.ConfirmImportAsync(new(pth,decisions2,true,p2.FileFingerprint));Assert.Equal(0,result.ImportedRows);Assert.Equal(4,result.SkippedRows);Assert.Equal(2,await db.WorkItems.CountAsync());Assert.Single(await db.PaymentTransactions.ToListAsync());Assert.Single(await db.Expenses.ToListAsync());Assert.Equal(135500,(await db.PaymentTransactions.SingleAsync()).AmountCents);Assert.Equal(35000,(await db.Expenses.SingleAsync()).AmountCents);Assert.Equal("测试项目",(await db.WorkItems.FindAsync(original))!.ProjectName);}finally{File.Delete(pth);File.Delete(changedPath);}}
    private sealed class SuccessfulMaintenance:IMaintenanceService
    { public Task<MaintenanceStatusDto> GetMaintenanceStatusAsync(CancellationToken c=default)=>throw new NotImplementedException(); public Task<IReadOnlyList<BackupFileDto>> ListBackupsAsync(CancellationToken c=default)=>throw new NotImplementedException(); public Task<MaintenanceBackupResultDto> CreateBackupAsync(CancellationToken c=default)=>Task.FromResult(new MaintenanceBackupResultDto(true,"fixture.db",DateTime.Now,null)); public Task<RestorePreviewDto> PreviewRestoreAsync(string p,CancellationToken c=default)=>throw new NotImplementedException(); public Task<RestoreResultDto> RestoreAsync(string p,string t,CancellationToken c=default)=>throw new NotImplementedException(); public Task RequestExitAsync(CancellationToken c=default)=>throw new NotImplementedException(); }
}












