using System.Globalization;
using System.Text.Json;

namespace LabelSystem.Api;

public sealed record InventoryCountUploadContext(string AccountId, string DatabaseName,
    bool CountByLocation, InventoryCountSource? Source = null);

public static class InventoryCountUploadRules
{
    public static void RequireTestAccount(LabelSystemOptions options)
    {
        if (options.ErpAccountId != "888" || options.ErpDatabaseName != "UFDATA_888_2021" ||
            !string.Equals(options.Environment, "development", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(409, "COUNT_UPLOAD_TEST_ONLY", "盘点上传仅开放888测试账套。");
        if (!options.InventoryCountUploadsEnabled || !options.U8WritesEnabled)
            throw new ApiException(409, "COUNT_UPLOAD_DISABLED", "盘点上传尚未开放。");
    }

    public static bool Enabled(LabelSystemOptions options) => options.InventoryCountUploadsEnabled &&
        options.U8WritesEnabled && options.ErpAccountId == "888" && options.ErpDatabaseName == "UFDATA_888_2021" &&
        string.Equals(options.Environment, "development", StringComparison.OrdinalIgnoreCase);

    public static void RequireContext(DocumentPrepareRequest request, LabelSystemOptions options)
    {
        if (request.InventoryCount is null) InventoryCountRules.RejectLegacyWrite("inventory-count");
        RequireTestAccount(options);
        if (request.Header is null || request.Lines is null || request.Lines.Any(l => l is null))
            throw new ApiException(400, "COUNT_REQUEST_INVALID", "盘点资料不完整。");
        var context = request.InventoryCount!;
        if (context.AccountId != options.ErpAccountId || context.DatabaseName != options.ErpDatabaseName)
            throw new ApiException(409, "COUNT_ACCOUNT_MISMATCH", "盘点账套不一致，请重新打开单据。");
        if (request.Header.DocumentDirection != "BLUE" || request.Lines.Any(l => l.ObjectType != "PRODUCT_LABEL"))
            throw new ApiException(400, "COUNT_LABEL_REQUIRED", "盘点请使用产品标签。");
        InventoryCountRules.RequireObservationList(Observations(request));
        if (request.Lines.Any(l => !InventoryCountRules.TryQuantity(l.RequestedQty.ToString(CultureInfo.InvariantCulture), out _)))
            throw new ApiException(400, "COUNT_QUANTITY_INVALID", "盘点数量无效。");
    }

    public static IReadOnlyDictionary<string, bool> RequiredFields(IReadOnlyDictionary<string, bool> fields)
    {
        var result = new Dictionary<string, bool>(fields, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "header.departmentCode", "header.categoryCode", "header.partnerCode",
            "header.productionBatch", "header.targetWarehouseCode", "header.targetLocationCode", "header.sourceLocationCode" })
            result[key] = false;
        return result;
    }

    public static InventoryCountObservation[] Observations(DocumentPrepareRequest request) =>
        request.Lines.Select(l => new InventoryCountObservation(l.LabelCode,
            l.RequestedQty.ToString("0.######", CultureInfo.InvariantCulture), l.TargetLocationCode ?? l.LocationCode)).ToArray();

    public static async Task ValidateAsync(DocumentPrepareRequest request,
        InventoryCountCollectionService counts, CancellationToken ct)
    {
        var context = request.InventoryCount!;
        if (context.Source is { } source)
        {
            if (context.CountByLocation != source.Document.CountByLocation ||
                request.Header.WarehouseCode != source.Document.WarehouseCode)
                throw new ApiException(409, "COUNT_SOURCE_CHANGED", "请重新选择原盘点单。");
            var current = await counts.GetSourceAsync(source.Document.Id, ct);
            if (!source.Document.DocumentDate.HasValue || request.Header.DocumentDate != DateOnly.FromDateTime(source.Document.DocumentDate.Value))
                throw new ApiException(409, "COUNT_SOURCE_CHANGED", "请重选同一原单，已扫数量保留。");
            // Also compare the submitted source content: a client cannot attach a valid fingerprint
            // to edited book quantities. Decimal scale and presentation names are not business changes.
            if (current.Fingerprint != source.Fingerprint || SourceIdentity(current) != SourceIdentity(source))
                throw new ApiException(409, "COUNT_SOURCE_CHANGED", "原盘点单已变化，请重选同一原单。");
            var result = await counts.ValidateAsync(source.Document.Id,
                new(context.AccountId, context.DatabaseName, source.Fingerprint, Observations(request)), ct);
            RequireMatched(result.Items.Select(x => (x.Status, x.Message)));
        }
        else
        {
            var result = await counts.ValidateDirectAsync(new(context.AccountId, context.DatabaseName,
                request.Header.WarehouseCode ?? "", Observations(request), context.CountByLocation), ct);
            RequireMatched(result.Items.Select(x => (x.Status, x.Message)));
        }
    }

    private static string SourceIdentity(InventoryCountSource source) => JsonSerializer.Serialize(new
    {
        source.AccountId, source.DatabaseName, source.Document,
        Lines = source.Lines.Select(l => new { l.Id, l.LineNo, l.MaterialCode, l.BatchNo,
            l.WarehouseCode, l.LocationCode, l.UnitCode,
            Book = l.BookQuantity.ToString("G29", CultureInfo.InvariantCulture),
            Actual = l.ActualQuantity?.ToString("G29", CultureInfo.InvariantCulture),
            l.FreeItems, l.RequiresAuxiliaryUnit })
    });

    private static void RequireMatched(IEnumerable<(string Status, string Message)> items)
    {
        var bad = items.FirstOrDefault(x => x.Status != "MATCHED");
        if (bad.Status is not null) throw new ApiException(409, bad.Status, bad.Message);
    }

    public static InventoryCountContribution[] PreviousForUpload(IEnumerable<InventoryCountContribution> previous,
        IReadOnlyList<MaterialOutboundExecutionLine> current, bool byLocation)
    {
        var codes = current.Select(l => l.LabelCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return previous.Where(p => !codes.Contains(p.Code) && current.Any(l =>
            Same(l.MaterialCode, p.MaterialCode) && Same(l.BatchNo, p.BatchNo) &&
            Same(TargetWarehouse(l), p.WarehouseCode) && (!byLocation || Same(TargetLocation(l), p.LocationCode)))).ToArray();
    }

    public static string TargetWarehouse(MaterialOutboundExecutionLine line) => string.IsNullOrEmpty(line.TargetWarehouseCode) ? line.WarehouseCode : line.TargetWarehouseCode;
    public static string? TargetLocation(MaterialOutboundExecutionLine line) => string.IsNullOrEmpty(line.TargetWarehouseCode) ? line.LocationCode : line.TargetLocationCode;

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    public static GatewayDocumentRequest Build(MaterialOutboundExecutionOperation operation,
        DocumentPrepareRequest request, ErpOperator erp, IReadOnlyList<InventoryCountContribution>? previous = null)
    {
        var context = request.InventoryCount ?? throw new ApiException(409, "COUNT_WRITE_NOT_READY", "请重新保存盘点单。");
        var header = new Dictionary<string, object?>
        {
            ["cirdcode"] = Additional(request.Header.AdditionalFields, "inCategoryCode") ?? "107",
            ["cordcode"] = Additional(request.Header.AdditionalFields, "outCategoryCode") ?? "207",
            ["ccvmemo"] = request.Reason ?? ""
        };
        var options = new Dictionary<string, object?> { ["countPartialSave"] = true,
            ["countByLocation"] = context.CountByLocation, ["countSource"] = context.Source,
            ["countDocumentDate"] = request.Header.DocumentDate.ToString("yyyy-MM-dd"),
            ["countWarehouseCode"] = request.Header.WarehouseCode,
            ["countDepartmentCode"] = request.Header.DepartmentCode };
        var prior = PreviousForUpload(previous ?? [], operation.Lines, context.CountByLocation);
        options["countPreviousLabels"] = prior;
        options["countLabels"] = operation.Lines.Select(l => new { Code = l.LabelCode,
            l.MaterialCode, l.BatchNo, l.WarehouseCode, l.LocationCode,
            TargetWarehouseCode = request.Header.WarehouseCode, TargetLocationCode = TargetLocation(l),
            Balance = l.InventoryQtySnapshot, LatestFlowId = l.TraceBackLatestFlowId, ActualQuantity = l.RequestedQty }).ToArray();
        var lines = new List<IReadOnlyDictionary<string, object?>>();
        var groups = operation.Lines.GroupBy(l => new { l.MaterialCode, l.BatchNo,
            Location = context.CountByLocation ? TargetLocation(l) ?? "" : "" });
        foreach (var group in groups)
        {
            var target = group.Sum(l => l.RequestedQty) + prior.Where(p => Same(p.MaterialCode, group.Key.MaterialCode) &&
                Same(p.BatchNo, group.Key.BatchNo) && (!context.CountByLocation || Same(p.LocationCode, group.Key.Location)))
                .Sum(p => p.ActualQuantity);
            if (context.Source is { } source)
            {
                var matches = source.Lines.Where(l => l.MaterialCode == group.Key.MaterialCode &&
                    (l.BatchNo ?? "") == (group.Key.BatchNo ?? "") &&
                    (!context.CountByLocation || l.LocationCode == group.Key.Location)).ToArray();
                if (matches.Length != 1) throw new ApiException(409, "COUNT_SOURCE_CHANGED", "原单明细需核对。");
                lines.Add(new Dictionary<string, object?> { ["autoid"] = matches[0].Id, ["icvcquantity"] = target });
            }
            else
                lines.Add(new Dictionary<string, object?> { ["cinvcode"] = group.Key.MaterialCode,
                    ["ccvbatch"] = group.Key.BatchNo ?? "", ["cposition"] = group.Key.Location,
                    ["icvquantity"] = 0m, ["icvcquantity"] = target, ["irowno"] = lines.Count + 1 });
        }
        if (context.Source is null)
        {
            header["dcvdate"] = request.Header.DocumentDate.ToString("yyyy-MM-dd");
            header["dacdate"] = request.Header.DocumentDate.ToString("yyyy-MM-dd");
            header["cwhcode"] = request.Header.WarehouseCode;
            header["bposcheck"] = context.CountByLocation ? "1" : "0";
            header["ccvtype"] = "普通仓库盘点"; header["csource"] = "1"; header["vt_id"] = 29;
        }
        return new(operation.ClientRequestId, erp.Code, erp.Name, operation.DeviceId ?? "h5",
            operation.DeviceModel, null, null, null, request.Reason, context.Source?.Document.Version,
            header, lines, [], new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), options);
    }

    private static string? Additional(JsonElement? fields, string name)
    {
        if (fields is not { ValueKind: JsonValueKind.Object } value) return null;
        return value.TryGetProperty("header." + name, out var field) || value.TryGetProperty(name, out field)
            ? field.ToString() : null;
    }

    public static long? SourceId(GatewayDocumentRequest request) =>
        request.Options.TryGetValue("countSource", out var value) && value is InventoryCountSource source ? source.Document.Id : null;

    public static IReadOnlyDictionary<int, (string? LineId, string? PositionId)> ReadRows(
        MaterialOutboundExecutionOperation operation, GatewayDocumentResult result, GatewayDocumentRequest request)
    {
        var rows = new Dictionary<int, (string?, string?)>();
        if (result.Document is not { ValueKind: JsonValueKind.Object } document ||
            !document.TryGetProperty("lines", out var nativeLines)) throw new InvalidOperationException("盘点明细回查不完整。");
        var byLocation = request.Options["countByLocation"] is true;
        foreach (var line in operation.Lines)
        {
            string Text(JsonElement row, string key) => row.TryGetProperty(key, out var v) ? v.ToString().Trim() : "";
            var found = nativeLines.EnumerateArray().Where(r => Text(r, "cinvcode") == line.MaterialCode &&
                Text(r, "ccvbatch") == (line.BatchNo ?? "") &&
                (!byLocation || Text(r, "cposition") == (TargetLocation(line) ?? ""))).ToArray();
            if (found.Length != 1 || string.IsNullOrEmpty(Text(found[0], "autoid")))
                throw new InvalidOperationException("盘点明细回查不完整。");
            rows[line.LineNo] = (Text(found[0], "autoid"), null);
        }
        return rows;
    }
}
