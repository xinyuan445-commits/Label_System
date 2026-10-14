/*
    迁移 014：待打印产品标签允许在实际打印前不分配条码 Code。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '013')
    THROW 50001, N'缺少前置迁移 013。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '014')
    THROW 50002, N'迁移 014 已执行，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    ALTER TABLE dbo.product_label
        DROP CONSTRAINT UQ_product_label_label_code;

    ALTER TABLE dbo.product_label
        ALTER COLUMN label_code nvarchar(100) NULL;

    CREATE UNIQUE INDEX UX_product_label_label_code
        ON dbo.product_label(label_code)
        WHERE label_code IS NOT NULL;

    ALTER TABLE dbo.product_label
        ADD CONSTRAINT CK_product_label_code_status
            CHECK (status = 'PENDING_PRINT' OR label_code IS NOT NULL);

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('014', N'待打印产品标签延迟到实际打印时分配条码 Code。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
