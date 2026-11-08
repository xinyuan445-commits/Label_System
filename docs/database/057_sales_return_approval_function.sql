/* Migration 022: independent return approval permission. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME() <> N'LabelSystem_Dev' THROW 51000, N'本次仅安装开发库。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '021')
    THROW 51000, N'缺少迁移021。', 1;
IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '022') RETURN;

BEGIN TRANSACTION;
IF NOT EXISTS (SELECT 1 FROM dbo.system_function WHERE function_code = 'approval-sales-return')
    INSERT dbo.system_function (function_code, function_name, function_kind, parent_function_code,
        group_code, group_name, group_sort, function_sort, icon_code, is_enabled, updated_by)
    VALUES ('approval-sales-return', N'退货单审核', 'APPROVAL', 'sales-return',
        'approval', N'审核管理', 40, 45, 'approve', 1, N'SYSTEM');
INSERT dbo.schema_migration (migration_id, description)
VALUES ('022', N'登记独立退货单审核权限。');
COMMIT TRANSACTION;
