/*
    迁移 012：缓存 U8 采购类型、销售类型和币种。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '011')
    THROW 50001, N'缺少前置迁移 011。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '012')
    THROW 50002, N'迁移 012 已执行，脚本停止。', 1;

IF OBJECT_ID(N'dbo.u8_purchase_type_cache', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.u8_sales_type_cache', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.u8_currency_cache', N'U') IS NOT NULL
    THROW 50003, N'迁移 012 目标表已经存在，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE dbo.u8_purchase_type_cache
    (
        type_code          nvarchar(20) NOT NULL,
        type_name          nvarchar(100) NOT NULL,
        rd_style_code      nvarchar(20) NULL,
        is_default         bit NOT NULL,
        source_row_version binary(8) NULL,
        is_active          bit NOT NULL CONSTRAINT DF_u8_purchase_type_cache_is_active DEFAULT (1),
        last_seen_at       datetime2(3) NOT NULL,
        created_at         datetime2(3) NOT NULL CONSTRAINT DF_u8_purchase_type_cache_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at         datetime2(3) NOT NULL CONSTRAINT DF_u8_purchase_type_cache_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version        rowversion NOT NULL,

        CONSTRAINT PK_u8_purchase_type_cache PRIMARY KEY CLUSTERED (type_code),
        CONSTRAINT CK_u8_purchase_type_cache_code CHECK (LEN(LTRIM(RTRIM(type_code))) > 0),
        CONSTRAINT CK_u8_purchase_type_cache_name CHECK (LEN(LTRIM(RTRIM(type_name))) > 0)
    );

    CREATE INDEX IX_u8_purchase_type_cache_selection
        ON dbo.u8_purchase_type_cache(is_active, is_default DESC, type_code)
        INCLUDE (type_name, rd_style_code);

    CREATE TABLE dbo.u8_sales_type_cache
    (
        type_code          nvarchar(20) NOT NULL,
        type_name          nvarchar(100) NOT NULL,
        rd_style_code      nvarchar(20) NULL,
        is_default         bit NOT NULL,
        source_row_version binary(8) NULL,
        is_active          bit NOT NULL CONSTRAINT DF_u8_sales_type_cache_is_active DEFAULT (1),
        last_seen_at       datetime2(3) NOT NULL,
        created_at         datetime2(3) NOT NULL CONSTRAINT DF_u8_sales_type_cache_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at         datetime2(3) NOT NULL CONSTRAINT DF_u8_sales_type_cache_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version        rowversion NOT NULL,

        CONSTRAINT PK_u8_sales_type_cache PRIMARY KEY CLUSTERED (type_code),
        CONSTRAINT CK_u8_sales_type_cache_code CHECK (LEN(LTRIM(RTRIM(type_code))) > 0),
        CONSTRAINT CK_u8_sales_type_cache_name CHECK (LEN(LTRIM(RTRIM(type_name))) > 0)
    );

    CREATE INDEX IX_u8_sales_type_cache_selection
        ON dbo.u8_sales_type_cache(is_active, is_default DESC, type_code)
        INCLUDE (type_name, rd_style_code);

    CREATE TABLE dbo.u8_currency_cache
    (
        currency_code      nvarchar(20) NOT NULL,
        currency_name      nvarchar(100) NOT NULL,
        is_local           bit NOT NULL,
        uses_exchange_rate bit NOT NULL,
        decimal_digits     tinyint NOT NULL,
        latest_rate        decimal(24, 10) NULL,
        rate_year          int NULL,
        rate_period        tinyint NULL,
        rate_date          nvarchar(20) NULL,
        source_row_version binary(8) NULL,
        is_active          bit NOT NULL CONSTRAINT DF_u8_currency_cache_is_active DEFAULT (1),
        last_seen_at       datetime2(3) NOT NULL,
        created_at         datetime2(3) NOT NULL CONSTRAINT DF_u8_currency_cache_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at         datetime2(3) NOT NULL CONSTRAINT DF_u8_currency_cache_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version        rowversion NOT NULL,

        CONSTRAINT PK_u8_currency_cache PRIMARY KEY CLUSTERED (currency_code),
        CONSTRAINT UQ_u8_currency_cache_name UNIQUE (currency_name),
        CONSTRAINT CK_u8_currency_cache_code CHECK (LEN(LTRIM(RTRIM(currency_code))) > 0),
        CONSTRAINT CK_u8_currency_cache_name CHECK (LEN(LTRIM(RTRIM(currency_name))) > 0),
        CONSTRAINT CK_u8_currency_cache_rate CHECK (latest_rate IS NULL OR latest_rate > 0)
    );

    CREATE INDEX IX_u8_currency_cache_selection
        ON dbo.u8_currency_cache(is_active, is_local DESC, currency_code)
        INCLUDE (currency_name, latest_rate);

    INSERT INTO dbo.reference_data_sync_state
        (sync_key, sync_name, status, item_count)
    VALUES ('document-master-data', N'单据基础档案', 'NEVER', 0);

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('012', N'缓存 U8 采购类型、销售类型和币种。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
