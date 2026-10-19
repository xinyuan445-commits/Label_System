using System.Globalization;
using System.Text.Json.Nodes;

namespace LabelSystem.Api;

public sealed partial class PrintRepository
{
    private void RequireSimulation()
    {
        _=Scope;
        if(!printOptions.CurrentValue.SimulationOnly)
            throw new ApiException(404,"PRINT_SIMULATION_DISABLED","未启用模拟测试。");
    }
    public Task<JsonObject> SeedSimulation(PrintSimulationSeedRequest request,SessionUser user,CancellationToken ct)
    {
        RequireSimulation();
        if(request.Quantity<=0 || request.Quantity>100000 || decimal.Round(request.Quantity,2)!=request.Quantity)
            throw new ApiException(400,"PRINT_QUANTITY_INVALID","测试数量须大于0且最多两位小数。");
        var source="SIM888-"+PrintRules.RequestId(request.ClientRequestId)[6..];
        return Transaction(async db=>
        {
            var old=await db.One("""
                SELECT l.generated_label_id AS labelId,l.id AS originLineId,l.requested_qty AS quantity,o.operator_id
                FROM dbo.business_operation o JOIN dbo.business_operation_line l ON l.operation_id=o.id
                WHERE o.operation_no=@source;
                """,("@source",source));
            if(old is not null)
            {
                Owner(old,user);
                if(old.D("quantity")!=request.Quantity)throw PrintRules.Conflict("PRINT_REQUEST_CONFLICT","同一请求内容不同。");
                return old;
            }
            var material=await db.One("""
                SELECT TOP(1) m.material_code,m.unit_code FROM dbo.u8_material_cache m
                JOIN UFDATA_888_2021.dbo.Inventory i ON i.cInvCode COLLATE DATABASE_DEFAULT=m.material_code
                  AND i.cComunitCode COLLATE DATABASE_DEFAULT=m.unit_code
                WHERE m.source_account_id='888' AND m.source_database_name='UFDATA_888_2021'
                  AND i.dEDate IS NULL AND LEN(m.material_code) BETWEEN 1 AND 25
                  AND LEN(m.unit_code)>0 AND LEN(m.material_name)>0 ORDER BY m.material_code;
                """)??throw PrintRules.Conflict("PRINT_MATERIAL_REQUIRED","请先同步888物料。");
            var warehouse=await db.One("""
                SELECT TOP(1) w.cWhCode,CASE WHEN w.bWhPos=1 THEN p.cPosCode ELSE '' END AS location
                FROM UFDATA_888_2021.dbo.Warehouse w LEFT JOIN UFDATA_888_2021.dbo.Position p ON p.cWhCode=w.cWhCode
                WHERE w.dWhEndDate IS NULL AND (w.bWhPos=0 OR p.cPosCode IS NOT NULL)
                ORDER BY w.bWhPos,w.cWhCode,p.cPosCode;
                """)??throw PrintRules.Conflict("PRINT_WAREHOUSE_REQUIRED","888缺少有效仓库。");
            return (await db.One("""
                INSERT dbo.business_operation(operation_no,operation_type,status,operator_id,operator_name_snapshot,client_request_id,
                  erp_environment,erp_account_id,erp_year,erp_database_name_snapshot,erp_document_type,erp_document_id,erp_document_no)
                VALUES(@source,'MATERIAL_RETURN','SUCCESS',@user,@name,@source,'development','888',2021,'UFDATA_888_2021',
                  'material-outbound',@source,@source);DECLARE @op bigint=SCOPE_IDENTITY();
                INSERT dbo.product_label(current_qty,status) VALUES(@qty,'PENDING_PRINT');DECLARE @label bigint=SCOPE_IDENTITY();
                INSERT dbo.business_operation_line(operation_id,line_no,line_status,material_code,batch_no,unit_code,requested_qty,generated_label_id,erp_line_id)
                VALUES(@op,1,'SUCCESS',@material,@batch,@unit,@qty,@label,'1');DECLARE @line bigint=SCOPE_IDENTITY();
                INSERT dbo.inventory_flow(operation_id,operation_line_id,object_type,label_id,object_sequence_no,operation_type,
                  unit_code,qty_before,change_qty,qty_after,material_code,batch_no,serial_no,to_warehouse_code,to_location_code)
                VALUES(@op,@line,'PRODUCT_LABEL',@label,1,'MATERIAL_RETURN',@unit,0,@qty,@qty,@material,@batch,'',@wh,@loc);
                DECLARE @flow bigint=SCOPE_IDENTITY();UPDATE dbo.product_label SET last_flow_id=@flow WHERE id=@label;
                SELECT @label AS labelId,@line AS originLineId,@qty AS quantity;
                """,("@source",source),("@user",user.Id),("@name",user.Name+"（模拟测试）"),
                ("@qty",request.Quantity),("@material",material.S("material_code")),("@unit",material.S("unit_code")),
                ("@batch","SIM888"+request.ClientRequestId.Replace("-","")[..12]),
                ("@wh",warehouse.S("cWhCode").Trim()),("@loc",warehouse.S("location").Trim())))!;
        },ct);
    }
    public Task<JsonObject> AuditSimulation(long label,SessionUser user,CancellationToken ct)
    {
        RequireSimulation();
        return Transaction(async db=>
        {
            var row=await db.One("""
                SELECT p.id AS labelId,p.label_code AS code,p.current_qty AS quantity,p.status,
                  o.operator_id,l.requested_qty AS sourceQuantity,
                  (SELECT COUNT(*) FROM dbo.inventory_flow f WHERE f.label_id=p.id AND f.operation_type='PRINT') AS firstEvents,
                  (SELECT COUNT(*) FROM dbo.inventory_flow f WHERE f.label_id=p.id AND f.operation_type='REPRINT') AS reprintEvents,
                  (SELECT ISNULL(SUM(f.change_qty),0) FROM dbo.inventory_flow f WHERE f.label_id=p.id AND f.operation_type IN('PRINT','REPRINT')) AS printQuantityDelta,
                  (SELECT COUNT(*) FROM dbo.label_code_registry r WHERE r.product_label_id=p.id) AS codes,
                  (SELECT COUNT(*) FROM UFDATA_888_2021.dbo.TraceBackOnlyCode t WHERE t.traceBackCode COLLATE DATABASE_DEFAULT=p.label_code
                     AND CONVERT(decimal(18,6),t.sum)>0) AS legacyPositiveRows,
                  (SELECT ISNULL(SUM(CONVERT(decimal(18,6),t.sum)),0) FROM UFDATA_888_2021.dbo.TraceBackOnlyCode t
                     WHERE t.traceBackCode COLLATE DATABASE_DEFAULT=p.label_code) AS legacyBalance
                FROM dbo.product_label p JOIN dbo.business_operation_line l ON l.generated_label_id=p.id
                JOIN dbo.business_operation o ON o.id=l.operation_id
                WHERE p.id=@label AND o.erp_account_id='888' AND o.erp_database_name_snapshot='UFDATA_888_2021'
                  AND (o.operation_no LIKE 'SIM888-%' OR o.operation_type='LABEL_SOURCE');
                """,("@label",label))??throw new ApiException(404,"PRINT_SIMULATION_LABEL_NOT_FOUND","请选择模拟测试标签。");
            Owner(row,user);row.Remove("operator_id");return row;
        },ct);
    }
}
