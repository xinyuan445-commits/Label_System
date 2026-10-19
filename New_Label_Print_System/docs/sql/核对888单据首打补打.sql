/*
  只读核对：默认 DBD26103204。仅查询 LabelSystem_Dev 和 UFDATA_888_2021。
  INSERT 只用于本会话表变量，不写任何业务表；不启动打印、不重试同步。
  逐个结果集查看：环境、操作、明细、标签、编码、流水、同步、事件、旧三表、汇总、异常。
  旧三表按完整 Code 查询；Code 中的数量不是当前余额。
*/
USE [LabelSystem_Dev];
SET NOCOUNT ON;
SET LOCK_TIMEOUT 10000;
IF DB_NAME() <> N'LabelSystem_Dev'
    THROW 50001, N'本脚本只允许在 LabelSystem_Dev 执行。', 1;

DECLARE @DocumentNo nvarchar(100) = N'DBD26103204';
DECLARE @DocumentOps TABLE(id bigint PRIMARY KEY);
DECLARE @Labels TABLE(id bigint PRIMARY KEY);
DECLARE @Ops TABLE(id bigint PRIMARY KEY);
DECLARE @Codes TABLE(code nvarchar(2048) COLLATE DATABASE_DEFAULT);
DECLARE @GoodsKeys TABLE(uuid nvarchar(3500) COLLATE DATABASE_DEFAULT);
DECLARE @OrderKeys TABLE(uuid nvarchar(3500) COLLATE DATABASE_DEFAULT);

INSERT @DocumentOps(id)
SELECT o.id
FROM dbo.business_operation o
WHERE o.erp_environment = 'development'
  AND o.erp_account_id = '888'
  AND o.erp_database_name_snapshot = 'UFDATA_888_2021'
  AND (o.erp_document_no = @DocumentNo OR o.source_document_no = @DocumentNo
       OR EXISTS (
           SELECT 1 FROM dbo.business_operation_line l
           WHERE l.operation_id = o.id
             AND JSON_VALUE(l.print_snapshot_json, '$.sourceDocumentNo') = @DocumentNo));

INSERT @Labels(id)
SELECT l.generated_label_id FROM dbo.business_operation_line l
WHERE l.operation_id IN (SELECT id FROM @DocumentOps) AND l.generated_label_id IS NOT NULL
UNION
SELECT l.label_id FROM dbo.business_operation_line l
WHERE l.operation_id IN (SELECT id FROM @DocumentOps) AND l.label_id IS NOT NULL;

INSERT @Ops(id)
SELECT id FROM @DocumentOps
UNION
SELECT operation_id FROM dbo.business_operation_line
WHERE label_id IN (SELECT id FROM @Labels) OR generated_label_id IN (SELECT id FROM @Labels)
UNION
SELECT operation_id FROM dbo.inventory_flow WHERE label_id IN (SELECT id FROM @Labels);

INSERT @Codes(code)
SELECT label_code FROM dbo.product_label
WHERE id IN (SELECT id FROM @Labels) AND label_code IS NOT NULL
UNION
SELECT JSON_VALUE(print_snapshot_json,'$.code') FROM dbo.business_operation_line
WHERE operation_id IN (SELECT id FROM @DocumentOps)
  AND JSON_VALUE(print_snapshot_json,'$.code') IS NOT NULL;

-- 补查按单号找到的旧记录，避免漏掉失去新表关联的额外登记；同号单据需看JSON来源类型。
INSERT @GoodsKeys(uuid)
SELECT g.pdaGoodsUuid FROM [UFDATA_888_2021].dbo.TraceBackGoods g
WHERE g.uploadOrderNumber COLLATE DATABASE_DEFAULT=@DocumentNo
   OR JSON_VALUE(CASE WHEN ISJSON(CONVERT(nvarchar(max),g.[json]))=1
                      THEN CONVERT(nvarchar(max),g.[json]) END,'$.documentNumber') COLLATE DATABASE_DEFAULT=@DocumentNo
   OR JSON_VALUE(CASE WHEN ISJSON(CONVERT(nvarchar(max),g.[json]))=1
                      THEN CONVERT(nvarchar(max),g.[json]) END,'$.labelPrintSource.documentNumber') COLLATE DATABASE_DEFAULT=@DocumentNo
UNION
SELECT t.pdaGoodsUuid FROM [UFDATA_888_2021].dbo.TraceBackOnlyCode t
WHERE t.traceBackCode COLLATE DATABASE_DEFAULT IN(SELECT code FROM @Codes)
UNION
SELECT CONVERT(nvarchar(36),s.pda_goods_uuid) FROM dbo.traceback_sync_task s
WHERE s.label_id IN(SELECT id FROM @Labels) AND s.pda_goods_uuid IS NOT NULL;

INSERT @OrderKeys(uuid)
SELECT o.pdaOrderUuid FROM [UFDATA_888_2021].dbo.TraceBackOrder o
WHERE JSON_VALUE(CASE WHEN ISJSON(CONVERT(nvarchar(max),o.[json]))=1
                     THEN CONVERT(nvarchar(max),o.[json]) END,'$.documentNumber') COLLATE DATABASE_DEFAULT=@DocumentNo
   OR JSON_VALUE(CASE WHEN ISJSON(CONVERT(nvarchar(max),o.[json]))=1
                     THEN CONVERT(nvarchar(max),o.[json]) END,'$.labelPrintSource.documentNumber') COLLATE DATABASE_DEFAULT=@DocumentNo
UNION
SELECT t.pdaOrderUuid FROM [UFDATA_888_2021].dbo.TraceBackOnlyCode t
WHERE t.traceBackCode COLLATE DATABASE_DEFAULT IN(SELECT code FROM @Codes)
   OR t.pdaGoodsUuid COLLATE DATABASE_DEFAULT IN(SELECT uuid FROM @GoodsKeys)
UNION
SELECT CONVERT(nvarchar(36),s.pda_order_uuid) FROM dbo.traceback_sync_task s
WHERE s.label_id IN(SELECT id FROM @Labels) AND s.pda_order_uuid IS NOT NULL;

-- 01：本次查询范围、时间。
SELECT SYSDATETIMEOFFSET() AS queried_at, DB_NAME() AS label_database,
       N'UFDATA_888_2021' AS traceback_database, @DocumentNo AS document_no;

-- 02：业务操作。LABEL_SOURCE为来源登记；PRINT为首打；REPRINT为补打。
SELECT o.* FROM dbo.business_operation o
WHERE o.id IN (SELECT id FROM @Ops) ORDER BY o.id;

-- 03：业务明细。每个打印副本有独立print_output_id；补打快照应沿用原Code/数量/模板。
SELECT l.*, JSON_VALUE(l.print_snapshot_json,'$.code') AS snapshot_code,
       JSON_VALUE(l.print_snapshot_json,'$.quantity') AS paper_quantity,
       JSON_VALUE(l.print_snapshot_json,'$.sourceDocumentNo') AS snapshot_document_no
FROM dbo.business_operation_line l
WHERE l.operation_id IN (SELECT id FROM @Ops) ORDER BY l.operation_id,l.line_no,l.id;

-- 04：标签当前余额、状态、最近流水及锁。
SELECT p.* FROM dbo.product_label p
WHERE p.id IN (SELECT id FROM @Labels) ORDER BY p.id;

-- 05：正式编码唯一登记。补打不应另取Code。
SELECT r.* FROM dbo.label_code_registry r
WHERE r.product_label_id IN (SELECT id FROM @Labels)
   OR r.raw_code IN (SELECT code FROM @Codes) ORDER BY r.id;

-- 06：数量流水。来源登记可为+Q；PRINT/REPRINT应change_qty=0。
SELECT f.* FROM dbo.inventory_flow f
WHERE f.label_id IN (SELECT id FROM @Labels) ORDER BY f.id;

-- 07：旧三表同步任务。PRINT_TARGET只属于首打；补打不应新建此任务。
SELECT t.* FROM dbo.traceback_sync_task t
WHERE t.operation_id IN (SELECT id FROM @Ops) OR t.label_id IN (SELECT id FROM @Labels)
ORDER BY t.id;

-- 08：准备、授权、签名回执、逐副本登记、恢复等事件。
SELECT e.* FROM dbo.business_operation_event e
WHERE e.operation_id IN (SELECT id FROM @Ops) ORDER BY e.operation_id,e.sequence_no,e.id;

-- 09：TraceBackOnlyCode原始流水：同一完整Code的全部正、负、零流水均显示。
SELECT t.* FROM [UFDATA_888_2021].dbo.TraceBackOnlyCode t
WHERE t.traceBackCode COLLATE DATABASE_DEFAULT IN (SELECT code FROM @Codes)
   OR t.pdaGoodsUuid COLLATE DATABASE_DEFAULT IN(SELECT uuid FROM @GoodsKeys)
   OR t.pdaOrderUuid COLLATE DATABASE_DEFAULT IN(SELECT uuid FROM @OrderKeys)
ORDER BY t.id;

-- 10：TraceBackGoods原始JSON及关联信息，包括同步任务对应的预定UUID。
SELECT g.* FROM [UFDATA_888_2021].dbo.TraceBackGoods g
WHERE g.pdaGoodsUuid COLLATE DATABASE_DEFAULT IN(SELECT uuid FROM @GoodsKeys)
ORDER BY g.[time],g.pdaGoodsUuid;

-- 11：TraceBackOrder原始JSON及关联信息。
SELECT o.* FROM [UFDATA_888_2021].dbo.TraceBackOrder o
WHERE o.pdaOrderUuid COLLATE DATABASE_DEFAULT IN(SELECT uuid FROM @OrderKeys)
ORDER BY o.[time],o.pdaOrderUuid;

-- 12：逐Code汇总。补打份数不应增加当前余额、首打正向登记数量或唯一编码数。
SELECT p.id AS label_id,p.label_code,p.current_qty AS local_current_qty,p.status,
       (SELECT COUNT(*) FROM dbo.label_code_registry r WHERE r.product_label_id=p.id) AS code_registry_rows,
       (SELECT COUNT(*) FROM dbo.inventory_flow f WHERE f.label_id=p.id AND f.operation_type='PRINT') AS first_print_flows,
       (SELECT COUNT(*) FROM dbo.inventory_flow f WHERE f.label_id=p.id AND f.operation_type='REPRINT') AS reprint_flows,
       (SELECT COALESCE(SUM(f.change_qty),0) FROM dbo.inventory_flow f
        WHERE f.label_id=p.id AND f.operation_type IN('PRINT','REPRINT')) AS print_quantity_delta,
       (SELECT COUNT(*) FROM dbo.traceback_sync_task t WHERE t.label_id=p.id AND t.event_phase='PRINT_TARGET') AS initial_sync_tasks,
       (SELECT COUNT(*) FROM [UFDATA_888_2021].dbo.TraceBackOnlyCode t
        WHERE t.traceBackCode COLLATE DATABASE_DEFAULT=p.label_code) AS legacy_flow_rows,
       (SELECT COUNT(*) FROM [UFDATA_888_2021].dbo.TraceBackOnlyCode t
        WHERE t.traceBackCode COLLATE DATABASE_DEFAULT=p.label_code AND TRY_CONVERT(decimal(18,6),t.[sum])>0) AS legacy_positive_rows,
       (SELECT COALESCE(SUM(TRY_CONVERT(decimal(18,6),t.[sum])),0)
        FROM [UFDATA_888_2021].dbo.TraceBackOnlyCode t
        WHERE t.traceBackCode COLLATE DATABASE_DEFAULT=p.label_code) AS legacy_current_balance
FROM dbo.product_label p WHERE p.id IN (SELECT id FROM @Labels) ORDER BY p.id;

-- 13：逐首打事件核对确定UUID。正常每个PRINT_TARGET对应Order、Goods、OnlyCode各1条。
SELECT s.id AS sync_task_id,s.event_key,s.status,s.attempt_count,s.traceback_code,
       TRY_CONVERT(decimal(18,6),JSON_VALUE(s.payload_json,'$.quantity')) AS expected_initial_quantity,
       (SELECT COUNT(*) FROM [UFDATA_888_2021].dbo.TraceBackOrder o
        WHERE o.pdaOrderUuid COLLATE DATABASE_DEFAULT=CONVERT(nvarchar(36),s.pda_order_uuid)) AS order_rows,
       (SELECT COUNT(*) FROM [UFDATA_888_2021].dbo.TraceBackGoods g
        WHERE g.pdaGoodsUuid COLLATE DATABASE_DEFAULT=CONVERT(nvarchar(36),s.pda_goods_uuid)) AS goods_rows,
       (SELECT COUNT(*) FROM [UFDATA_888_2021].dbo.TraceBackOnlyCode t
        WHERE t.traceBackCode COLLATE DATABASE_DEFAULT=s.traceback_code
          AND t.pdaOrderUuid COLLATE DATABASE_DEFAULT=CONVERT(nvarchar(36),s.pda_order_uuid)
          AND t.pdaGoodsUuid COLLATE DATABASE_DEFAULT=CONVERT(nvarchar(36),s.pda_goods_uuid)) AS initial_onlycode_rows,
       (SELECT COALESCE(SUM(TRY_CONVERT(decimal(18,6),t.[sum])),0)
        FROM [UFDATA_888_2021].dbo.TraceBackOnlyCode t
        WHERE t.traceBackCode COLLATE DATABASE_DEFAULT=s.traceback_code
          AND t.pdaOrderUuid COLLATE DATABASE_DEFAULT=CONVERT(nvarchar(36),s.pda_order_uuid)
          AND t.pdaGoodsUuid COLLATE DATABASE_DEFAULT=CONVERT(nvarchar(36),s.pda_goods_uuid)) AS registered_initial_quantity
FROM dbo.traceback_sync_task s
WHERE s.label_id IN (SELECT id FROM @Labels) AND s.event_phase='PRINT_TARGET' ORDER BY s.id;

-- 14：异常检查，应为空。三表后续合法业务移动需结合09原始流水判断，不能只数正数行。
SELECT N'打印流水数量非零' AS issue,CONVERT(nvarchar(100),f.id) AS record_id
FROM dbo.inventory_flow f WHERE f.label_id IN(SELECT id FROM @Labels)
  AND f.operation_type IN('PRINT','REPRINT') AND (f.change_qty<>0 OR f.qty_before<>f.qty_after)
UNION ALL
SELECT N'同一副本重复登记',CONVERT(nvarchar(100),f.print_output_line_id)
FROM dbo.inventory_flow f WHERE f.label_id IN(SELECT id FROM @Labels) AND f.print_output_line_id IS NOT NULL
GROUP BY f.print_output_line_id HAVING COUNT(*)>1
UNION ALL
SELECT N'同标签首打超过一次',CONVERT(nvarchar(100),f.label_id)
FROM dbo.inventory_flow f WHERE f.label_id IN(SELECT id FROM @Labels) AND f.operation_type='PRINT'
GROUP BY f.label_id HAVING COUNT(*)>1
UNION ALL
SELECT N'补打错误创建首次同步任务',CONVERT(nvarchar(100),t.id)
FROM dbo.traceback_sync_task t JOIN dbo.business_operation o ON o.id=t.operation_id
WHERE t.label_id IN(SELECT id FROM @Labels) AND o.operation_type='REPRINT' AND t.event_phase='PRINT_TARGET'
UNION ALL
SELECT N'同步任务未完成',CONVERT(nvarchar(100),t.id)
FROM dbo.traceback_sync_task t WHERE t.label_id IN(SELECT id FROM @Labels) AND t.status<>'COMPLETED'
UNION ALL
SELECT N'单据存在未关联新标签的旧条码（需核对）',CONVERT(nvarchar(100),t.id)
FROM [UFDATA_888_2021].dbo.TraceBackOnlyCode t
WHERE (t.pdaGoodsUuid COLLATE DATABASE_DEFAULT IN(SELECT uuid FROM @GoodsKeys)
    OR t.pdaOrderUuid COLLATE DATABASE_DEFAULT IN(SELECT uuid FROM @OrderKeys))
  AND NOT EXISTS(SELECT 1 FROM @Codes c WHERE c.code=t.traceBackCode COLLATE DATABASE_DEFAULT);
