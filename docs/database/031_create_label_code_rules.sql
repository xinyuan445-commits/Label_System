/*
    迁移 010：建立本地标签 Code 规则、不可变版本、流水和统一登记。
    本迁移不创建默认规则，不生成产品标签或托盘标签。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '009')
    THROW 50001, N'缺少前置迁移 009。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '010')
    THROW 50002, N'迁移 010 已执行，脚本停止。', 1;

IF OBJECT_ID(N'dbo.label_code_rule', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.label_code_rule_version', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.label_code_sequence', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.label_code_registry', N'U') IS NOT NULL
    THROW 50003, N'迁移 010 目标表已经存在，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE dbo.label_code_rule
    (
        id                  bigint IDENTITY(1,1) NOT NULL,
        rule_code           varchar(50) NOT NULL,
        rule_name           nvarchar(100) NOT NULL,
        object_type         varchar(20) NOT NULL,
        priority            int NOT NULL CONSTRAINT DF_label_code_rule_priority DEFAULT (100),
        is_enabled          bit NOT NULL CONSTRAINT DF_label_code_rule_enabled DEFAULT (1),
        created_by          nvarchar(100) NOT NULL,
        updated_by          nvarchar(100) NOT NULL,
        created_at          datetime2(3) NOT NULL
            CONSTRAINT DF_label_code_rule_created_at DEFAULT (DATEADD(HOUR,8,SYSUTCDATETIME())),
        updated_at          datetime2(3) NOT NULL
            CONSTRAINT DF_label_code_rule_updated_at DEFAULT (DATEADD(HOUR,8,SYSUTCDATETIME())),
        row_version         rowversion NOT NULL,

        CONSTRAINT PK_label_code_rule PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_label_code_rule_code UNIQUE (rule_code),
        CONSTRAINT FK_label_code_rule_created_by FOREIGN KEY (created_by)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT FK_label_code_rule_updated_by FOREIGN KEY (updated_by)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT CK_label_code_rule_code CHECK
            (LEN(LTRIM(RTRIM(rule_code))) > 0),
        CONSTRAINT CK_label_code_rule_object_type CHECK
            (object_type IN ('PRODUCT_LABEL', 'PALLET', 'EXTERNAL')),
        CONSTRAINT CK_label_code_rule_priority CHECK
            (priority BETWEEN 0 AND 10000)
    );

    CREATE INDEX IX_label_code_rule_resolve
        ON dbo.label_code_rule(is_enabled, priority, id)
        INCLUDE (rule_code, rule_name, object_type);

    CREATE TABLE dbo.label_code_rule_version
    (
        id                  bigint IDENTITY(1,1) NOT NULL,
        rule_id             bigint NOT NULL,
        version_no          int NOT NULL,
        version_status      varchar(20) NOT NULL,
        spec_json           nvarchar(max) NOT NULL,
        spec_sha256         char(64) NOT NULL,
        created_by          nvarchar(100) NOT NULL,
        updated_by          nvarchar(100) NOT NULL,
        activated_by        nvarchar(100) NULL,
        retired_by          nvarchar(100) NULL,
        created_at          datetime2(3) NOT NULL
            CONSTRAINT DF_label_code_rule_version_created_at DEFAULT (DATEADD(HOUR,8,SYSUTCDATETIME())),
        updated_at          datetime2(3) NOT NULL
            CONSTRAINT DF_label_code_rule_version_updated_at DEFAULT (DATEADD(HOUR,8,SYSUTCDATETIME())),
        activated_at        datetime2(3) NULL,
        retired_at          datetime2(3) NULL,
        row_version         rowversion NOT NULL,

        CONSTRAINT PK_label_code_rule_version PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_label_code_rule_version UNIQUE (rule_id, version_no),
        CONSTRAINT FK_label_code_rule_version_rule FOREIGN KEY (rule_id)
            REFERENCES dbo.label_code_rule(id),
        CONSTRAINT FK_label_code_rule_version_created_by FOREIGN KEY (created_by)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT FK_label_code_rule_version_updated_by FOREIGN KEY (updated_by)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT FK_label_code_rule_version_activated_by FOREIGN KEY (activated_by)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT FK_label_code_rule_version_retired_by FOREIGN KEY (retired_by)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT CK_label_code_rule_version_no CHECK (version_no > 0),
        CONSTRAINT CK_label_code_rule_version_status CHECK
            (version_status IN ('DRAFT', 'ACTIVE', 'RETIRED', 'BLOCKED')),
        CONSTRAINT CK_label_code_rule_version_json CHECK (ISJSON(spec_json) = 1),
        CONSTRAINT CK_label_code_rule_version_dates CHECK
        (
            (version_status = 'DRAFT' AND activated_at IS NULL AND retired_at IS NULL)
            OR (version_status = 'ACTIVE' AND activated_at IS NOT NULL AND retired_at IS NULL)
            OR (version_status IN ('RETIRED', 'BLOCKED') AND activated_at IS NOT NULL)
        )
    );

    CREATE UNIQUE INDEX UX_label_code_rule_one_draft
        ON dbo.label_code_rule_version(rule_id)
        WHERE version_status = 'DRAFT';

    CREATE UNIQUE INDEX UX_label_code_rule_one_active
        ON dbo.label_code_rule_version(rule_id)
        WHERE version_status = 'ACTIVE';

    CREATE INDEX IX_label_code_rule_version_catalog
        ON dbo.label_code_rule_version(version_status, rule_id, version_no DESC)
        INCLUDE (spec_sha256, activated_at, retired_at);

    CREATE TABLE dbo.label_code_sequence
    (
        rule_version_id     bigint NOT NULL,
        partition_key       nvarchar(200) NOT NULL,
        current_value       bigint NOT NULL,
        updated_at          datetime2(3) NOT NULL
            CONSTRAINT DF_label_code_sequence_updated_at DEFAULT (DATEADD(HOUR,8,SYSUTCDATETIME())),
        row_version         rowversion NOT NULL,

        CONSTRAINT PK_label_code_sequence PRIMARY KEY CLUSTERED
            (rule_version_id, partition_key),
        CONSTRAINT FK_label_code_sequence_version FOREIGN KEY (rule_version_id)
            REFERENCES dbo.label_code_rule_version(id),
        CONSTRAINT CK_label_code_sequence_value CHECK (current_value >= 0),
        CONSTRAINT CK_label_code_sequence_partition CHECK
            (LEN(LTRIM(RTRIM(partition_key))) > 0)
    );

    CREATE TABLE dbo.label_code_registry
    (
        id                  bigint IDENTITY(1,1) NOT NULL,
        code_hash           binary(32) NOT NULL,
        normalized_code     nvarchar(max) NOT NULL,
        raw_code            nvarchar(max) NOT NULL,
        object_type         varchar(20) NOT NULL,
        product_label_id    bigint NULL,
        pallet_id           bigint NULL,
        rule_version_id     bigint NULL,
        erp_account_id      nvarchar(20) NULL,
        parsed_payload_json nvarchar(max) NULL,
        registry_status     varchar(20) NOT NULL,
        created_at          datetime2(3) NOT NULL
            CONSTRAINT DF_label_code_registry_created_at DEFAULT (DATEADD(HOUR,8,SYSUTCDATETIME())),
        updated_at          datetime2(3) NOT NULL
            CONSTRAINT DF_label_code_registry_updated_at DEFAULT (DATEADD(HOUR,8,SYSUTCDATETIME())),
        row_version         rowversion NOT NULL,

        CONSTRAINT PK_label_code_registry PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_label_code_registry_hash UNIQUE (code_hash),
        CONSTRAINT FK_label_code_registry_product FOREIGN KEY (product_label_id)
            REFERENCES dbo.product_label(id),
        CONSTRAINT FK_label_code_registry_pallet FOREIGN KEY (pallet_id)
            REFERENCES dbo.pallet(id),
        CONSTRAINT FK_label_code_registry_version FOREIGN KEY (rule_version_id)
            REFERENCES dbo.label_code_rule_version(id),
        CONSTRAINT CK_label_code_registry_type CHECK
            (object_type IN ('PRODUCT_LABEL', 'PALLET', 'EXTERNAL')),
        CONSTRAINT CK_label_code_registry_status CHECK
            (registry_status IN ('RESERVED', 'PENDING_PRINT', 'ACTIVE', 'RETIRED', 'VOID', 'EXTERNAL')),
        CONSTRAINT CK_label_code_registry_code CHECK
            (LEN(normalized_code) BETWEEN 1 AND 2048 AND LEN(raw_code) BETWEEN 1 AND 2048),
        CONSTRAINT CK_label_code_registry_payload CHECK
            (parsed_payload_json IS NULL OR ISJSON(parsed_payload_json) = 1),
        CONSTRAINT CK_label_code_registry_target CHECK
        (
            (object_type = 'PRODUCT_LABEL' AND pallet_id IS NULL)
            OR (object_type = 'PALLET' AND product_label_id IS NULL)
            OR (object_type = 'EXTERNAL' AND product_label_id IS NULL AND pallet_id IS NULL)
        )
    );

    CREATE INDEX IX_label_code_registry_product
        ON dbo.label_code_registry(product_label_id)
        WHERE product_label_id IS NOT NULL;

    CREATE INDEX IX_label_code_registry_pallet
        ON dbo.label_code_registry(pallet_id)
        WHERE pallet_id IS NOT NULL;

    CREATE INDEX IX_label_code_registry_rule
        ON dbo.label_code_registry(rule_version_id, registry_status, id)
        WHERE rule_version_id IS NOT NULL;

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('010', N'建立本地标签 Code 规则、版本、流水和统一登记。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
