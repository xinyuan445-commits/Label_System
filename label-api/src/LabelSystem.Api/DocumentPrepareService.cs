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
    bool AllowZeroQuantity,
    bool SupportsSource,
    bool GroupByWarehouse,
    bool MatchCurrentWarehouse);

public sealed class DocumentPrepareService(
    LabelSystemRepository settings,
    DocumentPrepareRepository operations,
    ManualReturnValidationService manualReturns,
    U8GatewayClient u8,
    OperationArchiveService archive,
    IOptionsMonitor<LabelSystemOptions> options,
    InventoryCountCollectionService? inventoryCounts = null)
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
            "body.materialName", "body.batchNo", "body.warehouseCode", "body.locationCode",
            "body.availableQty", "body.quantity", "body.remark",
            "body.targetWarehouseCode", "body.targetLocationCode"
        ], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, DocumentPrepareDefinition> Definitions =
        new Dictionary<string, DocumentPrepareDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["transfer-order"] = new(
                "transfer-order", "调拨单", "TRANSFER", "transfer", "TR",
                false, false, false, true, false, false, false, false, false),
            ["location-adjustment"] = new(
                "location-adjustment", "货位调整", "LOCATION_ADJUSTMENT", "location-adjustment", "LA",
                false, false, false, false, false, false, false, true, false),
            ["inventory-count"] = new(
                "inventory-count", "盘点单", "INVENTORY_COUNT", "inventory-count", "IC",
                true, false, false, false, false, true, false, false, false),
            ["purchase-arrival"] = new(
                "purchase-arrival", "采购到货", "PURCHASE_ARRIVAL", "purchase-arrival", "PA",
                false, false, false, true, false, false, true, false, false),
            ["outsourcing-arrival"] = new(
                "outsourcing-arrival", "委外到货", "SUBCONTRACT_ARRIVAL", "subcontract-arrival", "OA",
                false, false, false, true, false, false, true, false, false),
            ["purchase-inbound"] = new(
                "purchase-inbound", "采购入库", "PURCHASE_RECEIPT", "purchase-receipt", "PI",
                true, false, false, true, true, false, true, false, false),
            ["delivery-note"] = new(
                "delivery-note", "发货单", "SALES_DELIVERY", "sales-delivery", "SD",
                true, false, false, true, false, false, true, false, true),
            ["sales-return"] = new(
                "sales-return", "退货单", "SALES_RETURN", "sales-delivery", "SR",
                true, false, false, true, false, false, true, false, false),
            ["sales-outbound"] = new(
                "sales-outbound", "销售出库单", "SALES_OUTBOUND", "sales-outbound", "SO",
                true, false, false, true, true, false, true, false, true),
            ["material-outbound"] = new(
                "material-outbound", "材料出库单", "MATERIAL_OUTBOUND", "material-outbound", "MO",
                false, false, false, true, true, false, false, true, false),
        };

    public async Task<MaterialOutboundPrepareResult> PrepareAsync(
        string documentType,
        DocumentPrepareRequest request,
        string requestId,
        CancellationToken cancellationToken)
    {
        documentType = documentType.Trim().ToLowerInvariant();
        var count = documentType == "inventory-count";
        if (count && request?.InventoryCount is null) InventoryCountRules.RejectLegacyWrite(documentType);
        if (request is null) throw new ApiException(400, "REQUEST_REQUIRED", "请提交完整单据。");
        if (!Definitions.TryGetValue(documentType, out var definition))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DOCUMENT_TYPE_NOT_READY", "当前单据尚未开放预提交。");

        var payloadJson = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var payloadHash = Hashing.Sha256(payloadJson);
        if (SalesDocumentRules.Applies(documentType)) SalesDocumentRules.RequireDirection(documentType, request.Header.DocumentDirection);
        if (count || SalesDocumentRules.Applies(documentType))
        {
            if (count) InventoryCountUploadRules.RequireContext(request, options.CurrentValue);
            var existing = await operations.FindExistingAsync(request.ClientRequestId, payloadHash,
                options.CurrentValue.U8WritesEnabled, cancellationToken);
            if (existing is not null) return existing;
            if (count) await InventoryCountUploadRules.ValidateAsync(request,
                inventoryCounts ?? throw new InvalidOperationException("盘点服务不可用。"), cancellationToken);
        }
        var hasSource = !count && HasSource(request);
        var requiredFields = await settings.GetRequiredDocumentFieldsAsync(documentType, cancellationToken);
        if (count) requiredFields = InventoryCountUploadRules.RequiredFields(requiredFields);
        ValidateBasicRequest(request, definition, requiredFields, hasSource);
        await ValidateManualReturnLinesAsync(request, definition, cancellationToken);

        if (hasSource)
            await ValidateSourceLinesAsync(request, definition.DocumentType, cancellationToken);

        var archiveResult = await archive.CreateAsync(
            definition.DocumentType, definition.Title, "prepare", requestId, request, cancellationToken);

        return await operations.PrepareAsync(
            request, definition, hasSource, payloadHash, archiveResult, options.CurrentValue,
            requestId, cancellationToken);
    }

    private async Task ValidateSourceLinesAsync(
        DocumentPrepareRequest request,
        string documentType,
        CancellationToken cancellationToken)
    {
        var lines = request.Lines;
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
            if (SalesDocumentRules.Applies(documentType))
                SalesDocumentRules.ValidateSources(documentType, request, current);
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

    private async Task ValidateManualReturnLinesAsync(
        DocumentPrepareRequest request,
        DocumentPrepareDefinition definition,
        CancellationToken cancellationToken)
    {
        var manual = request.Lines
            .Where(line => line.ObjectType == "UNLABELED_MATERIAL")
            .ToArray();
        if (manual.Length == 0) return;
        if (definition.DocumentType != "material-outbound" || request.Header.DocumentDirection != "RED")
            throw new ApiException(StatusCodes.Status400BadRequest,
                "UNLABELED_RETURN_NOT_SUPPORTED", "无标签录入只用于红字材料出库单。");
        await manualReturns.ValidateAsync(
            manual.Select(line => new ManualReturnValidationLine(
                line.MaterialCode, line.UnitCode, line.RequestedQty,
                line.BatchNo, line.WarehouseCode, line.LocationCode)).ToArray(),
            cancellationToken);
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
        var maximumLines = definition.DocumentType == "inventory-count" ? InventoryCountRules.MaximumObservations : 500;
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > maximumLines)
            throw new ApiException(StatusCodes.Status400BadRequest, "LINES_INVALID", "明细数量无效。");

        if (IsRequired(requiredFields, "header.documentDate", true) && request.Header.DocumentDate == default)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DOCUMENT_DATE_REQUIRED", "请选择日期。");
        Require(request.Header.PartnerCode,
            IsRequired(requiredFields, "header.partnerCode", RequiresPartner(definition.DocumentType)),
            "PARTNER_REQUIRED", "请输入往来单位。");
        var lineLevelPosition = definition.DocumentType is "transfer-order" or "location-adjustment";
        Require(request.Header.WarehouseCode, !lineLevelPosition && IsRequired(requiredFields, "header.warehouseCode", definition.RequireWarehouse), "WAREHOUSE_REQUIRED", "请输入仓库。");
        Require(request.Header.TargetWarehouseCode, !lineLevelPosition && IsRequired(requiredFields, "header.targetWarehouseCode", definition.RequireTargetWarehouse),
            "TARGET_WAREHOUSE_REQUIRED", "请输入调入仓库。");
        Require(request.Header.TargetLocationCode, !lineLevelPosition && IsRequired(requiredFields, "header.targetLocationCode", definition.RequireTargetLocation),
            "TARGET_LOCATION_REQUIRED", "请输入目标货位。");
        Require(request.Header.SourceLocationCode,
            !lineLevelPosition && IsRequired(requiredFields, "header.sourceLocationCode", false),
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
        if (hasSource && !definition.SupportsSource)
            throw new ApiException(StatusCodes.Status400BadRequest,
                "SOURCE_NOT_SUPPORTED", "当前单据不能关联源单。");
        if (hasSource && string.IsNullOrWhiteSpace(request.Header.SourceType))
            throw new ApiException(StatusCodes.Status400BadRequest, "SOURCE_REQUIRED", "请选择源单。");

        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in request.Lines)
        {
            if (!string.IsNullOrWhiteSpace(line.SourcePalletCode))
                throw new ApiException(400, "LEGACY_DETAIL_UNSUPPORTED", "旧托盘明细请删除后重新扫码。");
            DocumentAdditionalFields.Validate(line.AdditionalFields);
            DocumentAdditionalFields.ValidateRequired(
                line.AdditionalFields, requiredFields, "body.", MappedBodyFields);
            if (line.ObjectType is not ("PRODUCT_LABEL" or "UNLABELED_MATERIAL") ||
                string.IsNullOrWhiteSpace(line.LabelCode))
                throw new ApiException(StatusCodes.Status400BadRequest, "OBJECT_INVALID", "标签无效。");
            if (string.IsNullOrWhiteSpace(line.MaterialCode) || string.IsNullOrWhiteSpace(line.UnitCode))
                throw new ApiException(StatusCodes.Status400BadRequest, "LINE_REQUIRED", "明细料号或单位不能为空。");
            Require(line.MaterialName, IsRequired(requiredFields, "body.materialName", false),
                "MATERIAL_NAME_REQUIRED", "请输入物料名称。");
            Require(line.BatchNo, IsRequired(requiredFields, "body.batchNo", false),
                "BATCH_REQUIRED", "请输入批号。");
            Require(line.WarehouseCode, IsRequired(requiredFields, "body.warehouseCode", false),
                "WAREHOUSE_REQUIRED", "请选择仓库。");
            Require(line.LocationCode,
                definition.DocumentType == "location-adjustment" ||
                IsRequired(requiredFields, "body.locationCode", false),
                "LOCATION_REQUIRED", "请选择货位。");
            var targetWarehouse = line.TargetWarehouseCode ??
                                  AdditionalText(line.AdditionalFields, "targetWarehouseCode") ??
                                  request.Header.TargetWarehouseCode;
            var targetLocation = line.TargetLocationCode ??
                                 AdditionalText(line.AdditionalFields, "targetLocationCode") ??
                                 request.Header.TargetLocationCode;
            Require(targetWarehouse,
                definition.DocumentType == "transfer-order" || IsRequired(requiredFields, "body.targetWarehouseCode", false),
                "TARGET_WAREHOUSE_REQUIRED", "请选择调入仓库。");
            Require(targetLocation,
                definition.DocumentType == "location-adjustment" ||
                IsRequired(requiredFields, "body.targetLocationCode", false),
                "TARGET_LOCATION_REQUIRED", "请选择目标货位。");
            if (definition.DocumentType == "transfer-order" &&
                string.Equals(line.WarehouseCode?.Trim(), targetWarehouse?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new ApiException(StatusCodes.Status400BadRequest,
                    "WAREHOUSE_UNCHANGED", "调出仓库和调入仓库不能相同。");
            Require(line.Remark, IsRequired(requiredFields, "body.remark", false),
                "LINE_REMARK_REQUIRED", "请输入行备注。");
            if (line.RequestedQty < 0 || (!definition.AllowZeroQuantity && line.RequestedQty <= 0))
                throw new ApiException(StatusCodes.Status400BadRequest, "QUANTITY_INVALID", "明细数量无效。");
            if (!hasSource && !objects.Add($"{line.ObjectType}:{line.LabelCode.Trim()}"))
                throw new ApiException(StatusCodes.Status400BadRequest, "OBJECT_DUPLICATED", "标签重复。");
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

    private static string? AdditionalText(JsonElement? fields, string name)
    {
        if (fields is not { ValueKind: JsonValueKind.Object } value) return null;
        foreach (var property in value.EnumerateObject())
        {
            var logical = property.Name.StartsWith("body.", StringComparison.OrdinalIgnoreCase)
                ? property.Name[5..] : property.Name;
            if (!logical.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind == JsonValueKind.Null) continue;
            var text = property.Value.ToString().Trim();
            return text.Length == 0 ? null : text;
        }
        return null;
    }

    private static bool IsRequired(
        IReadOnlyDictionary<string, bool> requiredFields,
        string key,
        bool fallback) => requiredFields.TryGetValue(key, out var required) ? required : fallback;

    private static bool RequiresPartner(string documentType) => documentType is
        "purchase-arrival" or "outsourcing-arrival" or "purchase-inbound" or
        "delivery-note" or "sales-return" or "sales-outbound";

    public Task<MaterialOutboundPrepareResult?> FindExistingSalesAsync(string type, DocumentPrepareRequest request, CancellationToken ct) =>
        SalesDocumentRules.Applies(type) ? operations.FindExistingAsync(request.ClientRequestId,
            Hashing.Sha256(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            options.CurrentValue.U8WritesEnabled, ct) : Task.FromResult<MaterialOutboundPrepareResult?>(null);

    private static void ValidateDocumentDirection(string? value)
    {
        if (value is not ("BLUE" or "RED"))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DOCUMENT_DIRECTION_INVALID", "单据类型无效。");
    }
}
