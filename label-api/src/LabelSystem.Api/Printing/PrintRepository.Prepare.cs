using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabelSystem.Api;

public sealed partial class PrintRepository
{
    public Task<PrintJob> PrepareFirst(FirstPrintRequest request, SessionUser user, CancellationToken ct) =>
        Transaction(async db =>
        {
            if (request.Targets is null || request.Targets.Count is < 1 or > 100 ||
                request.Targets.Select(x => x.LabelId).Distinct().Count()!=request.Targets.Count ||
                string.IsNullOrWhiteSpace(request.Printer) || request.Printer.Length>200)
                throw new ApiException(400,"PRINT_TARGET_INVALID","请选择有效的待打印标签。");
            var payloadHash=PrintRules.Hash(request with { Targets=request.Targets.OrderBy(x=>x.LabelId).ToArray() });
            var replay=await Replay(db,request.ClientRequestId,payloadHash,user);
            if(replay is not null) return replay;
            await RequireDevice(db,request.DeviceId,user);
            var template=await db.One("""
                SELECT v.id FROM dbo.label_print_template_version v
                JOIN dbo.label_print_template t ON t.id=v.template_id
                WHERE v.id=@id AND t.is_enabled=1;
                """,("@id",request.TemplateVersionId))
                ?? throw PrintRules.Conflict("PRINT_TEMPLATE_INVALID","模板版本不可用。");
            var rule=await db.One("""
                SELECT v.id,v.spec_json FROM dbo.label_print_rule_binding b WITH(UPDLOCK,HOLDLOCK)
                JOIN dbo.label_code_rule_version v WITH(UPDLOCK,HOLDLOCK) ON v.id=b.rule_version_id
                JOIN dbo.label_code_rule r WITH(UPDLOCK,HOLDLOCK) ON r.id=v.rule_id
                WHERE b.singleton_id=1 AND v.version_status='ACTIVE' AND r.is_enabled=1 AND r.object_type='PRODUCT_LABEL';
                """) ?? throw PrintRules.Conflict("PRINT_RULE_REQUIRED","请先选择当前打印规则。");
            var validated=codeEngine.Validate(JsonDocument.Parse(rule.S("spec_json")).RootElement);
            var jobId=Guid.NewGuid();
            var op=await InsertOperation(db,jobId,"PRINT",request.ClientRequestId,payloadHash,
                user,request.DeviceId,request.Printer,request.TemplateVersionId,null);
            var number=0;
            foreach(var target in request.Targets.OrderBy(x=>x.LabelId))
            {
                var row=await Target(db,target.LabelId);
                Version(row,target.ExpectedRowVersion);
                if(row.S("status")!="PENDING_PRINT" || row["locked_operation_id"] is not null || row.D("current_qty")<=0)
                    throw PrintRules.Conflict("PRINT_TARGET_LOCKED","标签正在处理，请恢复原任务。");
                var origin=await db.One("""
                    SELECT l.*,o.status AS origin_status,o.operator_id,o.source_document_type,
                      o.source_document_id,o.source_document_no,o.erp_document_type,o.erp_document_no,o.erp_document_id,
                      o.erp_account_id,o.erp_database_name_snapshot,o.erp_environment
                    FROM dbo.business_operation_line l WITH(UPDLOCK,HOLDLOCK)
                    JOIN dbo.business_operation o WITH(UPDLOCK,HOLDLOCK) ON o.id=l.operation_id
                    WHERE l.id=@line AND l.generated_label_id=@label;
                    """,("@line",target.OriginLineId),("@label",target.LabelId))
                    ?? throw PrintRules.Conflict("PRINT_SOURCE_NOT_READY","待打印来源不匹配。");
                Owner(origin,user);
                if(row.D("current_qty")!=origin.D("requested_qty") ||
                    row.S("material_code")!=origin.S("material_code") ||
                    row.S("unit_code")!=origin.S("unit_code") || row.S("batch_no")!=origin.S("batch_no"))
                    throw PrintRules.Conflict("PRINT_SOURCE_CHANGED","待打印数量或物料与来源单据不一致。");
                if(origin.S("erp_account_id")!=labelOptions.CurrentValue.ErpAccountId ||
                    origin.S("erp_database_name_snapshot")!=labelOptions.CurrentValue.ErpDatabaseName ||
                    origin.S("erp_environment")!=labelOptions.CurrentValue.Environment)
                    throw PrintRules.Conflict("PRINT_ENVIRONMENT_MISMATCH","来源单据不属于当前环境。");
                if(origin.S("origin_status")!="SUCCESS" ||
                    await db.Scalar("SELECT TOP(1) id FROM dbo.traceback_sync_task WHERE operation_id=@op AND status<>'COMPLETED';",
                        ("@op",origin.L("operation_id"))) is not null)
                    throw PrintRules.Conflict("PRINT_SOURCE_NOT_READY","来源业务尚未完成。");
                if(await db.Scalar("""
                    SELECT TOP(1) id FROM dbo.inventory_flow WITH(UPDLOCK,HOLDLOCK)
                    WHERE label_id=@label AND operation_type='PRINT';
                    """,("@label",target.LabelId)) is not null)
                    throw PrintRules.Conflict("PRINT_ALREADY_CONFIRMED","标签已首次登记，请使用补打。");
                if(await db.Scalar("""
                    SELECT TOP(1) operation_id FROM dbo.business_operation_line WITH(UPDLOCK,HOLDLOCK)
                    WHERE label_id=@label AND print_kind='FIRST';
                    """,("@label",target.LabelId)) is not null)
                    throw PrintRules.Conflict("PRINT_RECOVERY_REQUIRED","请恢复原首次打印任务。");
                var snapshot=Snapshot(row,origin,rule.L("id"));
                if(string.IsNullOrWhiteSpace(snapshot.MaterialName))
                    throw PrintRules.Conflict("PRINT_MATERIAL_REQUIRED","请先同步物料档案。");
                if(string.IsNullOrEmpty(snapshot.Code))
                {
                    var uniqueSegment=validated.Spec.Segments.FirstOrDefault(x=>x.Kind=="FIELD" && x.Field=="LABEL_UNIQUE_VALUE");
                    if(uniqueSegment?.Length!=10)
                        throw PrintRules.Conflict("PRINT_RULE_SERIAL_FORMAT_INVALID","当前打印规则的唯一值必须为10位。");
                    var serial=await NextSerial(db,rule.L("id"),snapshot.FrozenAt);
                    var values=new Dictionary<string,string>(StringComparer.Ordinal)
                    {
                        ["ERP_ACCOUNT_ID"]=labelOptions.CurrentValue.ErpAccountId,
                        ["LABEL_TYPE"]="PRODUCT_LABEL",["MATERIAL_CODE"]=snapshot.MaterialCode,
                        ["BATCH_NO"]=snapshot.BatchNo,["ISSUED_QUANTITY"]=snapshot.Quantity,
                        ["UNIT_CODE"]=snapshot.UnitCode,["SOURCE_DOCUMENT_NO"]=snapshot.SourceDocumentNo,
                        ["CURRENT_DATE"]=snapshot.FrozenAt,["SEQUENCE"]=serial
                    };
                    values["LABEL_UNIQUE_VALUE"]=serial;
                    if(snapshot.ManufactureDate is not null) values["MANUFACTURE_DATE"]=snapshot.ManufactureDate;
                    var code=codeEngine.Render(validated.Spec,values,labelOptions.CurrentValue.ErpAccountId);
                    var parsed=codeEngine.ParseCode(validated.Spec,code,null,labelOptions.CurrentValue.ErpAccountId);
                    if(!parsed.Success || code.Length>100)
                        throw PrintRules.Conflict("PRINT_CODE_INCOMPATIBLE","当前规则生成的标签码不兼容。");
                    if(parsed.Fields?.TryGetValue("ISSUED_QUANTITY",out var encodedQuantity)==true &&
                        PrintRules.ParseQuantity(encodedQuantity)!=PrintRules.ParseQuantity(snapshot.Quantity))
                        throw PrintRules.Conflict("PRINT_QUANTITY_UNREPRESENTABLE","当前规则会损失数量精度。");
                    snapshot=snapshot with {Code=code, SerialNo=parsed.Fields?.GetValueOrDefault("LABEL_UNIQUE_VALUE")
                        ?? parsed.Fields?.GetValueOrDefault("SEQUENCE") ?? values["LABEL_UNIQUE_VALUE"]};
                    await db.Execute("""
                        INSERT dbo.label_code_registry(code_hash,normalized_code,raw_code,object_type,
                          product_label_id,rule_version_id,erp_account_id,parsed_payload_json,registry_status)
                        VALUES(@hash,@code,@code,'PRODUCT_LABEL',@label,@version,@account,@parsed,'PENDING_PRINT');
                        UPDATE dbo.product_label SET label_code=@code WHERE id=@label AND label_code IS NULL;
                        """,("@hash",Convert.FromHexString(Hashing.Sha256(code))),("@code",code),
                        ("@label",target.LabelId),("@version",rule.L("id")),("@account",labelOptions.CurrentValue.ErpAccountId),
                        ("@parsed",PrintRules.Canonical(parsed.Fields)));
                }
                else
                {
                    var registered=await db.One("SELECT rule_version_id FROM dbo.label_code_registry WHERE product_label_id=@label AND raw_code=@code;",
                        ("@label",target.LabelId),("@code",snapshot.Code));
                    if(registered is null) throw PrintRules.Conflict("PRINT_CODE_CONFLICT","已有标签码登记不完整。");
                    snapshot=snapshot with { RuleVersionId=registered["rule_version_id"] is null?null:registered.L("rule_version_id") };
                }
                snapshot=WithVariables(snapshot,row.S("unit_name"));
                await LockLabel(db,target.LabelId,op);
                await InsertLine(db,op,++number,Guid.NewGuid(),"FIRST",snapshot,row);
            }
            await Event(db,op,"PRINT_PREPARED",PrintRules.Key("prepare",Scope,jobId.ToString()),new{jobId,payloadHash});
            return await ReadJob(db,await JobRow(db,jobId));
        },ct);

    public Task<PrintJob> PrepareReprint(ReprintRequest request, SessionUser user, CancellationToken ct) =>
        Transaction(async db =>
        {
            if(request.Copies is <1 or >100) throw new ApiException(400,"PRINT_COPIES_INVALID","补打份数无效。");
            var hash=PrintRules.Hash(request);
            var replay=await Replay(db,request.ClientRequestId,hash,user);
            if(replay is not null) return replay;
            var row=await Target(db,request.LabelId); Version(row,request.ExpectedRowVersion);
            if(row["locked_operation_id"] is not null)
                throw PrintRules.Conflict("PRINT_TARGET_LOCKED","请先完成原任务登记。");
            if(row.S("status")=="PENDING_PRINT")
                throw PrintRules.Conflict("PRINT_RECOVERY_REQUIRED","请恢复原首次打印任务。");
            if(row.S("status")!="ACTIVE" && !request.ConfirmInactive)
                throw PrintRules.Conflict("PRINT_INACTIVE_CONFIRMATION_REQUIRED","请确认标签当前状态后补打。");
            var original=await db.One("""
                SELECT TOP(1) l.print_snapshot_json,o.print_device_id,o.print_printer,o.print_template_version_id
                FROM dbo.inventory_flow f JOIN dbo.business_operation_line l ON l.id=f.print_output_line_id
                JOIN dbo.business_operation o ON o.id=f.operation_id
                WHERE f.label_id=@label AND f.operation_type='PRINT' AND o.print_scope=@scope ORDER BY f.id;
                """,("@label",request.LabelId),("@scope",Scope));
            if(original is null)
                original=await db.One("""
                    SELECT TOP(1) event_data_json FROM dbo.business_operation_event
                    WHERE event_key=@key AND event_type='LEGACY_FIRST_PRINT_BASELINE';
                    """,("@key",PrintRules.Key("legacy-baseline",Scope,request.LabelId.ToString(CultureInfo.InvariantCulture))));
            if(original is null)
                throw PrintRules.Conflict("PRINT_LEGACY_EVIDENCE_REQUIRED","请先核验历史标签原始内容。");
            JsonObject source=original["event_data_json"] is null?original:JsonNode.Parse(original.S("event_data_json"))!.AsObject();
            var device=request.DeviceId??Guid.Parse(source.S("print_device_id"));
            await RequireDevice(db,device,user);
            var printer=request.Printer??source.S("print_printer");
            if(string.IsNullOrWhiteSpace(printer)||printer.Length>200)
                throw new ApiException(400,"PRINT_PRINTER_INVALID","请选择打印机。");
            var snapshot=JsonSerializer.Deserialize<PrintSnapshot>(source.S("print_snapshot_json"),PrintRules.Json)!;
            if(snapshot.Code!=row.S("label_code") || snapshot.LabelId!=request.LabelId)
                throw PrintRules.Conflict("PRINT_SNAPSHOT_CONFLICT","原打印内容不匹配。");
            snapshot=WithDisplayUnit(snapshot,row.S("unit_name"));
            var jobId=Guid.NewGuid();
            var op=await InsertOperation(db,jobId,"REPRINT",request.ClientRequestId,hash,user,device,
                printer,source.L("print_template_version_id"),request.Reason);
            await LockLabel(db,request.LabelId,op);
            for(var i=0;i<request.Copies;i++)
                await InsertLine(db,op,i+1,Guid.NewGuid(),"REPRINT",snapshot,row);
            await Event(db,op,"PRINT_PREPARED",PrintRules.Key("prepare",Scope,jobId.ToString()),new{jobId,hash});
            return await ReadJob(db,await JobRow(db,jobId));
        },ct);

    private async Task<PrintJob?> Replay(PrintDb db,string request,string hash,SessionUser user)
    {
        var row=await db.One("SELECT * FROM dbo.business_operation WITH(UPDLOCK,HOLDLOCK) WHERE client_request_id=@request;",
            ("@request",PrintRules.RequestId(request)));
        if(row is null)return null;
        Owner(row,user);
        if(row.S("request_payload_hash")!=hash || row["print_job_id"] is null)
            throw PrintRules.Conflict("PRINT_REQUEST_CONFLICT","同一请求内容不同。");
        return await ReadJob(db,await JobRow(db,Guid.Parse(row.S("print_job_id"))));
    }
    private async Task RequireDevice(PrintDb db,Guid device,SessionUser user)
    {
        var p=printOptions.CurrentValue;
        if(p.SimulationOnly)
        {
            if(device!=p.SimulationDeviceId)
                throw PrintRules.Conflict("PRINT_SIMULATION_DEVICE_REQUIRED","请选择888模拟打印设备。");
            var hash=await db.Scalar("SELECT public_key_sha256 FROM dbo.label_print_device WHERE id=@id;",("@id",device));
            if(hash is not byte[] bytes || Convert.ToHexString(bytes)!=p.SimulationDeviceKeyHash.ToUpperInvariant())
                throw PrintRules.Conflict("PRINT_SIMULATION_DEVICE_REQUIRED","模拟设备身份不匹配。");
        }
        if(await db.Scalar("""
            SELECT id FROM dbo.label_print_device WITH(UPDLOCK,HOLDLOCK)
            WHERE id=@id AND is_enabled=1 AND (owner_user_id=@user OR @admin=1);
            """,("@id",device),("@user",user.Id),("@admin",user.Roles.Contains("admin",StringComparer.OrdinalIgnoreCase))) is null)
            throw PrintRules.Conflict("PRINT_DEVICE_INVALID","打印设备不可用。");
    }
    private async Task<long> InsertOperation(PrintDb db,Guid job,string kind,string request,string hash,
        SessionUser user,Guid device,string printer,long template,string? reason)
    {
        var l=labelOptions.CurrentValue;
        return Convert.ToInt64(await db.Scalar("""
            INSERT dbo.business_operation(operation_no,operation_type,status,operator_id,operator_name_snapshot,
              client_request_id,request_payload_hash,erp_environment,erp_account_id,erp_year,
              erp_database_name_snapshot,erp_status,approval_status,archive_status,reason,
              print_job_id,print_scope,print_phase,print_printer,print_device_id,print_template_version_id)
            OUTPUT inserted.id VALUES(@number,@kind,'PENDING',@user,@name,@request,@hash,@env,@account,@year,
              @database,'NOT_REQUIRED','NOT_REQUIRED','PENDING',@reason,@job,@scope,'PREPARING',@printer,@device,@template);
            """,("@number","PR-"+job.ToString("N")),("@kind",kind),("@user",user.Id),("@name",user.Name),
            ("@request",PrintRules.RequestId(request)),("@hash",hash),("@env",l.Environment),("@account",l.ErpAccountId),
            ("@year",l.ErpYear),("@database",l.ErpDatabaseName),("@reason",reason),
            ("@job",job),("@scope",Scope),("@printer",printer),("@device",device),("@template",template)),CultureInfo.InvariantCulture);
    }
    private async Task<string> NextSerial(PrintDb db,long version,string frozenAt)
    {
        var day=DateTimeOffset.Parse(frozenAt,CultureInfo.InvariantCulture).ToString("yyMMdd",CultureInfo.InvariantCulture);
        var partition="PRINT-LEGACY:"+Hashing.Sha256(printOptions.CurrentValue.Namespace+"|"+
            labelOptions.CurrentValue.Environment+"|"+labelOptions.CurrentValue.ErpAccountId)+":"+day;
        var database=labelOptions.CurrentValue.ErpDatabaseName;
        if(database.Length is <1 or >128 || database.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c=='_')))
            throw new InvalidOperationException("Invalid ERP database name.");
        await db.Scalar("""
            DECLARE @lock int;
            EXEC @lock=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @lock<0 THROW 50001,N'打印流水正忙。',1; SELECT @lock;
            """,("@key",partition));
        var legacyTable=$"[{database}].dbo.TraceBackOnlyCode";
        var serial=Convert.ToInt64(await db.Scalar($$"""
            ;WITH used(value) AS
            (
                SELECT TRY_CONVERT(int,RIGHT(v.serial,4))
                FROM {{legacyTable}} t WITH(UPDLOCK,HOLDLOCK)
                CROSS APPLY (VALUES(CASE WHEN NULLIF(LTRIM(RTRIM(t.sn)),'') IS NOT NULL
                  THEN LTRIM(RTRIM(t.sn)) WHEN CHARINDEX('|',REVERSE(t.traceBackCode))>0
                  THEN RIGHT(t.traceBackCode,CHARINDEX('|',REVERSE(t.traceBackCode))-1) END)) v(serial)
                WHERE LEN(v.serial)=10 AND LEFT(v.serial,6)=@day AND v.serial NOT LIKE '%[^0-9]%'
                UNION ALL
                SELECT TRY_CONVERT(int,RIGHT(v.serial,4))
                FROM dbo.label_code_registry r WITH(UPDLOCK,HOLDLOCK)
                CROSS APPLY (VALUES(CASE WHEN CHARINDEX('|',REVERSE(r.raw_code))>0
                  THEN RIGHT(r.raw_code,CHARINDEX('|',REVERSE(r.raw_code))-1) END)) v(serial)
                WHERE LEN(v.serial)=10 AND LEFT(v.serial,6)=@day AND v.serial NOT LIKE '%[^0-9]%'
                UNION ALL
                SELECT TRY_CONVERT(int,RIGHT(LTRIM(RTRIM(f.serial_no)),4))
                FROM dbo.inventory_flow f WITH(UPDLOCK,HOLDLOCK)
                WHERE LEN(LTRIM(RTRIM(f.serial_no)))=10 AND LEFT(LTRIM(RTRIM(f.serial_no)),6)=@day
                  AND LTRIM(RTRIM(f.serial_no)) NOT LIKE '%[^0-9]%'
                UNION ALL
                SELECT TRY_CONVERT(int,current_value) FROM dbo.label_code_sequence WITH(UPDLOCK,HOLDLOCK)
                WHERE partition_key=@key
            )
            SELECT ISNULL(MAX(value),0)+1 FROM used;
            """,("@day",day),("@key",partition)),CultureInfo.InvariantCulture);
        if(serial>9999)
            throw PrintRules.Conflict("PRINT_SERIAL_EXHAUSTED","当天打印流水号已用完。");
        await db.Execute("""
            UPDATE dbo.label_code_sequence SET current_value=@n,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE rule_version_id=@version AND partition_key=@key;
            IF @@ROWCOUNT=0 INSERT dbo.label_code_sequence(rule_version_id,partition_key,current_value) VALUES(@version,@key,@n);
            """,("@n",serial),("@version",version),("@key",partition));
        return day+serial.ToString("D4",CultureInfo.InvariantCulture);
    }
    private Task<JsonObject?> TargetQuery(PrintDb db,long id)
    {
        var database=labelOptions.CurrentValue.ErpDatabaseName;
        if(database.Length is <1 or >128 || database.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c=='_')))
            throw new InvalidOperationException("Invalid ERP database name.");
        return db.One($$"""
        SELECT p.*,f.material_code,f.batch_no,f.unit_code,f.serial_no,f.manufacture_date,f.expiry_date,
          f.inventory_attributes_json,f.attribute_schema_version,f.inventory_identity_hash,
          f.to_warehouse_code,f.to_location_code,m.material_name,
          COALESCE(NULLIF(u.cComunitName COLLATE DATABASE_DEFAULT,N''),f.unit_code) AS unit_name
        FROM dbo.product_label p WITH(UPDLOCK,HOLDLOCK)
        JOIN dbo.inventory_flow f WITH(UPDLOCK,HOLDLOCK) ON f.id=p.last_flow_id AND f.label_id=p.id
        LEFT JOIN dbo.u8_material_cache m ON m.material_code=f.material_code
          AND m.source_account_id=@account AND m.source_database_name=@database
        LEFT JOIN [{{database}}].dbo.ComputationUnit u
          ON u.cComunitCode COLLATE DATABASE_DEFAULT=f.unit_code
        WHERE p.id=@id;
        """,("@id",id),("@account",labelOptions.CurrentValue.ErpAccountId),("@database",database));
    }
    private async Task<JsonObject> Target(PrintDb db,long id) => await TargetQuery(db,id)
        ?? throw new ApiException(404,"PRINT_TARGET_NOT_FOUND","标签不存在。");
    private static async Task LockLabel(PrintDb db,long label,long operation)
    {
        if(await db.Execute("""
            UPDATE dbo.product_label SET locked_operation_id=@op,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE id=@label AND locked_operation_id IS NULL;
            """,("@op",operation),("@label",label))!=1)
            throw PrintRules.Conflict("PRINT_TARGET_LOCKED","标签正在处理。");
    }
    private static PrintSnapshot Snapshot(JsonObject r,JsonObject origin,long? version)
    {
        string? Date(string key)=>r[key] is null?null:r.S(key)[..10];
        return new PrintSnapshot(r.L("id"),r.S("label_code"),r.S("serial_no"),PrintRules.Quantity(r.D("current_qty")),
            r.S("unit_code"),r.S("material_code"),r.S("material_name"),r.S("batch_no"),
            r.S("to_warehouse_code"),r.S("to_location_code"),Date("manufacture_date"),Date("expiry_date"),
            r["inventory_attributes_json"]?.ToString(),r["attribute_schema_version"] is null?1:(int)r.L("attribute_schema_version"),
            r["inventory_identity_hash"]?.ToString(),origin.L("id"),origin.S("erp_document_type"),
            origin.S("erp_document_id"),origin.S("erp_document_no"),origin["erp_line_id"]?.ToString(),
            version,BusinessClock.Now.ToString("O"),new JsonObject{["行号"]=origin.S("line_no")});
    }
    public static PrintSnapshot WithVariables(PrintSnapshot s,string? unitName=null) => s with {Variables=new JsonObject
    {
        ["条码"]=s.Code,["sn"]=s.SerialNo,["数量"]=s.Quantity,["单位"]=DisplayUnit(s,unitName),
        ["物料编码"]=s.MaterialCode,["物料名称"]=s.MaterialName,["批号"]=s.BatchNo,
        ["单据编码"]=s.SourceDocumentNo,["行号"]=s.Variables.S("行号"),
        ["仓库编码"]=s.WarehouseCode,["货位编码"]=s.LocationCode,
        ["Code"]=s.Code,["quantity"]=s.Quantity
    }};
    private static PrintSnapshot WithDisplayUnit(PrintSnapshot s,string? unitName)
    {
        var variables=(JsonObject)s.Variables.DeepClone();
        variables["单位"]=DisplayUnit(s,unitName);
        return s with {Variables=variables};
    }
    private static string DisplayUnit(PrintSnapshot s,string? unitName) =>
        string.IsNullOrWhiteSpace(unitName)?s.UnitCode:unitName.Trim();
    private static async Task InsertLine(PrintDb db,long op,int number,Guid output,string kind,PrintSnapshot s,JsonObject current)
    {
        await db.Execute("""
            INSERT dbo.business_operation_line(operation_id,line_no,line_status,label_id,selection_source,
              inventory_qty_snapshot,material_code,batch_no,unit_code,requested_qty,requested_label_count,
              from_warehouse_code,from_location_code,to_warehouse_code,to_location_code,
              printed_label_count,print_output_id,print_kind,print_origin_line_id,print_output_phase,print_snapshot_json)
            VALUES(@op,@n,'PENDING',@label,@source,@balance,@material,@batch,@unit,@qty,1,
              @warehouse,@location,@warehouse,@location,0,@output,@kind,@origin,'PREPARING',@snapshot);
            """,("@op",op),("@n",number),("@label",s.LabelId),("@source",kind=="FIRST"?"PRINT_TARGET":"PRINT_REPRINT"),
            ("@balance",current.D("current_qty")),("@material",s.MaterialCode),("@batch",s.BatchNo),("@unit",s.UnitCode),
            ("@qty",PrintRules.ParseQuantity(s.Quantity)),("@output",output),("@kind",kind),
            ("@origin",kind=="FIRST"?s.OriginLineId:null),("@snapshot",PrintRules.Canonical(s)),
            ("@warehouse",current.S("to_warehouse_code")),("@location",current.S("to_location_code")));
    }
}
