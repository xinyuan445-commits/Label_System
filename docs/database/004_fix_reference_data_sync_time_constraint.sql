/*
    迁移 004：修正基础资料重复同步的时间约束。
    last_success_at 可以早于当前一轮 last_started_at；失败或运行中的新一轮同步必须保留上次成功时间。
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '003')
    THROW 50001, N'缺少前置迁移 003。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '004')
    THROW 50002, N'迁移 004 已执行，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    ALTER TABLE dbo.reference_data_sync_state
        DROP CONSTRAINT CK_reference_data_sync_times;

    ALTER TABLE dbo.reference_data_sync_state WITH CHECK
        ADD CONSTRAINT CK_reference_data_sync_times CHECK
        (
            (last_completed_at IS NULL OR last_started_at IS NULL OR last_completed_at >= last_started_at)
            AND (last_success_at IS NULL OR last_completed_at IS NULL OR last_success_at <= last_completed_at)
        );

    ALTER TABLE dbo.reference_data_sync_state
        CHECK CONSTRAINT CK_reference_data_sync_times;

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('004', N'修正基础资料重复同步的时间约束。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
