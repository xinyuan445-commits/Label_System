/*
    迁移 007：在应用用户表增加本地密码凭据字段。
    只保存加盐派生值，不保存明文密码。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '006')
    THROW 50001, N'缺少前置迁移 006。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '007')
    THROW 50002, N'迁移 007 已执行，脚本停止。', 1;

IF COL_LENGTH(N'dbo.application_user', N'password_hash') IS NOT NULL
   OR COL_LENGTH(N'dbo.application_user', N'password_salt') IS NOT NULL
   OR COL_LENGTH(N'dbo.application_user', N'password_iterations') IS NOT NULL
    THROW 50003, N'迁移 007 目标字段已经存在，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    ALTER TABLE dbo.application_user ADD
        password_hash           varbinary(64) NULL,
        password_salt           varbinary(32) NULL,
        password_iterations     int NULL,
        failed_attempt_count    int NOT NULL CONSTRAINT DF_application_user_failed DEFAULT (0),
        locked_until            datetime2(3) NULL,
        password_changed_at     datetime2(3) NULL;

    EXEC(N'
        ALTER TABLE dbo.application_user ADD
            CONSTRAINT CK_application_user_password_fields CHECK
            (
                (password_hash IS NULL AND password_salt IS NULL
                 AND password_iterations IS NULL AND password_changed_at IS NULL)
                OR
                (password_hash IS NOT NULL AND DATALENGTH(password_hash) BETWEEN 32 AND 64
                 AND password_salt IS NOT NULL AND DATALENGTH(password_salt) BETWEEN 16 AND 32
                 AND password_iterations >= 100000 AND password_changed_at IS NOT NULL)
            ),
            CONSTRAINT CK_application_user_local_password CHECK
                (auth_source <> ''LOCAL'' OR password_hash IS NOT NULL),
            CONSTRAINT CK_application_user_failed CHECK (failed_attempt_count BETWEEN 0 AND 5);
    ');

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('007', N'在应用用户表增加本地密码凭据字段。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

