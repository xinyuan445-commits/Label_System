using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using U8Gateway.Contracts.TraceBack;

namespace U8Gateway.U8.TraceBack
{
    public sealed class TraceBackContractException : InvalidOperationException
    {
        public TraceBackContractException(string message) : base(message) { }
    }

    public sealed class TraceBackFlowAggregate
    {
        public string Code { get; set; }
        public int FlowCount { get; set; }
        public int InvalidQuantityCount { get; set; }
        public decimal? Balance { get; set; }
        public long? LatestFlowId { get; set; }
        public string GoodsUuid { get; set; }
        public string OrderUuid { get; set; }
        public string SerialNo { get; set; }
    }

    public sealed class TraceBackRecord
    {
        public IDictionary<string, object> Values { get; set; }
        public IList<JToken> JsonPayloads { get; set; }
        public bool HasInvalidJson { get; set; }
    }

    internal sealed class TraceBackColumnContract
    {
        public SqlDbType SqlDbType { get; set; }
        public int Size { get; set; }
    }

    internal sealed class TraceBackSqlContract
    {
        public TraceBackColumnContract Code { get; set; }
        public TraceBackColumnContract GoodsUuid { get; set; }
        public TraceBackColumnContract OrderUuid { get; set; }
    }

    public static class TraceBackSnapshotMapper
    {
        private static readonly string[] MaterialCodes =
            { "materialCode", "inventoryCode", "invCode", "cInvCode", "存货编码", "料号" };
        private static readonly string[] MaterialNames =
            { "materialName", "inventoryName", "invName", "cInvName", "存货名称", "物料名称" };
        private static readonly string[] BatchNos =
            { "batchNo", "batchCode", "batch", "cBatch", "批号" };
        private static readonly string[] UnitCodes =
            { "unitCode", "mainUnitCode", "cComUnitCode", "计量单位编码", "单位编码", "unit" };
        private static readonly string[] UnitNames =
            { "unitName", "mainUnitName", "计量单位", "单位名称" };
        private static readonly string[] WarehouseCodes =
            { "warehouseCode", "wareHouseCode", "cWhCode", "仓库编码" };
        private static readonly string[] WarehouseNames =
            { "warehouseName", "wareHouseName", "cWhName", "仓库名称" };
        private static readonly string[] LocationCodes =
            { "locationCode", "positionCode", "cPosCode", "货位编码" };
        private static readonly string[] LocationNames =
            { "locationName", "positionName", "cPosName", "货位名称" };
        private static readonly string[] BusinessTypes =
            { "businessType", "operationType", "documentType", "voucherType", "业务类型", "单据类型" };
        private static readonly string[] PdaDocumentNos =
            { "pdaDocumentNo", "pdaOrderNo", "pdaNo", "orderNo", "PDA单号" };
        private static readonly string[] ErpDocumentNos =
            { "erpDocumentNo", "erpOrderNo", "erpNo", "u8Code", "voucherCode", "ERP单号", "U8单号" };

        public static TraceBackLabelSnapshot Map(
            TraceBackFlowAggregate flow,
            IList<TraceBackRecord> goods,
            IList<TraceBackRecord> orders)
        {
            var anomalies = new List<TraceBackAnomaly>();
            if (flow == null || flow.FlowCount == 0)
                return Snapshot(flow?.Code, "NOT_FOUND", anomalies, "TRACEBACK_LABEL_NOT_FOUND", "未找到标签。");

            if (flow.InvalidQuantityCount > 0 || !flow.Balance.HasValue)
                Add(anomalies, "TRACEBACK_QUANTITY_INVALID", "标签数量数据待核对。");
            else if (flow.Balance.Value < 0)
                Add(anomalies, "TRACEBACK_NEGATIVE_BALANCE", "标签余额待核对。");
            if (string.IsNullOrWhiteSpace(flow.GoodsUuid))
                Add(anomalies, "TRACEBACK_GOODS_UUID_MISSING", "标签属性关联缺失。");
            else if (goods == null || goods.Count == 0)
                Add(anomalies, "TRACEBACK_GOODS_MISSING", "标签属性缺失。");
            else if (goods.Count > 1)
                Add(anomalies, "TRACEBACK_GOODS_DUPLICATED", "标签属性重复。");
            TraceBackRecord goodsRow = goods != null && goods.Count == 1 ? goods[0] : null;
            TraceBackRecord orderRow = orders != null && orders.Count == 1 ? orders[0] : null;
            if (goodsRow?.HasInvalidJson == true)
                Add(anomalies, "TRACEBACK_GOODS_JSON_INVALID", "标签属性格式无效。");
            if (orderRow?.HasInvalidJson == true)
                Add(anomalies, "TRACEBACK_ORDER_JSON_INVALID", "标签来源格式无效。");

            string materialCode = Value(goodsRow, MaterialCodes);
            string batchNo = Value(goodsRow, BatchNos);
            string unitCode = Value(goodsRow, UnitCodes);
            if (goodsRow != null && string.IsNullOrWhiteSpace(materialCode))
                Add(anomalies, "TRACEBACK_MATERIAL_MISSING", "标签料号缺失。");
            if (goodsRow != null && string.IsNullOrWhiteSpace(unitCode))
                Add(anomalies, "TRACEBACK_UNIT_MISSING", "标签单位缺失。");

            string[] codeParts = (flow.Code ?? string.Empty).Split('|');
            if (codeParts.Length >= 4)
            {
                if (!string.IsNullOrWhiteSpace(materialCode) &&
                    !Same(codeParts[1], materialCode))
                    Add(anomalies, "TRACEBACK_CODE_MATERIAL_CONFLICT", "标签料号不一致。");
                if (!string.IsNullOrWhiteSpace(batchNo) &&
                    !Same(codeParts[2], batchNo))
                    Add(anomalies, "TRACEBACK_CODE_BATCH_CONFLICT", "标签批号不一致。");
            }
            string serialNo = !string.IsNullOrWhiteSpace(flow.SerialNo)
                ? flow.SerialNo
                : codeParts.Length >= 5 ? codeParts[codeParts.Length - 1].Trim() : null;

            string warehouseCode = Value(goodsRow, WarehouseCodes);
            string locationCode = Value(goodsRow, LocationCodes);
            string identity = Hash(string.Join("\u001f", new[]
            {
                materialCode, batchNo, unitCode, warehouseCode, locationCode
            }).ToUpperInvariant());
            string status = anomalies.Count > 0
                ? "RECONCILIATION_REQUIRED"
                : flow.Balance == 0 ? "USED_UP" : "AVAILABLE";

            return new TraceBackLabelSnapshot
            {
                Code = flow.Code,
                SerialNo = serialNo,
                Balance = flow.Balance,
                LatestFlowId = flow.LatestFlowId,
                GoodsUuid = flow.GoodsUuid,
                OrderUuid = flow.OrderUuid,
                MaterialCode = materialCode,
                MaterialName = Value(goodsRow, MaterialNames),
                BatchNo = batchNo,
                UnitCode = unitCode,
                UnitName = Value(goodsRow, UnitNames),
                WarehouseCode = warehouseCode,
                WarehouseName = Value(goodsRow, WarehouseNames),
                LocationCode = locationCode,
                LocationName = Value(goodsRow, LocationNames),
                SourceBusinessType = Value(orderRow, BusinessTypes),
                PdaDocumentNo = Value(orderRow, PdaDocumentNos),
                ErpDocumentNo = Value(orderRow, ErpDocumentNos),
                IdentityHash = identity,
                Status = status,
                Anomalies = anomalies
            };
        }

        private static TraceBackLabelSnapshot Snapshot(
            string code, string status, IList<TraceBackAnomaly> anomalies,
            string anomalyCode, string message)
        {
            Add(anomalies, anomalyCode, message);
            return new TraceBackLabelSnapshot
            {
                Code = code,
                Status = status,
                Anomalies = anomalies
            };
        }

        private static string Value(TraceBackRecord record, params string[] names)
        {
            if (record == null) return null;
            foreach (string name in names)
            {
                object value;
                if (TryDictionary(record.Values, name, out value))
                {
                    string text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                foreach (JToken json in record.JsonPayloads ?? new List<JToken>())
                {
                    JToken token = Find(json, name);
                    if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Object ||
                        token.Type == JTokenType.Array) continue;
                    string text = token.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            return null;
        }

        private static bool TryDictionary(IDictionary<string, object> values, string name, out object value)
        {
            value = null;
            if (values == null) return false;
            foreach (KeyValuePair<string, object> pair in values)
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return value != null && value != DBNull.Value;
                }
            return false;
        }

        private static JToken Find(JToken token, string name)
        {
            JObject obj = token as JObject;
            if (obj != null)
            {
                foreach (JProperty property in obj.Properties())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                        return property.Value;
                    JToken nested = Find(property.Value, name);
                    if (nested != null) return nested;
                }
            }
            JArray array = token as JArray;
            if (array != null)
                foreach (JToken item in array)
                {
                    JToken nested = Find(item, name);
                    if (nested != null) return nested;
                }
            return null;
        }

        private static bool Same(string left, string right) =>
            string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);

        private static void Add(IList<TraceBackAnomaly> anomalies, string code, string message) =>
            anomalies.Add(new TraceBackAnomaly { Code = code, Message = message });

        private static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty);
        }
    }

    public sealed class TraceBackSnapshotReader
    {
        private readonly string _connectionString;

        public TraceBackSnapshotReader(string server, string database, string user, string password)
        {
            _connectionString = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                UserID = user,
                Password = password,
                IntegratedSecurity = false,
                Encrypt = false,
                TrustServerCertificate = true,
                ConnectTimeout = 8,
                ApplicationName = "U8Gateway.TraceBack"
            }.ConnectionString;
        }

        public IList<TraceBackLabelSnapshot> GetSnapshots(IList<string> requestedCodes)
        {
            string[] codes = (requestedCodes ?? new List<string>())
                .Select(code => (code ?? string.Empty).Trim())
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (codes.Length == 0 || codes.Length > 100 || codes.Any(code => code.Length > 100))
                throw new ArgumentException("条码数量无效。", nameof(requestedCodes));

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                TraceBackSqlContract contract = ReadContract(connection);
                IList<TraceBackFlowAggregate> flows = ReadFlows(connection, codes, contract.Code);
                Dictionary<string, IList<TraceBackRecord>> goods = ReadRecords(
                    connection, "dbo.TraceBackGoods", "pdaGoodsUuid",
                    flows.Select(item => item.GoodsUuid), contract.GoodsUuid);
                Dictionary<string, IList<TraceBackRecord>> orders = ReadRecords(
                    connection, "dbo.TraceBackOrder", "pdaOrderUuid",
                    flows.Select(item => item.OrderUuid), contract.OrderUuid);

                var byCode = flows.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
                var result = new List<TraceBackLabelSnapshot>(codes.Length);
                foreach (string code in codes)
                {
                    TraceBackFlowAggregate flow;
                    if (!byCode.TryGetValue(code, out flow))
                        flow = new TraceBackFlowAggregate { Code = code };
                    IList<TraceBackRecord> goodsRows;
                    IList<TraceBackRecord> orderRows;
                    goods.TryGetValue(flow.GoodsUuid ?? string.Empty, out goodsRows);
                    orders.TryGetValue(flow.OrderUuid ?? string.Empty, out orderRows);
                    result.Add(TraceBackSnapshotMapper.Map(
                        flow,
                        goodsRows ?? new List<TraceBackRecord>(),
                        orderRows ?? new List<TraceBackRecord>()));
                }
                return result;
            }
        }

        private static TraceBackSqlContract ReadContract(SqlConnection connection)
        {
            var required = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["TraceBackOnlyCode"] = new[] { "id", "traceBackCode", "sum", "pdaGoodsUuid", "pdaOrderUuid" },
                ["TraceBackGoods"] = new[] { "pdaGoodsUuid" },
                ["TraceBackOrder"] = new[] { "pdaOrderUuid" }
            };
            const string sql = @"
select t.name, c.name, ty.name, c.max_length
from sys.tables t
join sys.schemas s on s.schema_id=t.schema_id
join sys.columns c on c.object_id=t.object_id
join sys.types ty on ty.user_type_id=c.user_type_id
where s.name=N'dbo' and t.name in (N'TraceBackOnlyCode',N'TraceBackGoods',N'TraceBackOrder');";
            var actual = new Dictionary<string, Dictionary<string, TraceBackColumnContract>>(
                StringComparer.OrdinalIgnoreCase);
            using (var command = new SqlCommand(sql, connection))
            using (SqlDataReader reader = command.ExecuteReader())
                while (reader.Read())
                {
                    string table = reader.GetString(0);
                    Dictionary<string, TraceBackColumnContract> columns;
                    if (!actual.TryGetValue(table, out columns))
                    {
                        columns = new Dictionary<string, TraceBackColumnContract>(
                            StringComparer.OrdinalIgnoreCase);
                        actual.Add(table, columns);
                    }
                    columns[reader.GetString(1)] = ColumnContract(
                        reader.GetString(2), reader.GetInt16(3));
                }
            foreach (KeyValuePair<string, string[]> table in required)
            {
                Dictionary<string, TraceBackColumnContract> columns;
                if (!actual.TryGetValue(table.Key, out columns))
                    throw new TraceBackContractException("TraceBack 数据表缺失。");
                if (table.Value.Any(column => !columns.ContainsKey(column)))
                    throw new TraceBackContractException("TraceBack 数据字段缺失。");
            }
            return new TraceBackSqlContract
            {
                Code = KeyContract(actual["TraceBackOnlyCode"]["traceBackCode"]),
                GoodsUuid = KeyContract(actual["TraceBackGoods"]["pdaGoodsUuid"]),
                OrderUuid = KeyContract(actual["TraceBackOrder"]["pdaOrderUuid"])
            };
        }

        private static IList<TraceBackFlowAggregate> ReadFlows(
            SqlConnection connection, string[] codes, TraceBackColumnContract codeContract)
        {
            string values = string.Join(",", codes.Select((_, index) => "(@code" + index + ")"));
            string sql = @"
with requested(code) as (select code from (values " + values + @") v(code)),
flows as
(
    select o.*
    from dbo.TraceBackOnlyCode o
    join requested r on r.code=o.traceBackCode
),
aggregated as
(
    select traceBackCode,
           count_big(*) flow_count,
           sum(case when try_convert(decimal(28,6), [sum]) is null then 1 else 0 end) invalid_count,
           sum(try_convert(decimal(28,6), [sum])) balance,
           max(id) latest_id
    from flows group by traceBackCode
)
select a.traceBackCode,a.flow_count,a.invalid_count,a.balance,a.latest_id,
       convert(nvarchar(100),l.pdaGoodsUuid),convert(nvarchar(100),l.pdaOrderUuid)
from aggregated a join dbo.TraceBackOnlyCode l on l.id=a.latest_id
order by a.traceBackCode;";
            var result = new List<TraceBackFlowAggregate>();
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 30;
                for (int index = 0; index < codes.Length; index++)
                    AddKeyParameter(command, "@code" + index, codes[index], codeContract);
                using (SqlDataReader reader = command.ExecuteReader())
                    while (reader.Read())
                        result.Add(new TraceBackFlowAggregate
                        {
                            Code = reader.GetString(0).Trim(),
                            FlowCount = checked((int)reader.GetInt64(1)),
                            InvalidQuantityCount = reader.GetInt32(2),
                            Balance = reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3),
                            LatestFlowId = Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                            GoodsUuid = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                            OrderUuid = reader.IsDBNull(6) ? null : reader.GetString(6).Trim()
                        });
            }
            return result;
        }

        private static Dictionary<string, IList<TraceBackRecord>> ReadRecords(
            SqlConnection connection, string table, string keyColumn, IEnumerable<string> requestedKeys,
            TraceBackColumnContract keyContract)
        {
            string[] keys = requestedKeys.Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var result = new Dictionary<string, IList<TraceBackRecord>>(StringComparer.OrdinalIgnoreCase);
            if (keys.Length == 0) return result;
            string parameters = string.Join(",", keys.Select((_, index) => "@key" + index));
            string sql = "select * from " + table + " where " + keyColumn + " in (" + parameters + ");";
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 30;
                for (int index = 0; index < keys.Length; index++)
                    AddKeyParameter(command, "@key" + index, keys[index], keyContract);
                using (SqlDataReader reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        string key = null;
                        for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                        {
                            object value = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
                            values[reader.GetName(ordinal)] = value;
                            if (string.Equals(reader.GetName(ordinal), keyColumn, StringComparison.OrdinalIgnoreCase))
                                key = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
                        }
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        TraceBackRecord record = ParseRecord(values);
                        IList<TraceBackRecord> rows;
                        if (!result.TryGetValue(key, out rows))
                        {
                            rows = new List<TraceBackRecord>();
                            result.Add(key, rows);
                        }
                        rows.Add(record);
                    }
            }
            return result;
        }

        private static TraceBackColumnContract ColumnContract(string typeName, short maxLength)
        {
            switch ((typeName ?? string.Empty).ToLowerInvariant())
            {
                case "varchar":
                case "char":
                    return new TraceBackColumnContract
                    {
                        SqlDbType = SqlDbType.VarChar,
                        Size = maxLength < 0 ? -1 : maxLength
                    };
                case "nvarchar":
                case "nchar":
                    return new TraceBackColumnContract
                    {
                        SqlDbType = SqlDbType.NVarChar,
                        Size = maxLength < 0 ? -1 : Math.Max(1, maxLength / 2)
                    };
                case "uniqueidentifier":
                    return new TraceBackColumnContract
                    {
                        SqlDbType = SqlDbType.UniqueIdentifier,
                        Size = 16
                    };
                default:
                    return new TraceBackColumnContract
                    {
                        SqlDbType = SqlDbType.Variant,
                        Size = 0
                    };
            }
        }

        private static TraceBackColumnContract KeyContract(TraceBackColumnContract contract)
        {
            if (contract.SqlDbType == SqlDbType.Variant)
                throw new TraceBackContractException("TraceBack 关联字段类型不兼容。");
            return contract;
        }

        private static void AddKeyParameter(
            SqlCommand command, string name, string value, TraceBackColumnContract contract)
        {
            SqlParameter parameter = contract.SqlDbType == SqlDbType.UniqueIdentifier
                ? command.Parameters.Add(name, SqlDbType.UniqueIdentifier)
                : command.Parameters.Add(name, contract.SqlDbType, contract.Size);
            Guid guid;
            if (contract.SqlDbType == SqlDbType.UniqueIdentifier)
            {
                if (!Guid.TryParse(value, out guid))
                    throw new TraceBackContractException("TraceBack UUID 数据无效。");
                parameter.Value = guid;
            }
            else
            {
                parameter.Value = value;
            }
        }

        private static TraceBackRecord ParseRecord(IDictionary<string, object> values)
        {
            var json = new List<JToken>();
            bool invalid = false;
            foreach (KeyValuePair<string, object> pair in values)
            {
                string text = pair.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                string trimmed = text.Trim();
                bool looksJson = trimmed.StartsWith("{") || trimmed.StartsWith("[");
                bool namedJson = pair.Key.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!looksJson && !namedJson) continue;
                try { json.Add(JToken.Parse(trimmed)); }
                catch { invalid = true; }
            }
            return new TraceBackRecord { Values = values, JsonPayloads = json, HasInvalidJson = invalid };
        }
    }
}
