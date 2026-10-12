/*
    迁移 006：建立应用用户、用户功能权限和用户漫游偏好。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '005')
    THROW 50001, N'缺少前置迁移 005。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '006')
    THROW 50002, N'迁移 006 已执行，脚本停止。', 1;

IF OBJECT_ID(N'dbo.application_user', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.user_function_permission', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.user_preference', N'U') IS NOT NULL
    THROW 50003, N'迁移 006 目标表已经存在，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE dbo.application_user
    (
        user_id             nvarchar(100) NOT NULL,
        login_name          nvarchar(100) NOT NULL,
        display_name        nvarchar(100) NOT NULL,
        auth_source         varchar(20) NOT NULL,
        external_subject    nvarchar(200) NULL,
        erp_operator_code   nvarchar(100) NULL,
        erp_operator_name   nvarchar(100) NULL,
        is_admin            bit NOT NULL CONSTRAINT DF_application_user_is_admin DEFAULT (0),
        is_enabled          bit NOT NULL CONSTRAINT DF_application_user_is_enabled DEFAULT (1),
        last_login_at       datetime2(3) NULL,
        created_by          nvarchar(100) NOT NULL,
        updated_by          nvarchar(100) NOT NULL,
        created_at          datetime2(3) NOT NULL CONSTRAINT DF_application_user_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at          datetime2(3) NOT NULL CONSTRAINT DF_application_user_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version         rowversion NOT NULL,

        CONSTRAINT PK_application_user PRIMARY KEY CLUSTERED (user_id),
        CONSTRAINT UQ_application_user_login_name UNIQUE (login_name),
        CONSTRAINT CK_application_user_auth_source CHECK (auth_source IN ('DEVELOPMENT', 'LOCAL', 'EXTERNAL')),
        CONSTRAINT CK_application_user_id CHECK (LEN(LTRIM(RTRIM(user_id))) > 0),
        CONSTRAINT CK_application_user_login CHECK (LEN(LTRIM(RTRIM(login_name))) > 0),
        CONSTRAINT CK_application_user_name CHECK (LEN(LTRIM(RTRIM(display_name))) > 0),
        CONSTRAINT CK_application_user_erp_pair CHECK
            ((erp_operator_code IS NULL AND erp_operator_name IS NULL)
             OR (erp_operator_code IS NOT NULL AND erp_operator_name IS NOT NULL))
    );

    CREATE UNIQUE INDEX UX_application_user_external_subject
        ON dbo.application_user(external_subject)
        WHERE external_subject IS NOT NULL;

    CREATE INDEX IX_application_user_enabled
        ON dbo.application_user(is_enabled, login_name)
        INCLUDE (display_name, is_admin, erp_operator_code, erp_operator_name);

    CREATE TABLE dbo.user_function_permission
    (
        user_id             nvarchar(100) NOT NULL,
        function_code       varchar(100) NOT NULL,
        permission_state    varchar(10) NOT NULL,
        data_scope_json     nvarchar(max) NULL,
        updated_by          nvarchar(100) NOT NULL,
        created_at          datetime2(3) NOT NULL CONSTRAINT DF_user_function_permission_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at          datetime2(3) NOT NULL CONSTRAINT DF_user_function_permission_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version         rowversion NOT NULL,

        CONSTRAINT PK_user_function_permission PRIMARY KEY CLUSTERED (user_id, function_code),
        CONSTRAINT FK_user_function_permission_user FOREIGN KEY (user_id)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT FK_user_function_permission_function FOREIGN KEY (function_code)
            REFERENCES dbo.system_function(function_code),
        CONSTRAINT CK_user_function_permission_state CHECK (permission_state IN ('ALLOW', 'DENY')),
        CONSTRAINT CK_user_function_permission_scope CHECK
            (data_scope_json IS NULL OR ISJSON(data_scope_json) = 1)
    );

    CREATE INDEX IX_user_function_permission_function
        ON dbo.user_function_permission(function_code, permission_state, user_id);

    CREATE TABLE dbo.user_preference
    (
        user_id             nvarchar(100) NOT NULL,
        preference_key      varchar(100) NOT NULL,
        value_json          nvarchar(max) NOT NULL,
        preference_version  int NOT NULL CONSTRAINT DF_user_preference_version DEFAULT (1),
        updated_by_device   nvarchar(100) NULL,
        created_at          datetime2(3) NOT NULL CONSTRAINT DF_user_preference_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at          datetime2(3) NOT NULL CONSTRAINT DF_user_preference_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version         rowversion NOT NULL,

        CONSTRAINT PK_user_preference PRIMARY KEY CLUSTERED (user_id, preference_key),
        CONSTRAINT FK_user_preference_user FOREIGN KEY (user_id)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT CK_user_preference_key CHECK (LEN(LTRIM(RTRIM(preference_key))) > 0),
        CONSTRAINT CK_user_preference_json CHECK (ISJSON(value_json) = 1),
        CONSTRAINT CK_user_preference_version CHECK (preference_version > 0)
    );

    INSERT INTO dbo.application_user
        (user_id, login_name, display_name, auth_source, is_admin, is_enabled, created_by, updated_by)
    VALUES
        (N'admin', N'admin', N'管理员', 'DEVELOPMENT', 1, 1, N'migration-006', N'migration-006');

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('006', N'建立应用用户、用户功能权限和用户漫游偏好。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
