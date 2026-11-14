using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Services;

public interface IShippingService
{
    Task<IReadOnlyList<ShippingQueueItemDto>> GetQueue(ShippingQueueFilter filter = ShippingQueueFilter.All, CancellationToken ct = default);
    Task<ShippingDraftDto> GetDraft(Guid workItemId, CancellationToken ct = default);
    Task<ShippingDraftDto> PrepareHistoryReview(Guid workItemId, long expectedVersion, CancellationToken ct = default);
    Task<ShippingDraftDto> SaveDraft(Guid workItemId, ShippingDraftRequestDto request, CancellationToken ct = default);
    Task<ShippingDraftDto> MarkShipped(Guid workItemId, MarkShippedRequestDto request, CancellationToken ct = default);
    Task<ShippingDraftDto> MarkDelivered(Guid workItemId, MarkDeliveredRequestDto request, CancellationToken ct = default);
    Task<ShippingDraftDto> ReconcileHistory(Guid workItemId, ReconcileShipmentRequestDto request, CancellationToken ct = default);
    Task<ShippingDraftDto> BackfillDates(Guid workItemId, BackfillShipmentDatesRequestDto request, CancellationToken ct = default);
}
public enum ShippingQueueFilter { All, ToShip, NeedsConfirmation, History }
public sealed record ShippingQueueItemDto(Guid WorkItemId, string ProjectName, string? BusinessOrderNo, WorkStatus WorkStatus, ShippingRequirement ShippingRequirement, SourceKind SourceKind, ShipmentStatus? ShipmentStatus, bool FulfillmentPaused, DateOnly? LatestShipDate, string ShippingUrgency, string? Carrier, string? TrackingNumber, bool? NoTrackingNumber, long Version, bool IsArchived);
public sealed record ShippingDraftDto(Guid WorkItemId, ShipmentStatus ShipmentStatus, string? Carrier, string? TrackingNumber, bool? NoTrackingNumber, DateOnly? LatestShipDate, bool FulfillmentPaused, DateOnly? ShippedDate, DateKnowledge ShippedDateKnowledge, DateOnly? DeliveredDate, DateKnowledge DeliveredDateKnowledge, long Version, bool IsArchived, ShippingRequirement ShippingRequirement, SourceKind SourceKind, WorkStatus WorkStatus);
public sealed record ShippingDraftRequestDto(long ExpectedVersion, string? Carrier, string? TrackingNumber, bool? NoTrackingNumber, DateOnly? LatestShipDate, bool FulfillmentPaused);
public sealed record MarkShippedRequestDto(long ExpectedVersion, DateOnly ShippedDate, string? Carrier, string? TrackingNumber, bool? NoTrackingNumber);
public sealed record MarkDeliveredRequestDto(long ExpectedVersion, DateOnly DeliveredDate);
public sealed record ReconcileShipmentRequestDto(long ExpectedVersion, ShipmentStatus Status, DateOnly? ShippedDate, DateKnowledge ShippedDateKnowledge, DateOnly? DeliveredDate, DateKnowledge DeliveredDateKnowledge, string? Carrier, string? TrackingNumber, bool? NoTrackingNumber, string Reason);
public sealed record BackfillShipmentDatesRequestDto(long ExpectedVersion, DateOnly? ShippedDate, DateOnly? DeliveredDate);
