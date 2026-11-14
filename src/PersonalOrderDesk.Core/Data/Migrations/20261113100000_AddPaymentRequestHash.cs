using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PersonalOrderDesk.Data;

namespace PersonalOrderDesk.Data.Migrations;

[DbContext(typeof(DeskDbContext))]
[Migration("20261113100000_AddPaymentRequestHash")]
public sealed class AddPaymentRequestHash : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "RequestPayloadHash", table: "PaymentTransactions", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<long>(name: "Version", table: "PaymentTransactions", type: "INTEGER", nullable: false, defaultValue: 1L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RequestPayloadHash", table: "PaymentTransactions");
        migrationBuilder.DropColumn(name: "Version", table: "PaymentTransactions");
    }
}
