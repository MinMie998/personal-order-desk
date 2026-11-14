using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public interface IWorkbenchService
{
    Task<DashboardDto> GetDashboard(CancellationToken cancellationToken = default);
    Task<WorkItemListResultDto> GetList(WorkItemListQueryDto query, CancellationToken cancellationToken = default);
    Task<WorkItemDetailDto?> GetDetail(Guid workItemId, CancellationToken cancellationToken = default);
    Task<WorkItemEditorDto> GetEditor(Guid? workItemId = null, CancellationToken cancellationToken = default);
    Task<WorkItemDetailDto> Create(WorkItemSaveRequestDto request, CancellationToken cancellationToken = default);
    Task<WorkItemDetailDto> Update(Guid workItemId, WorkItemSaveRequestDto request, CancellationToken cancellationToken = default);
    Task<WorkItemDetailDto> CreateAdditionalWork(Guid sourceWorkItemId, WorkItemSaveRequestDto request, CancellationToken cancellationToken = default);
    Task<WorkItemDetailDto> BackfillCompletionDate(Guid workItemId, DateOnly completedDate, long expectedVersion, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    Task<WorkItemDetailDto> ChangeStatus(Guid workItemId, WorkStatusChangeRequestDto request, CancellationToken cancellationToken = default);
    Task<WorkItemDetailDto> Archive(Guid workItemId, ArchiveRequestDto request, CancellationToken cancellationToken = default);
    Task<WorkItemDetailDto> UpdateProgress(Guid id, UpdateProgressRequestDto request, CancellationToken cancellationToken = default);
    Task<WorkItemDeletePreviewDto?> GetDeletePreview(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeletedWorkItemDto>> GetRecycleBin(CancellationToken cancellationToken = default);
    Task Delete(Guid id, long expectedVersion, CancellationToken cancellationToken = default);
    Task RestoreDeleted(Guid id, long expectedVersion, CancellationToken cancellationToken = default);
    Task<WorkbenchSettingsDto> GetSettings(CancellationToken cancellationToken = default);
    Task<WorkbenchSettingsDto> SaveSettings(WorkbenchSettingsDto settings, long expectedVersion, CancellationToken cancellationToken = default);
    Task<BackupResultDto> CreateBackup(string? destinationPath = null, CancellationToken cancellationToken = default);
    ProjectPathCheckDto CheckProjectPath(string? projectPath);
}

public enum WorkUrgency { Overdue, DueToday, PhysicalUpcoming, DigitalUpcoming, NeedsRework, InProgress, NotStarted, NoDueDate }
public enum WorkSectionKind { Overdue, DueToday, Upcoming, InProgress, NeedsInformation, ReadyToShip, AwaitingPayment, HistoricalCompletionReview, HistoricalShipmentReview }
public sealed record WorkItemListQueryDto(string? Search = null, WorkStatus? Status = null, ProjectType? ProjectType = null, ScheduleCategory? ScheduleCategory = null, bool IncludeArchived = false, DateOnly? ReceivedFrom = null, DateOnly? ReceivedTo = null, int Page = 1, int PageSize = 50);
public sealed record WorkItemListResultDto(IReadOnlyList<WorkItemRowDto> Items, int TotalCount, int Page, int PageSize);
public sealed record WorkItemRowDto(Guid Id, string? BusinessOrderNo, string ProjectName, ProjectType ProjectType, ScheduleCategory ScheduleCategory, WorkStatus Status, DateOnly? ReceivedDate, DueDateMode DueDateMode, DateOnly? EffectiveDueDate, WorkUrgency Urgency, int? DaysUntilDue, string PaymentState, long? NetReceivedCents, long? OutstandingCents, string FulfillmentState, string? CurrentWork, string? NextStep, long Version, bool IsArchived);
public sealed record WorkSectionDto(WorkSectionKind Kind, string Title, int Count, IReadOnlyList<WorkItemRowDto> Items);
public sealed record DashboardDto(DateOnly BusinessDate, IReadOnlyList<WorkSectionDto> Sections);
public sealed record WorkItemEditorDto(Guid? Id, string? BusinessOrderNo, string ProjectName, ProjectType ProjectType, ScheduleCategory ScheduleCategory, ValueSource ScheduleCategorySource, ShippingRequirement ShippingRequirement, ValueSource ShippingRequirementSource, DateOnly? ReceivedDate, DueDateMode DueDateMode, DateOnly? SpecifiedDueDate, DateOnly? EffectiveDueDate, WorkStatus Status, string? CurrentWork, string? NextStep, long? OrderAmountCents, string? ProjectPath, string? Notes, long Version, bool IsArchived, DateOnly? CompletedDate = null, CompletionDateKnowledge CompletedDateKnowledge = CompletionDateKnowledge.NotApplicable, DateTimeOffset? CompletedAt = null, CancelledStatus? CancelledFromStatus = null, SourceKind SourceKind = SourceKind.Manual);
public sealed record WorkItemSaveRequestDto(long? ExpectedVersion, string? BusinessOrderNo, string ProjectName, ProjectType ProjectType, ScheduleCategory? ScheduleCategory, ShippingRequirement? ShippingRequirement, DateOnly? ReceivedDate, DueDateMode DueDateMode, DateOnly? SpecifiedDueDate, string? CurrentWork, string? NextStep, long? OrderAmountCents, string? ProjectPath, string? Notes, bool? ApplyProjectTypeDefaults = null, string? OrderAmountChangeReason = null);
public sealed record WorkStatusChangeRequestDto(WorkStatus TargetStatus, long ExpectedVersion, string? Reason = null, WorkStatus? RestoreChoice = null, bool ContinueFulfillment = false);
public sealed record ArchiveRequestDto(bool IsArchived, long ExpectedVersion);
public sealed record WorkStatusHistoryDto(WorkStatus OldStatus, WorkStatus NewStatus, HistoryEventType EventType, DateOnly? OccurredDate, DateTimeOffset? OccurredAt, DateTimeOffset RecordedAt, DateOnly? PreviousCompletedDate, CompletionDateKnowledge PreviousCompletedDateKnowledge, string? Reason, bool IsBackfilled);
public sealed record ActivityLogDto(string ActionType, DateTimeOffset RecordedAt, string Summary, string? BeforeData, string? AfterData);
public sealed record WorkItemRelationDto(Guid RelatedWorkItemId, string RelationType, string? BusinessOrderNo, string ProjectName, WorkStatus Status, string? Notes);
public sealed record WorkItemDetailDto(WorkItemEditorDto WorkItem, string PaymentState, long? NetReceivedCents, long? OutstandingCents, string CostConfirmationState, long? ConfirmedCostCents, string FulfillmentState, ShipmentStatus? ShipmentStatus, bool FulfillmentPaused, IReadOnlyList<WorkStatusHistoryDto> History, IReadOnlyList<ActivityLogDto> Activities, IReadOnlyList<WorkItemRelationDto> Relations);
public sealed record WorkbenchSettingsDto(long Version, int NonPhysicalDefaultDurationDays, int PhysicalReminderLeadDays, int DigitalReminderLeadDays, string BackupDirectory, int BackupRetentionDays, int AffectedAutomaticDueDateCount = 0);
public sealed record BackupResultDto(bool Succeeded, string? DestinationPath, DateTimeOffset? CreatedAt, string? Error);
public sealed record ProjectPathCheckDto(string? ProjectPath, bool IsSpecified, bool Exists, bool IsDirectory, string? Message);

public sealed record WorkItemDeletePreviewDto(WorkItemEditorDto WorkItem, int TransactionCount, long NetReceivedCents,
    int ExpenseCount, long TotalCostCents, ShipmentStatus? ShipmentStatus, int RelationCount, bool HasSharedTransaction);
public sealed record DeletedWorkItemDto(Guid Id, string? BusinessOrderNo, string ProjectName, DateTimeOffset? DeletedAt, long Version);

public sealed record UpdateProgressRequestDto(long ExpectedVersion, string? CurrentWork, string? NextStep, string? ProjectPath);
