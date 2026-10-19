using System.Globalization;
using System.Text.Json.Nodes;

namespace LabelSystem.Api;

public sealed partial class PrintRepository
{
    public Task<JsonObject> RecordLegacyBaseline(LegacyPrintBaselineRequest request,LegacyPrintEvidence evidence,
        SessionUser user,CancellationToken ct)=>Transaction(async db=>
    {
        var row=await db.One("SELECT * FROM dbo.product_label WITH(UPDLOCK,HOLDLOCK) WHERE label_code=@code;",("@code",request.Code))
            ??throw new ApiException(404,"PRINT_TARGET_NOT_FOUND","请先查询原标签。");
        var key=PrintRules.Key("legacy-baseline",Scope,row.L("id").ToString(CultureInfo.InvariantCulture));
        var old=await db.One("SELECT event_data_json FROM dbo.business_operation_event WHERE event_key=@key;",("@key",key));
        if(old is not null)
        {
            var baseline=JsonNode.Parse(old.S("event_data_json"))!.AsObject();
            if(baseline.S("evidenceHash")!=evidence.EvidenceHash ||
                baseline.S("print_device_id")!=request.DeviceId.ToString() ||
                baseline.L("print_template_version_id")!=request.TemplateVersionId ||
                baseline.S("print_printer")!=request.Printer)
                throw PrintRules.Conflict("PRINT_LEGACY_EVIDENCE_CONFLICT","历史依据已变化。");
            return baseline;
        }
        Version(row,request.ExpectedRowVersion);
        if(row["locked_operation_id"] is not null)
            throw PrintRules.Conflict("PRINT_TARGET_LOCKED","标签正在处理中。");
        if(row.S("mirror_source")!="TRACEBACK" || evidence.Code!=request.Code ||
            evidence.InitialQuantity<=0 || evidence.InitialFlowId<=0 ||
            string.IsNullOrWhiteSpace(evidence.EvidenceHash) || evidence.Original.Status!="AVAILABLE")
            throw PrintRules.Conflict("PRINT_LEGACY_EVIDENCE_REQUIRED","历史打印依据需核对。");
        await RequireDevice(db,request.DeviceId,user);
        if(await db.Scalar("SELECT id FROM dbo.label_print_template_version WHERE id=@id;",("@id",request.TemplateVersionId)) is null)
            throw PrintRules.Conflict("PRINT_TEMPLATE_INVALID","请选择历史标签重建模板。");
        if(string.IsNullOrWhiteSpace(request.Printer)||request.Printer.Length>200 ||
            (request.Printer.Contains("pdf",StringComparison.OrdinalIgnoreCase) && request.Printer!="Microsoft Print to PDF"))
            throw new ApiException(400,"PRINT_PRINTER_INVALID","请选择标签打印机。");
        var original=evidence.Original;
        _=PrintRules.ParseQuantity(PrintRules.Quantity(evidence.InitialQuantity));
        var snapshot=new PrintSnapshot(row.L("id"),evidence.Code,original.SerialNo!,
            PrintRules.Quantity(evidence.InitialQuantity),original.UnitCode!,original.MaterialCode!,original.MaterialName!,
            original.BatchNo??"",original.WarehouseCode??"",original.LocationCode??"",null,null,null,1,
            original.IdentityHash,null,original.SourceBusinessType??"LEGACY","",
            string.IsNullOrWhiteSpace(original.ErpDocumentNo)?original.PdaDocumentNo??"":original.ErpDocumentNo,
            null,null,evidence.PrintedAt,new JsonObject());
        snapshot=WithVariables(snapshot);
        var payload=new JsonObject
        {
            ["evidenceHash"]=evidence.EvidenceHash,["initialFlowId"]=evidence.InitialFlowId,
            ["print_snapshot_json"]=PrintRules.Canonical(snapshot),["print_device_id"]=request.DeviceId.ToString(),
            ["print_printer"]=request.Printer,["print_template_version_id"]=request.TemplateVersionId,
            ["reviewedBy"]=user.Id
        };
        var op=Convert.ToInt64(await db.Scalar("""
            INSERT dbo.business_operation(operation_no,operation_type,status,operator_id,operator_name_snapshot,
              client_request_id,request_payload_hash,erp_status,approval_status,archive_status,completed_at)
            OUTPUT inserted.id VALUES(@number,'LEGACY_PRINT_BASELINE','SUCCESS',@user,@name,@request,@hash,
              'NOT_REQUIRED','NOT_REQUIRED','COMPLETE',DATEADD(HOUR,8,SYSUTCDATETIME()));
            """,("@number","PLB-"+Guid.NewGuid().ToString("N")),("@user",user.Id),("@name",user.Name),
            ("@request",PrintRules.RequestId(request.ClientRequestId)),("@hash",PrintRules.Hash(new{request,evidence.EvidenceHash}))),CultureInfo.InvariantCulture);
        await Event(db,op,"LEGACY_FIRST_PRINT_BASELINE",key,payload);
        return payload;
    },ct);

    public Task<IReadOnlyList<JsonObject>> SourceSummary(SessionUser user,CancellationToken ct)=>
        Transaction<IReadOnlyList<JsonObject>>(async db=>await db.Query("""
            SELECT TOP(100) o.id AS operationId,o.erp_document_no AS documentNo,l.unit_code AS unitCode,
              SUM(l.requested_qty) AS requiredQty,
              SUM(CASE WHEN first_print.printed>0 THEN l.requested_qty ELSE 0 END) AS firstRegisteredQty,
              SUM(CASE WHEN first_print.printed=0 THEN l.requested_qty ELSE 0 END) AS remainingQty,
              SUM(CASE WHEN first_print.printed=0 AND p.locked_operation_id IS NOT NULL THEN l.requested_qty ELSE 0 END) AS reservedQty
            FROM dbo.business_operation o
            JOIN dbo.business_operation_line l ON l.operation_id=o.id
            JOIN dbo.product_label p ON p.id=l.generated_label_id
            OUTER APPLY(SELECT COUNT_BIG(*) AS printed FROM dbo.inventory_flow f
              WHERE f.label_id=p.id AND f.operation_type='PRINT') first_print
            WHERE o.status='SUCCESS' AND (p.current_qty>0 OR p.label_code IS NOT NULL) AND (o.operator_id=@user OR @admin=1)
            GROUP BY o.id,o.erp_document_no,l.unit_code ORDER BY o.id DESC;
            """,("@user",user.Id),("@admin",user.Roles.Contains("admin",StringComparer.OrdinalIgnoreCase))),ct);
}
