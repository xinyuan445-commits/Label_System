using System.Text.Json;

namespace LabelSystem.Api;

public sealed record ExecuteOperationGroupRequest(bool AutoApprove = false);

public sealed record OperationGroupValidationResult(
    string SubmissionGroupId,
    int OperationCount,
    int LineCount,
    bool Valid);

public sealed record GatewayDocumentRequest(
    string ClientRequestId,
    string OperatorId,
    string OperatorName,
    string DeviceId,
    string? DeviceModel,
    string? ApkVersion,
    string? H5Version,
    string? BridgeVersion,
    string? Reason,
    string? ExpectedVersion,
    IReadOnlyDictionary<string, object?> Header,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Lines,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Positions,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> Auxiliary,
    IReadOnlyDictionary<string, object?> Options);

public sealed record GatewayDocumentResult(
    bool Confirmed,
    string? Code,
    string? Message,
    string? VoucherId,
    string? VoucherCode,
    string? Version,
    JsonElement? Document);

public sealed record MaterialOutboundExecutionOperation(
    long Id,
    long RootOperationId,
    string SubmissionGroupId,
    string ClientRequestId,
    string OperatorId,
    string OperationType,
    string ErpDocumentType,
    string Status,
    string ErpStatus,
    string SourceDocumentType,
    string SourceDocumentId,
    string SourceDocumentNo,
    string WarehouseCode,
    string? DeviceId,
    string? DeviceModel,
    string? Reason,
    string ArchiveRelativePath,
    string ArchiveSha256,
    IReadOnlyList<MaterialOutboundExecutionLine> Lines);

public sealed record MaterialOutboundExecutionLine(
    long Id,
    int LineNo,
    string ObjectType,
    long? LabelId,
    long? PalletId,
    long? SourcePalletId,
    string LabelCode,
    int? SourceLineId,
    int? SourceLineNo,
    string MaterialCode,
    string? BatchNo,
    string UnitCode,
    decimal InventoryQtySnapshot,
    decimal RequestedQty,
    string WarehouseCode,
    string? LocationCode,
    string? TargetWarehouseCode,
    string? TargetLocationCode,
    string? Remark);

public sealed class GatewayWriteException(
    int statusCode,
    string code,
    string message,
    bool resultUnknown = false) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public bool ResultUnknown { get; } = resultUnknown;
}
