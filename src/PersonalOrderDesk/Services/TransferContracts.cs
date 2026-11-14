using System.Data;
using PersonalOrderDesk.Domain;
namespace PersonalOrderDesk.Services;

public sealed record TransferSheetPreview(string SheetName, int RowCount, IReadOnlyList<TransferSourceRow> Rows);
public sealed record TransferSourceRow(string SheetName, int RowNumber, IReadOnlyDictionary<string,string?> Values, string RowFingerprint);
public sealed record TransferPreview(string FileName, string FileFingerprint, DateTimeOffset? LastWriteTime, long Length, bool AlreadyImported, IReadOnlyList<TransferSheetPreview> Sheets, IReadOnlyList<TransferRowDecision> Decisions, IReadOnlyList<TransferDifference> Differences, IReadOnlyList<string> Warnings, IReadOnlyList<TransferWorkItemTarget>? ExistingTargets = null, IReadOnlyList<TransferDateIssue>? DateIssues = null);
public sealed record TransferRowDecision(string SheetName, int RowNumber, string EntityType, string? TargetEntityId, bool IsKnown, string? Note,
    bool? ConfirmReceipt = null, ExpensePaymentStatus? ExpensePaymentDecision = null, bool? CostCompletenessConfirmed = null, bool? NoTrackingNumber = null,
    ShippingRequirement? ShippingRequirementDecision = null, WorkStatus? WorkStatusDecision = null, CancelledStatus? CancelledFromStatusDecision = null,
    string? SuggestedWorkItemGroupKey = null, string? NewWorkItemGroupKey = null, string? NewWorkItemProjectName = null,
    DateOnly? ReceivedDateOverride = null, DateOnly? DueDateOverride = null, DateOnly? CompletedDateOverride = null,
    DateOnly? ShippedDateOverride = null, DateOnly? DeliveredDateOverride = null, DateOnly? PaidDateOverride = null);
public sealed record TransferDifference(string SheetName, int RowNumber, string EntityType, string? TargetEntityId, string Field, string? ExistingValue, string? IncomingValue);
public sealed record TransferWorkItemTarget(Guid Id, string? BusinessOrderNo, string ProjectName);
public sealed record TransferDateIssue(string SheetName, int RowNumber, string Field, string? RawValue, string Problem, string SuggestedAction, bool IsCrossRowConflict = false);
public sealed record TransferImportRequest(string FilePath, IReadOnlyList<TransferRowDecision> Decisions, bool ConfirmWholeBatch, string? PreviewFingerprint = null);
public sealed record TransferImportResult(Guid BatchId, int ImportedRows, int SkippedRows, bool WasDuplicate);
public sealed record TransferReportRow(string RecordType, string RecordId, string? BusinessOrderNo, string ProjectName, string Field, string? Value, string Source, string? Explanation);
public interface ITransferService
{
 Task<TransferPreview> PreviewAsync(Stream workbook, string fileName, DateTimeOffset? lastWriteTime = null, CancellationToken cancellationToken = default);
 Task<TransferPreview> PreviewFileAsync(string path, CancellationToken cancellationToken = default);
 Task<TransferImportResult> ConfirmImportAsync(TransferImportRequest request, CancellationToken cancellationToken = default);
 Task<byte[]> ExportReportAsync(CancellationToken cancellationToken = default);
 Task<TransferPreview> PreviewDifferencesAsync(Stream workbook, string fileName, IReadOnlyList<TransferRowDecision> decisions, CancellationToken cancellationToken = default);
}
