/*
    迁移 005：缓存 U8 启用中的仓库、仓位和部门。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '004')
    THROW 50001, N'缺少前置迁移 004。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '005')
    THROW 50002, N'迁移 005 已执行，脚本停止。', 1;

IF OBJECT_ID(N'dbo.u8_warehouse_cache', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.u8_position_cache', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.u8_department_cache', N'U') IS NOT NULL
    THROW 50003, N'迁移 005 目标表已经存在，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE dbo.u8_warehouse_cache
    (
        warehouse_code  nvarchar(100) NOT NULL,
        warehouse_name  nvarchar(200) NOT NULL,
        is_active       bit NOT NULL CONSTRAINT DF_u8_warehouse_cache_is_active DEFAULT (1),
        last_seen_at    datetime2(3) NOT NULL,
        created_at      datetime2(3) NOT NULL CONSTRAINT DF_u8_warehouse_cache_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at      datetime2(3) NOT NULL CONSTRAINT DF_u8_warehouse_cache_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version     rowversion NOT NULL,

        CONSTRAINT PK_u8_warehouse_cache PRIMARY KEY CLUSTERED (warehouse_code),
        CONSTRAINT CK_u8_warehouse_cache_code CHECK (LEN(LTRIM(RTRIM(warehouse_code))) > 0),
        CONSTRAINT CK_u8_warehouse_cache_name CHECK (LEN(LTRIM(RTRIM(warehouse_name))) > 0)
    );

    CREATE TABLE dbo.u8_position_cache
    (
        position_code   nvarchar(100) NOT NULL,
        warehouse_code  nvarchar(100) NOT NULL,
        position_name   nvarchar(200) NOT NULL,
        is_active       bit NOT NULL CONSTRAINT DF_u8_position_cache_is_active DEFAULT (1),
        last_seen_at    datetime2(3) NOT NULL,
        created_at      datetime2(3) NOT NULL CONSTRAINT DF_u8_position_cache_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at      datetime2(3) NOT NULL CONSTRAINT DF_u8_position_cache_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version     rowversion NOT NULL,

        CONSTRAINT PK_u8_position_cache PRIMARY KEY CLUSTERED (position_code),
        CONSTRAINT FK_u8_position_cache_warehouse FOREIGN KEY (warehouse_code)
            REFERENCES dbo.u8_warehouse_cache(warehouse_code),
        CONSTRAINT CK_u8_position_cache_code CHECK (LEN(LTRIM(RTRIM(position_code))) > 0),
        CONSTRAINT CK_u8_position_cache_name CHECK (LEN(LTRIM(RTRIM(position_name))) > 0)
    );

    CREATE INDEX IX_u8_position_cache_selection
        ON dbo.u8_position_cache(warehouse_code, is_active, position_code)
        INCLUDE (position_name);

    CREATE TABLE dbo.u8_department_cache
    (
        department_code  nvarchar(100) NOT NULL,
        department_name  nvarchar(200) NOT NULL,
        is_active        bit NOT NULL CONSTRAINT DF_u8_department_cache_is_active DEFAULT (1),
        last_seen_at     datetime2(3) NOT NULL,
        created_at       datetime2(3) NOT NULL CONSTRAINT DF_u8_department_cache_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at       datetime2(3) NOT NULL CONSTRAINT DF_u8_department_cache_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version      rowversion NOT NULL,

        CONSTRAINT PK_u8_department_cache PRIMARY KEY CLUSTERED (department_code),
        CONSTRAINT CK_u8_department_cache_code CHECK (LEN(LTRIM(RTRIM(department_code))) > 0),
        CONSTRAINT CK_u8_department_cache_name CHECK (LEN(LTRIM(RTRIM(department_name))) > 0)
    );

    CREATE INDEX IX_u8_department_cache_selection
        ON dbo.u8_department_cache(is_active, department_code)
        INCLUDE (department_name);

    INSERT INTO dbo.reference_data_sync_state
        (sync_key, sync_name, status, item_count)
    VALUES
        ('warehouse-positions', N'仓位信息', 'NEVER', 0),
        ('departments', N'部门信息', 'NEVER', 0);

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('005', N'缓存 U8 启用中的仓库、仓位和部门。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
