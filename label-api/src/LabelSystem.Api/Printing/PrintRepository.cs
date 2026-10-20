using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed partial class PrintRepository(SqlConnectionFactory connections,
    IOptionsMonitor<PrintingOptions> printOptions, IOptionsMonitor<LabelSystemOptions> labelOptions,
    LabelCodeRuleEngine codeEngine)
{
    public string Scope
    {
        get
        {
            var p = printOptions.CurrentValue;
            var l = labelOptions.CurrentValue;
            if (p.SimulationOnly)
            {
                using var connection = connections.Create();
                PrintSimulationBoundary.Validate(p, l, connection.Database);
            }
            if (!p.Enabled || string.IsNullOrWhiteSpace(p.Namespace) || p.Namespace.Length>100 ||
                p.Namespace.Any(ch=>!(char.IsAsciiLetterOrDigit(ch)||ch is '.' or '_' or '-')))
                throw new ApiException(503, "PRINT_DISABLED", "打印功能尚未启用。");
            return $"{p.Namespace}|{l.Environment}|{l.ErpAccountId}|{l.ErpYear}|{l.ErpDatabaseName}";
        }
    }
    private async Task<T> Transaction<T>(Func<PrintDb, Task<T>> action, CancellationToken ct)
    {
        _ = Scope;
        await using var connection = connections.Create();
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, ct);
        var db = new PrintDb(connection, transaction, ct);
        await db.Scalar("IF NOT EXISTS(SELECT 1 FROM dbo.schema_migration WHERE migration_id='017') " +
            "THROW 50001,N'打印结构尚未安装。',1; SELECT 1;");
        var result = await action(db);
        await transaction.CommitAsync(ct);
        return result;
    }
    internal static void Owner(JsonObject row, SessionUser user)
    {
        if (row.S("operator_id") != user.Id && !user.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase))
            throw new ApiException(404, "PRINT_JOB_NOT_FOUND", "打印任务不存在。");
    }
    internal static void Version(JsonObject row, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || row.S("row_version") != expected.ToUpperInvariant())
            throw PrintRules.Conflict("PRINT_VERSION_CHANGED", "数据已变化，请刷新。");
    }
    private async Task<JsonObject> JobRow(PrintDb db, Guid jobId)
    {
        var row=await db.One("SELECT * FROM dbo.business_operation WITH(UPDLOCK,HOLDLOCK) " +
            "WHERE print_job_id=@job;", ("@job", jobId))
            ?? throw new ApiException(404, "PRINT_JOB_NOT_FOUND", "打印任务不存在。");
        if(row.S("print_scope")!=Scope)throw PrintRules.Conflict("PRINT_ENVIRONMENT_MISMATCH","任务所属环境已变化，请恢复原环境处理。");
        return row;
    }
    private static async Task Event(PrintDb db, long op, string type, string key, object payload,
        Guid? attempt = null, long? line = null)
    {
        var json = PrintRules.Canonical(payload);
        var hash = Hashing.Sha256(json);
        var existing = await db.One("SELECT payload_hash FROM dbo.business_operation_event WITH(UPDLOCK,HOLDLOCK) " +
            "WHERE event_key=@key;", ("@key", key));
        if (existing is not null)
        {
            if (existing.S("payload_hash") != hash)
                throw PrintRules.Conflict("PRINT_REQUEST_CONFLICT", "同一请求内容不同。");
            return;
        }
        await db.Execute("""
            INSERT dbo.business_operation_event(operation_id,sequence_no,stage,source,event_type,
              result,event_data_json,payload_hash,event_key,print_attempt_id,operation_line_id)
            SELECT @op,ISNULL(MAX(sequence_no),0)+1,@type,'BUSINESS_API',@type,'INFO',@json,@hash,@key,@attempt,@line
            FROM dbo.business_operation_event WITH(UPDLOCK,HOLDLOCK) WHERE operation_id=@op;
            """, ("@op", op), ("@type", type), ("@json", json), ("@hash", hash),
            ("@key", key), ("@attempt", attempt), ("@line", line));
    }
    private static async Task<PrintJob> ReadJob(PrintDb db, JsonObject row)
    {
        var lines = await db.Query("""
            SELECT l.id,l.print_output_id,l.print_kind,l.print_output_phase,l.print_snapshot_json,f.id AS flow_id
            FROM dbo.business_operation_line l
            LEFT JOIN dbo.inventory_flow f ON f.print_output_line_id=l.id
            WHERE l.operation_id=@op ORDER BY l.line_no;
            """, ("@op", row.L("id")));
        var attempt=await db.One("""
            SELECT TOP(1) print_attempt_id,event_data_json FROM dbo.business_operation_event
            WHERE operation_id=@op AND event_type='PRINT_DISPATCH_AUTHORIZED' ORDER BY id DESC;
            """,("@op",row.L("id")));
        var device=await db.One("SELECT public_key_sha256 FROM dbo.label_print_device WHERE id=@id;",
            ("@id",Guid.Parse(row.S("print_device_id"))));
        return new PrintJob(Guid.Parse(row.S("print_job_id")), row.L("id"), row.S("operator_id"),
            row.S("print_phase"), row.S("status"), Guid.Parse(row.S("print_device_id")),
            row.S("print_printer"), row.L("print_template_version_id"), row.S("row_version"),
            row["print_manifest_sha256"]?.ToString(),
            row["print_manifest_json"] is null ? null : JsonNode.Parse(row.S("print_manifest_json"))!.AsObject(),
            lines.Select(l => new PrintOutput(Guid.Parse(l.S("print_output_id")), l.L("id"),
                l.S("print_kind"), l.S("print_output_phase"), l["flow_id"] is null ? null : l.L("flow_id"),
                JsonSerializer.Deserialize<PrintSnapshot>(l.S("print_snapshot_json"), PrintRules.Json)!)).ToArray(),
            attempt is null?null:Guid.Parse(attempt.S("print_attempt_id")),
            attempt is null?null:JsonSerializer.Deserialize<SignedPrintMessage>(
                JsonNode.Parse(attempt.S("event_data_json"))!["authorization"]!.ToJsonString(),PrintRules.Json),
            device!.S("public_key_sha256"));
    }
    public Task<PrintJob> Get(Guid jobId, SessionUser user, CancellationToken ct) =>
        Transaction(async db => { var row = await JobRow(db, jobId); Owner(row, user); return await ReadJob(db, row); }, ct);

    public Task<ArchivedPrintOutput?> ArchivedFirstPrint(long labelId,long templateVersionId,CancellationToken ct) =>
        Transaction(async db =>
        {
            var row=await db.One("""
                SELECT TOP(1) o.print_job_id,l.print_output_id,o.print_printer,o.print_template_version_id,
                  l.print_snapshot_json,o.print_manifest_json
                FROM dbo.inventory_flow f
                JOIN dbo.business_operation_line l ON l.id=f.print_output_line_id
                JOIN dbo.business_operation o ON o.id=f.operation_id
                WHERE f.label_id=@label AND f.operation_type='PRINT' AND o.print_scope=@scope
                  AND o.print_template_version_id=@template AND o.archive_status='COMPLETE'
                  AND o.print_manifest_json IS NOT NULL
                ORDER BY f.id;
                """,("@label",labelId),("@scope",Scope),("@template",templateVersionId));
            if(row is null)return null;
            return new ArchivedPrintOutput(Guid.Parse(row.S("print_job_id")),Guid.Parse(row.S("print_output_id")),
                row.S("print_printer"),row.L("print_template_version_id"),
                JsonSerializer.Deserialize<PrintSnapshot>(row.S("print_snapshot_json"),PrintRules.Json)!,
                JsonNode.Parse(row.S("print_manifest_json"))!.AsObject());
        },ct);

    public Task<IReadOnlyList<JsonObject>> List(SessionUser user, CancellationToken ct) =>
        Transaction<IReadOnlyList<JsonObject>>(async db => await db.Query("""
            SELECT TOP(100) print_job_id AS jobId,print_phase AS phase,status,created_at,updated_at,
              print_printer AS printer,row_version AS rowVersion
            FROM dbo.business_operation WHERE print_job_id IS NOT NULL AND print_scope=@scope AND (operator_id=@user OR @admin=1)
            ORDER BY id DESC;
            """, ("@user", user.Id),("@scope",Scope),("@admin",user.Roles.Contains("admin",StringComparer.OrdinalIgnoreCase))), ct);

    public Task<IReadOnlyList<JsonObject>> Pending(SessionUser user, CancellationToken ct) =>
        Transaction<IReadOnlyList<JsonObject>>(async db => await db.Query("""
            SELECT TOP(100) p.id AS labelId,l.id AS originLineId,p.label_code AS code,p.current_qty AS quantity,
              f.material_code AS materialCode,f.batch_no AS batchNo,f.unit_code AS unitCode,
              f.to_warehouse_code AS warehouseCode,f.to_location_code AS locationCode,
              p.row_version AS rowVersion,o.erp_document_no AS sourceDocumentNo,
              recovery.print_job_id AS recoveryJobId
            FROM dbo.product_label p
            JOIN dbo.business_operation_line l ON l.generated_label_id=p.id
            JOIN dbo.business_operation o ON o.id=l.operation_id
            JOIN dbo.inventory_flow f ON f.id=p.last_flow_id
            LEFT JOIN dbo.business_operation recovery ON recovery.id=p.locked_operation_id
            WHERE p.status='PENDING_PRINT' AND p.current_qty>0 AND o.status='SUCCESS'
              AND (o.operator_id=@user OR @admin=1)
            ORDER BY p.id;
            """, ("@user", user.Id), ("@admin", user.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase))), ct);

    public Task<JsonObject?> CurrentRule(CancellationToken ct) => Transaction(db => db.One("""
        SELECT b.rule_version_id AS versionId,b.row_version AS rowVersion,r.rule_name AS name,
          r.rule_code AS code,v.spec_json AS specJson,v.version_status AS versionStatus,r.is_enabled AS enabled
        FROM dbo.label_print_rule_binding b JOIN dbo.label_code_rule_version v ON v.id=b.rule_version_id
        JOIN dbo.label_code_rule r ON r.id=v.rule_id WHERE b.singleton_id=1;
        """), ct);

    public Task<JsonObject?> SetRule(PrintRuleRequest request, SessionUser user, CancellationToken ct) =>
        Transaction(async db =>
        {
            var rule = await db.One("""
                SELECT v.spec_json FROM dbo.label_code_rule_version v WITH(UPDLOCK,HOLDLOCK)
                JOIN dbo.label_code_rule r WITH(UPDLOCK,HOLDLOCK) ON r.id=v.rule_id
                WHERE v.id=@id AND v.version_status='ACTIVE' AND r.is_enabled=1 AND r.object_type='PRODUCT_LABEL';
                """, ("@id", request.RuleVersionId))
                ?? throw PrintRules.Conflict("PRINT_RULE_INVALID", "请选择有效的产品标签规则。");
            codeEngine.Validate(JsonDocument.Parse(rule.S("spec_json")).RootElement);
            var current = await db.One("SELECT row_version FROM dbo.label_print_rule_binding WITH(UPDLOCK,HOLDLOCK) WHERE singleton_id=1;");
            if (current is not null) Version(current, request.ExpectedRowVersion);
            else if (!string.IsNullOrEmpty(request.ExpectedRowVersion)) throw PrintRules.Conflict("PRINT_VERSION_CHANGED", "数据已变化。");
            await db.Execute("""
                UPDATE dbo.label_print_rule_binding SET rule_version_id=@id,updated_by=@user,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE singleton_id=1;
                IF @@ROWCOUNT=0 INSERT dbo.label_print_rule_binding(singleton_id,rule_version_id,updated_by) VALUES(1,@id,@user);
                """, ("@id", request.RuleVersionId), ("@user", user.Id));
            return await db.One("SELECT rule_version_id AS versionId,row_version AS rowVersion FROM dbo.label_print_rule_binding WHERE singleton_id=1;");
        }, ct);

    public Task<JsonObject> PairDevice(PrintDeviceRequest request, SessionUser user, CancellationToken ct) =>
        Transaction(async db =>
        {
            if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length>100)
                throw new ApiException(400, "PRINT_DEVICE_INVALID", "设备信息无效。");
            var key = PrintRules.PublicKey(request.PublicKey);
            var owner=string.IsNullOrWhiteSpace(request.OwnerUserId)?user.Id:request.OwnerUserId.Trim();
            if(owner.Length>100 || await db.Scalar("SELECT user_id FROM dbo.application_user WHERE user_id=@user AND is_enabled=1;",("@user",owner)) is null)
                throw new ApiException(400,"PRINT_DEVICE_OWNER_INVALID","设备使用人不存在或已停用。");
            var old = await db.One("SELECT * FROM dbo.label_print_device WITH(UPDLOCK,HOLDLOCK) WHERE id=@id;", ("@id", request.Id));
            if (old is not null)
            {
                if (old.S("public_key") != key || old.S("owner_user_id")!=owner || old.S("name")!=request.Name.Trim())
                    throw PrintRules.Conflict("PRINT_DEVICE_CONFLICT", "设备登记信息已变化。");
                return old;
            }
            await db.Execute("""
                INSERT dbo.label_print_device(id,name,public_key,public_key_sha256,protocol_version,paired_by,owner_user_id)
                VALUES(@id,@name,@key,@hash,2,@user,@owner);
                """, ("@id", request.Id), ("@name", request.Name.Trim()), ("@key", key),
                ("@hash", System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(key))), ("@user", user.Id),
                ("@owner",owner));
            return (await db.One("SELECT * FROM dbo.label_print_device WHERE id=@id;", ("@id", request.Id)))!;
        }, ct);
    public Task<IReadOnlyList<JsonObject>> Devices(SessionUser user, CancellationToken ct) =>
        Transaction<IReadOnlyList<JsonObject>>(async db => await db.Query("""
            SELECT id,name,public_key AS publicKey,protocol_version AS protocolVersion,is_enabled AS enabled,row_version AS rowVersion
            FROM dbo.label_print_device WHERE owner_user_id=@user OR @admin=1 ORDER BY name;
            """, ("@user", user.Id), ("@admin", user.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase))), ct);
    public Task<JsonObject> SetDeviceState(Guid id,PrintDeviceStateRequest request,CancellationToken ct)=>
        Transaction(async db=>{
            var row=await db.One("SELECT * FROM dbo.label_print_device WITH(UPDLOCK,HOLDLOCK) WHERE id=@id;",("@id",id))
                ??throw new ApiException(404,"PRINT_DEVICE_NOT_FOUND","设备不存在。");
            Version(row,request.ExpectedRowVersion);
            await db.Execute("UPDATE dbo.label_print_device SET is_enabled=@enabled,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@id;",
                ("@enabled",request.Enabled),("@id",id));
            return (await db.One("SELECT id,is_enabled AS enabled,row_version AS rowVersion FROM dbo.label_print_device WHERE id=@id;",("@id",id)))!;
        },ct);
    public Task<JsonObject> FindLabel(string code,CancellationToken ct)=>Transaction(async db=>
        await db.One("SELECT id AS labelId,label_code AS code,current_qty AS quantity,status,row_version AS rowVersion FROM dbo.product_label WHERE label_code=@code;",
            ("@code",code))??throw new ApiException(404,"PRINT_TARGET_NOT_FOUND","标签不存在。"),ct);
}

internal static class PrintRow
{
    public static string S(this JsonObject row, string name) => row[name]?.ToString() ?? "";
    public static long L(this JsonObject row, string name) => long.Parse(row.S(name), CultureInfo.InvariantCulture);
    public static decimal D(this JsonObject row, string name) => decimal.Parse(row.S(name), CultureInfo.InvariantCulture);
}

internal sealed class PrintDb(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
{
    private SqlCommand Command(string sql, (string, object?)[] args)
    {
        var cmd = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in args)
        {
            var p = cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            if (value is decimal) { p.SqlDbType=SqlDbType.Decimal; p.Precision=18; p.Scale=6; }
        }
        return cmd;
    }
    public async Task<int> Execute(string sql, params (string, object?)[] args)
    { await using var cmd=Command(sql,args); return await cmd.ExecuteNonQueryAsync(ct); }
    public async Task<object?> Scalar(string sql, params (string, object?)[] args)
    { await using var cmd=Command(sql,args); return await cmd.ExecuteScalarAsync(ct); }
    public async Task<JsonObject?> One(string sql, params (string, object?)[] args) => (await Query(sql,args)).FirstOrDefault();
    public async Task<List<JsonObject>> Query(string sql, params (string, object?)[] args)
    {
        await using var cmd=Command(sql,args);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        var rows=new List<JsonObject>();
        while (await reader.ReadAsync(ct))
        {
            var row=new JsonObject();
            for(var i=0;i<reader.FieldCount;i++)
            {
                var value=reader.GetValue(i);
                row[reader.GetName(i)] = value switch
                {
                    DBNull => null,
                    byte[] bytes => JsonValue.Create(Convert.ToHexString(bytes)),
                    DateTime date => JsonValue.Create(date.ToString("yyyy-MM-ddTHH:mm:ss.fff",CultureInfo.InvariantCulture)),
                    Guid guid => JsonValue.Create(guid.ToString()),
                    _ => JsonSerializer.SerializeToNode(value,PrintRules.Json)
                };
            }
            rows.Add(row);
        }
        return rows;
    }
}
