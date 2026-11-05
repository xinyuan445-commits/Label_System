using System;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using U8Gateway.Contracts.TraceBack;
using U8Gateway.U8.TraceBack;

internal static class Program
{
    // Explicit operator tool. Never called by service startup or an HTTP upload.
    // Only existing JSON may change; no U8 voucher or TraceBack quantity writes.
    private static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3 || !long.TryParse(args[1], out long operationId)) return 2;
        bool apply = args.Length == 3 && args[2] == "--apply";
        try
        {
            var cfg = JObject.Parse(File.ReadAllText(args[0]));
            var cs = new SqlConnectionStringBuilder((string)cfg["ConnectionStrings"]["LabelSystem"]);
            if (cs.InitialCatalog != "LabelSystem_Dev" || (string)cfg["LabelSystem"]["ErpAccountId"] != "888" ||
                (string)cfg["LabelSystem"]["ErpDatabaseName"] != "UFDATA_888_2021") throw new InvalidOperationException("Target mismatch");
            using (var db = new SqlConnection(cs.ConnectionString))
            {
                db.Open();
                using (var tx = db.BeginTransaction(IsolationLevel.Serializable))
                {
                    var op = Read(db, tx, @"SELECT operation_no,status,erp_status,erp_document_no,device_id,operator_id,reason,
archive_relative_path,archive_sha256 FROM dbo.business_operation WITH(UPDLOCK,HOLDLOCK)
WHERE id=@id AND erp_document_type='inventory-count' AND erp_account_id=N'888' AND erp_database_name_snapshot=N'UFDATA_888_2021';", operationId);
                    if (op.Rows.Count != 1 || Text(op.Rows[0], "status") != "SUCCESS" || Text(op.Rows[0], "erp_status") != "SUCCESS")
                        throw new InvalidOperationException("Operation must be a confirmed successful 888 count");
                    var row = op.Rows[0];
                    string root = Path.GetFullPath((string)cfg["Archive"]["RootPath"]);
                    string file = Path.GetFullPath(Path.Combine(root, Text(row, "archive_relative_path")));
                    if (!file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Archive path mismatch");
                    byte[] bytes = File.ReadAllBytes(file);
                    using (var sha = SHA256.Create())
                        if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "") != Text(row, "archive_sha256"))
                            throw new InvalidOperationException("Archive hash mismatch");
                    var payload = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))["payload"];
                    var context = payload["inventoryCount"];
                    if ((string)context["accountId"] != "888" || (string)context["databaseName"] != "UFDATA_888_2021")
                        throw new InvalidOperationException("Archive account mismatch");
                    var tasks = Read(db, tx, @"SELECT t.payload_json,t.operation_line_id,t.status,
 CONVERT(varchar(36),t.pda_goods_uuid) goods_uuid,CONVERT(varchar(36),t.pda_order_uuid) order_uuid
FROM dbo.traceback_sync_task t WITH(UPDLOCK,HOLDLOCK) WHERE t.operation_id=@id AND t.event_role='COUNT_ADJUSTMENT' ORDER BY t.id;", operationId);
                    if (tasks.Rows.Count == 0 || tasks.Rows.Count != ((JArray)payload["lines"]).Count ||
                        tasks.Rows.Cast<DataRow>().Any(t => Text(t, "status") != "COMPLETED")) throw new InvalidOperationException("Incomplete task set");
                    int goodsChanges = 0, orderChanges = 0;
                    string orderUuid = Text(tasks.Rows[0], "order_uuid"), normalizedOrder = null;
                    foreach (DataRow task in tasks.Rows)
                    {
                        if (Text(task, "order_uuid") != orderUuid) throw new InvalidOperationException("Order binding mismatch");
                        var request = JsonConvert.DeserializeObject<TraceBackSyncEventRequest>(Text(task, "payload_json"));
                        request.PdaDocumentNo = Text(row, "operation_no");
                        string Additional(string name) => (string)(payload["header"]["additionalFields"]?["header." + name] ??
                            payload["header"]["additionalFields"]?[name]);
                        request.CountContext = new TraceBackCountContext {
                            OperationId = operationId, OperationLineId = (long)task["operation_line_id"],
                            DocumentDate = ((string)payload["header"]["documentDate"]).Substring(0, 10),
                            CountByLocation = (bool)context["countByLocation"], WarehouseCode = (string)payload["header"]["warehouseCode"],
                            InCategoryCode = Additional("inCategoryCode") ?? "107", OutCategoryCode = Additional("outCategoryCode") ?? "207",
                            SourceDocumentId = (long?)context["source"]?["document"]?["id"],
                            SourceDocumentNo = (string)context["source"]?["document"]?["documentNo"],
                            DeviceId = Text(row, "device_id"), OperatorId = Text(row, "operator_id"), DepartmentCode = (string)payload["header"]["departmentCode"],
                            Remark = Text(row, "reason"), TotalQuantity = ((JArray)payload["lines"]).Sum(l => (decimal)l["requestedQty"]) };
                        var goods = ReadByUuid(db, tx, @"SELECT CONVERT(nvarchar(max),g.json) json,n.id,n.traceBackCode,n.[sum],
 (SELECT MAX(id) FROM UFDATA_888_2021.dbo.TraceBackOnlyCode WHERE traceBackCode=n.traceBackCode) latest_id,
 (SELECT SUM(TRY_CONVERT(decimal(28,6),[sum])) FROM UFDATA_888_2021.dbo.TraceBackOnlyCode WHERE traceBackCode=n.traceBackCode) balance
FROM UFDATA_888_2021.dbo.TraceBackGoods g WITH(UPDLOCK,HOLDLOCK)
JOIN UFDATA_888_2021.dbo.TraceBackOnlyCode n WITH(UPDLOCK,HOLDLOCK) ON n.pdaGoodsUuid=g.pdaGoodsUuid
WHERE g.pdaGoodsUuid=@uuid;", Text(task, "goods_uuid"));
                        if (goods.Rows.Count != 1 || Text(goods.Rows[0], "traceBackCode") != request.Code ||
                            Convert.ToInt64(goods.Rows[0]["id"]) != Convert.ToInt64(goods.Rows[0]["latest_id"]) ||
                            Convert.ToDecimal(goods.Rows[0]["sum"]) != request.Quantity ||
                            (decimal)goods.Rows[0]["balance"] != request.ExpectedBalance + request.Quantity)
                            throw new InvalidOperationException("Flow or balance changed; manual reconciliation required");
                        string before = Text(goods.Rows[0], "json");
                        string after = TraceBackCountJson.Goods(request, before).ToString(Formatting.None);
                        if (!JToken.DeepEquals(JObject.Parse(before), JObject.Parse(after)))
                        { goodsChanges++; if (apply) UpdateJson(db, tx, "TraceBackGoods", "pdaGoodsUuid", Text(task, "goods_uuid"), before, after); }
                        if (normalizedOrder == null)
                        {
                            var names = ReadNames(db, tx, request.CountContext);
                            normalizedOrder = TraceBackCountJson.Order(request, Text(names, "warehouse"), Text(names, "in_name"),
                                Text(names, "out_name"), Text(names, "department")).ToString(Formatting.None);
                        }
                    }
                    var orders = ReadByUuid(db, tx, "SELECT CONVERT(nvarchar(max),json) json FROM UFDATA_888_2021.dbo.TraceBackOrder WITH(UPDLOCK,HOLDLOCK) WHERE pdaOrderUuid=@uuid;", orderUuid);
                    if (orders.Rows.Count != 1) throw new InvalidOperationException("Order missing or duplicated");
                    string priorOrder = Text(orders.Rows[0], "json");
                    if (!JToken.DeepEquals(JObject.Parse(priorOrder), JObject.Parse(normalizedOrder)))
                    { orderChanges++; if (apply) UpdateJson(db, tx, "TraceBackOrder", "pdaOrderUuid", orderUuid, priorOrder, normalizedOrder); }
                    if (apply && goodsChanges + orderChanges > 0)
                    {
                        using (var audit = new SqlCommand(@"INSERT dbo.business_operation_event
(operation_id,sequence_no,stage,source,event_type,result,request_id,message)
SELECT @id,ISNULL(MAX(sequence_no),0)+1,'TRACEBACK_JSON_REPAIR','BUSINESS_API','TRACEBACK_JSON_REPAIRED','SUCCESS',
CONVERT(varchar(36),NEWID()),N'修复已确认盘点JSON；数量、流水、任务载荷及ERP均未改变。'
FROM dbo.business_operation_event WITH(UPDLOCK,HOLDLOCK) WHERE operation_id=@id;", db, tx))
                        { audit.Parameters.AddWithValue("@id", operationId); audit.ExecuteNonQuery(); }
                    }
                    if (apply) tx.Commit(); else tx.Rollback();
                    Console.WriteLine(JsonConvert.SerializeObject(new { operationId, apply, goodsChanges, orderChanges,
                        quantityWrites = 0, flowInserts = 0, voucherWrites = 0 }));
                }
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("Count JSON repair failed: " + e.GetType().Name); return 1; }
    }

    private static string Text(DataRow row, string name) => row[name] == DBNull.Value ? null : Convert.ToString(row[name]);
    private static DataTable Read(SqlConnection db, SqlTransaction tx, string sql, long id)
    { using (var c = new SqlCommand(sql, db, tx)) { c.Parameters.AddWithValue("@id", id); return Table(c); } }
    private static DataTable ReadByUuid(SqlConnection db, SqlTransaction tx, string sql, string uuid)
    { using (var c = new SqlCommand(sql, db, tx)) { c.Parameters.Add("@uuid", SqlDbType.VarChar, 3500).Value = uuid; return Table(c); } }
    private static DataTable Table(SqlCommand c)
    { var result = new DataTable(); using (var a = new SqlDataAdapter(c)) a.Fill(result); return result; }
    private static DataRow ReadNames(SqlConnection db, SqlTransaction tx, TraceBackCountContext c)
    {
        using (var q = new SqlCommand(@"SELECT
(SELECT cWhName FROM UFDATA_888_2021.dbo.Warehouse WHERE cWhCode=@wh) warehouse,
(SELECT cRdName FROM UFDATA_888_2021.dbo.Rd_Style WHERE cRdCode=@in) in_name,
(SELECT cRdName FROM UFDATA_888_2021.dbo.Rd_Style WHERE cRdCode=@out) out_name,
(SELECT cDepName FROM UFDATA_888_2021.dbo.Department WHERE cDepCode=@dep) department;", db, tx))
        { q.Parameters.AddWithValue("@wh", c.WarehouseCode); q.Parameters.AddWithValue("@in", c.InCategoryCode);
          q.Parameters.AddWithValue("@out", c.OutCategoryCode); q.Parameters.AddWithValue("@dep", c.DepartmentCode ?? ""); return Table(q).Rows[0]; }
    }
    private static void UpdateJson(SqlConnection db, SqlTransaction tx, string table, string key, string uuid, string before, string after)
    {
        using (var q = new SqlCommand("UPDATE UFDATA_888_2021.dbo." + table + " SET json=@after WHERE " + key + "=@uuid AND CONVERT(nvarchar(max),json)=@before;", db, tx))
        { q.Parameters.AddWithValue("@after", after); q.Parameters.AddWithValue("@before", before);
          q.Parameters.Add("@uuid", SqlDbType.VarChar, 3500).Value = uuid;
          if (q.ExecuteNonQuery() != 1) throw new InvalidOperationException("JSON changed concurrently"); }
    }
}
