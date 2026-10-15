namespace LabelSystem.Api;

public static class TraceBackSnapshotRules
{
    public static void RequireUsable(TraceBackLabelSnapshot snapshot, bool allowUsed)
    {
        if (snapshot.Status == "NOT_FOUND")
            throw new ApiException(StatusCodes.Status404NotFound,
                "LABEL_NOT_FOUND", "未找到标签。");
        if (snapshot.Status == "RECONCILIATION_REQUIRED" || snapshot.Anomalies?.Count > 0 ||
            snapshot.Balance is null || snapshot.LatestFlowId is null ||
            string.IsNullOrWhiteSpace(snapshot.MaterialCode) ||
            string.IsNullOrWhiteSpace(snapshot.UnitCode) ||
            string.IsNullOrWhiteSpace(snapshot.IdentityHash))
            throw new ApiException(StatusCodes.Status409Conflict,
                "TRACEBACK_RECONCILIATION_REQUIRED", "标签数据待核对。");
        if (snapshot.Balance.Value < 0)
            throw new ApiException(StatusCodes.Status409Conflict,
                "TRACEBACK_RECONCILIATION_REQUIRED", "标签数据待核对。");
        if (snapshot.Status == "USED_UP" && !allowUsed)
            throw new ApiException(StatusCodes.Status409Conflict,
                "LABEL_NOT_AVAILABLE", "标签已用完。");
    }

    public static void ValidateLine(
        TraceBackLabelSnapshot snapshot,
        string materialCode,
        string? batchNo,
        string unitCode,
        decimal requestedQty,
        string? warehouseCode,
        string? locationCode,
        bool allowUsed,
        bool comparePosition,
        decimal? expectedBalance = null,
        long? expectedLatestFlowId = null)
    {
        RequireUsable(snapshot, allowUsed);
        var balance = snapshot.Balance.GetValueOrDefault();
        if (!Same(snapshot.MaterialCode, materialCode) ||
            !Same(snapshot.BatchNo, batchNo) ||
            !Same(snapshot.UnitCode, unitCode))
            throw Changed();
        if (comparePosition &&
            (!Same(snapshot.WarehouseCode, warehouseCode) ||
             !Same(snapshot.LocationCode, locationCode)))
            throw Changed();
        if (expectedBalance.HasValue && balance != expectedBalance.Value)
            throw Changed();
        if (expectedLatestFlowId.HasValue && snapshot.LatestFlowId != expectedLatestFlowId.Value)
            throw Changed();
        if (!allowUsed && requestedQty > balance)
            throw new ApiException(StatusCodes.Status409Conflict,
                "LABEL_QUANTITY_INSUFFICIENT", "标签数量不足。");
    }

    private static ApiException Changed() => new(
        StatusCodes.Status409Conflict,
        "TRACEBACK_SNAPSHOT_CHANGED", "标签数据已变化，请重新扫描。");

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
}

public sealed class TraceBackSnapshotService(
    U8GatewayClient gateway,
    TraceBackMirrorRepository mirrors,
    LabelSystemRepository localInventory)
{
    private static readonly HashSet<string> TraceBackDocuments = new(
        ["material-outbound", "transfer-order", "location-adjustment"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<InventoryLookupResult> LookupAsync(
        string code,
        SessionUser user,
        CancellationToken cancellationToken)
    {
        var snapshot = (await gateway.GetTraceBackSnapshotsAsync([code], cancellationToken)).Single();
        if (snapshot.Status == "NOT_FOUND")
        {
            var local = await localInventory.LookupInventoryObjectAsync(code, cancellationToken);
            if (local.Pallet is not null) return local;
            throw new ApiException(StatusCodes.Status404NotFound, "LABEL_NOT_FOUND", "未找到标签。");
        }
        TraceBackSnapshotRules.RequireUsable(snapshot, allowUsed: true);
        var line = await mirrors.UpsertAsync(snapshot, user, rejectChangedSnapshot: false, cancellationToken);
        return new InventoryLookupResult([line], null);
    }

    public async Task RefreshBeforePrepareAsync(
        string documentType,
        string direction,
        IEnumerable<(string ObjectType, string Code, string MaterialCode, string? BatchNo,
            string UnitCode, decimal RequestedQty, string? WarehouseCode, string? LocationCode)> lines,
        SessionUser user,
        CancellationToken cancellationToken)
    {
        if (!TraceBackDocuments.Contains(documentType)) return;
        var products = lines.Where(line => line.ObjectType == "PRODUCT_LABEL").ToArray();
        if (products.Length == 0) return;
        var snapshots = await gateway.GetTraceBackSnapshotsAsync(
            products.Select(line => line.Code).ToArray(), cancellationToken);
        var byCode = snapshots.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
        var redReturn = documentType.Equals("material-outbound", StringComparison.OrdinalIgnoreCase) &&
                        direction.Equals("RED", StringComparison.OrdinalIgnoreCase);
        foreach (var line in products)
        {
            if (!byCode.TryGetValue(line.Code, out var snapshot))
                throw new ApiException(StatusCodes.Status404NotFound, "LABEL_NOT_FOUND", "未找到标签。");
            TraceBackSnapshotRules.ValidateLine(
                snapshot, line.MaterialCode, line.BatchNo, line.UnitCode, line.RequestedQty,
                line.WarehouseCode, line.LocationCode, redReturn, !redReturn);
            await mirrors.UpsertAsync(snapshot, user, rejectChangedSnapshot: true, cancellationToken);
        }
    }

    public async Task ValidateBeforeU8Async(
        string erpDocumentType,
        IReadOnlyList<MaterialOutboundExecutionLine> lines,
        bool redVoucher,
        CancellationToken cancellationToken)
    {
        if (erpDocumentType is not "material-outbound" and not "transfer" and not "location-adjustment")
            return;
        var products = lines.Where(line => line.ObjectType == "PRODUCT_LABEL").ToArray();
        if (products.Length == 0) return;
        var snapshots = await gateway.GetTraceBackSnapshotsAsync(
            products.Select(line => line.LabelCode).ToArray(), cancellationToken);
        var byCode = snapshots.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
        var redReturn = erpDocumentType == "material-outbound" && redVoucher;
        foreach (var line in products)
        {
            if (!byCode.TryGetValue(line.LabelCode, out var snapshot))
                throw new ApiException(StatusCodes.Status404NotFound, "LABEL_NOT_FOUND", "未找到标签。");
            if (!line.TraceBackLatestFlowId.HasValue)
                throw new ApiException(StatusCodes.Status409Conflict,
                    "TRACEBACK_SNAPSHOT_VERSION_MISSING", "标签数据待核对。");
            TraceBackSnapshotRules.ValidateLine(
                snapshot, line.MaterialCode, line.BatchNo, line.UnitCode, line.RequestedQty,
                line.WarehouseCode, line.LocationCode, redReturn, !redReturn,
                line.InventoryQtySnapshot, line.TraceBackLatestFlowId);
        }
    }
}
