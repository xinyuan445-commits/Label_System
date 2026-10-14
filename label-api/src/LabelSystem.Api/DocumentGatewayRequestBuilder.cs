using System.Globalization;
using System.Text.Json;

namespace LabelSystem.Api;

internal static class DocumentGatewayRequestBuilder
{
    public static GatewayDocumentRequest Build(
        MaterialOutboundExecutionOperation operation,
        DocumentPrepareRequest request,
        ErpOperator erp)
    {
        var red = string.Equals(request.Header.DocumentDirection, "RED", StringComparison.OrdinalIgnoreCase);
        var header = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<IReadOnlyDictionary<string, object?>>(operation.Lines.Count);
        var positions = new List<IReadOnlyDictionary<string, object?>>();
        var used = new HashSet<int>();

        BuildHeader(operation, request, red, header);
        ApplyCustomFields(header, request.Header.AdditionalFields, true);

        foreach (var line in operation.Lines)
        {
            var source = MatchLine(request.Lines, line, used);
            var row = BuildLine(operation, request, line, source, red);
            ApplyCustomFields(row, source.AdditionalFields, false);
            lines.Add(row);
            var position = BuildPosition(operation, line, red);
            if (position is not null) positions.Add(position);
        }

        return new GatewayDocumentRequest(
            operation.ClientRequestId,
            erp.Code,
            erp.Name,
            string.IsNullOrWhiteSpace(operation.DeviceId) ? "h5" : operation.DeviceId,
            operation.DeviceModel,
            null,
            null,
            null,
            request.Reason,
            null,
            header,
            lines,
            positions,
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["redVoucher"] = red
            });
    }

    private static void BuildHeader(
        MaterialOutboundExecutionOperation operation,
        DocumentPrepareRequest request,
        bool red,
        IDictionary<string, object?> header)
    {
        var value = request.Header;
        var date = value.DocumentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        switch (operation.ErpDocumentType)
        {
            case "purchase-arrival":
            case "subcontract-arrival":
                header["ddate"] = date;
                header["cptcode"] = AdditionalText(value.AdditionalFields, "purchaseTypeCode") ?? value.CategoryCode;
                header["cvencode"] = Empty(value.PartnerCode);
                header["cdepcode"] = Empty(value.DepartmentCode);
                header["cpocode"] = operation.SourceDocumentType == "purchase-order" ? Empty(operation.SourceDocumentNo) : null;
                header["cpaycode"] = AdditionalText(value.AdditionalFields, "paymentCode");
                header["cexch_name"] = AdditionalText(value.AdditionalFields, "currencyName") ?? "人民币";
                header["cexch_code"] = AdditionalText(value.AdditionalFields, "currencyCode") ?? "RMB";
                header["iexchrate"] = AdditionalDecimal(value.AdditionalFields, "exchangeRate") ?? 1m;
                header["itaxrate"] = AdditionalDecimal(value.AdditionalFields, "taxRate");
                header["cmemo"] = Empty(request.Reason);
                header["cbustype"] = operation.ErpDocumentType == "subcontract-arrival" ? "委外加工" : "普通采购";
                header["bnegative"] = red;
                break;
            case "purchase-receipt":
                header["ddate"] = date;
                header["cwhcode"] = operation.WarehouseCode;
                header["crdcode"] = Empty(value.CategoryCode);
                header["cdepcode"] = Empty(value.DepartmentCode);
                header["cptcode"] = AdditionalText(value.AdditionalFields, "purchaseTypeCode") ?? value.CategoryCode;
                header["cvencode"] = Empty(value.PartnerCode);
                header["cexch_name"] = AdditionalText(value.AdditionalFields, "currencyName") ?? "人民币";
                header["iexchrate"] = AdditionalDecimal(value.AdditionalFields, "exchangeRate") ?? 1m;
                header["itaxrate"] = AdditionalDecimal(value.AdditionalFields, "taxRate");
                header["cmemo"] = Empty(request.Reason);
                header["csource"] = SourceTitle(operation.SourceDocumentType, "库存");
                header["cbuscode"] = Empty(operation.SourceDocumentNo);
                header["carvcode"] = operation.SourceDocumentType == "purchase-arrival" ? Empty(operation.SourceDocumentNo) : null;
                if (TrySourceId(operation.SourceDocumentId, out var purchaseSourceId))
                {
                    if (operation.SourceDocumentType == "purchase-arrival") header["iarriveid"] = purchaseSourceId;
                    if (operation.SourceDocumentType == "purchase-order") header["ipurorderid"] = purchaseSourceId;
                }
                break;
            case "sales-delivery":
                header["ddate"] = date;
                header["cstcode"] = AdditionalText(value.AdditionalFields, "salesTypeCode") ?? value.CategoryCode;
                header["cdepcode"] = Empty(value.DepartmentCode);
                header["ccuscode"] = Empty(value.PartnerCode);
                header["cexch_name"] = AdditionalText(value.AdditionalFields, "currencyName") ?? "人民币";
                header["iexchrate"] = AdditionalDecimal(value.AdditionalFields, "exchangeRate") ?? 1m;
                header["itaxrate"] = AdditionalDecimal(value.AdditionalFields, "taxRate");
                header["cpaycode"] = AdditionalText(value.AdditionalFields, "paymentCode");
                header["cshipaddress"] = AdditionalText(value.AdditionalFields, "deliveryAddress");
                header["ccontactname"] = AdditionalText(value.AdditionalFields, "contactName");
                header["cmobilephone"] = AdditionalText(value.AdditionalFields, "contactPhone");
                header["csocode"] = operation.SourceDocumentType == "sales-order" ? Empty(operation.SourceDocumentNo) : null;
                header["cmemo"] = Empty(request.Reason);
                header["cbustype"] = "普通销售";
                header["breturnflag"] = red;
                break;
            case "sales-outbound":
                header["ddate"] = date;
                header["cwhcode"] = operation.WarehouseCode;
                header["crdcode"] = Empty(value.CategoryCode);
                header["cdepcode"] = Empty(value.DepartmentCode);
                header["ccuscode"] = Empty(value.PartnerCode);
                header["cstcode"] = AdditionalText(value.AdditionalFields, "salesTypeCode");
                header["csource"] = SourceTitle(operation.SourceDocumentType, "库存");
                header["cbuscode"] = Empty(operation.SourceDocumentNo);
                header["cdlcode"] = operation.SourceDocumentType == "sales-delivery" ? Empty(operation.SourceDocumentNo) : null;
                header["cordercode"] = operation.SourceDocumentType == "sales-order" ? Empty(operation.SourceDocumentNo) : null;
                header["cdeliverunit"] = AdditionalText(value.AdditionalFields, "deliveryUnit");
                header["cshipaddress"] = AdditionalText(value.AdditionalFields, "deliveryAddress");
                header["ccontactname"] = AdditionalText(value.AdditionalFields, "contactName");
                header["cmobilephone"] = AdditionalText(value.AdditionalFields, "contactPhone");
                header["cmemo"] = Empty(request.Reason);
                break;
            case "transfer":
                header["dtvdate"] = date;
                header["cowhcode"] = operation.WarehouseCode;
                header["ciwhcode"] = Empty(operation.Lines.FirstOrDefault()?.TargetWarehouseCode);
                header["cordcode"] = AdditionalText(value.AdditionalFields, "outCategoryCode") ?? value.CategoryCode;
                header["cirdcode"] = AdditionalText(value.AdditionalFields, "inCategoryCode") ?? value.CategoryCode;
                header["codepcode"] = Empty(value.DepartmentCode);
                header["cidepcode"] = AdditionalText(value.AdditionalFields, "targetDepartmentCode");
                header["ctvmemo"] = Empty(request.Reason);
                header["csource"] = "库存";
                header["cbustype"] = "调拨";
                break;
            case "location-adjustment":
                header["ddate"] = date;
                header["cwhcode"] = operation.WarehouseCode;
                header["cdepcode"] = Empty(value.DepartmentCode);
                header["cmemo"] = Empty(request.Reason);
                break;
            case "inventory-count":
                header["dcvdate"] = date;
                header["dacdate"] = date;
                header["cwhcode"] = operation.WarehouseCode;
                header["cdepcode"] = Empty(value.DepartmentCode);
                header["cirdcode"] = AdditionalText(value.AdditionalFields, "inCategoryCode") ?? "107";
                header["cordcode"] = AdditionalText(value.AdditionalFields, "outCategoryCode") ?? "207";
                header["ccvbatch"] = AdditionalText(value.AdditionalFields, "inventoryBatch");
                header["cposition"] = AdditionalText(value.AdditionalFields, "locationCode");
                header["ccvperiod"] = AdditionalText(value.AdditionalFields, "countPeriod");
                header["ccvtype"] = AdditionalText(value.AdditionalFields, "countType") ?? "普通仓库盘点";
                header["ccvmemo"] = Empty(request.Reason);
                header["csource"] = "1";
                header["vt_id"] = 29;
                break;
            default:
                throw new ApiException(StatusCodes.Status409Conflict,
                    "DOCUMENT_EXECUTION_NOT_READY", "该单据暂未开放 ERP 上传。");
        }
    }

    private static Dictionary<string, object?> BuildLine(
        MaterialOutboundExecutionOperation operation,
        DocumentPrepareRequest request,
        MaterialOutboundExecutionLine line,
        DocumentPrepareLine source,
        bool red)
    {
        var quantity = red ? -line.RequestedQty : line.RequestedQty;
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        switch (operation.ErpDocumentType)
        {
            case "purchase-arrival":
            case "subcontract-arrival":
                row["cwhcode"] = line.WarehouseCode;
                row["cinvcode"] = line.MaterialCode;
                row["cbatch"] = Empty(line.BatchNo);
                row["iquantity"] = quantity;
                row["ivouchrowno"] = line.LineNo;
                row["cbmemo"] = Empty(line.Remark);
                if (operation.SourceDocumentType == "purchase-order") row["iposid"] = line.SourceLineId;
                if (operation.SourceDocumentType is "outsourcing-order" or "subcontract-order")
                {
                    row["cordercode"] = Empty(operation.SourceDocumentNo);
                    row["iorderdid"] = line.SourceLineId;
                }
                break;
            case "purchase-receipt":
                AddInventoryLine(row, line, quantity);
                if (operation.SourceDocumentType == "purchase-arrival")
                {
                    row["iarrsid"] = line.SourceLineId;
                    row["cbarvcode"] = Empty(operation.SourceDocumentNo);
                }
                else if (operation.SourceDocumentType == "purchase-order")
                {
                    row["cpoid"] = line.SourceLineId?.ToString(CultureInfo.InvariantCulture);
                    row["iorderdid"] = line.SourceLineId;
                    row["iordercode"] = Empty(operation.SourceDocumentNo);
                }
                break;
            case "sales-delivery":
                row["cwhcode"] = line.WarehouseCode;
                row["cinvcode"] = line.MaterialCode;
                row["cbatch"] = Empty(line.BatchNo);
                row["iquantity"] = quantity;
                row["irowno"] = line.LineNo;
                row["cbodymemo"] = Empty(line.Remark);
                if (operation.SourceDocumentType == "sales-order")
                {
                    row["isosid"] = line.SourceLineId;
                    row["csocode"] = Empty(operation.SourceDocumentNo);
                }
                break;
            case "sales-outbound":
                AddInventoryLine(row, line, quantity);
                if (operation.SourceDocumentType == "sales-delivery")
                {
                    row["idlsid"] = line.SourceLineId;
                    row["cbdlcode"] = Empty(operation.SourceDocumentNo);
                }
                else if (operation.SourceDocumentType == "sales-order")
                {
                    row["isodid"] = line.SourceLineId;
                    row["csocode"] = Empty(operation.SourceDocumentNo);
                }
                break;
            case "transfer":
                row["cinvcode"] = line.MaterialCode;
                row["ctvbatch"] = Empty(line.BatchNo);
                row["itvquantity"] = line.RequestedQty;
                row["coutposcode"] = Empty(line.LocationCode);
                row["cinposcode"] = Empty(line.TargetLocationCode);
                row["irowno"] = line.LineNo;
                row["cbmemo"] = Empty(line.Remark);
                break;
            case "location-adjustment":
                row["cinvcode"] = line.MaterialCode;
                row["cbposcode"] = Empty(line.LocationCode);
                row["caposcode"] = Empty(line.TargetLocationCode);
                row["cbatch"] = Empty(line.BatchNo);
                row["iquantity"] = line.RequestedQty;
                row["irowno"] = line.LineNo;
                row["cbmemo"] = Empty(line.Remark);
                break;
            case "inventory-count":
                row["cinvcode"] = line.MaterialCode;
                row["cposition"] = Empty(line.LocationCode);
                row["ccvbatch"] = Empty(line.BatchNo);
                row["icvquantity"] = line.InventoryQtySnapshot;
                row["icvcquantity"] = line.RequestedQty;
                row["irowno"] = line.LineNo;
                row["cbmemo"] = Empty(line.Remark);
                break;
            default:
                throw new ApiException(StatusCodes.Status409Conflict,
                    "DOCUMENT_EXECUTION_NOT_READY", "该单据暂未开放 ERP 上传。");
        }
        return row;
    }

    private static void AddInventoryLine(
        IDictionary<string, object?> row,
        MaterialOutboundExecutionLine line,
        decimal quantity)
    {
        row["cinvcode"] = line.MaterialCode;
        row["cbatch"] = Empty(line.BatchNo);
        row["cposition"] = Empty(line.LocationCode);
        row["iquantity"] = quantity;
        row["irowno"] = line.LineNo;
        row["iposflag"] = string.IsNullOrWhiteSpace(line.LocationCode) ? 0 : 1;
        row["cbmemo"] = Empty(line.Remark);
    }

    private static IReadOnlyDictionary<string, object?>? BuildPosition(
        MaterialOutboundExecutionOperation operation,
        MaterialOutboundExecutionLine line,
        bool red)
    {
        if (operation.ErpDocumentType is not ("purchase-receipt" or "sales-outbound") ||
            string.IsNullOrWhiteSpace(line.LocationCode)) return null;
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cWhCode"] = line.WarehouseCode,
            ["cPosCode"] = line.LocationCode,
            ["cInvCode"] = line.MaterialCode,
            ["cBatch"] = Empty(line.BatchNo),
            ["iQuantity"] = red ? -line.RequestedQty : line.RequestedQty
        };
    }

    private static DocumentPrepareLine MatchLine(
        IReadOnlyList<DocumentPrepareLine> candidates,
        MaterialOutboundExecutionLine target,
        HashSet<int> used)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (used.Contains(index)) continue;
            var candidate = candidates[index];
            var labelMatches = target.ObjectType == "UNLABELED_MATERIAL"
                ? candidate.ObjectType == "UNLABELED_MATERIAL"
                : string.Equals(candidate.LabelCode, target.LabelCode, StringComparison.OrdinalIgnoreCase);
            if (!labelMatches ||
                !string.Equals(candidate.MaterialCode, target.MaterialCode, StringComparison.OrdinalIgnoreCase) ||
                candidate.SourceLineId != target.SourceLineId || candidate.RequestedQty != target.RequestedQty) continue;
            used.Add(index);
            return candidate;
        }
        throw new ApiException(StatusCodes.Status409Conflict,
            "ARCHIVE_LINE_MISMATCH", "操作凭证与业务明细不一致，需要核对。");
    }

    private static string SourceTitle(string sourceType, string fallback) => sourceType switch
    {
        "purchase-order" => "采购订单",
        "purchase-arrival" => "采购到货单",
        "sales-order" => "销售订单",
        "sales-delivery" => "发货单",
        "outsourcing-order" or "subcontract-order" => "委外订单",
        _ => fallback
    };

    private static bool TrySourceId(string source, out int value) =>
        int.TryParse(source, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string? AdditionalText(JsonElement? fields, string name)
    {
        if (!TryAdditional(fields, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var result = value.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static decimal? AdditionalDecimal(JsonElement? fields, string name)
    {
        if (!TryAdditional(fields, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number)
            ? number : null;
    }

    private static bool TryAdditional(JsonElement? fields, string name, out JsonElement value)
    {
        value = default;
        if (fields is not { ValueKind: JsonValueKind.Object } root) return false;
        foreach (var property in root.EnumerateObject())
        {
            var candidate = property.Name.StartsWith("header.", StringComparison.OrdinalIgnoreCase)
                ? property.Name[7..]
                : property.Name.StartsWith("body.", StringComparison.OrdinalIgnoreCase)
                    ? property.Name[5..]
                    : property.Name;
            if (!candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        return false;
    }

    private static void ApplyCustomFields(
        IDictionary<string, object?> target,
        JsonElement? fields,
        bool header)
    {
        if (fields is not { ValueKind: JsonValueKind.Object } root) return;
        foreach (var property in root.EnumerateObject())
        {
            var logical = property.Name.StartsWith(header ? "header." : "body.", StringComparison.OrdinalIgnoreCase)
                ? property.Name[(header ? 7 : 5)..]
                : property.Name;
            string? u8Name = null;
            if (logical.StartsWith("custom", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(logical[6..], out var custom)) u8Name = $"cdefine{custom}";
            else if (header && logical.StartsWith("longCustom", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(logical[10..], out var longCustom)) u8Name = $"chdefine{longCustom}";
            else if (!header) u8Name = logical.ToLowerInvariant() switch
            {
                "projectclasscode" => "citem_class",
                "projectcode" => "citemcode",
                _ => null
            };
            if (u8Name is null || target.ContainsKey(u8Name)) continue;
            target[u8Name] = ToObject(property.Value);
        }
    }

    private static object? ToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.ToString()
    };

    private static object? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
