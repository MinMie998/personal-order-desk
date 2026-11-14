using Microsoft.EntityFrameworkCore.Migrations;
namespace PersonalOrderDesk.Data.Migrations;
public partial class OptionalFinanceDates : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.DropCheckConstraint("CK_Payment_Positive", "PaymentTransactions");
        b.AddCheckConstraint("CK_Payment_Positive", "PaymentTransactions", "\"AmountCents\">0 AND ((\"Status\"='Reversed' AND \"ReversalReason\" IS NOT NULL AND trim(\"ReversalReason\")<>'') OR (\"Status\"='Active' AND \"ReversalReason\" IS NULL)) AND ((\"DateKnowledge\"='Known' AND \"TransactionDate\" IS NOT NULL) OR (\"DateKnowledge\"='Unknown' AND \"TransactionDate\" IS NULL)) AND ((\"SourceKind\"='Manual' AND \"ImportBatchId\" IS NULL) OR (\"SourceKind\"='HistoricalImport' AND \"ImportBatchId\" IS NOT NULL))");
        b.DropCheckConstraint("CK_Expense_Valid", "Expenses");
        b.AddCheckConstraint("CK_Expense_Valid", "Expenses", "\"AmountCents\">0 AND ((\"ExpenseDateKnowledge\"='Known' AND \"ExpenseDate\" IS NOT NULL) OR (\"ExpenseDateKnowledge\"='Unknown' AND \"ExpenseDate\" IS NULL)) AND ((\"PaymentStatus\"='Unpaid' AND \"PaidDate\" IS NULL AND \"PaidDateKnowledge\"='NotApplicable') OR (\"PaymentStatus\"='Paid' AND ((\"PaidDateKnowledge\"='Known' AND \"PaidDate\" IS NOT NULL) OR (\"PaidDateKnowledge\"='Unknown' AND \"PaidDate\" IS NULL)))) AND ((\"Status\"='Voided' AND \"ReversalReason\" IS NOT NULL) OR (\"Status\"='Active' AND \"ReversalReason\" IS NULL)) AND ((\"SourceKind\"='Manual' AND \"ImportBatchId\" IS NULL) OR (\"SourceKind\"='HistoricalImport' AND \"ImportBatchId\" IS NOT NULL))");
    }
    protected override void Down(MigrationBuilder b) => throw new System.NotSupportedException("包含可选收支日期的数据请从升级前保护备份恢复，不自动猜测日期。");
}
