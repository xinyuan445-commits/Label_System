namespace LabelSystem.Api;

public sealed partial class PrintRepository
{
    public Task<PrintJob> RecordReconciliation(Guid jobId,PrintReconcileRequest request,SessionUser user,CancellationToken ct)=>
        Transaction(async db=>{
            var row=await JobRow(db,jobId);Owner(row,user);
            if(string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length>1000)
                throw new ApiException(400,"PRINT_EVIDENCE_REQUIRED","请填写核对依据。");
            var key=PrintRules.Key("reconcile",Scope,PrintRules.RequestId(request.ClientRequestId));
            var old=await db.One("SELECT payload_hash FROM dbo.business_operation_event WHERE event_key=@key;",("@key",key));
            if(old is null)Version(row,request.ExpectedRowVersion);
            await Event(db,row.L("id"),"PRINT_RECONCILIATION_RECORDED",key,
                new{jobId,reason=request.Reason.Trim(),reviewedBy=user.Id,
                    receiptHash=request.Receipt is null?null:PrintRules.Hash(request.Receipt)});
            // A note is never evidence of a successful send and never changes inventory or unlocks.
            return await ReadJob(db,row);
        },ct);
    public Task<PrintJob> ChangeState(Guid jobId,PrintActionRequest request,bool resume,SessionUser user,CancellationToken ct)=>
        Transaction(async db=>
        {
            var row=await JobRow(db,jobId);Owner(row,user);
            var key=PrintRules.Key(resume?"resume":"cancel",Scope,PrintRules.RequestId(request.ClientRequestId));
            if(await db.Scalar("SELECT id FROM dbo.business_operation_event WHERE event_key=@key AND operation_id=@op;",
                ("@key",key),("@op",row.L("id"))) is not null)return await ReadJob(db,row);
            Version(row,request.ExpectedRowVersion);
            var job=await ReadJob(db,row);
            if(resume)
            {
                if(row.S("print_phase") is not ("CANCELLED" or "FAILED_NO_OUTPUT"))
                    throw PrintRules.Conflict("PRINT_RESULT_UNKNOWN","请先核验原发送结果。");
                await RequireDevice(db,job.DeviceId,user);
                foreach(var group in job.Outputs.Where(x=>x.FlowId is null).GroupBy(x=>x.Snapshot.LabelId).OrderBy(x=>x.Key))
                {
                    var label=await Target(db,group.Key);
                    if(label.S("label_code")!=group.First().Snapshot.Code ||
                        (label["locked_operation_id"] is not null&&label.L("locked_operation_id")!=job.OperationId) ||
                        (group.First().Kind=="FIRST"&&(label.S("status")!="PENDING_PRINT"||
                            label.D("current_qty")!=PrintRules.ParseQuantity(group.First().Snapshot.Quantity))))
                        throw PrintRules.Conflict("PRINT_TARGET_LOCKED","标签状态已变化，请核对。");
                    if(label["locked_operation_id"] is null)await LockLabel(db,group.Key,job.OperationId);
                }
                await db.Execute("""
                    UPDATE dbo.business_operation SET status='PENDING',print_phase=@phase,completed_at=NULL,
                      updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@op;
                    UPDATE dbo.business_operation_line SET print_output_phase=@linephase WHERE operation_id=@op AND printed_label_count=0;
                    """,("@phase",job.Manifest is null?"PREPARING":"READY"),("@linephase",job.Manifest is null?"PREPARING":"READY"),("@op",job.OperationId));
            }
            else
            {
                PrintRules.RequireKnownUnsent(job.Phase);
                if(job.Outputs.Any(x=>x.FlowId.HasValue))
                    throw PrintRules.Conflict("PRINT_ALREADY_CONFIRMED","已有发送登记，不能取消。");
                await db.Execute("""
                    UPDATE dbo.business_operation SET status='CANCELLED',print_phase='CANCELLED',
                      completed_at=DATEADD(HOUR,8,SYSUTCDATETIME()),updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@op;
                    UPDATE dbo.business_operation_line SET line_status='SKIPPED',print_output_phase='CANCELLED' WHERE operation_id=@op;
                    UPDATE dbo.product_label SET locked_operation_id=NULL,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
                      WHERE locked_operation_id=@op;
                    """,("@op",job.OperationId));
            }
            await Event(db,job.OperationId,resume?"PRINT_RESUMED":"PRINT_CANCELLED",key,new{jobId,resume});
            return await ReadJob(db,await JobRow(db,jobId));
        },ct);
}
