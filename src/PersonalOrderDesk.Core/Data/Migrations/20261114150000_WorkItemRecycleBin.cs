using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalOrderDesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkItemRecycleBin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "WorkItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "WorkItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_IsDeleted",
                table: "WorkItems",
                column: "IsDeleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => throw new NotSupportedException("请从升级前备份恢复旧版本；直接移除回收站字段会丢失删除状态。");
    }
}
