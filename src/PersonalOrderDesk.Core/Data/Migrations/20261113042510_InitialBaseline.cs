using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonalOrderDesk.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceFileName = table.Column<string>(type: "TEXT", nullable: false),
                    FileFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorSummary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    TransactionDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PaymentMethod = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalReference = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ReversalReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DateKnowledge = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTransactions", x => x.Id);
                    table.CheckConstraint("CK_Payment_Positive", "\"AmountCents\">0 AND ((\"Status\"='Reversed' AND \"ReversalReason\" IS NOT NULL AND trim(\"ReversalReason\")<>'') OR (\"Status\"='Active' AND \"ReversalReason\" IS NULL)) AND ((\"DateKnowledge\"='Known' AND \"TransactionDate\" IS NOT NULL) OR (\"DateKnowledge\"='Unknown' AND \"SourceKind\"='HistoricalImport' AND \"TransactionDate\" IS NULL)) AND ((\"SourceKind\"='Manual' AND \"ImportBatchId\" IS NULL) OR (\"SourceKind\"='HistoricalImport' AND \"ImportBatchId\" IS NOT NULL))");
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ImportRowMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SheetName = table.Column<string>(type: "TEXT", nullable: false),
                    SourceRowNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", nullable: false),
                    EntityOrdinal = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRowMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportRowMappings_ImportBatches_ImportBatchId",
                        column: x => x.ImportBatchId,
                        principalTable: "ImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BusinessOrderNo = table.Column<string>(type: "TEXT", nullable: true),
                    ProjectName = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectType = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduleCategory = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduleCategorySource = table.Column<string>(type: "TEXT", nullable: false),
                    ShippingRequirement = table.Column<string>(type: "TEXT", nullable: false),
                    ShippingRequirementSource = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReceivedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DueDateMode = table.Column<string>(type: "TEXT", nullable: false),
                    SpecifiedDueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EffectiveDueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    WorkStatus = table.Column<string>(type: "TEXT", nullable: false),
                    CancelledFromStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    CurrentWork = table.Column<string>(type: "TEXT", nullable: true),
                    NextStep = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CompletedDateKnowledge = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OrderAmountCents = table.Column<long>(type: "INTEGER", nullable: true),
                    CostConfirmationStatus = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectPath = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.Id);
                    table.CheckConstraint("CK_WorkItem_Core", "(\"OrderAmountCents\" IS NULL OR \"OrderAmountCents\">=0) AND \"Version\">0 AND ((\"SourceKind\"='Manual' AND \"ImportBatchId\" IS NULL AND \"ReceivedDate\" IS NOT NULL) OR (\"SourceKind\"='HistoricalImport' AND \"ImportBatchId\" IS NOT NULL)) AND ((\"DueDateMode\"='Auto' AND \"SpecifiedDueDate\" IS NULL) OR (\"DueDateMode\"='Manual' AND \"SpecifiedDueDate\" IS NOT NULL AND \"EffectiveDueDate\"=\"SpecifiedDueDate\") OR (\"DueDateMode\"='HistoricalUnknown' AND \"SourceKind\"='HistoricalImport' AND \"SpecifiedDueDate\" IS NULL AND \"EffectiveDueDate\" IS NULL)) AND ((\"WorkStatus\"='Completed' AND ((\"CompletedDateKnowledge\"='Known' AND \"CompletedDate\" IS NOT NULL) OR (\"CompletedDateKnowledge\"='Unknown' AND \"SourceKind\"='HistoricalImport' AND \"CompletedDate\" IS NULL AND \"CompletedAt\" IS NULL))) OR (\"WorkStatus\"<>'Completed' AND \"CompletedDateKnowledge\"='NotApplicable' AND \"CompletedDate\" IS NULL AND \"CompletedAt\" IS NULL)) AND ((\"WorkStatus\"='Cancelled' AND \"CancelledFromStatus\" IS NOT NULL) OR (\"WorkStatus\"<>'Cancelled' AND \"CancelledFromStatus\" IS NULL)) AND (\"IsArchived\"=0 OR \"WorkStatus\" IN ('Completed','Cancelled'))");
                    table.ForeignKey(
                        name: "FK_WorkItems_ImportBatches_ImportBatchId",
                        column: x => x.ImportBatchId,
                        principalTable: "ImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActionType = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeData = table.Column<string>(type: "TEXT", nullable: true),
                    AfterData = table.Column<string>(type: "TEXT", nullable: true),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityLogs_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseGroups_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PaymentTransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAllocations", x => x.Id);
                    table.CheckConstraint("CK_Allocation_Positive", "\"AmountCents\">0");
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_PaymentTransactions_PaymentTransactionId",
                        column: x => x.PaymentTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Shipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShipmentStatus = table.Column<string>(type: "TEXT", nullable: false),
                    Carrier = table.Column<string>(type: "TEXT", nullable: true),
                    TrackingNumber = table.Column<string>(type: "TEXT", nullable: true),
                    NoTrackingNumber = table.Column<bool>(type: "INTEGER", nullable: true),
                    LatestShipDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    FulfillmentPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShippedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ShippedDateKnowledge = table.Column<string>(type: "TEXT", nullable: false),
                    DeliveredDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DeliveredDateKnowledge = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipments", x => x.Id);
                    table.CheckConstraint("CK_Shipment_Valid", "((\"SourceKind\"='Manual' AND \"ImportBatchId\" IS NULL) OR (\"SourceKind\"='HistoricalImport' AND \"ImportBatchId\" IS NOT NULL)) AND (\"FulfillmentPaused\"=0 OR \"ShipmentStatus\"='NotShipped') AND ((\"NoTrackingNumber\"=1 AND \"TrackingNumber\" IS NULL AND \"ShipmentStatus\"<>'NotShipped') OR (\"NoTrackingNumber\"=0 AND \"TrackingNumber\" IS NOT NULL) OR (\"NoTrackingNumber\" IS NULL AND \"TrackingNumber\" IS NULL AND (\"ShipmentStatus\"='NotShipped' OR \"SourceKind\"='HistoricalImport'))) AND ((\"ShipmentStatus\"='NotShipped' AND \"ShippedDateKnowledge\"='NotApplicable' AND \"ShippedDate\" IS NULL AND \"DeliveredDateKnowledge\"='NotApplicable' AND \"DeliveredDate\" IS NULL) OR (\"ShipmentStatus\"='Unknown' AND \"SourceKind\"='HistoricalImport') OR (\"ShipmentStatus\" IN ('Shipped','Delivered') AND (\"ShippedDateKnowledge\"='Known' AND \"ShippedDate\" IS NOT NULL OR \"ShippedDateKnowledge\"='Unknown' AND \"SourceKind\"='HistoricalImport' AND \"ShippedDate\" IS NULL))) AND (\"DeliveredDate\" IS NULL OR \"ShippedDate\" IS NULL OR \"DeliveredDate\">=\"ShippedDate\")");
                    table.ForeignKey(
                        name: "FK_Shipments_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceWorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetWorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelationType = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemRelations", x => x.Id);
                    table.CheckConstraint("CK_Relation_NoSelf", "\"SourceWorkItemId\"<>\"TargetWorkItemId\"");
                    table.ForeignKey(
                        name: "FK_WorkItemRelations_WorkItems_SourceWorkItemId",
                        column: x => x.SourceWorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItemRelations_WorkItems_TargetWorkItemId",
                        column: x => x.TargetWorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkStatusHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OldStatus = table.Column<string>(type: "TEXT", nullable: false),
                    NewStatus = table.Column<string>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PreviousCompletedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PreviousCompletedDateKnowledge = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousCompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsBackfilled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkStatusHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkStatusHistories_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Expenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpenseGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpenseDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ExpenseDateKnowledge = table.Column<string>(type: "TEXT", nullable: false),
                    PaymentStatus = table.Column<string>(type: "TEXT", nullable: false),
                    PaidDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PaidDateKnowledge = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ReversalReason = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Expenses", x => x.Id);
                    table.CheckConstraint("CK_Expense_Valid", "\"AmountCents\">0 AND ((\"ExpenseDateKnowledge\"='Known' AND \"ExpenseDate\" IS NOT NULL) OR (\"ExpenseDateKnowledge\"='Unknown' AND \"SourceKind\"='HistoricalImport' AND \"ExpenseDate\" IS NULL)) AND ((\"PaymentStatus\"='Unpaid' AND \"PaidDate\" IS NULL AND \"PaidDateKnowledge\"='NotApplicable') OR (\"PaymentStatus\"='Paid' AND ((\"PaidDateKnowledge\"='Known' AND \"PaidDate\" IS NOT NULL) OR (\"PaidDateKnowledge\"='Unknown' AND \"SourceKind\"='HistoricalImport' AND \"PaidDate\" IS NULL)))) AND ((\"Status\"='Voided' AND \"ReversalReason\" IS NOT NULL) OR (\"Status\"='Active' AND \"ReversalReason\" IS NULL)) AND ((\"SourceKind\"='Manual' AND \"ImportBatchId\" IS NULL) OR (\"SourceKind\"='HistoricalImport' AND \"ImportBatchId\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_Expenses_ExpenseGroups_ExpenseGroupId",
                        column: x => x.ExpenseGroupId,
                        principalTable: "ExpenseGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_WorkItemId",
                table: "ActivityLogs",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseGroups_WorkItemId",
                table: "ExpenseGroups",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_ExpenseGroupId",
                table: "Expenses",
                column: "ExpenseGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_FileFingerprint",
                table: "ImportBatches",
                column: "FileFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportRowMappings_ImportBatchId_SheetName_SourceRowNumber_EntityType_EntityOrdinal",
                table: "ImportRowMappings",
                columns: new[] { "ImportBatchId", "SheetName", "SourceRowNumber", "EntityType", "EntityOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_PaymentTransactionId_WorkItemId",
                table: "PaymentAllocations",
                columns: new[] { "PaymentTransactionId", "WorkItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_WorkItemId",
                table: "PaymentAllocations",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_IdempotencyKey",
                table: "PaymentTransactions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_WorkItemId",
                table: "Shipments",
                column: "WorkItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemRelations_SourceWorkItemId_TargetWorkItemId_RelationType",
                table: "WorkItemRelations",
                columns: new[] { "SourceWorkItemId", "TargetWorkItemId", "RelationType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemRelations_TargetWorkItemId",
                table: "WorkItemRelations",
                column: "TargetWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_BusinessOrderNo",
                table: "WorkItems",
                column: "BusinessOrderNo");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_EffectiveDueDate",
                table: "WorkItems",
                column: "EffectiveDueDate");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ImportBatchId",
                table: "WorkItems",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_IsArchived",
                table: "WorkItems",
                column: "IsArchived");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ReceivedDate",
                table: "WorkItems",
                column: "ReceivedDate");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_WorkStatus",
                table: "WorkItems",
                column: "WorkStatus");

            migrationBuilder.CreateIndex(
                name: "IX_WorkStatusHistories_WorkItemId",
                table: "WorkStatusHistories",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityLogs");

            migrationBuilder.DropTable(
                name: "Expenses");

            migrationBuilder.DropTable(
                name: "ImportRowMappings");

            migrationBuilder.DropTable(
                name: "PaymentAllocations");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Shipments");

            migrationBuilder.DropTable(
                name: "WorkItemRelations");

            migrationBuilder.DropTable(
                name: "WorkStatusHistories");

            migrationBuilder.DropTable(
                name: "ExpenseGroups");

            migrationBuilder.DropTable(
                name: "PaymentTransactions");

            migrationBuilder.DropTable(
                name: "WorkItems");

            migrationBuilder.DropTable(
                name: "ImportBatches");
        }
    }
}
