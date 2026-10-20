using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabelSystem.Api;

public sealed partial class PrintRepository
{
    public Task<PrintJob> SaveManifest(Guid jobId,JsonObject manifest,SessionUser user,CancellationToken ct) =>
        Transaction(async db =>
        {
            var row=await JobRow(db,jobId); Owner(row,user);
            if(row.S("print_phase")!="PREPARING")return await ReadJob(db,row);
            await db.Execute("""
                UPDATE dbo.business_operation SET print_manifest_json=@json,print_manifest_relative_path=@path,
                  print_manifest_sha256=@hash,print_phase='READY',archive_status='COMPLETE',
                  updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@op;
                UPDATE dbo.business_operation_line SET print_output_phase='READY' WHERE operation_id=@op;
                """,("@json",PrintRules.Canonical(manifest)),("@path",manifest.S("manifestPath")),
                ("@hash",Convert.FromHexString(PrintRules.Hash(manifest))),("@op",row.L("id")));
            return await ReadJob(db,await JobRow(db,jobId));
        },ct);

    public Task<SignedPrintMessage> Authorize(Guid jobId,PrintDispatchRequest request,SessionUser user,
        Func<JsonObject,SignedPrintMessage> sign,CancellationToken ct) =>
        Transaction(async db =>
        {
            var row=await JobRow(db,jobId); Owner(row,user);
            await RequireDevice(db,Guid.Parse(row.S("print_device_id")),user);
            if(request.OutputIds is null||request.OutputIds.Count is <1 or >100)
                throw new ApiException(400,"PRINT_OUTPUT_INVALID","打印副本不匹配。");
            var key=PrintRules.Key("dispatch",Scope,PrintRules.RequestId(request.ClientRequestId));
            var hash=PrintRules.Hash(new{jobId,outputIds=request.OutputIds.Order().ToArray()});
            var old=await db.One("SELECT event_data_json FROM dbo.business_operation_event WHERE event_key=@key;",("@key",key));
            if(old is not null)
            {
                var stored=JsonNode.Parse(old.S("event_data_json"))!.AsObject();
                if(stored.S("requestHash")!=hash)throw PrintRules.Conflict("PRINT_REQUEST_CONFLICT","同一请求内容不同。");
                return JsonSerializer.Deserialize<SignedPrintMessage>(stored["authorization"]!.ToJsonString(),PrintRules.Json)!;
            }
            Version(row,request.ExpectedRowVersion);
            if(row.S("print_phase")!="READY")
                throw PrintRules.Conflict("PRINT_RESULT_UNKNOWN","请恢复原打印任务，不要重复发送。");
            var job=await ReadJob(db,row);
            if(request.OutputIds is null || request.OutputIds.Count==0 ||
                request.OutputIds.Distinct().Count()!=request.OutputIds.Count ||
                request.OutputIds.Any(id=>!job.Outputs.Any(x=>x.OutputId==id && x.FlowId is null)))
                throw new ApiException(400,"PRINT_OUTPUT_INVALID","打印副本不匹配。");
            var attempt=Guid.NewGuid();
            var payload=new JsonObject
            {
                ["version"]=2,["scope"]=Scope,["jobId"]=jobId.ToString(),["attemptId"]=attempt.ToString(),
                ["deviceId"]=job.DeviceId.ToString(),["printer"]=job.Printer,["manifestHash"]=job.ManifestHash,
                ["deviceKeyHash"]=job.DeviceKeyHash,
                ["bundleHash"]=job.Manifest!.S("bundleSha256"),
                ["outputIds"]=new JsonArray(request.OutputIds.Order().Select(x=>JsonValue.Create(x.ToString())).ToArray()),
                ["expiresAt"]=BusinessClock.Now.AddMinutes(5).ToUnixTimeSeconds()
            };
            var signed=sign(payload);
            await Event(db,job.OperationId,"PRINT_DISPATCH_AUTHORIZED",key,
                new{requestHash=hash,authorization=signed,ticket=payload},attempt);
            await db.Execute("""
                UPDATE dbo.business_operation SET print_phase='DISPATCH_AUTHORIZED',updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@op;
                """,("@op",job.OperationId));
            foreach(var id in request.OutputIds)await db.Execute("""
                UPDATE dbo.business_operation_line SET print_output_phase='AUTHORIZED'
                WHERE operation_id=@op AND print_output_id=@id AND printed_label_count=0;
                """,("@op",job.OperationId),("@id",id));
            return signed;
        },ct);

    public Task<PrintJob> Receipt(Guid jobId,SignedPrintMessage receipt,SessionUser user,CancellationToken ct) =>
        Transaction(async db =>
        {
            var row=await JobRow(db,jobId); Owner(row,user);
            var device=await db.One("SELECT public_key FROM dbo.label_print_device WHERE id=@device;",
                ("@device",Guid.Parse(row.S("print_device_id"))))
                ?? throw PrintRules.Conflict("PRINT_DEVICE_INVALID","设备不存在。");
            // Historical receipts remain verifiable after device disable; no new dispatch is allowed.
            var data=PrintRules.Verify(receipt,device.S("public_key"));
            if(data.S("version")!="2" || data.S("scope")!=Scope || data.S("jobId")!=jobId.ToString() ||
                data.S("deviceId")!=row.S("print_device_id") ||
                data.S("manifestHash")!=row.S("print_manifest_sha256") ||
                data.S("printer")!=row.S("print_printer") ||
                !Guid.TryParse(data.S("attemptId"),out var attempt))
                throw PrintRules.Conflict("PRINT_RECEIPT_CONFLICT","回执与原打印任务不匹配。");
            var authorized=await db.One("""
                SELECT event_data_json FROM dbo.business_operation_event
                WHERE operation_id=@op AND print_attempt_id=@attempt AND event_type='PRINT_DISPATCH_AUTHORIZED';
                """,("@op",row.L("id")),("@attempt",attempt))
                ?? throw PrintRules.Conflict("PRINT_RECEIPT_CONFLICT","打印授权不存在。");
            var ticket=JsonNode.Parse(authorized.S("event_data_json"))!["ticket"]!.AsObject();
            var ids=(data["outputIds"] as JsonArray)?.Select(x=>Guid.Parse(x!.ToString())).ToArray()??[];
            var allowed=(ticket["outputIds"] as JsonArray)!.Select(x=>Guid.Parse(x!.ToString())).ToHashSet();
            if(data.S("deviceKeyHash")!=ticket.S("deviceKeyHash") ||
                data.S("expiresAt")!=ticket.S("expiresAt") || data.S("bundleHash")!=ticket.S("bundleHash") ||
                ids.Length==0 || ids.Distinct().Count()!=ids.Length ||
                ids.Any(x=>!allowed.Contains(x)))
                throw PrintRules.Conflict("PRINT_RECEIPT_CONFLICT","打印副本不匹配。");
            var job=await ReadJob(db,row);
            var status=data.S("status");
            if(job.AttemptId!=attempt)
            {
                if(status=="submitted" && ids.Length>0 && ids.All(id=>job.Outputs.Any(x=>x.OutputId==id&&x.FlowId.HasValue)))
                    return job;
                throw PrintRules.Conflict("PRINT_STALE_ATTEMPT","旧发送尝试结果有冲突，请核对原任务。");
            }
            if(status!="submitted")
            {
                if(status is not ("unknown" or "not_submitted"))
                    throw PrintRules.Conflict("PRINT_RECEIPT_INVALID","打印发送状态无效。");
                if(!allowed.SetEquals(ids))
                    throw PrintRules.Conflict("PRINT_RECEIPT_CONFLICT","未发送或未知回执必须覆盖原授权全部副本。");
                if(job.LocalRecorded)return job;
                var key=PrintRules.Key("receipt-state",Scope,attempt+":"+status);
                await Event(db,job.OperationId,"PRINT_SEND_RESULT",key,data,attempt);
                await db.Execute("""
                    UPDATE dbo.business_operation SET print_phase=@phase,status=@status,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@op;
                    UPDATE dbo.business_operation_line SET print_output_phase=@linephase WHERE operation_id=@op AND printed_label_count=0;
                    """,("@phase",status=="unknown"?"RECONCILIATION_REQUIRED":"FAILED_NO_OUTPUT"),
                    ("@status",status=="unknown"?"NEEDS_RECONCILIATION":"FAILED"),
                    ("@linephase",status=="unknown"?"UNKNOWN":"FAILED_NO_OUTPUT"),("@op",job.OperationId));
                return await ReadJob(db,await JobRow(db,jobId));
            }
            if(ids.Length==0)throw PrintRules.Conflict("PRINT_RECEIPT_INVALID","成功回执缺少打印副本。");
            foreach(var output in job.Outputs.Where(x=>ids.Contains(x.OutputId)).OrderBy(x=>x.Snapshot.LabelId).ThenBy(x=>x.LineId))
                await ConfirmOutput(db,row,output,attempt,user.Name);
            await Event(db,job.OperationId,"PRINT_SEND_CONFIRMED",
                PrintRules.Key("receipt",Scope,attempt+":"+string.Join(",",ids.Order())),data,attempt);
            await FinalizePrint(db,row);
            return await ReadJob(db,await JobRow(db,jobId));
        },ct);

    private async Task ConfirmOutput(PrintDb db,JsonObject operation,PrintOutput output,Guid attempt,string operatorName)
    {
        var existing=await db.One("SELECT id FROM dbo.inventory_flow WITH(UPDLOCK,HOLDLOCK) WHERE print_output_line_id=@line;",
            ("@line",output.LineId));
        if(existing is not null)return;
        var s=output.Snapshot;
        var label=await Target(db,s.LabelId);
        if(label["locked_operation_id"] is null || label.L("locked_operation_id")!=operation.L("id") || label.S("label_code")!=s.Code)
            throw PrintRules.Conflict("PRINT_TARGET_LOCKED","标签占用或内容已变化。");
        if(output.Kind=="FIRST" && label.S("status")!="PENDING_PRINT")
            throw PrintRules.Conflict("PRINT_ALREADY_CONFIRMED","首打状态已变化。");
        var flow=Convert.ToInt64(await db.Scalar("""
            INSERT dbo.inventory_flow(operation_id,operation_line_id,print_output_line_id,
              object_type,label_id,object_sequence_no,flow_role,operation_type,unit_code,qty_before,change_qty,qty_after,
              material_code,batch_no,manufacture_date,expiry_date,serial_no,inventory_attributes_json,
              attribute_schema_version,inventory_identity_hash,from_warehouse_code,from_location_code,
              to_warehouse_code,to_location_code,from_pallet_id,to_pallet_id,remark)
            OUTPUT inserted.id
            SELECT @op,@line,@line,'PRODUCT_LABEL',p.id,
              (SELECT ISNULL(MAX(object_sequence_no),0)+1 FROM dbo.inventory_flow WITH(UPDLOCK,HOLDLOCK) WHERE label_id=p.id),
              'NORMAL',@kind,f.unit_code,p.current_qty,0,p.current_qty,f.material_code,f.batch_no,
              f.manufacture_date,f.expiry_date,@serial,f.inventory_attributes_json,f.attribute_schema_version,
              f.inventory_identity_hash,f.to_warehouse_code,f.to_location_code,f.to_warehouse_code,f.to_location_code,
              f.to_pallet_id,f.to_pallet_id,N'数据发送成功登记'
            FROM dbo.product_label p JOIN dbo.inventory_flow f ON f.id=p.last_flow_id AND f.label_id=p.id WHERE p.id=@label;
            """,("@op",operation.L("id")),("@line",output.LineId),("@kind",output.Kind=="FIRST"?"PRINT":"REPRINT"),
            ("@serial",s.SerialNo),("@label",s.LabelId)),CultureInfo.InvariantCulture);
        await db.Execute("""
            UPDATE dbo.product_label SET last_flow_id=@flow,status=CASE WHEN @first=1 THEN 'ACTIVE' ELSE status END,
              updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@label AND locked_operation_id=@op;
            UPDATE dbo.business_operation_line SET printed_label_count=1,line_status='SUCCESS',print_output_phase='CONFIRMED',
              updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@line AND printed_label_count=0;
            """,("@flow",flow),("@first",output.Kind=="FIRST"),("@label",s.LabelId),("@op",operation.L("id")),("@line",output.LineId));
        if(output.Kind=="FIRST")
        {
            var registration=PrintRules.Registration(Scope,output,operatorName);
            await db.Execute("""
                UPDATE dbo.label_code_registry SET registry_status='ACTIVE',updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                  WHERE product_label_id=@label AND raw_code=@code;
                IF NOT EXISTS(SELECT 1 FROM dbo.traceback_sync_task WITH(UPDLOCK,HOLDLOCK) WHERE event_key=@key)
                INSERT dbo.traceback_sync_task(event_key,operation_id,operation_line_id,label_id,event_phase,event_role,
                  traceback_code,pda_order_uuid,pda_goods_uuid,erp_document_type,erp_document_no,payload_json,payload_hash)
                VALUES(@key,@op,@line,@label,'PRINT_TARGET','PRINT_TARGET',@code,@order,@goods,@type,@number,@json,@hash);
                """,("@label",s.LabelId),("@code",s.Code),("@key",registration.EventKey),("@op",operation.L("id")),
                ("@line",output.LineId),("@order",Guid.Parse(registration.OrderUuid)),("@goods",Guid.Parse(registration.GoodsUuid)),
                ("@type",s.SourceDocumentType),("@number",s.SourceDocumentNo),
                ("@json",PrintRules.Canonical(registration)),("@hash",registration.PayloadHash));
        }
        await Event(db,operation.L("id"),"PRINT_OUTPUT_RECORDED",
            PrintRules.Key("output",Scope,output.OutputId.ToString()),new{output.OutputId,flow},attempt,output.LineId);
    }
    private static async Task FinalizePrint(PrintDb db,JsonObject operation)
    {
        var op=operation.L("id");
        var pending=await db.Query("SELECT print_output_phase FROM dbo.business_operation_line WHERE operation_id=@op AND printed_label_count=0;",("@op",op));
        var sync=Convert.ToInt32(await db.Scalar("SELECT COUNT(*) FROM dbo.traceback_sync_task WHERE operation_id=@op AND status<>'COMPLETED';",("@op",op)));
        var phase=PrintRules.AggregatePhase(pending.Select(p=>p.S("print_output_phase")),sync>0);
        await db.Execute("""
            UPDATE dbo.product_label SET locked_operation_id=NULL,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
            WHERE locked_operation_id=@op
              AND NOT EXISTS(SELECT 1 FROM dbo.business_operation_line l WHERE l.operation_id=@op AND l.label_id=product_label.id AND l.printed_label_count=0)
              AND NOT EXISTS(SELECT 1 FROM dbo.traceback_sync_task t WHERE t.operation_id=@op AND t.label_id=product_label.id AND t.status<>'COMPLETED');
            UPDATE dbo.business_operation SET print_phase=@phase,status=@status,
              completed_at=CASE WHEN @done=1 THEN DATEADD(HOUR,8,SYSUTCDATETIME()) ELSE NULL END,
              updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@op;
            """,("@op",op),("@phase",phase),
            ("@status",phase switch{"READY"=>"PENDING","FAILED_NO_OUTPUT"=>"FAILED","COMPLETED"=>"SUCCESS",_=>"NEEDS_RECONCILIATION"}),
            ("@done",pending.Count==0&&sync==0));
    }
    public Task<IReadOnlyList<JsonObject>> SyncTasks(Guid jobId,SessionUser user,CancellationToken ct) =>
        Transaction<IReadOnlyList<JsonObject>>(async db =>
        {
            var row=await JobRow(db,jobId);Owner(row,user);
            return await db.Query("SELECT * FROM dbo.traceback_sync_task WHERE operation_id=@op AND event_phase='PRINT_TARGET' AND status<>'COMPLETED';",("@op",row.L("id")));
        },ct);

    public Task<PrintJob> CompleteSync(Guid jobId,long taskId,InitialLabelRegistrationResult result,SessionUser user,CancellationToken ct) =>
        Transaction(async db =>
        {
            var row=await JobRow(db,jobId); Owner(row,user);
            var task=await db.One("SELECT * FROM dbo.traceback_sync_task WITH(UPDLOCK,HOLDLOCK) WHERE id=@id AND operation_id=@op AND event_phase='PRINT_TARGET';",
                ("@id",taskId),("@op",row.L("id")))??throw PrintRules.Conflict("PRINT_SYNC_INVALID","登记任务不匹配。");
            if(task.S("status")=="COMPLETED")return await ReadJob(db,row);
            var payload=JsonSerializer.Deserialize<InitialLabelRegistration>(task.S("payload_json"),PrintRules.Json)!;
            var snapshot=result.Snapshot;
            if(result.EventKey!=task.S("event_key") || result.PayloadHash!=task.S("payload_hash") || result.FlowId<=0 ||
                snapshot.Code!=payload.Code || snapshot.Status!="AVAILABLE" || snapshot.Balance!=payload.Quantity ||
                snapshot.MaterialCode!=payload.MaterialCode || snapshot.UnitCode!=payload.UnitCode ||
                (snapshot.BatchNo??"")!=payload.BatchNo || (snapshot.WarehouseCode??"")!=payload.TargetWarehouseCode ||
                (snapshot.LocationCode??"")!=payload.TargetLocationCode || snapshot.LatestFlowId is null ||
                string.IsNullOrEmpty(snapshot.IdentityHash))
                throw PrintRules.Conflict("PRINT_SYNC_SNAPSHOT_CHANGED","登记结果需核对，请勿重新打印。");
            var label=await Target(db,task.L("label_id"));
            if(label.D("current_qty")!=payload.Quantity || label.L("locked_operation_id")!=row.L("id"))
                throw PrintRules.Conflict("PRINT_SYNC_SNAPSHOT_CHANGED","本地标签已变化。");
            await db.Execute("""
                UPDATE dbo.product_label SET mirror_source='TRACEBACK',traceback_account_id=@account,
                  traceback_latest_flow_id=@flow,traceback_balance=@balance,traceback_identity_hash=@identity,
                  traceback_sync_status='SYNCED',traceback_synced_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                  updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@label AND locked_operation_id=@op;
                UPDATE dbo.traceback_sync_task SET status='COMPLETED',completed_at=DATEADD(HOUR,8,SYSUTCDATETIME()),
                  updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()),last_error_code=NULL,last_error_message=NULL WHERE id=@task;
                """,("@account",labelOptions.CurrentValue.ErpAccountId),("@flow",snapshot.LatestFlowId.Value),
                ("@balance",snapshot.Balance!.Value),("@identity",snapshot.IdentityHash),("@label",task.L("label_id")),
                ("@op",row.L("id")),("@task",taskId));
            await Event(db,row.L("id"),"PRINT_TRACEBACK_REGISTERED",PrintRules.Key("sync",Scope,payload.EventKey),
                new{payload.EventKey,result.FlowId});
            await FinalizePrint(db,row);
            return await ReadJob(db,await JobRow(db,jobId));
        },ct);

    public Task<bool> FailSync(Guid jobId,long taskId,SessionUser user,CancellationToken ct) =>
        Transaction(async db =>
        {
            var row=await JobRow(db,jobId);Owner(row,user);
            await db.Execute("""
                UPDATE dbo.traceback_sync_task SET status='UNKNOWN',attempt_count=attempt_count+1,
                  last_error_code='PRINT_SYNC_UNCONFIRMED',last_error_message=N'登记未完成，请恢复原任务。',
                  last_attempt_at=DATEADD(HOUR,8,SYSUTCDATETIME()),updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                  WHERE id=@task AND operation_id=@op AND status<>'COMPLETED';
                """,("@task",taskId),("@op",row.L("id")));
            return true;
        },ct);
}
