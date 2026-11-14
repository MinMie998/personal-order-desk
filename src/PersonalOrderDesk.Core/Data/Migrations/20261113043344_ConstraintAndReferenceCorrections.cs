using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalOrderDesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConstraintAndReferenceCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Shipment_Valid",
                table: "Shipments");

            migrationBuilder.CreateIndex(
                name: "IX_WorkStatusHistories_ImportBatchId",
                table: "WorkStatusHistories",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_ImportBatchId",
                table: "Shipments",
                column: "ImportBatchId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shipment_Valid",
                table: "Shipments",
                sql: "((\"SourceKind\"='Manual' AND \"ImportBatchId\" IS NULL) OR (\"SourceKind\"='HistoricalImport' AND \"ImportBatchId\" IS NOT NULL)) AND (\"FulfillmentPaused\"=0 OR \"ShipmentStatus\"='NotShipped') AND ((\"NoTrackingNumber\"=1 AND \"TrackingNumber\" IS NULL AND \"ShipmentStatus\"<>'NotShipped') OR (\"NoTrackingNumber\"=0 AND \"TrackingNumber\" IS NOT NULL) OR (\"NoTrackingNumber\" IS NULL AND \"TrackingNumber\" IS NULL AND (\"ShipmentStatus\"='NotShipped' OR \"SourceKind\"='HistoricalImport'))) AND ((\"ShipmentStatus\"='NotShipped' AND \"ShippedDateKnowledge\"='NotApplicable' AND \"ShippedDate\" IS NULL AND \"DeliveredDateKnowledge\"='NotApplicable' AND \"DeliveredDate\" IS NULL) OR (\"ShipmentStatus\"='Unknown' AND \"SourceKind\"='HistoricalImport' AND \"ShippedDate\" IS NULL AND \"DeliveredDate\" IS NULL) OR (\"ShipmentStatus\"='Shipped' AND ((\"ShippedDateKnowledge\"='Known' AND \"ShippedDate\" IS NOT NULL) OR (\"ShippedDateKnowledge\"='Unknown' AND \"SourceKind\"='HistoricalImport' AND \"ShippedDate\" IS NULL)) AND \"DeliveredDateKnowledge\"='NotApplicable' AND \"DeliveredDate\" IS NULL) OR (\"ShipmentStatus\"='Delivered' AND ((\"ShippedDateKnowledge\"='Known' AND \"ShippedDate\" IS NOT NULL) OR (\"ShippedDateKnowledge\"='Unknown' AND \"SourceKind\"='HistoricalImport' AND \"ShippedDate\" IS NULL)) AND ((\"DeliveredDateKnowledge\"='Known' AND \"DeliveredDate\" IS NOT NULL) OR (\"DeliveredDateKnowledge\"='Unknown' AND \"SourceKind\"='HistoricalImport' AND \"DeliveredDate\" IS NULL)))) AND (\"DeliveredDate\" IS NULL OR \"ShippedDate\" IS NULL OR \"DeliveredDate\">=\"ShippedDate\")");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_ImportBatchId",
                table: "PaymentTransactions",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_ImportBatchId",
                table: "Expenses",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_ImportBatchId",
                table: "ActivityLogs",
                column: "ImportBatchId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActivityLog_ImportBatch",
                table: "ActivityLogs",
                sql: "\"SourceKind\"<>'Import' OR \"ImportBatchId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityLogs_ImportBatches_ImportBatchId",
                table: "ActivityLogs",
                column: "ImportBatchId",
                principalTable: "ImportBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Expenses_ImportBatches_ImportBatchId",
                table: "Expenses",
                column: "ImportBatchId",
                principalTable: "ImportBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentTransactions_ImportBatches_ImportBatchId",
                table: "PaymentTransactions",
                column: "ImportBatchId",
                principalTable: "ImportBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Shipments_ImportBatches_ImportBatchId",
                table: "Shipments",
                column: "ImportBatchId",
                principalTable: "ImportBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkStatusHistories_ImportBatches_ImportBatchId",
                table: "WorkStatusHistories",
                column: "ImportBatchId",
                principalTable: "ImportBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityLogs_ImportBatches_ImportBatchId",
                table: "ActivityLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_ImportBatches_ImportBatchId",
                table: "Expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentTransactions_ImportBatches_ImportBatchId",
                table: "PaymentTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_Shipments_ImportBatches_ImportBatchId",
                table: "Shipments");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkStatusHistories_ImportBatches_ImportBatchId",
                table: "WorkStatusHistories");

            migrationBuilder.DropIndex(
                name: "IX_WorkStatusHistories_ImportBatchId",
                table: "WorkStatusHistories");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_ImportBatchId",
                table: "Shipments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shipment_Valid",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTransactions_ImportBatchId",
                table: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_ImportBatchId",
                table: "Expenses");

            migrationBuilder.DropIndex(
                name: "IX_ActivityLogs_ImportBatchId",
                table: "ActivityLogs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActivityLog_ImportBatch",
                table: "ActivityLogs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shipment_Valid",
                table: "Shipments",
                sql: "((\"SourceKind\"='Manual' AND \"ImportBatchId\" IS NULL) OR (\"SourceKind\"='HistoricalImport' AND \"ImportBatchId\" IS NOT NULL)) AND (\"FulfillmentPaused\"=0 OR \"ShipmentStatus\"='NotShipped') AND ((\"NoTrackingNumber\"=1 AND \"TrackingNumber\" IS NULL AND \"ShipmentStatus\"<>'NotShipped') OR (\"NoTrackingNumber\"=0 AND \"TrackingNumber\" IS NOT NULL) OR (\"NoTrackingNumber\" IS NULL AND \"TrackingNumber\" IS NULL AND (\"ShipmentStatus\"='NotShipped' OR \"SourceKind\"='HistoricalImport'))) AND ((\"ShipmentStatus\"='NotShipped' AND \"ShippedDateKnowledge\"='NotApplicable' AND \"ShippedDate\" IS NULL AND \"DeliveredDateKnowledge\"='NotApplicable' AND \"DeliveredDate\" IS NULL) OR (\"ShipmentStatus\"='Unknown' AND \"SourceKind\"='HistoricalImport') OR (\"ShipmentStatus\" IN ('Shipped','Delivered') AND (\"ShippedDateKnowledge\"='Known' AND \"ShippedDate\" IS NOT NULL OR \"ShippedDateKnowledge\"='Unknown' AND \"SourceKind\"='HistoricalImport' AND \"ShippedDate\" IS NULL))) AND (\"DeliveredDate\" IS NULL OR \"ShippedDate\" IS NULL OR \"DeliveredDate\">=\"ShippedDate\")");
        }
    }
}
