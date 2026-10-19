using System;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using U8Gateway.Contracts.TraceBack;

namespace U8Gateway.U8.TraceBack
{
    public static class InitialRegistrationRules
    {
        public static string Hash(InitialLabelRegistrationRequest r)
        {
            var fields = new[] { "1", r.EventKey, r.Code, r.SerialNo,
                r.Quantity.ToString("0.######", CultureInfo.InvariantCulture),
                r.OrderUuid.ToLowerInvariant(), r.GoodsUuid.ToLowerInvariant(), r.OperatorName,
                r.MaterialCode, r.MaterialName, r.UnitCode, r.BatchNo, r.TargetWarehouseCode,
                r.TargetLocationCode, r.ManufactureDate ?? "", r.ExpiryDate ?? "", r.AttributesJson ?? "",
                r.SourceDocumentType, r.SourceDocumentId, r.SourceDocumentNo, r.ErpDetailId ?? "" };
            string text = string.Concat(fields.Select(s => Encoding.UTF8.GetByteCount(s ?? "")
                .ToString(CultureInfo.InvariantCulture) + ":" + (s ?? "")));
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }
        public static void Validate(InitialLabelRegistrationRequest r)
        {
            if (r == null || r.ContractVersion != 1 || !r.ExpectedAbsent || r.ExpectedLatestFlowId != 0 ||
                r.ExpectedBalance != 0 || string.IsNullOrWhiteSpace(r.EventKey) || r.EventKey.Length > 200 ||
                string.IsNullOrWhiteSpace(r.Code) || r.Code.Length > 100 || r.Code.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(r.SerialNo) || r.SerialNo.Length > 200 ||
                r.Quantity <= 0 || r.Quantity > 999999999999.999999m || decimal.Round(r.Quantity, 6) != r.Quantity ||
                !Guid.TryParse(r.OrderUuid, out _) || !Guid.TryParse(r.GoodsUuid, out _) ||
                string.IsNullOrWhiteSpace(r.MaterialCode) || r.MaterialCode.Length > 100 ||
                string.IsNullOrWhiteSpace(r.MaterialName) || r.MaterialName.Length > 200 ||
                string.IsNullOrWhiteSpace(r.OperatorName) || r.OperatorName.Length > 100 ||
                string.IsNullOrWhiteSpace(r.SourceDocumentType) || r.SourceDocumentType.Length > 50 ||
                string.IsNullOrWhiteSpace(r.SourceDocumentNo) || r.SourceDocumentNo.Length > 100 ||
                (r.SourceDocumentId ?? "").Length > 100 || (r.ErpDetailId ?? "").Length > 100 ||
                string.IsNullOrWhiteSpace(r.UnitCode) || r.UnitCode.Length > 50 ||
                string.IsNullOrWhiteSpace(r.TargetWarehouseCode) || r.TargetWarehouseCode.Length > 100 ||
                (r.TargetLocationCode ?? "").Length > 100 || (r.BatchNo ?? "").Length > 100 ||
                (r.AttributesJson ?? "").Length > 65536 || !string.Equals(r.PayloadHash, Hash(r), StringComparison.Ordinal))
                throw new TraceBackSyncException("TRACEBACK_INITIAL_INVALID", "首次登记参数无效。");
            foreach (string value in new[] { r.ManufactureDate, r.ExpiryDate })
                if (!string.IsNullOrEmpty(value) && !DateTime.TryParseExact(value, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new TraceBackSyncException("TRACEBACK_INITIAL_INVALID", "标签日期无效。");
            if (!string.IsNullOrEmpty(r.AttributesJson))
                try { JObject.Parse(r.AttributesJson); }
                catch (JsonException) { throw new TraceBackSyncException("TRACEBACK_INITIAL_INVALID", "标签属性无效。"); }
            string[] parts = r.Code.Split('|');
            if (parts.Length >= 4 &&
                (!string.Equals(parts[1], r.MaterialCode, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrEmpty(r.BatchNo) && !string.Equals(parts[2], r.BatchNo, StringComparison.OrdinalIgnoreCase))))
                throw new TraceBackSyncException("TRACEBACK_INITIAL_CODE_INCOMPATIBLE", "编码规则与当前追溯读取不兼容。");
        }
    }

    public sealed partial class TraceBackSyncWriter
    {
        public void ValidateInitial(InitialLabelRegistrationRequest request)
        {
            InitialRegistrationRules.Validate(request);
            using (var c = new SqlConnection(_connectionString))
            {
                c.Open();
                using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
                {
                    ValidateInitialMasterData(c, tx, request);
                    using (var cmd = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code;", c, tx))
                    {
                        AddVarchar(cmd, "@code", request.Code, 3500);
                        if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                            throw new TraceBackSyncException("TRACEBACK_INITIAL_CONFLICT", "标签码已存在，请核对。");
                    }
                    tx.Commit();
                }
            }
        }
        public InitialLabelRegistrationResult ApplyInitial(InitialLabelRegistrationRequest request)
        {
            InitialRegistrationRules.Validate(request);
            bool test = string.Equals(_environment, "test", StringComparison.OrdinalIgnoreCase);
            bool production = string.Equals(_environment, "production", StringComparison.OrdinalIgnoreCase);
            if ((!test && !(production && _productionWritesEnabled)) ||
                !Same(_database, _writeAllowedDatabase) || !Same(AccountCode(_accountId), AccountCode(_writeAllowedAccountId)))
                throw new TraceBackSyncException("TRACEBACK_WRITE_ACCOUNT_BLOCKED", "追溯写入未绑定当前账套。");

            long flowId;
            bool applied;
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (var tx = connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    AcquireLock(connection, tx, "TraceBackEvent:" + request.EventKey);
                    AcquireLock(connection, tx, "TraceBackCode:" + request.Code);
                    AcquireLock(connection, tx, "TraceBackOrder:" + request.OrderUuid);
                    var existing = ReadExistingEvent(connection, tx, request.GoodsUuid);
                    if (existing.FlowCount > 0 || existing.GoodsCount > 0)
                    {
                        if (existing.FlowCount != 1 || existing.GoodsCount != 1 ||
                            !Same(existing.Code, request.Code) || !Same(existing.OrderUuid, request.OrderUuid) ||
                            existing.Quantity != request.Quantity)
                            throw new TraceBackSyncException("TRACEBACK_EVENT_CONFLICT", "首次登记事件内容冲突。");
                        VerifyInitialJson(connection, tx, request);
                        flowId = existing.FlowId;
                        applied = true;
                    }
                    else
                    {
                        using (var check = new SqlCommand(@"
SELECT (SELECT COUNT_BIG(*) FROM dbo.TraceBackOnlyCode WITH(UPDLOCK,HOLDLOCK) WHERE traceBackCode=@code),
       (SELECT COUNT_BIG(*) FROM dbo.TraceBackOrder WITH(UPDLOCK,HOLDLOCK) WHERE pdaOrderUuid=@order);", connection, tx))
                        {
                            AddVarchar(check, "@code", request.Code, 3500);
                            AddVarchar(check, "@order", request.OrderUuid, 3500);
                            using (var reader = check.ExecuteReader())
                            {
                                reader.Read();
                                if (reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0)
                                    throw new TraceBackSyncException("TRACEBACK_INITIAL_CONFLICT", "标签码或首次关联已存在，请核对。");
                            }
                        }
                        JObject names = ValidateInitialMasterData(connection, tx, request);
                        var marker = new JObject
                        {
                            ["contractVersion"] = 1, ["eventKey"] = request.EventKey,
                            ["payloadHash"] = request.PayloadHash, ["sourceType"] = request.SourceDocumentType,
                            ["sourceId"] = request.SourceDocumentId, ["documentNumber"] = request.SourceDocumentNo,
                            ["detailId"] = request.ErpDetailId
                        };
                        var order = new JObject
                        {
                            ["documentNumber"] = request.SourceDocumentNo, ["orderType"] = "打印平台",
                            ["erpDocumentNo"] = request.SourceDocumentNo, ["labelPrintSource"] = marker.DeepClone()
                        };
                        var goods = new JObject
                        {
                            ["materialCode"] = request.MaterialCode, ["name"] = request.MaterialName,
                            ["batchNo"] = request.BatchNo, ["unitCode"] = request.UnitCode,
                            ["warehouseCode"] = request.TargetWarehouseCode, ["warehouseName"] = names["warehouse"],
                            ["storageLocationCode"] = request.TargetLocationCode, ["locationName"] = names["location"],
                            ["quantity"] = request.Quantity, ["manufactureDate"] = request.ManufactureDate,
                            ["expiryDate"] = request.ExpiryDate, ["labelPrintSource"] = marker.DeepClone(),
                            ["attributes"] = string.IsNullOrEmpty(request.AttributesJson) ? new JObject() : JObject.Parse(request.AttributesJson),
                            ["inventory"] = new JObject
                            {
                                ["code"] = request.MaterialCode, ["warehouseId"] = request.TargetWarehouseCode,
                                ["warehouse"] = names["warehouse"], ["storageLocationCode"] = request.TargetLocationCode,
                                ["storageLocation"] = names["location"], ["unitCode"] = request.UnitCode
                            }
                        };
                        using (var insert = new SqlCommand(@"
INSERT dbo.TraceBackOrder(json,pdaOrderUuid,orderType,[time]) VALUES(@json,@uuid,N'打印平台',GETDATE());", connection, tx))
                        {
                            insert.Parameters.Add("@json", SqlDbType.NText).Value = order.ToString(Formatting.None);
                            AddVarchar(insert, "@uuid", request.OrderUuid, 3500);
                            insert.ExecuteNonQuery();
                        }
                        using (var insert = new SqlCommand(@"
INSERT dbo.TraceBackGoods(json,pdaGoodsUuid,[time],uploadOrderNumber,accountCode,ErpDetailId)
VALUES(@json,@uuid,GETDATE(),@number,@account,@detail);", connection, tx))
                        {
                            insert.Parameters.Add("@json", SqlDbType.NText).Value = goods.ToString(Formatting.None);
                            AddVarchar(insert, "@uuid", request.GoodsUuid, 3500);
                            AddNullableVarchar(insert, "@number", request.SourceDocumentNo, 1000);
                            AddNullableVarchar(insert, "@account", AccountCode(_accountId), 1000);
                            AddNullableVarchar(insert, "@detail", request.ErpDetailId, 1000);
                            insert.ExecuteNonQuery();
                        }
                        flowId = InsertFlow(connection, tx, new TraceBackSyncEventRequest
                        {
                            Code = request.Code, Quantity = request.Quantity, OrderUuid = request.OrderUuid,
                            GoodsUuid = request.GoodsUuid, OperatorName = request.OperatorName
                        }, request.SerialNo);
                        applied = false;
                    }
                    tx.Commit();
                }
            }
            return new InitialLabelRegistrationResult
            {
                EventKey = request.EventKey, PayloadHash = request.PayloadHash,
                AlreadyApplied = applied, FlowId = flowId,
                Snapshot = new TraceBackSnapshotReader(_server, _database, _user, _password)
                    .GetSnapshots(new[] { request.Code })[0]
            };
        }

        private void VerifyInitialJson(SqlConnection c, SqlTransaction tx, InitialLabelRegistrationRequest r)
        {
            using (var cmd = new SqlCommand(@"
SELECT CONVERT(nvarchar(max),o.json),CONVERT(nvarchar(max),g.json),o.orderType,g.accountCode,g.uploadOrderNumber,g.ErpDetailId,
 f.sn,f.[User],f.traceBackType
FROM dbo.TraceBackOrder o JOIN dbo.TraceBackGoods g ON g.pdaGoodsUuid=@goods
JOIN dbo.TraceBackOnlyCode f ON f.pdaGoodsUuid=g.pdaGoodsUuid AND f.pdaOrderUuid=o.pdaOrderUuid
WHERE o.pdaOrderUuid=@order;", c, tx))
            {
                AddVarchar(cmd, "@goods", r.GoodsUuid, 3500);
                AddVarchar(cmd, "@order", r.OrderUuid, 3500);
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read()) throw new TraceBackSyncException("TRACEBACK_EVENT_CONFLICT", "首次关联不完整。");
                    try
                    {
                        var order = JObject.Parse(reader.GetString(0));
                        var goods = JObject.Parse(reader.GetString(1));
                        if ((string)order["labelPrintSource"]?["eventKey"] != r.EventKey ||
                            (string)order["labelPrintSource"]?["payloadHash"] != r.PayloadHash ||
                            (string)goods["labelPrintSource"]?["eventKey"] != r.EventKey ||
                            (string)goods["labelPrintSource"]?["payloadHash"] != r.PayloadHash ||
                            (string)goods["materialCode"] != r.MaterialCode ||
                            (string)goods["unitCode"] != r.UnitCode ||
                            (string)goods["name"] != r.MaterialName ||
                            (string)goods["batchNo"] != r.BatchNo ||
                            (string)goods["warehouseCode"] != r.TargetWarehouseCode ||
                            (string)goods["storageLocationCode"] != r.TargetLocationCode ||
                            (decimal?)goods["quantity"] != r.Quantity ||
                            (string)goods["manufactureDate"] != r.ManufactureDate ||
                            (string)goods["expiryDate"] != r.ExpiryDate ||
                            (string)order["documentNumber"] != r.SourceDocumentNo ||
                            (string)order["labelPrintSource"]?["sourceType"] != r.SourceDocumentType ||
                            (string)order["labelPrintSource"]?["sourceId"] != r.SourceDocumentId ||
                            (string)order["labelPrintSource"]?["detailId"] != r.ErpDetailId ||
                            !Same(Convert.ToString(reader[2]),"打印平台") ||
                            !Same(Convert.ToString(reader[3]),AccountCode(_accountId)) ||
                            Convert.ToString(reader[4])!=r.SourceDocumentNo ||
                            Convert.ToString(reader[5])!=(r.ErpDetailId??"") ||
                            Convert.ToString(reader[6])!=r.SerialNo ||
                            Convert.ToString(reader[7])!=r.OperatorName ||
                            Convert.ToString(reader[8])!="条码" ||
                            !JToken.DeepEquals(goods["attributes"], string.IsNullOrEmpty(r.AttributesJson)
                                ? new JObject() : JObject.Parse(r.AttributesJson)) || reader.Read())
                            throw new TraceBackSyncException("TRACEBACK_EVENT_CONFLICT", "首次登记载荷冲突。");
                    }
                    catch (JsonException) { throw new TraceBackSyncException("TRACEBACK_EVENT_CONFLICT", "首次登记内容无效。"); }
                }
            }
        }
        private static JObject ValidateInitialMasterData(SqlConnection c, SqlTransaction tx, InitialLabelRegistrationRequest r)
        {
            using (var cmd = new SqlCommand(@"
SELECT i.cComunitCode,i.bInvBatch,w.cWhName,w.bWhPos,
 (SELECT TOP(1) cPosName FROM dbo.Position WHERE cWhCode=@warehouse AND cPosCode=@location)
FROM dbo.Inventory i CROSS JOIN dbo.Warehouse w
WHERE i.cInvCode=@material AND i.dEDate IS NULL AND w.cWhCode=@warehouse AND w.dWhEndDate IS NULL;", c, tx))
            {
                cmd.Parameters.Add("@material", SqlDbType.NVarChar, 100).Value = r.MaterialCode;
                cmd.Parameters.Add("@warehouse", SqlDbType.NVarChar, 100).Value = r.TargetWarehouseCode;
                cmd.Parameters.Add("@location", SqlDbType.NVarChar, 100).Value = r.TargetLocationCode ?? "";
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read() || !Same(Convert.ToString(reader[0]), r.UnitCode) ||
                        (Convert.ToBoolean(reader[1]) && string.IsNullOrWhiteSpace(r.BatchNo)) ||
                        (Convert.ToBoolean(reader[3]) && (string.IsNullOrWhiteSpace(r.TargetLocationCode) || reader.IsDBNull(4))) ||
                        (!Convert.ToBoolean(reader[3]) && !string.IsNullOrWhiteSpace(r.TargetLocationCode)))
                        throw new TraceBackSyncException("TRACEBACK_INITIAL_MASTER_INVALID", "物料、单位、批次或仓位无效。");
                    return new JObject { ["warehouse"] = reader.GetString(2).Trim(),
                        ["location"] = reader.IsDBNull(4) ? "" : reader.GetString(4).Trim() };
                }
            }
        }
    }
}
