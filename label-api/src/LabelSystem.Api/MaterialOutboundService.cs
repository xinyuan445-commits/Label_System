using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed class MaterialOutboundService(
    LabelSystemRepository settings,
    MaterialOutboundRepository operations,
    ManualReturnValidationService manualReturns,
    U8GatewayClient u8,
    OperationArchiveService archive,
    IOptionsMonitor<LabelSystemOptions> options)
{
    private const decimal QuantityTolerance = 0.000001m;
    private static readonly IReadOnlySet<string> MappedHeaderFields = new HashSet<string>(
        [
            "header.documentDate", "header.departmentCode", "header.categoryCode",
            "header.productionBatch", "header.remark"
        ], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> MappedBodyFields = new HashSet<string>(
        ["body.labelCode", "body.objectType", "body.materialCode", "body.unitCode", "body.quantity"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<MaterialOutboundPrepareResult> PrepareAsync(
        MaterialOutboundPrepareRequest request,
        string requestId,
        CancellationToken cancellationToken)
    {
        var requiredFields = await settings.GetRequiredDocumentFieldsAsync(
            "material-outbound", cancellationToken);
        ValidateBasicRequest(request, requiredFields);

        var reasonRequired = await settings.GetBooleanSettingAsync(
            "reason_required", null, false, cancellationToken);
        if (reasonRequired && string.IsNullOrWhiteSpace(request.Reason))
            throw new ApiException(StatusCodes.Status400BadRequest, "REASON_REQUIRED", "请输入原因。");
        var manual = request.Lines.Where(line => line.ObjectType == "UNLABELED_MATERIAL").ToArray();
        if (manual.Length > 0 && string.IsNullOrWhiteSpace(request.Reason))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "RETURN_REASON_REQUIRED", "请输入退料原因。");
        await manualReturns.ValidateAsync(
            manual.Select(line => new ManualReturnValidationLine(
                line.MaterialCode, line.UnitCode, line.RequestedQty,
                line.BatchNo, line.WarehouseCode, line.LocationCode)).ToArray(),
            manual.FirstOrDefault()?.SourceDocumentNo ?? request.ClientRequestId,
            cancellationToken);

        var requirements = await u8.GetMaterialRequirementsAsync(
            request.Lines.Select(line => line.SourceLineId).Distinct().ToArray(), cancellationToken);
        await ValidateRequirementsAsync(
            request.Lines, requirements, request.DocumentDirection, cancellationToken);

        var payloadJson = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var payloadHash = Hashing.Sha256(payloadJson);
        var archiveResult = await archive.CreateAsync(
            "material-outbound", "材料出库单", "prepare", requestId, request, cancellationToken);

        var allowPalletizedDirectUse = await settings.GetBooleanSettingAsync(
            "allow_direct_use_of_palletized_label", null, false, cancellationToken);

        return await operations.PrepareAsync(
            request,
            payloadHash,
            archiveResult,
            allowPalletizedDirectUse,
            options.CurrentValue,
            requestId,
            cancellationToken);
    }

    private async Task ValidateRequirementsAsync(
        IReadOnlyList<MaterialOutboundAllocationRequest> lines,
        IReadOnlyList<MaterialRequirementLine> requirements,
        string documentDirection,
        CancellationToken cancellationToken)
    {
        var byId = requirements.ToDictionary(line => line.RequirementId);
        foreach (var line in lines)
        {
            if (!byId.TryGetValue(line.RequirementId, out var requirement) ||
                requirement.SourceLineId != line.SourceLineId ||
                requirement.SourceDocumentId != line.SourceDocumentId ||
                !string.Equals(requirement.SourceDocumentNo, line.SourceDocumentNo, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(requirement.MaterialCode, line.MaterialCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(requirement.ProductCode, line.ProductCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException(StatusCodes.Status409Conflict,
                    "SOURCE_REQUIREMENT_CHANGED", "领料明细已变化，请重新选单。");
            }
        }

        foreach (var group in lines.GroupBy(line => line.RequirementId))
        {
            var normal = group.Sum(line => line.NormalQty);
            var available = documentDirection == "RED"
                ? byId[group.Key].IssuedQuantity
                : byId[group.Key].RemainingQuantity;
            if (normal - available > QuantityTolerance)
                throw new ApiException(StatusCodes.Status409Conflict,
                    documentDirection == "RED" ? "RETURN_QUANTITY_EXCEEDED" : "NORMAL_ISSUE_EXCEEDED",
                    documentDirection == "RED" ? "退料数量超过原单已领数量。" : "领料数量超过可领数量。");
        }

        if (documentDirection == "RED")
        {
            if (lines.Any(line => line.OverQty > 0))
                throw new ApiException(StatusCodes.Status400BadRequest,
                    "RETURN_OVER_ISSUE_INVALID", "红字退料不能使用超领数量。");
            return;
        }

        var allowOverIssue = await settings.GetBooleanSettingAsync(
            "allow_over_issue", "material-outbound", false, cancellationToken);
        foreach (var materialGroup in lines.GroupBy(line => line.MaterialCode, StringComparer.OrdinalIgnoreCase))
        {
            var over = materialGroup.Sum(line => line.OverQty);
            if (over <= 0) continue;
            if (!allowOverIssue)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "OVER_ISSUE_DISABLED", "当前不允许超领。");

            var requirementIds = requirements
                .Where(item => string.Equals(item.MaterialCode, materialGroup.Key, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.RequirementId)
                .ToHashSet();
            var remaining = requirements
                .Where(item => requirementIds.Contains(item.RequirementId))
                .Sum(item => item.RemainingQuantity);
            var normal = materialGroup.Sum(line => line.NormalQty);
            if (Math.Abs(normal - remaining) > QuantityTolerance)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "OVER_ISSUE_TARGET_INVALID", "请先领完正常数量，再选择超领产品。");
        }
    }

    private static void ValidateBasicRequest(
        MaterialOutboundPrepareRequest request,
        IReadOnlyDictionary<string, bool> requiredFields)
    {
        if (string.IsNullOrWhiteSpace(request.ClientRequestId) || request.ClientRequestId.Length > 64)
            throw new ApiException(StatusCodes.Status400BadRequest, "CLIENT_REQUEST_ID_INVALID", "请求编号无效。");
        if (string.IsNullOrWhiteSpace(request.OperatorId) || string.IsNullOrWhiteSpace(request.OperatorName))
            throw new ApiException(StatusCodes.Status400BadRequest, "OPERATOR_REQUIRED", "操作人不能为空。");
        if (request.DocumentDate == default)
            throw new ApiException(StatusCodes.Status400BadRequest, "DOCUMENT_DATE_REQUIRED", "请选择日期。");
        if (request.DocumentDirection is not ("BLUE" or "RED"))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "DOCUMENT_DIRECTION_INVALID", "单据类型无效。");
        if (IsRequired(requiredFields, "header.departmentCode", true) &&
            string.IsNullOrWhiteSpace(request.DepartmentCode))
            throw new ApiException(StatusCodes.Status400BadRequest, "DEPARTMENT_REQUIRED", "请输入部门。");
        if (IsRequired(requiredFields, "header.categoryCode", true) &&
            string.IsNullOrWhiteSpace(request.CategoryCode))
            throw new ApiException(StatusCodes.Status400BadRequest, "CATEGORY_REQUIRED", "请选择业务类别。");
        if (!string.Equals(request.CategoryCode?.Trim(), "202", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "CATEGORY_INVALID", "生产订单领料的出库类别必须为 202。");
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 500)
            throw new ApiException(StatusCodes.Status400BadRequest, "LINES_INVALID", "明细数量无效。");
        if (request.ProductionBatch?.Length > 100)
            throw new ApiException(StatusCodes.Status400BadRequest, "PRODUCTION_BATCH_INVALID", "生产批号无效。");
        if (IsRequired(requiredFields, "header.productionBatch", false) &&
            string.IsNullOrWhiteSpace(request.ProductionBatch))
            throw new ApiException(StatusCodes.Status400BadRequest,
                "PRODUCTION_BATCH_REQUIRED", "请输入生产批号。");
        if (IsRequired(requiredFields, "header.remark", false) &&
            string.IsNullOrWhiteSpace(request.Reason))
            throw new ApiException(StatusCodes.Status400BadRequest, "REMARK_REQUIRED", "请输入备注。");
        DocumentAdditionalFields.Validate(request.AdditionalFields);
        DocumentAdditionalFields.ValidateRequired(
            request.AdditionalFields, requiredFields, "header.", MappedHeaderFields);

        foreach (var line in request.Lines)
        {
            DocumentAdditionalFields.Validate(line.AdditionalFields);
            DocumentAdditionalFields.ValidateRequired(
                line.AdditionalFields, requiredFields, "body.", MappedBodyFields);
            if (line.ObjectType is not ("PRODUCT_LABEL" or "PALLET" or "UNLABELED_MATERIAL") ||
                string.IsNullOrWhiteSpace(line.LabelCode) ||
                line.LabelCode.Length > 100 ||
                !string.Equals(line.SourceType, "production-order", StringComparison.OrdinalIgnoreCase) ||
                line.SourceDocumentId <= 0 || line.SourceLineId <= 0 || line.RequirementId <= 0 ||
                string.IsNullOrWhiteSpace(line.SourceDocumentNo) ||
                string.IsNullOrWhiteSpace(line.ProductCode) ||
                string.IsNullOrWhiteSpace(line.MaterialCode) ||
                string.IsNullOrWhiteSpace(line.UnitCode) ||
                line.NormalQty < 0 || line.OverQty < 0 || line.RequestedQty <= 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "LINE_INVALID", "明细内容无效。");
            }
            if (line.ObjectType == "UNLABELED_MATERIAL" && request.DocumentDirection != "RED")
                throw new ApiException(StatusCodes.Status400BadRequest,
                    "UNLABELED_RETURN_NOT_SUPPORTED", "无标签录入只用于红字材料出库单。");
            if (line.ObjectType == "PALLET" && line.RequestedQty != 1)
                throw new ApiException(StatusCodes.Status400BadRequest, "PALLET_QUANTITY_INVALID", "托盘数量必须为 1。");
        }
    }

    private static bool IsRequired(
        IReadOnlyDictionary<string, bool> requiredFields,
        string key,
        bool fallback) => requiredFields.TryGetValue(key, out var required) ? required : fallback;
}
