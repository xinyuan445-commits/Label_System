/* Migration 018: freeze cumulative count contributions and allow count TraceBack events.
   Apply explicitly to LabelSystem_Dev only in this release. No U8 schema/data changes. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'LabelSystem_Dev' THROW 51000,N'本次仅安装开发库。',1;
IF NOT EXISTS(SELECT 1 FROM dbo.schema_migration WHERE migration_id='017') THROW 51000,N'缺少迁移017。',1;
IF EXISTS(SELECT 1 FROM dbo.schema_migration WHERE migration_id='018') THROW 51000,N'迁移018已执行。',1;
BEGIN TRANSACTION;
CREATE TABLE dbo.inventory_count_upload_plan (
    operation_id bigint NOT NULL PRIMARY KEY REFERENCES dbo.business_operation(id),
    source_document_id bigint NULL,
    previous_labels_json nvarchar(max) NOT NULL CHECK(ISJSON(previous_labels_json)=1),
    created_at datetime2(3) NOT NULL DEFAULT(DATEADD(HOUR,8,SYSUTCDATETIME()))
);
CREATE INDEX IX_inventory_count_upload_source ON dbo.inventory_count_upload_plan(source_document_id,operation_id);
ALTER TABLE dbo.traceback_sync_task DROP CONSTRAINT CK_traceback_sync_task_role;
ALTER TABLE dbo.traceback_sync_task WITH CHECK ADD CONSTRAINT CK_traceback_sync_task_role
    CHECK(event_role IN ('MATERIAL_ISSUE','MATERIAL_RETURN','FULL_MOVE','PARTIAL_SOURCE','PRINT_TARGET','COUNT_ADJUSTMENT'));
INSERT dbo.schema_migration(migration_id,description) VALUES('018',N'盘点上传累计快照及TraceBack盘点差异事件。');
COMMIT;
