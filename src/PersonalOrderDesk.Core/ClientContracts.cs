using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Client;

public static class DeskJson
{
    public static readonly JsonSerializerOptions Options = Create();
    private static JsonSerializerOptions Create() { var o = new JsonSerializerOptions(JsonSerializerDefaults.Web); o.Converters.Add(new JsonStringEnumConverter()); return o; }
}
public sealed record OrderMoneySummary(Guid Id, string? Number, string Name, long? Quote, long Received, long Cost, long? Due);
public sealed record WorkspaceState(DateOnly Today, IReadOnlyList<WorkItemRowDto> Orders, IReadOnlyList<OrderMoneySummary> Money,
    IReadOnlyList<DeletedWorkItemDto> Recycle, IReadOnlyList<WorkSectionDto> Sections);
public sealed record ClientOrder(WorkItemDetailDto Detail, WorkItemFinanceDto Money, ShippingDraftDto? Shipping);
public sealed record ServerInfo(string ProductId, int ApiVersion, string InstanceId, string Name, string Schema,string? AppVersion=null,string? LocalDataDirectory=null);
public sealed record SessionRequest(string Password);
public sealed record SessionResponse(string Token, DateTimeOffset ExpiresAt, ServerInfo Server);
public sealed record VersionRequest(long ExpectedVersion);
public sealed record QuickExpenseRequest(long ExpectedVersion, long AmountCents, DateOnly? Date, string Category, string? Note);
public sealed record SnapshotManifest(string ProductId, int FormatVersion, string InstanceId, string Name, DateTimeOffset CreatedAt,
    string[] Migrations, string DatabaseSha256);
public sealed record ClientApiError(string Message);

public interface IWorkspaceGateway
{
    Task<WorkspaceState> State(CancellationToken ct = default);
    Task<ClientOrder> Order(Guid id, CancellationToken ct = default);
    Task<WorkItemEditorDto> Editor(Guid? id = null, CancellationToken ct = default);
    Task<WorkItemDetailDto> SaveOrder(Guid? id, WorkItemSaveRequestDto request, CancellationToken ct = default);
    Task<WorkItemDetailDto> AddWork(Guid sourceId, WorkItemSaveRequestDto request, CancellationToken ct = default);
    Task<WorkItemDetailDto> Progress(Guid id, UpdateProgressRequestDto request, CancellationToken ct = default);
    Task<WorkItemDetailDto> Status(Guid id, WorkStatusChangeRequestDto request, CancellationToken ct = default);
    Task<WorkItemDetailDto> Archive(Guid id, ArchiveRequestDto request, CancellationToken ct = default);
    Task Delete(Guid id, long version, CancellationToken ct = default);
    Task Restore(Guid id, long version, CancellationToken ct = default);
    Task<PaymentTransactionDto> Payment(CreatePaymentRequestDto request, CancellationToken ct = default);
    Task<ExpenseGroupDto> Expense(Guid id, QuickExpenseRequest request, CancellationToken ct = default);
    Task<PaymentTransactionDto> ReversePayment(Guid id, ReverseTransactionRequestDto request, CancellationToken ct = default);
    Task<ExpenseDto> ReverseExpense(Guid id, ReverseExpenseRequestDto request, CancellationToken ct = default);
    Task<ShippingDraftDto> Ship(Guid id, MarkShippedRequestDto request, CancellationToken ct = default);
    Task<ShippingDraftDto> Deliver(Guid id, MarkDeliveredRequestDto request, CancellationToken ct = default);
}
