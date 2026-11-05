/*
  盘点新表核对：只读查询，不是数据库迁移。
  当前开发环境：LabelSystem_Dev -> 888 / UFDATA_888_2021。
  2026-11-05已核对操作159，ERP单号PDD26101311。
  查询其他上传时，只改下面的 @operation_id。
  当前余额可能因后续业务变化；业务明细和流水保存的是该次操作历史。
*/
USE [LabelSystem_Dev];
SET NOCOUNT ON;
DECLARE @operation_id bigint = 159;
DECLARE @group_id varchar(100) =
    (SELECT submission_group_id FROM dbo.business_operation WHERE id=@operation_id);

/* 1. 业务单头：谁做的、整体结果、ERP号。
   当前159：status/erp_status均SUCCESS；SUCCESS不代表U8已审核。 */
SELECT id,operation_no,submission_group_id,operation_type,status,erp_status,
       erp_document_id,erp_document_no,approval_status,
       operator_name_snapshot,created_at,error_code,error_message
FROM dbo.business_operation WHERE id=@operation_id;

/* 2. 业务明细：每张标签盘前余额、用户实盘、对应ERP行。
   inventory_qty_snapshot是本次盘前，requested_qty是本次实盘。
   同料批货位的多张标签可以对应同一个erp_line_id。 */
SELECT l.id AS operation_line_id,l.line_no,l.label_id,p.label_code,
       l.material_code,l.batch_no,l.inventory_qty_snapshot,l.requested_qty,
       l.from_warehouse_code,l.from_location_code,l.erp_line_id,l.line_status
FROM dbo.business_operation_line l
LEFT JOIN dbo.product_label p ON p.id=l.label_id
WHERE l.operation_id=@operation_id ORDER BY l.line_no;

/* 3. 标签当前镜像：当前余额、旧表同步状态、锁和最新位置。
   迁移期仍以旧TraceBack三表为权威，此表不是独立账本。
   成功后：current_qty=traceback_balance，SYNCED，锁为空。 */
SELECT p.id AS label_id,p.label_code,p.current_qty,p.status,p.mirror_source,
       p.traceback_latest_flow_id,p.traceback_balance,p.traceback_sync_status,
       p.locked_operation_id,p.last_flow_id,
       f.to_warehouse_code,f.to_location_code
FROM dbo.product_label p
LEFT JOIN dbo.inventory_flow f ON f.id=p.last_flow_id
WHERE EXISTS(SELECT 1 FROM dbo.business_operation_line l
             WHERE l.operation_id=@operation_id AND l.label_id=p.id)
ORDER BY p.id;

/* 4. 库存流水：本次前/增减/后。
   当前159为5/0/5、49/+1/50、6/0/6、14/0/14。
   其他TRACEBACK_MIRROR操作是扫码镜像，不是又做了一张ERP盘点单。 */
SELECT id,operation_id,operation_line_id,label_id,operation_type,flow_role,
       qty_before,change_qty,qty_after,from_warehouse_code,from_location_code,
       to_warehouse_code,to_location_code,created_at
FROM dbo.inventory_flow WHERE operation_id=@operation_id ORDER BY id;

/* 5. 旧三表同步任务：稳定事件键、目标UUID、重试及完成情况。
   当前159应为4条COUNT_ADJUSTMENT / COMPLETED，attempt_count=1。
   event_key/pda_order_uuid/pda_goods_uuid可用于追到旧表。
   payload_json是原始冻结事件；历史JSON修复不会重写已完成任务载荷。 */
SELECT id,operation_id,operation_line_id,label_id,event_key,event_role,
       traceback_code,pda_order_uuid,pda_goods_uuid,erp_document_no,status,
       attempt_count,last_error_code,last_error_message,completed_at,payload_json
FROM dbo.traceback_sync_task WHERE operation_id=@operation_id ORDER BY id;

/* 6. 本次上传基线：与其他日志共用business_operation_event，不再有盘点专表。
   当前159为首轮，previousLabels=[]正常；本次标签见业务明细。
   后续上传按命中料批仓/货位读取历史贡献，并排除本次重盘的同码。
   019将原辅助表的数据迁入此通用事件，原快照保持不变。 */
SELECT operation_id,JSON_VALUE(event_data_json,'$.sourceDocumentId') AS source_document_id,
       JSON_QUERY(event_data_json,'$.previousLabels') AS previous_labels_json,created_at
FROM dbo.business_operation_event
WHERE operation_id=@operation_id AND event_type='UPLOAD_BASELINE';

/* 7. 操作事件：执行过程、失败/恢复和修复审计。
   TRACEBACK_JSON_REPAIRED是JSON修复记录，不是库存数量流水。 */
SELECT sequence_no,stage,event_type,result,message,created_at
FROM dbo.business_operation_event WHERE operation_id=@operation_id ORDER BY sequence_no;

/* 8. 服务端草稿：页面内容及上传前后的状态。
   只同步到本机、尚未同步服务端的草稿，不会出现在此表。 */
SELECT draft_id,document_type,business_status,operation_group_id,source_document_no,
       document_date,warehouse_code,line_count,total_quantity,last_synced_at,payload_json
FROM dbo.document_draft
WHERE operation_group_id=@group_id OR draft_id=@group_id;
