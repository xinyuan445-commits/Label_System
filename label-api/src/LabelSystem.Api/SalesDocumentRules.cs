using System.Text.Json;

namespace LabelSystem.Api;

public static class SalesDocumentRules
{
    public static bool Applies(string type) => type is "delivery-note" or "sales-return";
    public static void RequireDirection(string type, string direction)
    {
        if (type == "delivery-note" && direction != "BLUE")
            throw new ApiException(400, "SALES_DIRECTION_INVALID", "退货请使用退货单。");
        if (type == "sales-return" && direction != "RED")
            throw new ApiException(400, "SALES_DIRECTION_INVALID", "退货单方向无效，请重新打开。");
    }

    public static void ValidateSources(string type, DocumentPrepareRequest request, IReadOnlyList<SourceDocumentLine> current)
    {
        RequireDirection(type, request.Header.DocumentDirection);
        if (type == "sales-return")
        {
            foreach (var order in current.Where(s => s.SalesOrderLineId.HasValue && s.SalesOrderAvailableQuantity.HasValue)
                         .GroupBy(s => s.SalesOrderLineId))
            {
                var ids = order.Select(s => s.SourceLineId).ToHashSet();
                if (request.Lines.Where(l => l.SourceLineId.HasValue && ids.Contains(l.SourceLineId.Value)).Sum(l => l.RequestedQty) >
                    order.Min(s => s.SalesOrderAvailableQuantity!.Value))
                    throw new ApiException(409, "SOURCE_QUANTITY_EXCEEDED", "超过订单可退数量，请重新选单。");
            }
        }
        var groups = request.Lines.Where(l => l.SourceLineId.HasValue).GroupBy(l => (l.SourceType, l.SourceLineId));
        foreach (var group in groups)
        {
            bool allowed = type == "delivery-note" ? group.Key.SourceType == "sales-order" :
                group.Key.SourceType is "sales-order-return" or "sales-delivery-return";
            if (!allowed || request.Header.SourceType != group.Key.SourceType)
                throw new ApiException(400, "SALES_SOURCE_INVALID", "请选择本业务支持的原单。");
            var rows = current.Where(s => s.SourceType == group.Key.SourceType && s.SourceLineId == group.Key.SourceLineId).ToArray();
            var saved = request.SalesSourceLines?.Where(s => s.SourceType == group.Key.SourceType && s.SourceLineId == group.Key.SourceLineId).ToArray();
            if (rows.Length != 1 || saved?.Length != 1)
                throw new ApiException(409, "SALES_SOURCE_CHANGED", "请重选原单，已扫记录保留。");
            var row = rows[0]; var snapshot = saved[0];
            if (group.Any(l => l.SourceDocumentId != row.SourceDocumentId || l.SourceDocumentNo != row.SourceDocumentNo || l.MaterialCode != row.ProductCode) ||
                group.Sum(l => l.RequestedQty) > (row.RemainingQuantity ?? row.OrderQuantity))
                throw new ApiException(409, "SOURCE_QUANTITY_EXCEEDED", type == "sales-return" ? "超过原单可退数量，请重新选单。" : "超过订单剩余数量，请重新选单。");
            if (string.IsNullOrWhiteSpace(row.PartnerCode) || request.Header.PartnerCode != row.PartnerCode ||
                snapshot.PartnerCode != row.PartnerCode || snapshot.ProductCode != row.ProductCode ||
                snapshot.UnitPrice != row.UnitPrice || snapshot.TaxUnitPrice != row.TaxUnitPrice || snapshot.TaxRate != row.TaxRate ||
                snapshot.CurrencyName != row.CurrencyName || snapshot.ExchangeRate != row.ExchangeRate ||
                snapshot.SalesTypeCode != row.SalesTypeCode || Text(request.Header.AdditionalFields, "salesTypeCode") != row.SalesTypeCode ||
                snapshot.SalesOrderLineId != row.SalesOrderLineId || snapshot.SalesOrderNo != row.SalesOrderNo ||
                snapshot.SourceDocumentId != row.SourceDocumentId || snapshot.SourceDocumentNo != row.SourceDocumentNo)
                throw new ApiException(409, "SALES_SOURCE_CHANGED", "原单客户或价格已变化，请重新选单。");
            if (Text(request.Header.AdditionalFields, "currencyName") != row.CurrencyName ||
                !decimal.TryParse(Text(request.Header.AdditionalFields, "exchangeRate"), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var rate) || rate != row.ExchangeRate)
                throw new ApiException(409, "SALES_SOURCE_CHANGED", "原单币种或汇率不一致，请重新选单。");
        }
    }

    private static string? Text(JsonElement? fields, string name) => fields is { ValueKind: JsonValueKind.Object } value &&
        (value.TryGetProperty("header." + name, out var field) || value.TryGetProperty(name, out field)) ? field.ToString() : null;
}
