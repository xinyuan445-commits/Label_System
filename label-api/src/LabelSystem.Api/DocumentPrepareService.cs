using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed record DocumentPrepareDefinition(
    string DocumentType,
    string Title,
    string OperationType,
    string ErpDocumentType,
    string OperationPrefix,
    bool RequireWarehouse,
    bool RequireTargetWarehouse,
    bool RequireTargetLocation,
    bool RequireDepartment,
    bool RequireCategory,
    bool WholePalletMove,
    bool AllowZeroQuantity,
    bool SupportsSource,
    bool GroupByWarehouse,
    bool MatchCurrentWarehouse);

public sealed class DocumentPrepareService(
    LabelSystemRepository settings,
    DocumentPrepareRepository operations,
    U8GatewayClient u8,
    OperationArchiveService archive,
    IOptionsMonitor<LabelSystemOptions> options)
{
    private static readonly IReadOnlySet<string> MappedHeaderFields = new HashSet<string>(
        [
            "header.documentDate", "header.partnerCode", "header.warehouseCode",
            "header.targetWarehouseCode", "header.sourceLocationCode", "header.targetLocationCode",
            "header.departmentCode", "header.categoryCode", "header.productionBatch", "header.remark"
        ], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> MappedBodyFields = new HashSet<string>(
        [
            "body.labelCode", "body.objectType", "body.materialCode", "body.unitCode",
            "body.quantity", "body.targetWarehouseCode", "body.targetLocationCode"
        ], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, DocumentPrepareDefinition> Definitions =
        new Dictionary<string, DocumentPrepareDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["transfer-order"] = new(
                "transfer-order", "调拨单", "TRANSFER", "transfer", "TR",
                true, true, false, true, true, true, false, false, false, true),
            ["location-adjustment"] = new(
                "location-adjustment", "货位调整", "LOCATION_ADJUSTMENT", "location-adjustment", "LA",
                true, false, true, false, false, true, false, false, false, true),
            ["inventory-count"] = new(
                "inventory-count", "盘点单", "INVENTORY_COUNT", "inventory-count", "IC",
                true, false, false, true, true, false, true, false, false, true),
            ["purchase-arrival"] = new(
                "purchase-arrival", "采购到货", "PURCHASE_ARRIVAL", "purchase-arrival", "PA",
                false, false, false, true, true, false, false, true, false, false),
            ["outsourcing-arrival"] = new(
                "outsourcing-arrival", "委外到货", "SUBCONTRACT_ARRIVAL", "subcontract-arrival", "OA",
                false, false, false, true, true, false, false, true, false, false),
            ["purchase-inbound"] = new(
                "purchase-inbound", "采购入库", "PURCHASE_RECEIPT", "purchase-receipt", "PI",
                true, false, false, true, true, false, false, true, false, false),
            ["delivery-note"] = new(
                "delivery-note", "发货单", "SALES_DELIVERY", "sales-delivery", "SD",
                true, false, false, true, true, false, false, true, false, true),
            ["sales-outbound"] = new(
                "sales-outbound", "销售出库单", "SALES_OUTBOUND", "sales-outbound", "SO",
                true, false, false, true, true, false, false, true, false, true),
            ["material-outbound"] = new(
                "material-outbound", "材料出库单", "MATERIAL_OUTBOUND", "material-outbound", "MO",
                false, false, false, true, true, false, false, false, true, false),
        };

    public async Task<MaterialOutboundPrepareResult> PrepareAsync(
        string documentType,
        DocumentPrepareRequest request,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!Definitions.TryGetValue(documentType, out var definition))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DOCUMENT_TYPE_NOT_READY", "当前单据尚未开放预提交。");

        var hasSource = HasSource(request);
        var requiredFields = await settings.GetRequiredDocumentFieldsAsync(documentType, cancellationToken);
        ValidateBasicRequest(request, definition, requiredFields, hasSource);
        var reasonRequired = await settings.GetBooleanSettingAsync(
            "reason_required", null, false, cancellationToken);
        if (reasonRequired && string.IsNullOrWhiteSpace(request.Reason))
            throw new ApiException(StatusCodes.Status400BadRequest, "REASON_REQUIRED", "请输入原因。");

        if (hasSource)
            await ValidateSourceLinesAsync(request.Lines, cancellationToken);

        var payloadJson = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var payloadHash = Hashing.Sha256(payloadJson);
        var archiveResult = await archive.CreateAsync(
            definition.DocumentType, definition.Title, "prepare", requestId, request, cancellationToken);

        return await operations.PrepareAsync(
            request, definition, hasSource, payloadHash, archiveResult, options.CurrentValue,
            requestId, cancellationToken);
    }

    private async Task ValidateSourceLinesAsync(
        IReadOnlyList<DocumentPrepareLine> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Any(line => string.IsNullOrWhiteSpace(line.SourceType) ||
                              !line.SourceDocumentId.HasValue || !line.SourceLineId.HasValue ||
                              string.IsNullOrWhiteSpace(line.SourceDocumentNo) ||
                              string.IsNullOrWhiteSpace(line.SourceProductCode)))
            throw new ApiException(StatusCodes.Status400BadRequest, "SOURCE_LINE_REQUIRED", "请选择源单明细。");

        foreach (var sourceGroup in lines.GroupBy(line => line.SourceType!, StringComparer.OrdinalIgnoreCase))
        {
            var current = new List<SourceDocumentLine>();
            foreach (var documentIds in sourceGroup
                         .Select(line => line.SourceDocumentId!.Value)
                         .Distinct()
                         .Order()
                         .Chunk(20))
                current.AddRange(await u8.GetSourceDocumentLinesAsync(
                    sourceGroup.Key, documentIds, cancellationToken));
            var byLine = current.ToDictionary(line => line.SourceLineId);
            foreach (var requestGroup in sourceGroup.GroupBy(line => line.SourceLineId!.Value))
            {
                if (!byLine.TryGetValue(requestGroup.Key, out var source) ||
                    requestGroup.Any(line => line.SourceDocumentId != source.SourceDocumentId ||
                                             !string.Equals(line.SourceDocumentNo, source.SourceDocumentNo, StringComparison.OrdinalIgnoreCase) ||
                                             !string.Equals(line.SourceProductCode, source.ProductCode, StringComparison.OrdinalIgnoreCase) ||
                                             !string.Equals(line.MaterialCode, source.ProductCode, StringComparison.OrdinalIgnoreCase)))
                    throw new ApiException(StatusCodes.Status409Conflict,
                        "SOURCE_LINE_CHANGED", "源单明细已变化，请重新选单。");
                if (requestGroup.Sum(line => line.RequestedQty) > source.OrderQuantity)
                    throw new ApiException(StatusCodes.Status409Conflict,
                        "SOURCE_QUANTITY_EXCEEDED", "处理数量超过源单可用数量。");
            }
        }
    }

    private static void ValidateBasicRequest(
        DocumentPrepareRequest request,
        DocumentPrepareDefinition definition,
        IReadOnlyDictionary<string, bool> requiredFields,
        bool hasSource)
    {
        if (string.IsNullOrWhiteSpace(request.ClientRequestId) || request.ClientRequestId.Length > 64)
            throw new ApiException(StatusCodes.Status400BadRequest, "CLIENT_REQUEST_ID_INVALID", "请求编号无效。");
        if (string.IsNullOrWhiteSpace(request.OperatorId) || string.IsNullOrWhiteSpace(request.OperatorName))
            throw new ApiException(StatusCodes.Status400BadRequest, "OPERATOR_REQUIRED", "操作人不能为空。");
        if (request.Header is null)
            throw new ApiException(StatusCodes.Status400BadRequest, "HEADER_REQUIRED", "单据表头不能为空。");
        ValidateDocumentDirection(request.Header.DocumentDirection);
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500)
            throw new ApiException(StatusCodes.Status400BadRequest, "LINES_INVALID", "明细数量无效。");

        if (IsRequired(requiredFields, "header.documentDate", true) && request.Header.DocumentDate == default)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DOCUMENT_DATE_REQUIRED", "请选择日期。");
        Require(request.Header.PartnerCode,
            IsRequired(requiredFields, "header.partnerCode", RequiresPartner(definition.DocumentType)),
            "PARTNER_REQUIRED", "请输入往来单位。");
        Require(request.Header.WarehouseCode, IsRequired(requiredFields, "header.warehouseCode", definition.RequireWarehouse), "WAREHOUSE_REQUIRED", "请输入仓库。");
        Require(request.Header.TargetWarehouseCode, IsRequired(requiredFields, "header.targetWarehouseCode", definition.RequireTargetWarehouse),
            "TARGET_WAREHOUSE_REQUIRED", "请输入调入仓库。");
        Require(request.Header.TargetLocationCode, IsRequired(requiredFields, "header.targetLocationCode", definition.RequireTargetLocation),
            "TARGET_LOCATION_REQUIRED", "请输入目标货位。");
        Require(request.Header.SourceLocationCode,
            IsRequired(requiredFields, "header.sourceLocationCode", false),
            "SOURCE_LOCATION_REQUIRED", "请输入原货位。");
        Require(request.Header.DepartmentCode, IsRequired(requiredFields, "header.departmentCode", definition.RequireDepartment), "DEPARTMENT_REQUIRED", "请输入部门。");
        Require(request.Header.CategoryCode, IsRequired(requiredFields, "header.categoryCode", definition.RequireCategory), "CATEGORY_REQUIRED", "请输入业务类别。");
        Require(request.Header.ProductionBatch,
            IsRequired(requiredFields, "header.productionBatch", false),
            "PRODUCTION_BATCH_REQUIRED", "请输入生产批号。");
        Require(request.Reason, IsRequired(requiredFields, "header.remark", false),
            "REMARK_REQUIRED", "请输入备注。");
        DocumentAdditionalFields.Validate(request.Header.AdditionalFields);
        DocumentAdditionalFields.ValidateRequired(
            request.Header.AdditionalFields, requiredFields, "header.", MappedHeaderFields);
        Require(request.Header.TargetWarehouseCode,
            IsRequired(requiredFields, "body.targetWarehouseCode", false),
            "TARGET_WAREHOUSE_REQUIRED", "请输入调入仓库。");
        Require(request.Header.TargetLocationCode,
            IsRequired(requiredFields, "body.targetLocationCode", false),
            "TARGET_LOCATION_REQUIRED", "请输入目标货位。");
        if (hasSource && !definition.SupportsSource)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "SOURCE_NOT_SUPPORTED", "当前单据不能关联源单。");
        if (hasSource && string.IsNullOrWhiteSpace(request.Header.SourceType))
            throw new ApiException(StatusCodes.Status400BadRequest, "SOURCE_REQUIRED", "请选择源单。");

        if (definition.DocumentType == "transfer-order" &&
            string.Equals(request.Header.WarehouseCode?.Trim(), request.Header.TargetWarehouseCode?.Trim(),
                StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "WAREHOUSE_UNCHANGED", "调出仓库和调入仓库不能相同。");

        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in request.Lines)
        {
            DocumentAdditionalFields.Validate(line.AdditionalFields);
            DocumentAdditionalFields.ValidateRequired(
                line.AdditionalFields, requiredFields, "body.", MappedBodyFields);
            if (line.ObjectType is not ("PRODUCT_LABEL" or "PALLET") || string.IsNullOrWhiteSpace(line.LabelCode))
                throw new ApiException(StatusCodes.Status400BadRequest, "OBJECT_INVALID", "标签或托盘无效。");
            if (string.IsNullOrWhiteSpace(line.MaterialCode) || string.IsNullOrWhiteSpace(line.UnitCode))
                throw new ApiException(StatusCodes.Status400BadRequest, "LINE_REQUIRED", "明细料号或单位不能为空。");
            if (line.RequestedQty < 0 || (!definition.AllowZeroQuantity && line.RequestedQty <= 0))
                throw new ApiException(StatusCodes.Status400BadRequest, "QUANTITY_INVALID", "明细数量无效。");
            if (line.ObjectType == "PALLET" && line.RequestedQty != 1)
                throw new ApiException(StatusCodes.Status400BadRequest, "PALLET_QUANTITY_INVALID", "托盘数量必须为 1。");
            if (!hasSource && !objects.Add($"{line.ObjectType}:{line.LabelCode.Trim()}"))
                throw new ApiException(StatusCodes.Status400BadRequest, "OBJECT_DUPLICATED", "标签或托盘重复。");
        }
    }

    private static bool HasSource(DocumentPrepareRequest request) =>
        !string.IsNullOrWhiteSpace(request.Header.SourceType) ||
        !string.IsNullOrWhiteSpace(request.Header.SourceDocumentNo) ||
        request.Lines.Any(line =>
            !string.IsNullOrWhiteSpace(line.SourceType) ||
            line.SourceDocumentId.HasValue ||
            !string.IsNullOrWhiteSpace(line.SourceDocumentNo) ||
            line.SourceLineId.HasValue ||
            line.SourceLineNo.HasValue ||
            !string.IsNullOrWhiteSpace(line.SourceProductCode));

    private static void Require(string? value, bool required, string code, string message)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new ApiException(StatusCodes.Status400BadRequest, code, message);
    }

    private static bool IsRequired(
        IReadOnlyDictionary<string, bool> requiredFields,
        string key,
        bool fallback) => requiredFields.TryGetValue(key, out var required) ? required : fallback;

    private static bool RequiresPartner(string documentType) => documentType is
        "purchase-arrival" or "outsourcing-arrival" or "purchase-inbound" or
        "delivery-note" or "sales-outbound";

    private static void ValidateDocumentDirection(string? value)
    {
        if (value is not ("BLUE" or "RED"))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DOCUMENT_DIRECTION_INVALID", "单据类型无效。");
    }
}
