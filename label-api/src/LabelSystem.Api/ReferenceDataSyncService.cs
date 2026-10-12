namespace LabelSystem.Api;

public sealed class ReferenceDataSyncService(
    U8GatewayClient gateway,
    ReferenceDataRepository repository)
{
    public async Task<DataSyncResult> SyncRdStylesAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        const string syncKey = "rd-styles";
        await repository.BeginSyncAsync(syncKey, userId, cancellationToken);
        try
        {
            var source = await gateway.GetRdStylesAsync(cancellationToken);
            var rows = ValidateRdStyles(source);
            return await repository.ReplaceRdStylesAsync(rows, userId, cancellationToken);
        }
        catch
        {
            try { await repository.FailSyncAsync(syncKey, userId, CancellationToken.None); }
            catch { /* 保留原始同步异常。 */ }
            throw;
        }
    }

    public async Task<DataSyncResult> SyncWarehousePositionsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        const string syncKey = "warehouse-positions";
        await repository.BeginSyncAsync(syncKey, userId, cancellationToken);
        try
        {
            var source = await gateway.GetWarehousePositionsAsync(cancellationToken);
            var rows = ValidateWarehousePositions(source);
            return await repository.ReplaceWarehousePositionsAsync(rows, userId, cancellationToken);
        }
        catch
        {
            try { await repository.FailSyncAsync(syncKey, userId, CancellationToken.None); }
            catch { /* 保留原始同步异常。 */ }
            throw;
        }
    }

    public async Task<DataSyncResult> SyncDepartmentsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        const string syncKey = "departments";
        await repository.BeginSyncAsync(syncKey, userId, cancellationToken);
        try
        {
            var source = await gateway.GetDepartmentsAsync(cancellationToken);
            var rows = ValidateDepartments(source);
            return await repository.ReplaceDepartmentsAsync(rows, userId, cancellationToken);
        }
        catch
        {
            try { await repository.FailSyncAsync(syncKey, userId, CancellationToken.None); }
            catch { /* 保留原始同步异常。 */ }
            throw;
        }
    }

    private static IReadOnlyList<RdStyleSourceItem> ValidateRdStyles(
        IReadOnlyList<RdStyleSourceItem> source)
    {
        if (source.Count == 0 || source.Count > 5000)
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "RD_STYLE_SOURCE_INVALID", "U8 出入库类别数据无效。");

        var result = new List<RdStyleSourceItem>(source.Count);
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            var code = item.Code?.Trim() ?? string.Empty;
            var name = item.Name?.Trim() ?? string.Empty;
            var direction = item.Direction?.Trim().ToUpperInvariant() ?? string.Empty;
            if (code.Length is < 1 or > 10 || name.Length is < 1 or > 24 ||
                direction is not ("IN" or "OUT") || !codes.Add(code))
                throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                    "RD_STYLE_SOURCE_INVALID", "U8 出入库类别数据无效。");

            result.Add(item with
            {
                Code = code,
                Name = name,
                Direction = direction,
                OppositeCode = string.IsNullOrWhiteSpace(item.OppositeCode)
                    ? null
                    : item.OppositeCode.Trim()
            });
        }
        return result;
    }

    private static IReadOnlyList<WarehousePositionSourceItem> ValidateWarehousePositions(
        IReadOnlyList<WarehousePositionSourceItem> source)
    {
        if (source.Count == 0 || source.Count > 100000)
            throw InvalidSource();

        var result = new List<WarehousePositionSourceItem>(source.Count);
        var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            var warehouseCode = item.WarehouseCode?.Trim() ?? string.Empty;
            var warehouseName = item.WarehouseName?.Trim() ?? string.Empty;
            var positionCode = NullIfWhiteSpace(item.PositionCode);
            var positionName = NullIfWhiteSpace(item.PositionName);
            if (warehouseCode.Length is < 1 or > 100 || warehouseName.Length is < 1 or > 200 ||
                positionCode?.Length > 100 || positionName?.Length > 200 ||
                (positionCode is null) != (positionName is null) ||
                !pairs.Add($"{warehouseCode}\u001f{positionCode ?? string.Empty}"))
                throw InvalidSource();
            result.Add(new WarehousePositionSourceItem(
                warehouseCode, warehouseName, positionCode, positionName));
        }
        return result;

        static ApiException InvalidSource() => new(StatusCodes.Status503ServiceUnavailable,
            "WAREHOUSE_POSITION_SOURCE_INVALID", "U8 仓位数据无效。");
    }

    private static IReadOnlyList<DepartmentSourceItem> ValidateDepartments(
        IReadOnlyList<DepartmentSourceItem> source)
    {
        if (source.Count == 0 || source.Count > 10000)
            throw InvalidSource();

        var result = new List<DepartmentSourceItem>(source.Count);
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            var code = item.Code?.Trim() ?? string.Empty;
            var name = item.Name?.Trim() ?? string.Empty;
            if (code.Length is < 1 or > 100 || name.Length is < 1 or > 200 || !codes.Add(code))
                throw InvalidSource();
            result.Add(new DepartmentSourceItem(code, name));
        }
        return result;

        static ApiException InvalidSource() => new(StatusCodes.Status503ServiceUnavailable,
            "DEPARTMENT_SOURCE_INVALID", "U8 部门数据无效。");
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
