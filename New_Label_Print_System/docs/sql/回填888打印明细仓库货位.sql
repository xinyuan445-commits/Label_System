/*
  DBD26103204 打印明细位置修复。
  默认只预览；只有同一数据库会话设置 SESSION_CONTEXT('allow_position_backfill')=1 才提交。
  只允许 LabelSystem_Dev / development / 888 / UFDATA_888_2021。
  本脚本只更新 business_operation_line 的四个仓库、货位字段，不生成 Code、库存流水或旧三表记录。
*/
USE [LabelSystem_Dev];
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 10000;

IF DB_NAME() <> N'LabelSystem_Dev'
    THROW 50001, N'本脚本只允许在 LabelSystem_Dev 执行。', 1;

DECLARE @DocumentNo nvarchar(100)=N'DBD26103204';
DECLARE @Apply bit=COALESCE(TRY_CONVERT(bit,SESSION_CONTEXT(N'allow_position_backfill')),0);
DECLARE @LockResult int;
DECLARE @UpdatedLines int=0;
BEGIN TRANSACTION;
EXEC @LockResult=sys.sp_getapplock
    @Resource=N'print-position-backfill:development:888:DBD26103204',
    @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
IF @LockResult<0 THROW 50002,N'打印明细位置修复正在执行。',1;

DECLARE @DocumentOps TABLE(id bigint PRIMARY KEY);
DECLARE @Labels TABLE(id bigint PRIMARY KEY);
DECLARE @Relevant TABLE(id bigint PRIMARY KEY);
DECLARE @Expected TABLE(
    id bigint PRIMARY KEY,
    line_kind varchar(20) NOT NULL,
    from_warehouse_code nvarchar(100) NULL,
    from_location_code nvarchar(100) NULL,
    to_warehouse_code nvarchar(100) NULL,
    to_location_code nvarchar(100) NULL
);

INSERT @DocumentOps(id)
SELECT o.id
FROM dbo.business_operation o WITH(UPDLOCK,HOLDLOCK)
WHERE o.operation_type='LABEL_SOURCE'
  AND o.erp_environment='development'
  AND o.erp_account_id='888'
  AND o.erp_database_name_snapshot='UFDATA_888_2021'
  AND (o.erp_document_no=@DocumentNo OR o.source_document_no=@DocumentNo);

IF NOT EXISTS(SELECT 1 FROM @DocumentOps)
    THROW 50003,N'没有找到888开发环境的来源操作。',1;

INSERT @Labels(id)
SELECT DISTINCT l.generated_label_id
FROM dbo.business_operation_line l WITH(UPDLOCK,HOLDLOCK)
WHERE l.operation_id IN(SELECT id FROM @DocumentOps) AND l.generated_label_id IS NOT NULL;

IF NOT EXISTS(SELECT 1 FROM @Labels)
    THROW 50004,N'没有找到待修复标签。',1;

-- 锁定标签，避免修复过程中并发开始首打或补打。
IF EXISTS(
    SELECT 1 FROM dbo.product_label p WITH(UPDLOCK,HOLDLOCK)
    WHERE p.id IN(SELECT id FROM @Labels) AND p.locked_operation_id IS NOT NULL)
    THROW 50005,N'标签存在未完成打印任务，请先恢复后再修复。',1;

INSERT @Relevant(id)
SELECT l.id
FROM dbo.business_operation_line l WITH(UPDLOCK,HOLDLOCK)
WHERE l.operation_id IN(SELECT id FROM @DocumentOps)
  AND (l.generated_label_id IN(SELECT id FROM @Labels) OR l.label_id IN(SELECT id FROM @Labels))
UNION
SELECT l.id
FROM dbo.business_operation_line l WITH(UPDLOCK,HOLDLOCK)
JOIN dbo.business_operation o WITH(UPDLOCK,HOLDLOCK) ON o.id=l.operation_id
WHERE l.label_id IN(SELECT id FROM @Labels)
  AND l.print_kind IN('FIRST','REPRINT')
  AND o.erp_environment='development' AND o.erp_account_id='888'
  AND o.erp_database_name_snapshot='UFDATA_888_2021';

-- 新标签来源：来源明细只记录目标位置。
INSERT @Expected(id,line_kind,to_warehouse_code,to_location_code)
SELECT l.id,'LABEL_SOURCE',f.to_warehouse_code,f.to_location_code
FROM dbo.business_operation_line l
JOIN dbo.inventory_flow f ON f.operation_line_id=l.id AND f.label_id=l.generated_label_id
WHERE l.id IN(SELECT id FROM @Relevant) AND l.generated_label_id IS NOT NULL
  AND f.operation_type='LABEL_SOURCE';

-- 拆包消耗：没有发生位置移动，来源和目标均为拆包当时的位置。
INSERT @Expected(id,line_kind,from_warehouse_code,from_location_code,to_warehouse_code,to_location_code)
SELECT l.id,'LABEL_PACK',f.to_warehouse_code,f.to_location_code,f.to_warehouse_code,f.to_location_code
FROM dbo.business_operation_line l
JOIN dbo.inventory_flow f ON f.operation_line_id=l.id AND f.label_id=l.label_id
WHERE l.id IN(SELECT id FROM @Relevant) AND l.selection_source='LABEL_SCAN'
  AND l.source_line_id LIKE 'PF:%' AND f.operation_type='LABEL_PACK'
  AND NOT EXISTS(SELECT 1 FROM @Expected e WHERE e.id=l.id);

-- 已确认首打、补打：用对应零数量流水保存的发送时位置，不取今天的最新位置。
INSERT @Expected(id,line_kind,from_warehouse_code,from_location_code,to_warehouse_code,to_location_code)
SELECT l.id,l.print_kind,f.from_warehouse_code,f.from_location_code,f.to_warehouse_code,f.to_location_code
FROM dbo.business_operation_line l
JOIN dbo.inventory_flow f ON f.print_output_line_id=l.id AND f.label_id=l.label_id
WHERE l.id IN(SELECT id FROM @Relevant) AND l.print_kind IN('FIRST','REPRINT')
  AND NOT EXISTS(SELECT 1 FROM @Expected e WHERE e.id=l.id);

IF EXISTS(SELECT 1 FROM @Relevant r WHERE NOT EXISTS(SELECT 1 FROM @Expected e WHERE e.id=r.id))
    THROW 50006,N'存在无法由原库存流水证明位置的明细，已停止修复。',1;

IF EXISTS(
    SELECT 1 FROM dbo.business_operation_line l JOIN @Expected e ON e.id=l.id
    WHERE (NULLIF(l.from_warehouse_code,'') IS NOT NULL AND ISNULL(l.from_warehouse_code,'')<>ISNULL(e.from_warehouse_code,''))
       OR (NULLIF(l.from_location_code,'') IS NOT NULL AND ISNULL(l.from_location_code,'')<>ISNULL(e.from_location_code,''))
       OR (NULLIF(l.to_warehouse_code,'') IS NOT NULL AND ISNULL(l.to_warehouse_code,'')<>ISNULL(e.to_warehouse_code,''))
       OR (NULLIF(l.to_location_code,'') IS NOT NULL AND ISNULL(l.to_location_code,'')<>ISNULL(e.to_location_code,'')))
    THROW 50007,N'明细已有不同位置，已停止修复，未覆盖原值。',1;

SELECT @Apply AS apply_mode,l.id,l.operation_id,e.line_kind,l.material_code,l.batch_no,
       l.from_warehouse_code AS old_from_warehouse_code,l.from_location_code AS old_from_location_code,
       l.to_warehouse_code AS old_to_warehouse_code,l.to_location_code AS old_to_location_code,
       e.from_warehouse_code,e.from_location_code,e.to_warehouse_code,e.to_location_code
FROM dbo.business_operation_line l JOIN @Expected e ON e.id=l.id
ORDER BY l.operation_id,l.line_no,l.id;

IF @Apply=1
BEGIN
    UPDATE l
       SET from_warehouse_code=CASE WHEN NULLIF(l.from_warehouse_code,'') IS NULL THEN e.from_warehouse_code ELSE l.from_warehouse_code END,
           from_location_code=CASE WHEN NULLIF(l.from_location_code,'') IS NULL THEN e.from_location_code ELSE l.from_location_code END,
           to_warehouse_code=CASE WHEN NULLIF(l.to_warehouse_code,'') IS NULL THEN e.to_warehouse_code ELSE l.to_warehouse_code END,
           to_location_code=CASE WHEN NULLIF(l.to_location_code,'') IS NULL THEN e.to_location_code ELSE l.to_location_code END
    FROM dbo.business_operation_line l JOIN @Expected e ON e.id=l.id
    WHERE (NULLIF(l.from_warehouse_code,'') IS NULL AND e.from_warehouse_code IS NOT NULL)
       OR (NULLIF(l.from_location_code,'') IS NULL AND e.from_location_code IS NOT NULL)
       OR (NULLIF(l.to_warehouse_code,'') IS NULL AND e.to_warehouse_code IS NOT NULL)
       OR (NULLIF(l.to_location_code,'') IS NULL AND e.to_location_code IS NOT NULL);
    SET @UpdatedLines=@@ROWCOUNT;

    IF EXISTS(
        SELECT 1 FROM dbo.business_operation_line l JOIN @Expected e ON e.id=l.id
        WHERE ISNULL(l.from_warehouse_code,'')<>ISNULL(e.from_warehouse_code,'')
           OR ISNULL(l.from_location_code,'')<>ISNULL(e.from_location_code,'')
           OR ISNULL(l.to_warehouse_code,'')<>ISNULL(e.to_warehouse_code,'')
           OR ISNULL(l.to_location_code,'')<>ISNULL(e.to_location_code,''))
        THROW 50008,N'位置修复结果不完整，已回滚。',1;
    COMMIT TRANSACTION;
END
ELSE
    ROLLBACK TRANSACTION;

SELECT CASE WHEN @Apply=1 THEN 'APPLIED' ELSE 'PREVIEW' END AS result,
       (SELECT COUNT(*) FROM @Expected) AS matched_lines,@UpdatedLines AS updated_lines;
