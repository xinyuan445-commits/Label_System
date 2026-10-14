using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed record ManualReturnValidationLine(
    string MaterialCode,
    string UnitCode,
    decimal RequestedQty,
    string? BatchNo,
    string? WarehouseCode,
    string? LocationCode);

public sealed class ManualReturnValidationService(
    LabelCodeRuleRepository labelRules,
    LabelCodeRuleEngine codeRules,
    U8GatewayClient u8,
    IOptionsMonitor<LabelSystemOptions> options)
{
    public async Task ValidateAsync(
        IReadOnlyList<ManualReturnValidationLine> lines,
        string sourceDocumentNo,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return;

        var activeRule = (await labelRules.ListAsync(true, cancellationToken))
            .Where(rule => rule.IsEnabled &&
                           (rule.ObjectType is "PRODUCT_LABEL" or "INVENTORY_OBJECT"))
            .OrderBy(rule => rule.ObjectType == "PRODUCT_LABEL" ? 0 : 1)
            .ThenBy(rule => rule.Priority)
            .ThenBy(rule => rule.Id)
            .Select(rule => new
            {
                Rule = rule,
                Version = rule.Versions.FirstOrDefault(version => version.Status == "ACTIVE")
            })
            .FirstOrDefault(item => item.Version is not null);
        if (activeRule is null)
            throw new ApiException(StatusCodes.Status409Conflict,
                "LABEL_RULE_NOT_CONFIGURED", "没有可用的产品标签编码规则。");
        var validatedRule = codeRules.Validate(activeRule.Version!.Spec);
        var uniqueSegment = validatedRule.Spec.Segments.FirstOrDefault(segment =>
            segment.Kind == "FIELD" && segment.Field == "LABEL_UNIQUE_VALUE");
        if (uniqueSegment is null && !validatedRule.Spec.Segments.Any(segment => segment.Kind == "SEQUENCE"))
            throw new ApiException(StatusCodes.Status409Conflict,
                "LABEL_RULE_NOT_UNIQUE", "产品标签编码规则缺少唯一值或流水号。");

        var positions = await u8.GetWarehousePositionsAsync(cancellationToken);
        foreach (var line in lines)
        {
            var matches = await u8.SearchMaterialsAsync(line.MaterialCode, 20, cancellationToken);
            var material = matches.FirstOrDefault(item =>
                string.Equals(item.Code, line.MaterialCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (material is null || string.IsNullOrWhiteSpace(material.UnitCode))
                throw new ApiException(StatusCodes.Status409Conflict,
                    "MATERIAL_CHANGED", $"物料不存在或缺少库存单位：{line.MaterialCode}");
            if (!string.Equals(material.UnitCode, line.UnitCode.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new ApiException(StatusCodes.Status409Conflict,
                    "MATERIAL_UNIT_CHANGED", $"物料单位已变化：{line.MaterialCode}");
            if (material.RequiresBatch && string.IsNullOrWhiteSpace(line.BatchNo))
                throw new ApiException(StatusCodes.Status400BadRequest,
                    "BATCH_REQUIRED", $"请输入批号：{line.MaterialCode}");
            if (string.IsNullOrWhiteSpace(line.WarehouseCode))
                throw new ApiException(StatusCodes.Status400BadRequest,
                    "WAREHOUSE_REQUIRED", $"请选择仓库：{line.MaterialCode}");
            var warehouseRows = positions.Where(item => string.Equals(
                item.WarehouseCode, line.WarehouseCode.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (warehouseRows.Length == 0)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "WAREHOUSE_CHANGED", $"仓库不存在：{line.WarehouseCode}");
            var requiresPosition = warehouseRows.Any(item => !string.IsNullOrWhiteSpace(item.PositionCode));
            if (requiresPosition && string.IsNullOrWhiteSpace(line.LocationCode))
                throw new ApiException(StatusCodes.Status400BadRequest,
                    "LOCATION_REQUIRED", $"请选择货位：{line.MaterialCode}");
            if (!string.IsNullOrWhiteSpace(line.LocationCode) && !warehouseRows.Any(item =>
                    string.Equals(item.PositionCode, line.LocationCode.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new ApiException(StatusCodes.Status409Conflict,
                    "LOCATION_CHANGED", $"货位不属于所选仓库：{line.MaterialCode}");

            var uniqueLength = uniqueSegment?.Length ?? uniqueSegment?.MaxLength ?? 10;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LABEL_TYPE"] = "PRODUCT",
                ["MATERIAL_CODE"] = line.MaterialCode.Trim(),
                ["BATCH_NO"] = string.IsNullOrWhiteSpace(line.BatchNo) ? "NO_BATCH" : line.BatchNo.Trim(),
                ["ISSUED_QUANTITY"] = line.RequestedQty.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["UNIT_CODE"] = line.UnitCode.Trim(),
                ["SOURCE_DOCUMENT_NO"] = sourceDocumentNo.Trim(),
                ["LABEL_UNIQUE_VALUE"] = new string('0', uniqueLength),
                ["SEQUENCE"] = "1",
                ["CURRENT_DATE"] = BusinessClock.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            };
            var previewCode = codeRules.Render(
                validatedRule.Spec, values, options.CurrentValue.ErpAccountId);
            if (previewCode.Length > 100)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "LABEL_CODE_TOO_LONG", $"标签编码超过数据库长度：{line.MaterialCode}");
        }
    }
}
