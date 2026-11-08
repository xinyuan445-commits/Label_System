/* Migration 021: sales stock movements reuse the existing TraceBack task queue. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'LabelSystem_Dev' THROW 51000, N'本次仅安装开发库。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '020')
    THROW 51000, N'缺少迁移020。', 1;
IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '021') RETURN;

BEGIN TRANSACTION;
ALTER TABLE dbo.traceback_sync_task DROP CONSTRAINT CK_traceback_sync_task_role;
ALTER TABLE dbo.traceback_sync_task WITH CHECK ADD CONSTRAINT CK_traceback_sync_task_role
    CHECK (event_role IN ('MATERIAL_ISSUE', 'MATERIAL_RETURN', 'FULL_MOVE', 'PARTIAL_SOURCE',
        'PRINT_TARGET', 'COUNT_ADJUSTMENT', 'SALES_OUTBOUND_ISSUE', 'SALES_OUTBOUND_RETURN'));
INSERT dbo.schema_migration(migration_id, description)
VALUES ('021', N'既有追溯同步任务支持销售出库与红字入库事件。');
COMMIT TRANSACTION;
