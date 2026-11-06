/* Migration 020: independent sales-return entry and existing document settings.
   No business/quantity table, no U8 writes, no grants copied from delivery-note. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_NAME()<>N'LabelSystem_Dev' THROW 51000,N'本次仅安装开发库。',1;
IF NOT EXISTS(SELECT 1 FROM dbo.schema_migration WHERE migration_id='019') THROW 51000,N'缺少迁移019。',1;
IF EXISTS(SELECT 1 FROM dbo.schema_migration WHERE migration_id='020') THROW 51000,N'迁移020已执行。',1;
BEGIN TRY
    BEGIN TRANSACTION;
    IF NOT EXISTS(SELECT 1 FROM dbo.system_function WHERE function_code='sales-return')
        INSERT dbo.system_function(function_code,function_name,function_kind,parent_function_code,
            group_code,group_name,group_sort,function_sort,icon_code,is_enabled,updated_by)
        VALUES('sales-return',N'退货单','DOCUMENT',NULL,'sales',N'销售管理',20,15,'document',1,N'SYSTEM');
    INSERT dbo.system_setting(setting_scope,document_type,setting_key,setting_name,value_json,is_enabled,setting_version,updated_by)
    SELECT 'DOCUMENT','sales-return',s.setting_key,s.setting_name,N'false',1,1,N'SYSTEM'
    FROM (VALUES('auto_add_scanned_detail',N'扫码后自动添加到明细'),
                ('auto_upload_on_complete',N'完成自动上传'),('auto_approve_after_upload',N'上传自动审核'))s(setting_key,setting_name)
    WHERE NOT EXISTS(SELECT 1 FROM dbo.system_setting t WHERE t.setting_scope='DOCUMENT' AND t.document_type='sales-return' AND t.setting_key=s.setting_key);
    INSERT dbo.schema_migration(migration_id,description) VALUES('020',N'新增独立退货单功能目录及通用单据设置。');
    COMMIT;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;
