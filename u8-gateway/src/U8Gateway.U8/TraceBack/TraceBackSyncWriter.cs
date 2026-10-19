using System;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using Newtonsoft.Json.Linq;
using U8Gateway.Contracts.TraceBack;

namespace U8Gateway.U8.TraceBack
{
    public sealed class TraceBackSyncException : InvalidOperationException
    {
        public TraceBackSyncException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    public sealed partial class TraceBackSyncWriter
    {
        private readonly string _database;
        private readonly string _connectionString;
        private readonly string _server;
        private readonly string _user;
        private readonly string _password;
        private readonly string _environment;
        private readonly bool _productionWritesEnabled;
        private readonly string _accountId;
        private readonly string _writeAllowedDatabase;
        private readonly string _writeAllowedAccountId;

        public TraceBackSyncWriter(
            string server, string database, string user, string password,
            string environment, bool productionWritesEnabled, string accountId,
            string writeAllowedDatabase, string writeAllowedAccountId)
        {
            _database = database ?? string.Empty;
            _server = server;
            _user = user;
            _password = password;
            _environment = environment;
            _productionWritesEnabled = productionWritesEnabled;
            _accountId = accountId;
            _writeAllowedDatabase = writeAllowedDatabase;
            _writeAllowedAccountId = writeAllowedAccountId;
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
                ApplicationName = "U8Gateway.TraceBack.Sync"
            }.ConnectionString;
        }

        public TraceBackSyncEventResult Apply(TraceBackSyncEventRequest request)
        {
            Validate(request);
            bool test = string.Equals(_environment, "test", StringComparison.OrdinalIgnoreCase);
            bool production = string.Equals(
                _environment, "production", StringComparison.OrdinalIgnoreCase);
            if ((!test && !(production && _productionWritesEnabled)) ||
                !Same(_database, _writeAllowedDatabase) ||
                !Same(AccountCode(_accountId), AccountCode(_writeAllowedAccountId)))
                throw new TraceBackSyncException(
                    "TRACEBACK_WRITE_ACCOUNT_BLOCKED", "追溯写入未绑定当前账套。");

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (SqlTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    try
                    {
                        AcquireLock(connection, transaction, "TraceBackEvent:" + request.EventKey);
                        AcquireLock(connection, transaction, "TraceBackOrder:" + request.OrderUuid);

                        ExistingEvent existing = ReadExistingEvent(connection, transaction, request.GoodsUuid);
                        if (existing.FlowCount > 0 || existing.GoodsCount > 0)
                        {
                            if (existing.FlowCount != 1 || existing.GoodsCount != 1 ||
                                !Same(existing.Code, request.Code) ||
                                !Same(existing.OrderUuid, request.OrderUuid) ||
                                existing.Quantity != request.Quantity)
                                throw new TraceBackSyncException(
                                    "TRACEBACK_EVENT_CONFLICT", "追溯同步事件已存在不同内容。");
                            decimal balance = ReadBalance(connection, transaction, request.Code, out _);
                            transaction.Commit();
                            return Result(request, true, existing.FlowId, balance);
                        }

                        long latestId;
                        decimal currentBalance = ReadBalance(connection, transaction, request.Code, out latestId);
                        if (latestId != request.ExpectedLatestFlowId || currentBalance != request.ExpectedBalance)
                            throw new TraceBackSyncException(
                                "TRACEBACK_SNAPSHOT_CHANGED", "标签数据已变化，请重新核对。");

                        SourceGoods source = ReadSourceGoods(connection, transaction, request.Code, latestId);
                        EnsureOrder(connection, transaction, request);
                        InsertGoods(connection, transaction, request, source);
                        long flowId = InsertFlow(connection, transaction, request, source.SerialNo);
                        decimal after = currentBalance + request.Quantity;
                        if (after < 0)
                            throw new TraceBackSyncException(
                                "TRACEBACK_NEGATIVE_BALANCE", "追溯写入后余额不能为负数。");
                        if (request.ValidateOnly)
                        {
                            transaction.Rollback();
                            return new TraceBackSyncEventResult
                            {
                                EventKey = request.EventKey,
                                AlreadyApplied = false,
                                FlowId = flowId,
                                Balance = after,
                                Snapshot = null
                            };
                        }
                        transaction.Commit();
                        return Result(request, false, flowId, after);
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        private TraceBackSyncEventResult Result(
            TraceBackSyncEventRequest request, bool alreadyApplied, long flowId, decimal balance)
        {
            TraceBackLabelSnapshot snapshot = new TraceBackSnapshotReader(
                _server, _database, _user, _password).GetSnapshots(new[] { request.Code })[0];
            return new TraceBackSyncEventResult
            {
                EventKey = request.EventKey,
                AlreadyApplied = alreadyApplied,
                FlowId = flowId,
                Balance = balance,
                Snapshot = snapshot
            };
        }

        private static void Validate(TraceBackSyncEventRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.EventKey) || request.EventKey.Length > 200 ||
                string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 100 ||
                string.IsNullOrWhiteSpace(request.OrderUuid) || string.IsNullOrWhiteSpace(request.GoodsUuid) ||
                !Guid.TryParse(request.OrderUuid, out _) || !Guid.TryParse(request.GoodsUuid, out _) ||
                request.ExpectedLatestFlowId <= 0 || request.ExpectedBalance < 0)
                throw new TraceBackSyncException("TRACEBACK_EVENT_INVALID", "追溯同步参数无效。");
            switch ((request.EventRole ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "MATERIAL_ISSUE":
                case "PARTIAL_SOURCE":
                    if (request.Quantity >= 0) throw InvalidQuantity();
                    break;
                case "MATERIAL_RETURN":
                case "PRINT_TARGET":
                    if (request.Quantity <= 0) throw InvalidQuantity();
                    if (string.IsNullOrWhiteSpace(request.TargetWarehouseCode))
                        throw new TraceBackSyncException("TRACEBACK_TARGET_MISSING", "目标位置缺失。");
                    break;
                case "FULL_MOVE":
                    if (request.Quantity != 0) throw InvalidQuantity();
                    if (string.IsNullOrWhiteSpace(request.TargetWarehouseCode))
                        throw new TraceBackSyncException("TRACEBACK_TARGET_MISSING", "目标位置缺失。");
                    break;
                default:
                    throw new TraceBackSyncException("TRACEBACK_EVENT_ROLE_INVALID", "追溯同步类型无效。");
            }
        }

        private static TraceBackSyncException InvalidQuantity() =>
            new TraceBackSyncException("TRACEBACK_EVENT_QUANTITY_INVALID", "追溯同步数量方向无效。");

        private static void AcquireLock(SqlConnection connection, SqlTransaction transaction, string resource)
        {
            using (var command = new SqlCommand(@"
DECLARE @result int;
EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',
    @LockOwner='Transaction',@LockTimeout=10000;
SELECT @result;", connection, transaction))
            {
                command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
                int result = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (result < 0)
                    throw new TraceBackSyncException("TRACEBACK_EVENT_BUSY", "追溯同步正忙，请稍后重试。");
            }
        }

        private static ExistingEvent ReadExistingEvent(
            SqlConnection connection, SqlTransaction transaction, string goodsUuid)
        {
            using (var command = new SqlCommand(@"
SELECT (SELECT COUNT_BIG(*) FROM dbo.TraceBackGoods WHERE pdaGoodsUuid=@goods_uuid),
       COUNT_BIG(*),MAX(id),MAX(traceBackCode),MAX(pdaOrderUuid),
       MAX(TRY_CONVERT(decimal(28,6),[sum]))
FROM dbo.TraceBackOnlyCode WHERE pdaGoodsUuid=@goods_uuid;", connection, transaction))
            {
                AddVarchar(command, "@goods_uuid", goodsUuid, 3500);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    reader.Read();
                    return new ExistingEvent
                    {
                        GoodsCount = reader.GetInt64(0),
                        FlowCount = reader.GetInt64(1),
                        FlowId = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        Code = reader.IsDBNull(3) ? null : reader.GetString(3),
                        OrderUuid = reader.IsDBNull(4) ? null : reader.GetString(4),
                        Quantity = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5)
                    };
                }
            }
        }

        private static decimal ReadBalance(
            SqlConnection connection, SqlTransaction transaction, string code, out long latestId)
        {
            using (var command = new SqlCommand(@"
SELECT MAX(id),SUM(TRY_CONVERT(decimal(28,6),[sum])),
       SUM(CASE WHEN TRY_CONVERT(decimal(28,6),[sum]) IS NULL THEN 1 ELSE 0 END)
FROM dbo.TraceBackOnlyCode WITH (UPDLOCK,HOLDLOCK) WHERE traceBackCode=@code;",
                connection, transaction))
            {
                AddVarchar(command, "@code", code, 3500);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    reader.Read();
                    if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetInt32(2) != 0)
                        throw new TraceBackSyncException("TRACEBACK_SOURCE_INVALID", "标签来源数据待核对。");
                    latestId = reader.GetInt32(0);
                    return reader.GetDecimal(1);
                }
            }
        }

        private static SourceGoods ReadSourceGoods(
            SqlConnection connection, SqlTransaction transaction, string code, long latestId)
        {
            using (var command = new SqlCommand(@"
SELECT CONVERT(nvarchar(max),g.json),g.uploadOrderNumber,g.MachineCode,g.accountCode,
       g.ErpDetailId,g.BindBoxCode,g.FromDataType,o.sn
FROM dbo.TraceBackOnlyCode o
JOIN dbo.TraceBackGoods g ON g.pdaGoodsUuid=o.pdaGoodsUuid
WHERE o.id=@id AND o.traceBackCode=@code;", connection, transaction))
            {
                command.Parameters.Add("@id", SqlDbType.Int).Value = latestId;
                AddVarchar(command, "@code", code, 3500);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read() || reader.IsDBNull(0))
                        throw new TraceBackSyncException("TRACEBACK_GOODS_MISSING", "标签属性缺失。");
                    var result = new SourceGoods
                    {
                        Json = reader.GetString(0),
                        UploadOrderNumber = Text(reader, 1),
                        MachineCode = Text(reader, 2),
                        AccountCode = Text(reader, 3),
                        ErpDetailId = Text(reader, 4),
                        BindBoxCode = Text(reader, 5),
                        FromDataType = Text(reader, 6),
                        SerialNo = Text(reader, 7)
                    };
                    if (reader.Read())
                        throw new TraceBackSyncException(
                            "TRACEBACK_GOODS_DUPLICATED", "标签属性重复。");
                    return result;
                }
            }
        }

        private static void EnsureOrder(
            SqlConnection connection, SqlTransaction transaction, TraceBackSyncEventRequest request)
        {
            using (var check = new SqlCommand(
                "SELECT COUNT_BIG(*) FROM dbo.TraceBackOrder WITH (UPDLOCK,HOLDLOCK) WHERE pdaOrderUuid=@uuid;",
                connection, transaction))
            {
                AddVarchar(check, "@uuid", request.OrderUuid, 3500);
                long count = Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (count > 1)
                    throw new TraceBackSyncException("TRACEBACK_ORDER_DUPLICATED", "追溯来源记录重复。");
                if (count == 1) return;
            }
            var json = new JObject
            {
                ["documentNumber"] = request.PdaDocumentNo ?? string.Empty,
                ["orderType"] = request.OrderType ?? string.Empty,
                ["CurrencyOrderData"] = new JObject
                {
                    ["uploadedErpOrder"] = request.ErpDocumentNo ?? string.Empty,
                    ["sourceOrderType"] = request.OrderType ?? string.Empty
                }
            }.ToString(Newtonsoft.Json.Formatting.None);
            using (var insert = new SqlCommand(@"
INSERT INTO dbo.TraceBackOrder(json,pdaOrderUuid,orderType,[time])
VALUES(@json,@uuid,@order_type,GETDATE());", connection, transaction))
            {
                insert.Parameters.Add("@json", SqlDbType.NText).Value = json;
                AddVarchar(insert, "@uuid", request.OrderUuid, 3500);
                AddVarchar(insert, "@order_type", request.OrderType, 3500);
                insert.ExecuteNonQuery();
            }
        }

        private static void InsertGoods(
            SqlConnection connection, SqlTransaction transaction,
            TraceBackSyncEventRequest request, SourceGoods source)
        {
            JObject json;
            try { json = JObject.Parse(source.Json); }
            catch { throw new TraceBackSyncException("TRACEBACK_GOODS_JSON_INVALID", "标签属性格式无效。"); }
            decimal quantity = string.Equals(
                request.EventRole, "FULL_MOVE", StringComparison.OrdinalIgnoreCase)
                ? request.ExpectedBalance
                : Math.Abs(request.Quantity);
            TargetNames target = ReadTargetNames(connection, transaction, request);
            json["quantity"] = quantity;
            if (json["countQuantity"] != null)
                json["countQuantity"] = quantity;
            json["pdaOrderNumber"] = request.PdaDocumentNo ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(request.TargetWarehouseCode))
            {
                json["warehouseCode"] = request.TargetWarehouseCode;
                json["warehouseName"] = target.WarehouseName;
            }
            if (request.TargetLocationCode != null)
            {
                json["storageLocationCode"] = request.TargetLocationCode;
                json["locationName"] = target.LocationName;
            }
            JObject inventory = json["inventory"] as JObject;
            if (inventory != null)
            {
                if (!string.IsNullOrWhiteSpace(request.TargetWarehouseCode))
                {
                    inventory["warehouseId"] = request.TargetWarehouseCode;
                    inventory["warehouse"] = target.WarehouseName;
                }
                if (request.TargetLocationCode != null)
                {
                    inventory["storageLocationId"] = request.TargetLocationCode;
                    inventory["storageLocationCode"] = request.TargetLocationCode;
                    inventory["storageLocation"] = target.LocationName;
                }
            }
            using (var insert = new SqlCommand(@"
INSERT INTO dbo.TraceBackGoods
    (json,pdaGoodsUuid,[time],uploadOrderNumber,MachineCode,accountCode,
     ErpDetailId,BindBoxCode,FromDataType)
VALUES(@json,@uuid,GETDATE(),@erp_no,@machine,@account,@detail,@box,@from_type);",
                connection, transaction))
            {
                insert.Parameters.Add("@json", SqlDbType.NText).Value =
                    json.ToString(Newtonsoft.Json.Formatting.None);
                AddVarchar(insert, "@uuid", request.GoodsUuid, 3500);
                AddNullableVarchar(insert, "@erp_no", request.ErpDocumentNo, 1000);
                AddNullableVarchar(insert, "@machine", source.MachineCode, 1000);
                AddNullableVarchar(insert, "@account", source.AccountCode, 1000);
                AddNullableVarchar(insert, "@detail", request.ErpDetailId ?? source.ErpDetailId, 1000);
                AddNullableVarchar(insert, "@box", source.BindBoxCode, 1000);
                AddNullableVarchar(insert, "@from_type", source.FromDataType, 1000);
                insert.ExecuteNonQuery();
            }
        }

        private static TargetNames ReadTargetNames(
            SqlConnection connection, SqlTransaction transaction,
            TraceBackSyncEventRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TargetWarehouseCode))
                return new TargetNames();
            using (var command = new SqlCommand(@"
SELECT
    (SELECT TOP (1) cWhName FROM dbo.Warehouse WHERE cWhCode=@warehouse),
    CASE WHEN NULLIF(@location,N'') IS NULL THEN N'' ELSE
        (SELECT TOP (1) cPosName FROM dbo.Position
         WHERE cWhCode=@warehouse AND cPosCode=@location) END;", connection, transaction))
            {
                command.Parameters.Add("@warehouse", SqlDbType.NVarChar, 100).Value =
                    request.TargetWarehouseCode;
                command.Parameters.Add("@location", SqlDbType.NVarChar, 100).Value =
                    (object)request.TargetLocationCode ?? DBNull.Value;
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    reader.Read();
                    return new TargetNames
                    {
                        WarehouseName = reader.IsDBNull(0)
                            ? request.TargetWarehouseCode
                            : reader.GetString(0).Trim(),
                        LocationName = reader.IsDBNull(1)
                            ? request.TargetLocationCode ?? string.Empty
                            : reader.GetString(1).Trim()
                    };
                }
            }
        }

        private static long InsertFlow(
            SqlConnection connection, SqlTransaction transaction,
            TraceBackSyncEventRequest request, string serialNo)
        {
            using (var insert = new SqlCommand(@"
INSERT INTO dbo.TraceBackOnlyCode
    (traceBackCode,traceBackType,pdaOrderUuid,pdaGoodsUuid,sn,[sum],[time],[User])
OUTPUT inserted.id
VALUES(@code,@type,@order_uuid,@goods_uuid,@sn,@quantity,GETDATE(),@user);",
                connection, transaction))
            {
                AddVarchar(insert, "@code", request.Code, 3500);
                AddVarchar(insert, "@type", "条码", 3500);
                AddVarchar(insert, "@order_uuid", request.OrderUuid, 3500);
                AddVarchar(insert, "@goods_uuid", request.GoodsUuid, 3500);
                AddNullableVarchar(insert, "@sn", serialNo, 3500);
                AddVarchar(insert, "@quantity",
                    request.Quantity.ToString("0.######", CultureInfo.InvariantCulture), 3500);
                AddNullableVarchar(insert, "@user", request.OperatorName, 1000);
                return Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static void AddVarchar(SqlCommand command, string name, string value, int size) =>
            command.Parameters.Add(name, SqlDbType.VarChar, size).Value = value ?? string.Empty;

        private static void AddNullableVarchar(
            SqlCommand command, string name, string value, int size) =>
            command.Parameters.Add(name, SqlDbType.VarChar, size).Value =
                string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;

        private static string Text(SqlDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

        private static bool Same(string left, string right) =>
            string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);

        private static string AccountCode(string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            int separator = trimmed.LastIndexOf('@');
            return separator >= 0 ? trimmed.Substring(separator + 1) : trimmed;
        }

        private sealed class ExistingEvent
        {
            public long GoodsCount { get; set; }
            public long FlowCount { get; set; }
            public long FlowId { get; set; }
            public string Code { get; set; }
            public string OrderUuid { get; set; }
            public decimal Quantity { get; set; }
        }

        private sealed class SourceGoods
        {
            public string Json { get; set; }
            public string UploadOrderNumber { get; set; }
            public string MachineCode { get; set; }
            public string AccountCode { get; set; }
            public string ErpDetailId { get; set; }
            public string BindBoxCode { get; set; }
            public string FromDataType { get; set; }
            public string SerialNo { get; set; }
        }

        private sealed class TargetNames
        {
            public string WarehouseName { get; set; }
            public string LocationName { get; set; }
        }
    }
}
