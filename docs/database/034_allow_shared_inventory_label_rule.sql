/*
    迁移 011：允许产品标签和托盘共用同一套 Code 解析规则。
    统一登记仍记录最终对象类型，不允许用通用类型代替实际标签对象。
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '010')
    THROW 50001, N'缺少前置迁移 010。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '011')
    THROW 50002, N'迁移 011 已执行，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    ALTER TABLE dbo.label_code_rule
        DROP CONSTRAINT CK_label_code_rule_object_type;

    ALTER TABLE dbo.label_code_rule WITH CHECK
        ADD CONSTRAINT CK_label_code_rule_object_type CHECK
        (object_type IN ('PRODUCT_LABEL', 'PALLET', 'INVENTORY_OBJECT', 'EXTERNAL'));

    ALTER TABLE dbo.label_code_rule
        CHECK CONSTRAINT CK_label_code_rule_object_type;

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('011', N'允许产品标签和托盘共用标签 Code 规则。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
