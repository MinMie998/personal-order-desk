namespace PersonalOrderDesk.Client;
public sealed record SyncProtectionRequest(Guid OperationId);
public sealed record SyncProtectionReceipt(Guid OperationId,string BackupFile,DateTimeOffset CreatedAt);
