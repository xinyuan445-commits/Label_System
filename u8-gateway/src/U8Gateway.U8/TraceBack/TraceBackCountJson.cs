using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using U8Gateway.Contracts.TraceBack;

namespace U8Gateway.U8.TraceBack
{
    // Count quantities differ from movement quantities. Never retain the previous
    // movement's transfer fields, source allocation or cached warehouse stock.
    public static class TraceBackCountJson
    {
        public static void Validate(TraceBackSyncEventRequest request)
        {
            var c = request.CountContext;
            DateTime date;
            if (c == null || c.OperationId <= 0 || c.OperationLineId <= 0 ||
                !DateTime.TryParseExact(c.DocumentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
                string.IsNullOrWhiteSpace(c.WarehouseCode) || string.IsNullOrWhiteSpace(c.InCategoryCode) ||
                string.IsNullOrWhiteSpace(c.OutCategoryCode) || string.IsNullOrWhiteSpace(request.PdaDocumentNo) ||
                string.IsNullOrWhiteSpace(request.ErpDocumentNo) || string.IsNullOrWhiteSpace(request.ErpDetailId) ||
                c.TotalQuantity < 0 || (c.SourceDocumentId.HasValue &&
                    (c.SourceDocumentId <= 0 || string.IsNullOrWhiteSpace(c.SourceDocumentNo))))
                throw new TraceBackSyncException("COUNT_TRACE_CONTEXT_REQUIRED", "盘点追溯资料不完整。");
        }

        public static JObject Goods(TraceBackSyncEventRequest request, string sourceJson)
        {
            Validate(request);
            var json = JObject.Parse(sourceJson);
            decimal actual = request.ExpectedBalance + request.Quantity;
            if (actual < 0) throw new TraceBackSyncException("TRACEBACK_NEGATIVE_BALANCE", "盘点余额不能为负数。");
            json["pdaID"] = request.CountContext.OperationLineId;
            json["pdaOrderNumber"] = request.PdaDocumentNo;
            json["quantity"] = actual;
            json["quantity2"] = actual;
            if (json["countQuantity"] != null) json["countQuantity"] = actual;
            var attributes = json["Attribute"] as JObject;
            if (attributes == null) { attributes = new JObject(); json["Attribute"] = attributes; }
            attributes["quantity2"] = actual;
            attributes["quantityD11"] = request.ExpectedBalance;
            foreach (string name in new[] { "SourceOrder", "soPdaId", "mergeDataJson" }) json.Remove(name);
            var inventory = json["inventory"] as JObject;
            inventory?.Remove("onHandQuantity");
            inventory?.Remove("availableQuantity");
            var u8 = json["CurrencyGoodsData"]?["U8"] as JObject;
            if (u8 != null)
                foreach (string name in new[] { "transferInWarehouse", "transferInLocation", "transferOutWarehouse",
                    "transferOutLocation", "beforeAdjustmentStorageLocation", "adjustedStorageLocation" }) u8.Remove(name);
            // CurrencyGoodsData.uniquekeyGuid is a legacy goods identity, not the
            // TraceBackOnlyCode.pdaGoodsUuid. Samples prove they need not be equal.
            return json;
        }

        private static JObject Archive(string code, string name) =>
            string.IsNullOrWhiteSpace(code) ? null : new JObject { ["id"] = code, ["name"] = name ?? code, ["code"] = code };

        public static void Position(JObject json, TraceBackSyncEventRequest request, string warehouseName, string locationName)
        {
            if (request.TargetWarehouseCode == null) return;
            json["warehouseCode"] = request.TargetWarehouseCode; json["warehouseName"] = warehouseName;
            foreach (string alias in new[] { "wareHouseCode", "cWhCode", "仓库编码" })
                if (json[alias] != null) json[alias] = request.TargetWarehouseCode;
            if (request.TargetLocationCode == null) return;
            json["locationCode"] = request.TargetLocationCode; json["storageLocationCode"] = request.TargetLocationCode;
            json["locationName"] = locationName;
            foreach (string alias in new[] { "positionCode", "cPosCode", "货位编码" })
                if (json[alias] != null) json[alias] = request.TargetLocationCode;
            foreach (string alias in new[] { "positionName", "storageLocation", "cPosName", "货位名称" })
                if (json[alias] != null) json[alias] = locationName;
            var inventory = json["inventory"] as JObject;
            if (inventory != null)
            {
                inventory["warehouseId"] = request.TargetWarehouseCode; inventory["warehouse"] = warehouseName;
                inventory["storageLocationId"] = request.TargetLocationCode; inventory["storageLocationCode"] = request.TargetLocationCode;
                inventory["storageLocation"] = locationName;
                foreach (string alias in new[] { "locationCode", "positionCode", "cPosCode" })
                    if (inventory[alias] != null) inventory[alias] = request.TargetLocationCode;
            }
        }

        public static JObject Order(TraceBackSyncEventRequest request, string warehouseName,
            string inCategoryName, string outCategoryName, string departmentName)
        {
            Validate(request);
            var c = request.CountContext;
            var data = new JObject {
                ["U8"] = new JObject {
                    ["documentType"] = Archive("0", "蓝单"), ["cWhCode"] = c.WarehouseCode,
                    ["bPosCheck"] = new JObject { ["name"] = c.CountByLocation ? "1" : "0" } },
                ["phoneMac"] = c.DeviceId ?? "", ["pdaUser"] = request.OperatorName ?? "",
                ["department"] = Archive(c.DepartmentCode, departmentName), ["salesman"] = null,
                ["warehouse"] = Archive(c.WarehouseCode, warehouseName),
                ["inboundCategory"] = Archive(c.InCategoryCode, inCategoryName),
                ["outboundCategory"] = Archive(c.OutCategoryCode, outCategoryName),
                ["uniquekeyGuid"] = request.OrderUuid, ["AcceptDate"] = c.DocumentDate,
                ["uploadedErpOrder"] = request.ErpDocumentNo, ["remark"] = c.Remark ?? "", ["automaticJumpTo"] = false };
            var display = new JArray();
            if (c.SourceDocumentId.HasValue)
            {
                data["SourceVoucherID"] = c.SourceDocumentId.Value.ToString(CultureInfo.InvariantCulture);
                data["SourceVoucherCode"] = c.SourceDocumentNo;
                data["sourceOrderType"] = "盘点单";
                display.Add(new JObject { ["title"] = "源单", ["message"] = c.SourceDocumentNo + "," });
            }
            display.Add(new JObject { ["title"] = "日期", ["message"] = c.DocumentDate });
            display.Add(new JObject { ["title"] = "盘点仓库", ["message"] = warehouseName ?? c.WarehouseCode });
            display.Add(new JObject { ["title"] = "入库类别", ["message"] = inCategoryName ?? c.InCategoryCode });
            display.Add(new JObject { ["title"] = "出库类别", ["message"] = outCategoryName ?? c.OutCategoryCode });
            display.Add(new JObject { ["title"] = "制单人", ["message"] = request.OperatorName ?? "" });
            display.Add(new JObject { ["title"] = "合计数量", ["message"] = c.TotalQuantity.ToString("0.######", CultureInfo.InvariantCulture) });
            var date = DateTime.ParseExact(c.DocumentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var order = new JObject { ["pdaID"] = c.OperationId, ["documentNumber"] = request.PdaDocumentNo,
                ["orderType"] = "盘点单", ["CurrencyOrderData"] = data, ["documentStatus"] = 2,
                ["isSelect"] = false, ["showList"] = display, ["documentDate"] = c.DocumentDate,
                ["documentDateTs"] = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeMilliseconds() };
            if (c.SourceDocumentId.HasValue) order["SourceOrderList"] = c.SourceDocumentNo + ",";
            return order;
        }
    }
}
