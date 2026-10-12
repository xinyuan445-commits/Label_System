/*
    迁移 003：缓存 U8 出入库类别（Rd_Style）并记录基础资料同步状态。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '002')
    THROW 50001, N'缺少前置迁移 002。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '003')
    THROW 50002, N'迁移 003 已执行，脚本停止。', 1;

IF OBJECT_ID(N'dbo.u8_rd_style_cache', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.reference_data_sync_state', N'U') IS NOT NULL
    THROW 50003, N'迁移 003 目标表已经存在，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE dbo.reference_data_sync_state
    (
        sync_key            varchar(50) NOT NULL,
        sync_name           nvarchar(100) NOT NULL,
        status              varchar(20) NOT NULL,
        item_count          int NOT NULL CONSTRAINT DF_reference_data_sync_item_count DEFAULT (0),
        last_started_at     datetime2(3) NULL,
        last_completed_at   datetime2(3) NULL,
        last_success_at     datetime2(3) NULL,
        synced_by           nvarchar(100) NULL,
        error_message       nvarchar(500) NULL,
        row_version         rowversion NOT NULL,

        CONSTRAINT PK_reference_data_sync_state PRIMARY KEY CLUSTERED (sync_key),
        CONSTRAINT CK_reference_data_sync_status CHECK
            (status IN ('NEVER', 'RUNNING', 'SUCCESS', 'FAILED')),
        CONSTRAINT CK_reference_data_sync_item_count CHECK (item_count >= 0),
        CONSTRAINT CK_reference_data_sync_times CHECK
        (
            (last_completed_at IS NULL OR last_started_at IS NULL OR last_completed_at >= last_started_at)
            AND (last_success_at IS NULL OR last_started_at IS NULL OR last_success_at >= last_started_at)
        )
    );

    CREATE TABLE dbo.u8_rd_style_cache
    (
        rd_style_code       nvarchar(10) NOT NULL,
        rd_style_name       nvarchar(24) NOT NULL,
        rd_grade            tinyint NOT NULL,
        direction           varchar(10) NOT NULL,
        is_terminal         bit NOT NULL,
        opposite_code       nvarchar(80) NULL,
        is_retail           bit NOT NULL CONSTRAINT DF_u8_rd_style_cache_is_retail DEFAULT (0),
        source_row_version  binary(8) NULL,
        is_active           bit NOT NULL CONSTRAINT DF_u8_rd_style_cache_is_active DEFAULT (1),
        last_seen_at        datetime2(3) NOT NULL,
        created_at          datetime2(3) NOT NULL CONSTRAINT DF_u8_rd_style_cache_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at          datetime2(3) NOT NULL CONSTRAINT DF_u8_rd_style_cache_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version         rowversion NOT NULL,

        CONSTRAINT PK_u8_rd_style_cache PRIMARY KEY CLUSTERED (rd_style_code),
        CONSTRAINT CK_u8_rd_style_cache_direction CHECK (direction IN ('IN', 'OUT')),
        CONSTRAINT CK_u8_rd_style_cache_code CHECK (LEN(LTRIM(RTRIM(rd_style_code))) > 0),
        CONSTRAINT CK_u8_rd_style_cache_name CHECK (LEN(LTRIM(RTRIM(rd_style_name))) > 0)
    );

    CREATE INDEX IX_u8_rd_style_cache_selection
        ON dbo.u8_rd_style_cache(direction, is_active, is_terminal, rd_style_code)
        INCLUDE (rd_style_name, rd_grade);

    INSERT INTO dbo.reference_data_sync_state
        (sync_key, sync_name, status, item_count)
    VALUES
        ('rd-styles', N'出入库类别', 'NEVER', 0);

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('003', N'缓存 U8 出入库类别，并记录基础资料同步状态。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
