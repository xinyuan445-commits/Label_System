using System.Text.Json;

namespace LabelSystem.Api;

public sealed record ApiEnvelope<T>(
    bool Success,
    string Code,
    string Message,
    T? Data,
    string RequestId,
    DateTimeOffset ServerTime);

public static class ApiResults
{
    public static IResult Ok<T>(HttpContext context, T data, string message = "") =>
        Results.Json(new ApiEnvelope<T>(
            true,
            "OK",
            message,
            data,
            RequestContext.Id(context),
            BusinessClock.Now));

    public static IResult Error(HttpContext context, int status, string code, string message) =>
        Results.Json(new ApiEnvelope<object>(
            false,
            code,
            message,
            null,
            RequestContext.Id(context),
            BusinessClock.Now), statusCode: status);
}

public sealed class ApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResult(
    string Token,
    DateTimeOffset ExpiresAt,
    SessionUser User,
    int ExpiresInSeconds);
public sealed record SessionUser(string Id, string Name, string[] Roles);
public sealed record ManagementAccessRequest(string Password);
public sealed record ManagementAccessResult(DateTimeOffset ExpiresAt, int ExpiresInSeconds);

public sealed record UserItem(
    string Id,
    string LoginName,
    string Name,
    string AuthSource,
    string? ErpOperatorCode,
    string? ErpOperatorName,
    bool IsAdmin,
    bool IsEnabled,
    DateTimeOffset? LastLoginAt,
    int FailedAttemptCount,
    DateTimeOffset? LockedUntil,
    DateTimeOffset? PasswordChangedAt,
    string RowVersion);

public sealed record CreateUserRequest(
    string Id,
    string LoginName,
    string Name,
    string AuthSource,
    string? ErpOperatorCode,
    string? ErpOperatorName,
    bool IsAdmin,
    bool IsEnabled,
    string? Password);

public sealed record UpdateUserRequest(
    string Name,
    string? ErpOperatorCode,
    string? ErpOperatorName,
    bool IsAdmin,
    bool IsEnabled,
    string ExpectedRowVersion);

public sealed record ResetUserPasswordRequest(string Password);

public sealed record ErpOperator(string Code, string Name);

public sealed record UserFunctionPermissionItem(
    string FunctionCode,
    string FunctionName,
    string FunctionKind,
    string GroupCode,
    string GroupName,
    int GroupSort,
    int FunctionSort,
    string State,
    bool IsEffective,
    JsonElement? DataScope,
    string? RowVersion);

public sealed record UpdateUserFunctionPermissionRequest(
    string State,
    JsonElement? DataScope,
    string? ExpectedRowVersion);

public sealed record UserAdminAuditContext(
    string ActorUserId,
    string RequestId,
    string? ClientIp,
    string? UserAgent);

public sealed record UserAdminAuditItem(
    long Id,
    string Action,
    string ActorUserId,
    string ActorName,
    string TargetUserId,
    string TargetName,
    string RequestId,
    DateTimeOffset CreatedAt);

public sealed record UserPreferenceItem(
    string Key,
    JsonElement Value,
    int Version,
    string? UpdatedByDevice,
    DateTimeOffset UpdatedAt,
    string RowVersion);

public sealed record UpdateUserPreferenceRequest(
    JsonElement Value,
    string? DeviceId,
    string? ExpectedRowVersion);

public sealed record FunctionItem(
    string Code,
    string Name,
    string Kind,
    string? ParentCode,
    string GroupCode,
    string GroupName,
    int GroupSort,
    int FunctionSort,
    string? IconCode,
    bool IsEnabled,
    string RowVersion);

public sealed record UpdateFunctionRequest(bool IsEnabled, string ExpectedRowVersion);

public sealed record SettingItem(
    string Scope,
    string? DocumentType,
    string Key,
    string Name,
    JsonElement Value,
    bool IsEnabled,
    int Version,
    string RowVersion);

public sealed record UpdateSettingRequest(
    string? DocumentType,
    string Name,
    JsonElement Value,
    bool IsEnabled,
    string? ExpectedRowVersion);

public sealed record LabelCodeRuleVersionItem(
    long Id,
    int Version,
    string Status,
    JsonElement Spec,
    string SpecSha256,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    string? ActivatedBy,
    DateTimeOffset? ActivatedAt,
    string? RetiredBy,
    DateTimeOffset? RetiredAt,
    string RowVersion);

public sealed record LabelCodeRuleItem(
    long Id,
    string Code,
    string Name,
    string ObjectType,
    int Priority,
    bool IsEnabled,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    string RowVersion,
    IReadOnlyList<LabelCodeRuleVersionItem> Versions);

public sealed record CreateLabelCodeRuleRequest(
    string Code,
    string Name,
    string ObjectType,
    int Priority,
    JsonElement Spec);

public sealed record SaveLabelCodeRuleDraftRequest(
    string Name,
    int Priority,
    bool IsEnabled,
    JsonElement Spec,
    string ExpectedRuleRowVersion,
    string? ExpectedVersionRowVersion);

public sealed record ChangeLabelCodeRuleVersionRequest(
    string ExpectedRuleRowVersion,
    string ExpectedVersionRowVersion);

public sealed record LabelCodePreviewRequest(
    JsonElement Spec,
    JsonElement Values,
    string? Symbology);

public sealed record LabelCodePreviewResult(
    string Code,
    int Length,
    IReadOnlyDictionary<string, string> Parsed,
    IReadOnlyList<string> Symbologies,
    string SpecSha256);

public sealed record ParseLabelCodeRequest(
    string Code,
    string? Symbology);

public sealed record ParsedLabelCodeResult(
    long RuleId,
    string RuleCode,
    string RuleName,
    string ObjectType,
    long VersionId,
    int Version,
    string VersionStatus,
    IReadOnlyDictionary<string, string> Fields,
    string NormalizedCode,
    string? Symbology);

public sealed record LabelCodeRuleCatalog(
    string CatalogVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<LabelCodeRuleItem> Rules);

public sealed record InventoryObjectLine(
    string LabelCode,
    string ObjectType,
    string MaterialCode,
    string MaterialName,
    string BatchNo,
    string UnitCode,
    string UnitName,
    string WarehouseCode,
    string WarehouseName,
    string LocationCode,
    string LocationName,
    decimal AvailableQty,
    string? PalletCode,
    string Status,
    string RowVersion);

public sealed record InventoryLookupResult(
    IReadOnlyList<InventoryObjectLine> Lines,
    InventoryObjectLine? Pallet);

public sealed record TraceBackSnapshotRequest(IReadOnlyList<string> Codes);

public sealed record TraceBackAnomaly(string Code, string Message);

public sealed record TraceBackLabelSnapshot(
    string Code,
    string? SerialNo,
    decimal? Balance,
    long? LatestFlowId,
    string? GoodsUuid,
    string? OrderUuid,
    string? MaterialCode,
    string? MaterialName,
    string? BatchNo,
    string? UnitCode,
    string? UnitName,
    string? WarehouseCode,
    string? WarehouseName,
    string? LocationCode,
    string? LocationName,
    string? SourceBusinessType,
    string? PdaDocumentNo,
    string? ErpDocumentNo,
    string? IdentityHash,
    string Status,
    IReadOnlyList<TraceBackAnomaly> Anomalies);

public sealed record ApprovalActionRequest(
    string ClientRequestId,
    string ExpectedVersion,
    string? Reason,
    string? DeviceId,
    string? DeviceModel,
    string? ApkVersion,
    string? H5Version,
    string? BridgeVersion);

public sealed record MaterialOutboundPrepareRequest(
    string ClientRequestId,
    string OperatorId,
    string OperatorName,
    string? DeviceId,
    string? DeviceCode,
    string? DeviceModel,
    string? Reason,
    DateOnly DocumentDate,
    string DocumentDirection,
    string? DepartmentCode,
    string? CategoryCode,
    string? ProductionBatch,
    JsonElement? AdditionalFields,
    IReadOnlyList<MaterialOutboundAllocationRequest> Lines);

public sealed record MaterialOutboundAllocationRequest(
    string ObjectType,
    string LabelCode,
    string? SourcePalletCode,
    string SourceType,
    int SourceDocumentId,
    string SourceDocumentNo,
    int SourceLineId,
    int? SourceLineNo,
    int RequirementId,
    string ProductCode,
    string MaterialCode,
    string UnitCode,
    decimal NormalQty,
    decimal OverQty,
    string? Remark,
    JsonElement? AdditionalFields,
    string? MaterialName = null,
    string? BatchNo = null,
    string? WarehouseCode = null,
    string? LocationCode = null)
{
    public decimal RequestedQty => NormalQty + OverQty;
}

public sealed record PreparedOperation(
    long Id,
    string OperationNo,
    string SourceDocumentNo,
    string WarehouseCode,
    int LineCount,
    decimal TotalQty,
    string Status = "PENDING",
    string ErpStatus = "PENDING",
    string ApprovalStatus = "NOT_REQUIRED",
    string? ErpDocumentNo = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? CompletedAt = null);

public sealed record MaterialOutboundPrepareResult(
    string SubmissionGroupId,
    string Status,
    bool U8WriteEnabled,
    IReadOnlyList<PreparedOperation> Operations);

public sealed record DocumentPrepareRequest(
    string ClientRequestId,
    string OperatorId,
    string OperatorName,
    string? DeviceId,
    string? DeviceCode,
    string? DeviceModel,
    string? Reason,
    DocumentPrepareHeader Header,
    IReadOnlyList<DocumentPrepareLine> Lines);

public sealed record DocumentPrepareHeader(
    DateOnly DocumentDate,
    string DocumentDirection,
    string? PartnerCode,
    string? WarehouseCode,
    string? TargetWarehouseCode,
    string? SourceLocationCode,
    string? TargetLocationCode,
    string? DepartmentCode,
    string? CategoryCode,
    string? ProductionBatch,
    string? SourceType,
    string? SourceDocumentNo,
    JsonElement? AdditionalFields);

public sealed record DocumentPrepareLine(
    string ObjectType,
    string LabelCode,
    string? SourcePalletCode,
    string MaterialCode,
    string UnitCode,
    decimal RequestedQty,
    string? Remark,
    string? SourceType,
    int? SourceDocumentId,
    string? SourceDocumentNo,
    int? SourceLineId,
    int? SourceLineNo,
    string? SourceProductCode,
    JsonElement? AdditionalFields,
    string? MaterialName = null,
    string? BatchNo = null,
    string? WarehouseCode = null,
    string? LocationCode = null,
    bool RequiresBatch = false,
    string? TargetWarehouseCode = null,
    string? TargetLocationCode = null);

public sealed record SourceDocumentLine(
    string SourceType,
    int SourceDocumentId,
    string SourceDocumentNo,
    int SourceLineId,
    int? SourceLineNo,
    DateTime? DocumentDate,
    string ProductCode,
    string ProductName,
    decimal OrderQuantity,
    string DepartmentCode,
    int MaterialCount);

public sealed record OperationGroupResult(
    string SubmissionGroupId,
    string DocumentType,
    string Status,
    bool IsTerminal,
    string OperatorId,
    string OperatorName,
    string SourceDocumentNo,
    int LineCount,
    decimal TotalQty,
    DateTimeOffset? LockExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<PreparedOperation> Operations,
    IReadOnlyList<IssuedLabelItem>? IssuedLabels = null);

public sealed record IssuedLabelItem(
    long Id,
    string? Code,
    string MaterialCode,
    string? MaterialName,
    string? BatchNo,
    string UnitCode,
    decimal Quantity,
    string WarehouseCode,
    string? LocationCode,
    string Status);

public sealed record OperationGroupListResult(
    int Skip,
    int Take,
    int Total,
    IReadOnlyList<OperationGroupResult> Items);

public sealed record SourceDocumentLinesRequest(string SourceType, IReadOnlyList<int> SourceDocumentIds);
public sealed record MaterialRequirementRequest(string SourceType, IReadOnlyList<int> SourceLineIds);

public sealed record MaterialRequirementLine(
    string SourceType,
    int SourceDocumentId,
    string SourceDocumentNo,
    int SourceLineId,
    int? SourceLineNo,
    int RequirementId,
    string ProductCode,
    string ProductName,
    string MaterialCode,
    string MaterialName,
    string DepartmentCode,
    string OperationSequence,
    bool RequiresBatch,
    decimal PlannedQuantity,
    decimal IssuedQuantity,
    decimal RemainingQuantity);

public sealed record GatewayEnvelope<T>(
    bool Success,
    string? Code,
    string? Message,
    T? Data,
    string? RequestId,
    DateTimeOffset? ServerTime);

public sealed record RdStyleSourceItem(
    string Code,
    string Name,
    byte Grade,
    string Direction,
    bool IsTerminal,
    string? OppositeCode,
    bool IsRetail,
    string? SourceRowVersion);

public sealed record RdStyleCacheItem(
    string Code,
    string Name,
    byte Grade,
    string Direction,
    bool IsTerminal,
    string? OppositeCode,
    bool IsRetail,
    bool IsActive,
    DateTimeOffset UpdatedAt);

public sealed record WarehousePositionSourceItem(
    string WarehouseCode,
    string WarehouseName,
    string? PositionCode,
    string? PositionName);

public sealed record WarehousePositionCacheItem(
    string WarehouseCode,
    string WarehouseName,
    string? PositionCode,
    string? PositionName,
    DateTimeOffset UpdatedAt);

public sealed record DepartmentSourceItem(
    string Code,
    string Name);

public sealed record DepartmentCacheItem(
    string Code,
    string Name,
    DateTimeOffset UpdatedAt);

public sealed record MaterialSourceItem(
    string Code,
    string Name,
    string Specification,
    string UnitCode,
    bool RequiresBatch);

public sealed record DocumentTypeSourceItem(
    string Code,
    string Name,
    string? RdStyleCode,
    bool IsDefault,
    string? SourceRowVersion);

public sealed record CurrencySourceItem(
    string Code,
    string Name,
    bool IsLocal,
    bool UsesExchangeRate,
    byte DecimalDigits,
    decimal? LatestRate,
    int? RateYear,
    byte? RatePeriod,
    string? RateDate,
    string? SourceRowVersion);

public sealed record DocumentMasterDataSource(
    IReadOnlyList<DocumentTypeSourceItem> PurchaseTypes,
    IReadOnlyList<DocumentTypeSourceItem> SalesTypes,
    IReadOnlyList<CurrencySourceItem> Currencies);

public sealed record DocumentTypeCacheItem(
    string Code,
    string Name,
    string? RdStyleCode,
    bool IsDefault,
    DateTimeOffset UpdatedAt);

public sealed record CurrencyCacheItem(
    string Code,
    string Name,
    bool IsLocal,
    bool UsesExchangeRate,
    byte DecimalDigits,
    decimal? LatestRate,
    int? RateYear,
    byte? RatePeriod,
    string? RateDate,
    DateTimeOffset UpdatedAt);

public sealed record DataSyncStatusItem(
    string Key,
    string Name,
    string Status,
    int ItemCount,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCompletedAt,
    DateTimeOffset? LastSuccessAt,
    string? SyncedBy,
    string? ErrorMessage);

public sealed record DataSyncResult(
    string Key,
    string Status,
    int ItemCount,
    DateTimeOffset CompletedAt);

public sealed record DraftSummaryRequest(
    DateOnly? DocumentDate,
    string? SourceTypeLabel,
    string? SourceDocumentNo,
    string? WarehouseCode,
    string? DepartmentCode,
    int LineCount,
    decimal TotalQuantity);

public sealed record SyncDocumentDraftRequest(
    string DeviceId,
    string? DeviceName,
    string DocumentType,
    string BusinessStatus,
    int SchemaVersion,
    long ClientRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? OperationGroupId,
    DraftSummaryRequest Summary,
    JsonElement Payload);

public sealed record DeleteDocumentDraftRequest(
    string DeviceId,
    long ClientRevision);

public sealed record DocumentDraftItem(
    string Id,
    string OwnerUserId,
    string OwnerName,
    string DeviceId,
    string? DeviceName,
    string DocumentType,
    string BusinessStatus,
    int SchemaVersion,
    long ClientRevision,
    DateOnly? DocumentDate,
    string? SourceTypeLabel,
    string? SourceDocumentNo,
    string? WarehouseCode,
    string? DepartmentCode,
    int LineCount,
    decimal TotalQuantity,
    string? OperationGroupId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastSyncedAt,
    DateTimeOffset? DeletedAt,
    string RowVersion,
    JsonElement? Payload);

public sealed record DeviceHeartbeatRequest(
    string DeviceId,
    string? DeviceName,
    string? DeviceModel,
    string? AppVersion,
    int LocalDraftCount,
    int PendingSyncCount,
    int FailedSyncCount,
    DateTimeOffset? LastSuccessfulSyncAt);

public sealed record ClientDeviceStateItem(
    string UserId,
    string UserName,
    string DeviceId,
    string? DeviceName,
    string? DeviceModel,
    string? AppVersion,
    int LocalDraftCount,
    int PendingSyncCount,
    int FailedSyncCount,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastSuccessfulSyncAt);
