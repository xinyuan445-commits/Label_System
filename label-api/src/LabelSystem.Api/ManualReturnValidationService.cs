namespace LabelSystem.Api;

public sealed record ManualReturnValidationLine(
    string MaterialCode,
    string UnitCode,
    decimal RequestedQty,
    string? BatchNo,
    string? WarehouseCode,
    string? LocationCode);

public sealed class ManualReturnValidationService(U8GatewayClient u8)
{
    public async Task ValidateAsync(
        IReadOnlyList<ManualReturnValidationLine> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return;

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

        }
    }
}
