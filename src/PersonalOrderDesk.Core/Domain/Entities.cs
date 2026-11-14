namespace PersonalOrderDesk.Domain;

public enum ProjectType { Simulation, SchematicPcbDesign, CodeReport, PhysicalProduction, Other, MixedOrUnknown }
public enum ScheduleCategory { Physical, Digital, Pending }
public enum ValueSource { Default, Manual, Imported }
public enum ShippingRequirement { None, Required, Pending }
public enum SourceKind { Manual, HistoricalImport }
public enum DueDateMode { Auto, Manual, HistoricalUnknown }
public enum WorkStatus { NotStarted, InProgress, Completed, NeedsRework, Cancelled }
public enum CancelledStatus { NotStarted, InProgress, Completed, NeedsRework, Unknown }
public enum CompletionDateKnowledge { Known, Unknown, NotApplicable }
public enum CostConfirmationStatus { Pending, ConfirmedNoCost, ConfirmedComplete }
public enum RelationType { AdditionalWork, RelatedWork }
public enum TransactionType { Receipt, Refund }
public enum RecordStatus { Active, Reversed }
public enum ExpensePaymentStatus { Unpaid, Paid }
public enum DateKnowledge { Known, Unknown, NotApplicable }
public enum ExpenseStatus { Active, Voided }
public enum ShipmentStatus { NotShipped, Shipped, Delivered, Unknown }
public enum HistoryEventType { StatusChanged, Completed, Reopened, Cancelled, Restored }
public enum ImportStatus { Previewed, Imported, Failed }
public enum LogSourceKind { Manual, Import, System }
public sealed class WorkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? BusinessOrderNo { get; set; }
    public required string ProjectName { get; set; }
    public ProjectType ProjectType { get; set; }
    public ScheduleCategory ScheduleCategory { get; set; }
    public ValueSource ScheduleCategorySource { get; set; }
    public ShippingRequirement ShippingRequirement { get; set; }
    public ValueSource ShippingRequirementSource { get; set; }
    public SourceKind SourceKind { get; set; }
    public Guid? ImportBatchId { get; set; }
    public DateOnly? ReceivedDate { get; set; }
    public DueDateMode DueDateMode { get; set; }
    public DateOnly? SpecifiedDueDate { get; set; }
    public DateOnly? EffectiveDueDate { get; set; }
    public WorkStatus WorkStatus { get; set; }
    public CancelledStatus? CancelledFromStatus { get; set; }
    public string? CurrentWork { get; set; }
    public string? NextStep { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public CompletionDateKnowledge CompletedDateKnowledge { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? OrderAmountCents { get; set; }
    public CostConfirmationStatus CostConfirmationStatus { get; set; }
    public string? ProjectPath { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class WorkItemRelation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceWorkItemId { get; set; }
    public Guid TargetWorkItemId { get; set; }
    public RelationType RelationType { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Notes { get; set; }
}
public sealed class PaymentTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TransactionType Type { get; set; }
    public long AmountCents { get; set; }
    public DateOnly? TransactionDate { get; set; }
    public string? PaymentMethod { get; set; }
    public string? ExternalReference { get; set; }
    public string? Notes { get; set; }
    public required string IdempotencyKey { get; set; }
    public string? RequestPayloadHash { get; set; }
    public long Version { get; set; } = 1;
    public RecordStatus Status { get; set; }
    public string? ReversalReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public SourceKind SourceKind { get; set; }
    public Guid? ImportBatchId { get; set; }
    public DateKnowledge DateKnowledge { get; set; }
}
public sealed class PaymentAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentTransactionId { get; set; }
    public Guid WorkItemId { get; set; }
    public long AmountCents { get; set; }
}
public sealed class ExpenseGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public required string Title { get; set; }
    public required string Category { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Expense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExpenseGroupId { get; set; }
    public long AmountCents { get; set; }
    public DateOnly? ExpenseDate { get; set; }
    public DateKnowledge ExpenseDateKnowledge { get; set; }
    public ExpensePaymentStatus PaymentStatus { get; set; }
    public DateOnly? PaidDate { get; set; }
    public DateKnowledge PaidDateKnowledge { get; set; }
    public SourceKind SourceKind { get; set; }
    public Guid? ImportBatchId { get; set; }
    public ExpenseStatus Status { get; set; }
    public string? ReversalReason { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Shipment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public ShipmentStatus ShipmentStatus { get; set; }
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
    public bool? NoTrackingNumber { get; set; }
    public DateOnly? LatestShipDate { get; set; }
    public bool FulfillmentPaused { get; set; }
    public DateOnly? ShippedDate { get; set; }
    public DateKnowledge ShippedDateKnowledge { get; set; }
    public DateOnly? DeliveredDate { get; set; }
    public DateKnowledge DeliveredDateKnowledge { get; set; }
    public SourceKind SourceKind { get; set; }
    public Guid? ImportBatchId { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class WorkStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public WorkStatus OldStatus { get; set; }
    public WorkStatus NewStatus { get; set; }
    public HistoryEventType EventType { get; set; }
    public DateOnly? OccurredDate { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateOnly? PreviousCompletedDate { get; set; }
    public CompletionDateKnowledge PreviousCompletedDateKnowledge { get; set; }
    public DateTimeOffset? PreviousCompletedAt { get; set; }
    public string? Reason { get; set; }
    public SourceKind SourceKind { get; set; }
    public Guid? ImportBatchId { get; set; }
    public bool IsBackfilled { get; set; }
}
public sealed class ActivityLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string EntityType { get; set; }
    public Guid EntityId { get; set; }
    public Guid? WorkItemId { get; set; }
    public required string ActionType { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string Summary { get; set; }
    public string? BeforeData { get; set; }
    public string? AfterData { get; set; }
    public LogSourceKind SourceKind { get; set; }
    public Guid? ImportBatchId { get; set; }
}
public sealed class ImportBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string SourceFileName { get; set; }
    public required string FileFingerprint { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public ImportStatus Status { get; set; }
    public string? Summary { get; set; }
    public string? ErrorSummary { get; set; }
}
public sealed class ImportRowMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImportBatchId { get; set; }
    public required string SheetName { get; set; }
    public int SourceRowNumber { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public int EntityOrdinal { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Setting
{
    public string Key { get; set; } = null!;
    public string Value { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

