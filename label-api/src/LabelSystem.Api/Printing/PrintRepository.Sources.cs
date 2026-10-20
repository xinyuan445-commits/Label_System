using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabelSystem.Api;

public sealed partial class PrintRepository
{
    public Task<JsonObject?> OriginalLabel(long id,SessionUser user,CancellationToken ct)=>Transaction(async db=>
    {
        var original=await db.One("""
            SELECT TOP(1) o.print_job_id AS jobId,l.print_output_id AS outputId,l.print_snapshot_json AS snapshot,
              o.print_template_version_id AS templateVersionId,v.template_id AS templateId
            FROM dbo.inventory_flow f JOIN dbo.business_operation_line l ON l.id=f.print_output_line_id
            JOIN dbo.business_operation o ON o.id=f.operation_id
            JOIN dbo.label_print_template_version v ON v.id=o.print_template_version_id
            WHERE f.label_id=@id AND f.operation_type='PRINT' AND o.print_scope=@scope AND (o.operator_id=@user OR @admin=1) ORDER BY f.id;
            """,("@id",id),("@scope",Scope),("@user",user.Id),("@admin",user.Roles.Contains("admin",StringComparer.OrdinalIgnoreCase)));
        if(original is not null)return original;
        return await db.One("""
            SELECT JSON_VALUE(e.event_data_json,'$.print_snapshot_json') AS snapshot,
              v.id AS templateVersionId,v.template_id AS templateId,NULL AS jobId,NULL AS outputId
            FROM dbo.business_operation_event e JOIN dbo.business_operation o ON o.id=e.operation_id
            JOIN dbo.label_print_template_version v ON v.id=TRY_CONVERT(bigint,JSON_VALUE(e.event_data_json,'$.print_template_version_id'))
            WHERE e.event_key=@key AND e.event_type='LEGACY_FIRST_PRINT_BASELINE' AND (o.operator_id=@user OR @admin=1);
            """,("@key",PrintRules.Key("legacy-baseline",Scope,id.ToString(CultureInfo.InvariantCulture))),
            ("@user",user.Id),("@admin",user.Roles.Contains("admin",StringComparer.OrdinalIgnoreCase)));
    },ct);
    public Task<JsonObject> ReserveSource(SignedPrintMessage envelope,SessionUser user,CancellationToken ct)
    {
        _=Scope;
        using(var connection=connections.Create())
            if(!printOptions.CurrentValue.SourceEnabled || connection.Database!="LabelSystem_Dev" ||
               labelOptions.CurrentValue.Environment!="development" || labelOptions.CurrentValue.ErpAccountId!="888" ||
               labelOptions.CurrentValue.ErpDatabaseName!="UFDATA_888_2021")
                throw new ApiException(409,"PRINT_SOURCE_DISABLED","当前环境未开放来源打印。");
        var source=PrintSourceVerifier.Verify(envelope,printOptions.CurrentValue.SourceVerificationKey,user.Id,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var number="PS-"+PrintRules.RequestId(source.ClientRequestId)[6..];
        return Transaction(async db=>
        {
            await db.Scalar("DECLARE @n int; EXEC @n=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @n<0 THROW 50001,N'来源正在处理',1;SELECT 1;",("@key","print-source:"+number));
            var old=await db.One("SELECT * FROM dbo.business_operation WHERE operation_no=@no;",("@no",number));
            if(old is not null)
            {
                Owner(old,user);
                if(old.S("request_payload_hash")!=source.PlanHash)throw PrintRules.Conflict("PRINT_REQUEST_CONFLICT","同一请求内容不同。");
                return await SourceTargets(db,old.L("id"));
            }
            // All source entrances serialize on the same complete stock identity, in a stable order.
            foreach(var identity in source.Rows.Select(r=>PrintRules.Hash(new{r.MaterialCode,r.BatchNo,r.UnitCode,r.WarehouseCode,r.LocationCode})).Distinct().Order(StringComparer.Ordinal))
                await db.Scalar("DECLARE @n int; EXEC @n=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @n<0 THROW 50001,N'库存标签正在处理',1;SELECT 1;",("@key","print-stock:"+identity));
            var op=Convert.ToInt64(await db.Scalar("""
                INSERT dbo.business_operation(operation_no,operation_type,status,operator_id,operator_name_snapshot,client_request_id,
                 request_payload_hash,erp_environment,erp_account_id,erp_year,erp_database_name_snapshot,erp_document_type,erp_document_id,erp_document_no,
                 erp_status,approval_status,archive_status)
                OUTPUT inserted.id VALUES(@no,'LABEL_SOURCE','SUCCESS',@user,@name,@no,@hash,'development','888',2021,'UFDATA_888_2021',
                 @type,@doc,@number,'NOT_REQUIRED','NOT_REQUIRED','COMPLETE');
                """,("@no",number),("@user",user.Id),("@name",user.Name),("@hash",source.PlanHash),
                ("@type",source.Rows[0].SourceKind=="STOCK"?"U8_STOCK":"U8_BILL:"+source.Rows[0].DocumentId.Split(':')[0]),
                ("@doc",source.Rows[0].DocumentId), ("@number",source.Rows[0].DocumentNo)),CultureInfo.InvariantCulture);
            int lineNo=0;
            foreach(var row in source.Rows.OrderBy(r=>r.SourceKey,StringComparer.Ordinal))
            {
                await db.Scalar("DECLARE @n int; EXEC @n=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @n<0 THROW 50001,N'来源正在处理',1;SELECT 1;",("@key","print-source:"+row.SourceKey));
                var allocated=await db.Query("""
                    SELECT l.id AS originLineId,l.requested_qty,p.id AS labelId,p.label_code,p.current_qty,p.status,
                      p.locked_operation_id,p.row_version AS expectedRowVersion,l.material_code,l.batch_no,l.unit_code,
                      l.to_warehouse_code,l.to_location_code
                    FROM dbo.business_operation_line l
                    JOIN dbo.business_operation o ON o.id=l.operation_id JOIN dbo.product_label p ON p.id=l.generated_label_id
                    WHERE o.operation_type='LABEL_SOURCE' AND l.source_line_id='PS:'+@source;
                    """,("@source",row.SourceKey));
                if(allocated.Count>0)
                {
                    if(allocated.Any(a=>a["locked_operation_id"] is not null) ||
                       allocated.Select(a=>a.S("status")).Distinct(StringComparer.Ordinal).Count()>1)
                        throw PrintRules.Conflict("PRINT_RECOVERY_REQUIRED","原打印任务尚未完成，请在打印记录中恢复。");
                    var reusable=MatchSourceRepeats(allocated,row.Amounts);
                    if(reusable.Count!=row.Amounts.Count)
                        throw PrintRules.Conflict("PRINT_SOURCE_REPEAT_MISMATCH","原标签暂不可直接再次打印，请到打印记录恢复原任务。");
                    foreach(var item in reusable)
                    {
                        var repeat=item.S("status")=="ACTIVE";
                        await db.Execute("""
                            INSERT dbo.business_operation_line(operation_id,line_no,line_status,source_line_id,erp_position_id,
                              label_id,selection_source,inventory_qty_snapshot,material_code,batch_no,unit_code,requested_qty,
                              to_warehouse_code,to_location_code)
                            VALUES(@op,@line,'SUCCESS',@prefix+@source,@origin,@label,@selection,@balance,
                              @material,@batch,@unit,0,@warehouse,@location);
                            """,("@op",op),("@line",++lineNo),("@prefix",repeat?"RR:":"RF:"),("@source",row.SourceKey),
                            ("@origin",item.L("originLineId").ToString(CultureInfo.InvariantCulture)),("@material",item.S("material_code")),
                            ("@batch",item.S("batch_no")),("@unit",item.S("unit_code")),("@label",item.L("labelId")),
                            ("@selection",repeat?"PRINT_REPRINT":"PRINT_TARGET"),("@balance",item.D("current_qty")),
                            ("@warehouse",item.S("to_warehouse_code")),("@location",item.S("to_location_code")));
                    }
                    continue;
                }
                if(row.LegacyCodes.Count>0 || row.LegacyQuantity>0)
                    throw PrintRules.Conflict("PRINT_LEGACY_EVIDENCE_REQUIRED","该明细已有历史标签，请在补打页按原条码补打。");
                var overlapping=await db.Query("""
                    SELECT p.label_code,p.current_qty,p.status,o.erp_document_type FROM dbo.product_label p
                    JOIN dbo.inventory_flow f ON f.id=p.last_flow_id
                    JOIN dbo.business_operation_line l ON l.generated_label_id=p.id
                    JOIN dbo.business_operation o ON o.id=l.operation_id
                    WHERE p.current_qty>0 AND f.material_code=@material AND ISNULL(f.batch_no,'')=@batch
                     AND (f.unit_code=@unit OR EXISTS(SELECT 1 FROM UFDATA_888_2021.dbo.ComputationUnit u WHERE
                       u.cComunitCode COLLATE DATABASE_DEFAULT=@unit AND u.cComunitName COLLATE DATABASE_DEFAULT=f.unit_code))
                     AND f.to_warehouse_code=@wh AND ISNULL(f.to_location_code,'')=@loc
                     AND NOT(ISNULL(l.source_line_id,'')='PS:'+@source);
                    """,("@material",row.MaterialCode),("@batch",row.BatchNo),("@unit",row.UnitCode),
                    ("@wh",row.WarehouseCode),("@loc",row.LocationCode),("@source",row.SourceKey));
                if(row.SourceKind=="BILL" && overlapping.Any(p=>p.S("erp_document_type")=="U8_STOCK"))
                    throw PrintRules.Conflict("PRINT_SOURCE_OVERLAP","此库存已有存货打印标签，请核对原标签后补打。");
                if(row.SourceKind=="STOCK" && overlapping.Count>0)
                    throw PrintRules.Conflict("PRINT_SOURCE_OVERLAP","此库存已有标签，请按原标签补打。");
                var requested=row.Amounts.Sum();
                var parents=row.SourceKind=="BILL"?await db.Query("""
                    SELECT p.id,p.current_qty,p.last_flow_id,l.id AS origin_line,f.inventory_attributes_json,f.inventory_identity_hash,
                      f.to_warehouse_code,f.to_location_code FROM dbo.product_label p
                    JOIN dbo.business_operation_line l ON l.generated_label_id=p.id
                    JOIN dbo.business_operation o ON o.id=l.operation_id
                    JOIN dbo.inventory_flow f ON f.id=p.last_flow_id
                    WHERE p.status='PENDING_PRINT' AND p.current_qty>0 AND p.label_code IS NULL AND p.locked_operation_id IS NULL
                     AND o.operation_type<>'LABEL_SOURCE' AND o.erp_account_id='888' AND o.erp_database_name_snapshot='UFDATA_888_2021'
                     AND o.erp_document_no=@number AND o.erp_document_type=@type AND l.erp_line_id=@detail AND f.material_code=@material
                     AND ISNULL(f.batch_no,'')=@batch
                     AND (f.unit_code=@unit OR EXISTS(SELECT 1 FROM UFDATA_888_2021.dbo.ComputationUnit u WHERE
                       u.cComunitCode COLLATE DATABASE_DEFAULT=@unit AND u.cComunitName COLLATE DATABASE_DEFAULT=f.unit_code))
                     AND f.to_warehouse_code=@wh AND ISNULL(f.to_location_code,'')=@loc;
                    """,("@type",SourceDocumentType(row)),("@number",row.DocumentNo),("@detail",row.DetailId),("@material",row.MaterialCode),
                    ("@batch",row.BatchNo),("@unit",row.UnitCode),("@wh",row.WarehouseCode),("@loc",row.LocationCode)):[];
                if(row.SourceKind=="BILL")
                {
                    var originalQty=Convert.ToDecimal(await db.Scalar("""
                        SELECT ISNULL(SUM(p.current_qty),0) FROM dbo.product_label p
                        JOIN dbo.business_operation_line l ON l.generated_label_id=p.id
                        JOIN dbo.business_operation o ON o.id=l.operation_id
                        WHERE p.status='PENDING_PRINT' AND p.current_qty>0 AND o.operation_type<>'LABEL_SOURCE'
                          AND o.erp_account_id='888' AND o.erp_document_no=@number AND o.erp_document_type=@type AND l.erp_line_id=@detail;
                        """,("@type",SourceDocumentType(row)),("@number",row.DocumentNo),("@detail",row.DetailId)),CultureInfo.InvariantCulture);
                    if(originalQty!=parents.Sum(p=>p.D("current_qty")))
                        throw PrintRules.Conflict("PRINT_SOURCE_CHANGED","业务待打印标签与当前单据不一致，请核对原标签。");
                }
                // Include unallocated remainders created by earlier packing of the same business target.
                var remainders=await db.Query("""
                    SELECT p.id,p.current_qty,p.last_flow_id,l.id AS origin_line,f.inventory_attributes_json,f.inventory_identity_hash,
                      f.to_warehouse_code,f.to_location_code FROM dbo.product_label p
                    JOIN dbo.business_operation_line l ON l.generated_label_id=p.id
                    JOIN dbo.inventory_flow f ON f.id=p.last_flow_id
                    WHERE p.status='PENDING_PRINT' AND p.current_qty>0 AND p.label_code IS NULL AND p.locked_operation_id IS NULL
                     AND l.source_line_id='PR:'+@source;
                    """,("@source",row.SourceKey));
                parents.AddRange(remainders);
                var parentTotal=parents.Sum(p=>p.D("current_qty"));
                if(parents.Select(p=>p.S("inventory_identity_hash")+"|"+p.S("inventory_attributes_json")).Distinct(StringComparer.Ordinal).Count()>1)
                    throw PrintRules.Conflict("PRINT_SOURCE_CHANGED","待打印标签属性不同，请分别处理。");
                if(row.SourceKind=="BILL" && await db.Scalar("""
                    SELECT TOP(1) p.id FROM dbo.product_label p JOIN dbo.business_operation_line l ON l.generated_label_id=p.id
                    JOIN dbo.business_operation o ON o.id=l.operation_id
                    WHERE o.operation_type<>'LABEL_SOURCE' AND o.erp_account_id='888' AND o.erp_document_no=@number AND o.erp_document_type=@type
                     AND l.erp_line_id=@detail AND l.material_code=@material AND ISNULL(l.batch_no,'')=@batch
                     AND p.status='PENDING_PRINT' AND (p.label_code IS NOT NULL OR p.locked_operation_id IS NOT NULL);
                    """,("@type",SourceDocumentType(row)),("@number",row.DocumentNo),("@detail",row.DetailId),("@material",row.MaterialCode),("@batch",row.BatchNo)) is not null)
                    throw PrintRules.Conflict("PRINT_RECOVERY_REQUIRED","已有待恢复的打印任务，请先恢复原任务。");
                if(parents.Count>0&&parentTotal<requested)throw PrintRules.Conflict("PRINT_SOURCE_CHANGED","业务待打印余额不足，请重新查询。");
                foreach(var parent in parents)
                {
                    var packingLine=Convert.ToInt64(await db.Scalar("""
                        INSERT dbo.business_operation_line(operation_id,line_no,line_status,label_id,selection_source,source_line_id,
                         material_code,batch_no,unit_code,requested_qty,inventory_qty_snapshot,
                         from_warehouse_code,from_location_code,to_warehouse_code,to_location_code)
                        OUTPUT inserted.id VALUES(@op,@n,'SUCCESS',@id,'LABEL_SCAN','PF:'+@source,@material,@batch,@unit,@qty,@qty,
                         @warehouse,@location,@warehouse,@location);
                        """,("@op",op),("@n",++lineNo),("@id",parent.L("id")),("@source",row.SourceKey),
                        ("@material",row.MaterialCode),("@batch",row.BatchNo),("@unit",row.UnitCode),("@qty",parent.D("current_qty")),
                        ("@warehouse",parent.S("to_warehouse_code")),("@location",parent.S("to_location_code"))),CultureInfo.InvariantCulture);
                    await db.Execute("""
                        INSERT dbo.inventory_flow(operation_id,operation_line_id,object_type,label_id,object_sequence_no,operation_type,
                         unit_code,qty_before,change_qty,qty_after,material_code,batch_no,to_warehouse_code,to_location_code)
                        SELECT @op,@line,'PRODUCT_LABEL',p.id,ISNULL((SELECT MAX(object_sequence_no) FROM dbo.inventory_flow WHERE label_id=p.id),0)+1,
                         'LABEL_PACK',f.unit_code,p.current_qty,-p.current_qty,0,f.material_code,f.batch_no,f.to_warehouse_code,f.to_location_code
                        FROM dbo.product_label p JOIN dbo.inventory_flow f ON f.id=p.last_flow_id WHERE p.id=@id;
                        DECLARE @flow bigint=SCOPE_IDENTITY(); UPDATE dbo.product_label SET current_qty=0,last_flow_id=@flow WHERE id=@id;
                        """,("@op",op),("@line",packingLine),("@id",parent.L("id")));
                }
                var parentFlow=parents.Count>0?parents[0].L("last_flow_id"):(long?)null;
                foreach(var amount in row.Amounts)await InsertSourceTarget(db,op,++lineNo,row,amount,false,parentFlow);
                if(parentTotal>requested)await InsertSourceTarget(db,op,++lineNo,row,parentTotal-requested,true,parentFlow);
            }
            await Event(db,op,"PRINT_SOURCE_RESERVED",PrintRules.Key("source",Scope,source.ClientRequestId),new{source.ClientRequestId,source.PlanHash});
            return await SourceTargets(db,op);
        },ct);
    }
    private static string SourceDocumentType(PrintSourceRow row)=>row.DocumentId.Split(':')[0] switch
    {"DBD"=>"transfer","HWTZ"=>"location-adjustment","CLCK"=>"material-outbound",var type=>type};
    private static async Task InsertSourceTarget(PrintDb db,long op,int number,PrintSourceRow source,decimal qty,bool remainder,long? parentFlow)
    {
        await db.Execute("""
            INSERT dbo.product_label(current_qty,status) VALUES(@qty,'PENDING_PRINT');DECLARE @label bigint=SCOPE_IDENTITY();
            INSERT dbo.business_operation_line(operation_id,line_no,line_status,material_code,batch_no,unit_code,requested_qty,
             generated_label_id,source_line_id,erp_line_id,to_warehouse_code,to_location_code)
            VALUES(@op,@n,'SUCCESS',@material,@batch,@unit,@qty,@label,@selection+@source,@detail,@wh,@loc);DECLARE @line bigint=SCOPE_IDENTITY();
            INSERT dbo.inventory_flow(operation_id,operation_line_id,object_type,label_id,object_sequence_no,operation_type,
             unit_code,qty_before,change_qty,qty_after,material_code,batch_no,serial_no,to_warehouse_code,to_location_code)
            VALUES(@op,@line,'PRODUCT_LABEL',@label,1,'LABEL_SOURCE',@unit,0,@qty,@qty,@material,@batch,'',@wh,@loc);
            DECLARE @flow bigint=SCOPE_IDENTITY();UPDATE dbo.product_label SET last_flow_id=@flow WHERE id=@label;
            UPDATE child SET manufacture_date=parent.manufacture_date,expiry_date=parent.expiry_date,
              inventory_attributes_json=parent.inventory_attributes_json,attribute_schema_version=parent.attribute_schema_version,
              inventory_identity_hash=CASE WHEN child.unit_code=parent.unit_code THEN parent.inventory_identity_hash ELSE NULL END
            FROM dbo.inventory_flow child JOIN dbo.inventory_flow parent ON parent.id=@parent WHERE child.id=@flow;
            """,("@qty",qty),("@op",op),("@n",number),("@material",source.MaterialCode),("@batch",source.BatchNo),
            ("@unit",source.UnitCode),("@source",source.SourceKey),("@selection",remainder?"PR:":"PS:"),
            ("@detail",source.DetailId),("@wh",source.WarehouseCode),("@loc",source.LocationCode),("@parent",parentFlow));
    }
    private static async Task<JsonObject> SourceTargets(PrintDb db,long op)
    {
        var rows=await db.Query("""
            SELECT p.id AS labelId,
              CASE WHEN l.source_line_id LIKE 'RF:%' THEN TRY_CONVERT(bigint,l.erp_position_id) ELSE l.id END AS originLineId,
              p.row_version AS expectedRowVersion
            FROM dbo.business_operation_line l JOIN dbo.product_label p ON p.id=COALESCE(l.generated_label_id,l.label_id)
            WHERE l.operation_id=@op AND (l.source_line_id LIKE 'PS:%' OR l.source_line_id LIKE 'RF:%') ORDER BY l.line_no;
            """,("@op",op));
        var repeats=await db.Query("""
            SELECT p.id AS labelId,p.row_version AS expectedRowVersion,COUNT(*) AS copies
            FROM dbo.business_operation_line l JOIN dbo.product_label p ON p.id=l.label_id
            WHERE l.operation_id=@op AND l.source_line_id LIKE 'RR:%'
            GROUP BY p.id,p.row_version ORDER BY p.id;
            """,("@op",op));
        return new JsonObject{
            ["operationId"]=op,
            ["targets"]=new JsonArray(rows.Select(x=>(JsonNode)x).ToArray()),
            ["reprints"]=new JsonArray(repeats.Select(x=>(JsonNode)x).ToArray())
        };
    }

    public static List<JsonObject> MatchSourceRepeats(IReadOnlyList<JsonObject> allocated,IReadOnlyList<decimal> amounts)
    {
        if(allocated.Select(a=>a.S("status")).Distinct(StringComparer.Ordinal).Count()>1)return [];
        var active=allocated.Where(a=>a.S("status")=="ACTIVE" && a.S("label_code")!="")
            .OrderBy(a=>a.L("labelId")).ToArray();
        if(active.Length>0)
            return amounts.Select((_,index)=>active[index%active.Length]).ToList();
        var pending=allocated.Where(a=>a.S("status")=="PENDING_PRINT")
            .GroupBy(a=>a.L("labelId")).Select(group=>group.First()).OrderBy(a=>a.L("labelId")).ToArray();
        return pending.Length==amounts.Count?pending.ToList():[];
    }
}
