/* 迁移 016：U8 在用物料档案缓存及账套来源。 */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id='015')
    THROW 50001, N'缺少前置迁移 015。', 1;
IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id='016')
    THROW 50002, N'迁移 016 已执行，脚本停止。', 1;
IF OBJECT_ID(N'dbo.u8_material_cache', N'U') IS NOT NULL
    THROW 50003, N'物料缓存表已经存在，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    CREATE TABLE dbo.u8_material_cache
    (
        source_account_id    nvarchar(20) NOT NULL,
        source_database_name nvarchar(128) NOT NULL,
        material_code        nvarchar(100) NOT NULL,
        material_name        nvarchar(200) NOT NULL,
        specification        nvarchar(500) NOT NULL,
        unit_code            nvarchar(50) NOT NULL,
        requires_batch       bit NOT NULL,
        is_active            bit NOT NULL CONSTRAINT DF_u8_material_cache_active DEFAULT (1),
        last_seen_at         datetime2(3) NOT NULL,
        created_at           datetime2(3) NOT NULL CONSTRAINT DF_u8_material_cache_created DEFAULT (DATEADD(HOUR,8,SYSUTCDATETIME())),
        updated_at           datetime2(3) NOT NULL CONSTRAINT DF_u8_material_cache_updated DEFAULT (DATEADD(HOUR,8,SYSUTCDATETIME())),
        row_version          rowversion NOT NULL,
        CONSTRAINT PK_u8_material_cache PRIMARY KEY CLUSTERED
            (source_account_id,source_database_name,material_code),
        CONSTRAINT CK_u8_material_cache_code CHECK (LEN(LTRIM(RTRIM(material_code)))>0)
    );
    CREATE INDEX IX_u8_material_cache_selection
        ON dbo.u8_material_cache(source_account_id,source_database_name,is_active,material_code)
        INCLUDE (material_name,specification,unit_code,requires_batch);

    ALTER TABLE dbo.reference_data_sync_state ADD
        source_account_id nvarchar(20) NULL,
        source_database_name nvarchar(128) NULL;

    INSERT dbo.reference_data_sync_state(sync_key,sync_name,status,item_count)
    VALUES ('materials',N'物料档案','NEVER',0);
    INSERT dbo.schema_migration(migration_id,description)
    VALUES ('016',N'缓存 U8 在用物料档案，按账套和数据库隔离并记录同步来源。');
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
