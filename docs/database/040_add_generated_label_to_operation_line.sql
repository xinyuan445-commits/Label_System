/*
    迁移 013：记录无标签红字退料成功后生成的新产品标签。
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '012')
    THROW 50001, N'缺少前置迁移 012。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '013')
    THROW 50002, N'迁移 013 已执行，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    ALTER TABLE dbo.business_operation_line
        ADD generated_label_id bigint NULL;

    EXEC(N'ALTER TABLE dbo.business_operation_line
        ADD CONSTRAINT FK_business_operation_line_generated_label
            FOREIGN KEY (generated_label_id) REFERENCES dbo.product_label(id);');

    EXEC(N'CREATE UNIQUE INDEX UX_business_operation_line_generated_label
        ON dbo.business_operation_line(generated_label_id)
        WHERE generated_label_id IS NOT NULL;');

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('013', N'记录无标签红字退料生成的新产品标签。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
