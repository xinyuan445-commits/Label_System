/*
    迁移 002：
    1. 写入已确认的全局及单据设置默认值。
    2. 增加源单明细占用查询索引，支持跨设备并发校验。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '001')
    THROW 50001, N'缺少前置迁移 001。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '002')
    THROW 50002, N'迁移 002 已执行，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    ALTER TABLE dbo.system_setting DROP CONSTRAINT CK_system_setting_value_json;
    ALTER TABLE dbo.system_setting ADD CONSTRAINT CK_system_setting_value_json CHECK
    (
        ISJSON(value_json) = 1
        OR value_json IN (N'true', N'false', N'null')
        OR TRY_CONVERT(decimal(38,10), value_json) IS NOT NULL
        OR
        (
            LEN(value_json) >= 2
            AND LEFT(value_json, 1) = N'"'
            AND RIGHT(value_json, 1) = N'"'
            AND ISJSON(N'[' + value_json + N']') = 1
        )
    );

    CREATE INDEX IX_business_operation_line_source_line
        ON dbo.business_operation_line(source_line_id, operation_id)
        INCLUDE (material_code)
        WHERE source_line_id IS NOT NULL;

    INSERT INTO dbo.system_setting
    (
        setting_scope, document_type, setting_key, setting_name,
        value_json, is_enabled, setting_version, updated_by
    )
    SELECT
        seed.setting_scope, seed.document_type, seed.setting_key, seed.setting_name,
        seed.value_json, 1, 1, N'SYSTEM'
    FROM
    (
        VALUES
        ('GLOBAL', CAST(NULL AS varchar(50)), 'reason_required', N'原因必填', N'false'),
        ('GLOBAL', NULL, 'allow_mixed_materials', N'不同物料混托', N'true'),
        ('GLOBAL', NULL, 'allow_mixed_batches', N'同料不同批次混托', N'true'),
        ('GLOBAL', NULL, 'allow_mixed_warehouses', N'不同仓库混托', N'false'),
        ('GLOBAL', NULL, 'allow_mixed_locations', N'不同货位混托', N'false'),
        ('GLOBAL', NULL, 'allow_pallet_reuse', N'空托盘允许复用', N'true'),
        ('GLOBAL', NULL, 'allow_direct_use_of_palletized_label', N'托内标签单独使用', N'false'),

        ('DOCUMENT', 'purchase-arrival', 'include_pallet_in_document_by_default', N'扫托包含托盘', N'true'),
        ('DOCUMENT', 'purchase-arrival', 'auto_upload_on_complete', N'完成自动上传', N'false'),
        ('DOCUMENT', 'purchase-arrival', 'auto_approve_after_upload', N'上传自动审核', N'false'),
        ('DOCUMENT', 'outsourcing-arrival', 'include_pallet_in_document_by_default', N'扫托包含托盘', N'true'),
        ('DOCUMENT', 'outsourcing-arrival', 'auto_upload_on_complete', N'完成自动上传', N'false'),
        ('DOCUMENT', 'outsourcing-arrival', 'auto_approve_after_upload', N'上传自动审核', N'false'),
        ('DOCUMENT', 'purchase-inbound', 'include_pallet_in_document_by_default', N'扫托包含托盘', N'true'),
        ('DOCUMENT', 'purchase-inbound', 'auto_upload_on_complete', N'完成自动上传', N'false'),
        ('DOCUMENT', 'purchase-inbound', 'auto_approve_after_upload', N'上传自动审核', N'false'),
        ('DOCUMENT', 'delivery-note', 'include_pallet_in_document_by_default', N'扫托包含托盘', N'true'),
        ('DOCUMENT', 'delivery-note', 'auto_upload_on_complete', N'完成自动上传', N'false'),
        ('DOCUMENT', 'delivery-note', 'auto_approve_after_upload', N'上传自动审核', N'false'),
        ('DOCUMENT', 'sales-outbound', 'include_pallet_in_document_by_default', N'扫托包含托盘', N'true'),
        ('DOCUMENT', 'sales-outbound', 'auto_upload_on_complete', N'完成自动上传', N'false'),
        ('DOCUMENT', 'sales-outbound', 'auto_approve_after_upload', N'上传自动审核', N'false'),
        ('DOCUMENT', 'material-outbound', 'include_pallet_in_document_by_default', N'扫托包含托盘', N'true'),
        ('DOCUMENT', 'material-outbound', 'auto_upload_on_complete', N'完成自动上传', N'false'),
        ('DOCUMENT', 'material-outbound', 'auto_approve_after_upload', N'上传自动审核', N'false'),
        ('DOCUMENT', 'material-outbound', 'allow_over_issue', N'允许超领', N'false'),
        ('DOCUMENT', 'transfer-order', 'include_pallet_in_document_by_default', N'扫托包含托盘', N'true'),
        ('DOCUMENT', 'transfer-order', 'auto_upload_on_complete', N'完成自动上传', N'false'),
        ('DOCUMENT', 'transfer-order', 'auto_approve_after_upload', N'上传自动审核', N'false'),
        ('DOCUMENT', 'location-adjustment', 'include_pallet_in_document_by_default', N'扫托包含托盘', N'true'),
        ('DOCUMENT', 'location-adjustment', 'auto_upload_on_complete', N'完成自动上传', N'false'),
        ('DOCUMENT', 'location-adjustment', 'auto_approve_after_upload', N'上传自动审核', N'false'),
        ('DOCUMENT', 'inventory-count', 'include_pallet_in_document_by_default', N'扫托包含托盘', N'true'),
        ('DOCUMENT', 'inventory-count', 'auto_upload_on_complete', N'完成自动上传', N'false'),
        ('DOCUMENT', 'inventory-count', 'auto_approve_after_upload', N'上传自动审核', N'false')
    ) AS seed(setting_scope, document_type, setting_key, setting_name, value_json);

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('002', N'写入系统设置默认值，并增加源单明细占用查询索引。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
