/* Migration 019: merge upload baselines into common operation events.
   Explicit Dev-only migration. Preserves existing snapshots and removes the count-only table.
   No U8, label quantity, inventory flow, sync task payload or write-setting changes. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME()<>N'LabelSystem_Dev' THROW 51000,N'本次仅安装开发库。',1;
IF NOT EXISTS(SELECT 1 FROM dbo.schema_migration WHERE migration_id='018') THROW 51000,N'缺少迁移018。',1;
IF EXISTS(SELECT 1 FROM dbo.schema_migration WHERE migration_id='019') THROW 51000,N'迁移019已执行。',1;
IF OBJECT_ID(N'dbo.inventory_count_upload_plan',N'U') IS NULL THROW 51000,N'原基线表不存在。',1;
BEGIN TRY
    BEGIN TRANSACTION;
    IF EXISTS(SELECT 1 FROM dbo.business_operation WHERE status='WRITING_ERP')
        THROW 51000,N'存在正在上传的单据，稍后迁移。',1;
    IF EXISTS(SELECT 1 FROM dbo.inventory_count_upload_plan p JOIN dbo.business_operation o ON o.id=p.operation_id
              WHERE ISNULL(o.erp_document_type,'')<>'inventory-count' OR ISNULL(o.erp_account_id,N'')<>N'888'
                 OR ISNULL(o.erp_database_name_snapshot,N'')<>N'UFDATA_888_2021'
                 OR (p.source_document_id IS NOT NULL AND NULLIF(o.source_document_id,N'') IS NOT NULL
                     AND o.source_document_id<>CONVERT(nvarchar(100),p.source_document_id)))
        THROW 51000,N'原单绑定需核对。',1;
    IF EXISTS(SELECT 1 FROM dbo.inventory_count_upload_plan p JOIN dbo.business_operation_event e ON e.operation_id=p.operation_id
              WHERE e.event_type='UPLOAD_BASELINE')
        THROW 51000,N'已存在上传基线事件，停止避免覆盖。',1;

    INSERT dbo.business_operation_event
        (operation_id,sequence_no,stage,source,event_type,result,message,event_data_json,created_at)
    SELECT p.operation_id,ISNULL(seq.last_sequence,0)+1,'UPLOAD_BASELINE','BUSINESS_API','UPLOAD_BASELINE','SUCCESS',
           N'从原辅助表迁入本次上传基线。',
           (SELECT 'inventory-count' AS documentType,p.source_document_id AS sourceDocumentId,
                   JSON_QUERY(p.previous_labels_json) AS previousLabels FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER),
           p.created_at
    FROM dbo.inventory_count_upload_plan p
    OUTER APPLY(SELECT MAX(e.sequence_no) last_sequence FROM dbo.business_operation_event e WITH(UPDLOCK,HOLDLOCK)
                WHERE e.operation_id=p.operation_id) seq;

    IF EXISTS(SELECT 1 FROM dbo.inventory_count_upload_plan p
              WHERE NOT EXISTS(SELECT 1 FROM dbo.business_operation_event e
                  WHERE e.operation_id=p.operation_id AND e.event_type='UPLOAD_BASELINE'
                    AND JSON_QUERY(e.event_data_json,'$.previousLabels')=JSON_QUERY(p.previous_labels_json)
                    AND ISNULL(JSON_VALUE(e.event_data_json,'$.sourceDocumentId'),N'')=
                        ISNULL(CONVERT(nvarchar(100),p.source_document_id),N'')))
        THROW 51000,N'上传基线迁移不一致。',1;

    UPDATE o SET source_document_type='inventory-count',source_document_id=CONVERT(nvarchar(100),p.source_document_id),
                 updated_at=DATEADD(HOUR,8,SYSUTCDATETIME())
    FROM dbo.business_operation o JOIN dbo.inventory_count_upload_plan p ON p.operation_id=o.id
    WHERE p.source_document_id IS NOT NULL;

    DROP TABLE dbo.inventory_count_upload_plan;
    INSERT dbo.schema_migration(migration_id,description)
    VALUES('019',N'上传基线合并到通用操作事件，移除盘点辅助表。');
    COMMIT;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;
