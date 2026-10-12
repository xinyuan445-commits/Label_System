/*
    迁移 008：建立用户管理审计记录。
    审计内容不得保存密码、密码哈希或盐值。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '007')
    THROW 50001, N'缺少前置迁移 007。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '008')
    THROW 50002, N'迁移 008 已执行，脚本停止。', 1;

IF OBJECT_ID(N'dbo.user_admin_audit', N'U') IS NOT NULL
    THROW 50003, N'迁移 008 目标表已经存在，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE dbo.user_admin_audit
    (
        audit_id            bigint IDENTITY(1,1) NOT NULL,
        action_code         varchar(40) NOT NULL,
        actor_user_id       nvarchar(100) NOT NULL,
        target_user_id      nvarchar(100) NOT NULL,
        before_json         nvarchar(max) NULL,
        after_json          nvarchar(max) NULL,
        request_id          nvarchar(100) NOT NULL,
        client_ip           nvarchar(64) NULL,
        user_agent          nvarchar(500) NULL,
        created_at          datetime2(3) NOT NULL
            CONSTRAINT DF_user_admin_audit_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),

        CONSTRAINT PK_user_admin_audit PRIMARY KEY CLUSTERED (audit_id),
        CONSTRAINT FK_user_admin_audit_actor FOREIGN KEY (actor_user_id)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT FK_user_admin_audit_target FOREIGN KEY (target_user_id)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT CK_user_admin_audit_action CHECK
            (action_code IN ('USER_CREATED', 'USER_UPDATED', 'PASSWORD_RESET',
                             'ACCOUNT_UNLOCKED', 'SESSIONS_REVOKED',
                             'PREFERENCES_RESET', 'PERMISSION_UPDATED')),
        CONSTRAINT CK_user_admin_audit_before_json CHECK
            (before_json IS NULL OR ISJSON(before_json) = 1),
        CONSTRAINT CK_user_admin_audit_after_json CHECK
            (after_json IS NULL OR ISJSON(after_json) = 1)
    );

    CREATE INDEX IX_user_admin_audit_target_time
        ON dbo.user_admin_audit(target_user_id, created_at DESC, audit_id DESC)
        INCLUDE (action_code, actor_user_id, request_id);

    CREATE INDEX IX_user_admin_audit_actor_time
        ON dbo.user_admin_audit(actor_user_id, created_at DESC, audit_id DESC)
        INCLUDE (action_code, target_user_id, request_id);

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('008', N'建立用户管理审计记录。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
