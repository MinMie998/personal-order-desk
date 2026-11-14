using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Presentation;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;

public sealed class EverydayPresentationTests
{
    [Fact]
    public void Imported_prompts_are_separate_from_current_multiline_notes()
    {
        var notes = "客户要求白色外壳\r\n历史结款待核：导入时尚未确认。\r\n已确认改用USB供电\r\n历史成本待核：原始支出350。";
        var sections = WorkItemNoteSections.From(notes, SourceKind.HistoricalImport);
        Assert.Equal("客户要求白色外壳\n已确认改用USB供电", sections.CurrentNotes);
        Assert.Contains("历史结款待核", sections.ImportHints);
        Assert.Contains("历史成本待核", sections.ImportHints);
    }

    [Fact]
    public void Updating_or_clearing_current_notes_preserves_import_prompts_once()
    {
        var original = "旧备注\n历史成本待核：原始支出350。";
        var saved = WorkItemNoteSections.MergeCurrentNotes("新备注", original, SourceKind.HistoricalImport);
        Assert.Equal("新备注\n历史成本待核：原始支出350。", saved);
        Assert.Equal(saved, WorkItemNoteSections.MergeCurrentNotes("新备注", saved, SourceKind.HistoricalImport));
        Assert.Equal("历史成本待核：原始支出350。", WorkItemNoteSections.MergeCurrentNotes(null, saved, SourceKind.HistoricalImport));
    }

    [Fact]
    public void Manual_notes_and_unrecognized_historical_notes_remain_current()
    {
        const string notes = "历史成本待核：用户手写说明\n普通备注";
        Assert.Equal(notes, WorkItemNoteSections.From(notes, SourceKind.Manual).CurrentNotes);
        Assert.Empty(WorkItemNoteSections.From(notes, SourceKind.Manual).ImportHints);
        Assert.Equal("历史返工备注：保留", WorkItemNoteSections.From("历史返工备注：保留", SourceKind.HistoricalImport).CurrentNotes);
    }

    [Fact]
    public void Unsettled_copy_uses_remaining_balance_merges_additions_and_excludes_paid_rows()
    {
        WorkItemFinanceDto Row(string? number, long? due) => new(Guid.NewGuid(),number,"测试",10000,2000,due,0,0,0,CostConfirmationStatus.Pending,null,2000,[],[]);
        var result = UnsettledCopyList.From([Row("B-002",3000),Row("A-001",1250),Row("B-002",2000),Row("PAID",0),Row(null,200),Row("UNKNOWN",null)]);
        Assert.Equal("A-001 12.50\nB-002 50.00",result.Text);
        Assert.Equal(6250,result.TotalCents); Assert.Equal(2,result.LineCount);
        Assert.Equal(1,result.MissingNumberCount); Assert.Equal(1,result.MissingAmountCount);
        Assert.Empty(UnsettledCopyList.From([Row("PAID",0)]).Text);
    }

    [Fact]
    public void Unified_ledger_keeps_shared_order_share_refunds_and_reversed_expenses()
    {
        var item = Guid.NewGuid(); var other = Guid.NewGuid(); var group = Guid.NewGuid();
        var receipt = new PaymentTransactionDto(Guid.NewGuid(), TransactionType.Receipt, 70000, new(2026,11,14), DateKnowledge.Known,
            "微信", null, null, "one", RecordStatus.Active, null, DateTimeOffset.UtcNow, 1,
            [new(item,"A","本单",20000),new(other,"B","另单",50000)]);
        var refund = new PaymentTransactionDto(Guid.NewGuid(), TransactionType.Refund, 5000, null, DateKnowledge.Unknown,
            "微信", null, null, "two", RecordStatus.Active, null, DateTimeOffset.UtcNow, 1,[new(item,"A","本单",5000)]);
        var expense = new ExpenseDto(Guid.NewGuid(), group, 35000, new(2026,11,13), DateKnowledge.Known,
            ExpensePaymentStatus.Paid, new(2026,11,13), DateKnowledge.Known, ExpenseStatus.Voided,"误记",null);
        var finance = new WorkItemFinanceDto(item,"A","本单",70000,15000,55000,0,0,0,CostConfirmationStatus.Pending,null,15000,
            [receipt,refund],[new(group,item,"材料","材料",null,0,[expense])]);
        var rows = FinanceLedgerRow.From(finance);
        Assert.Equal(3, rows.Count);
        Assert.Equal(receipt.Id, rows[0].Id);
        Assert.Equal(20000, rows[0].AmountCents);
        Assert.Equal("支出", rows[1].Label);
        Assert.False(rows[1].IsActive);
        Assert.Equal("退款", rows[2].Label);
        Assert.Null(rows[2].Date);
    }
}
