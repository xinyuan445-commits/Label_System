/*
    迁移 009：建立多端草稿、设备同步状态和草稿状态事件。
    草稿正文仅保留最新 JSON；事件表不保存正文历史。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '008')
    THROW 50001, N'缺少前置迁移 008。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '009')
    THROW 50002, N'迁移 009 已执行，脚本停止。', 1;

IF OBJECT_ID(N'dbo.document_draft', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.document_draft_event', N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.client_device_state', N'U') IS NOT NULL
    THROW 50003, N'迁移 009 目标表已经存在，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE dbo.document_draft
    (
        draft_id                 nvarchar(100) NOT NULL,
        owner_user_id            nvarchar(100) NOT NULL,
        owner_display_name       nvarchar(100) NOT NULL,
        device_id                nvarchar(100) NOT NULL,
        device_name              nvarchar(200) NULL,
        document_type            varchar(50) NOT NULL,
        business_status          varchar(20) NOT NULL,
        schema_version           int NOT NULL,
        client_revision          bigint NOT NULL,
        source_type_label        nvarchar(100) NULL,
        source_document_no       nvarchar(100) NULL,
        document_date            date NULL,
        warehouse_code           nvarchar(100) NULL,
        department_code          nvarchar(100) NULL,
        line_count               int NOT NULL,
        total_quantity           decimal(18,6) NOT NULL,
        payload_json             nvarchar(max) NOT NULL,
        payload_hash             char(64) NOT NULL,
        operation_group_id       varchar(100) NULL,
        created_at               datetime2(3) NOT NULL,
        client_updated_at        datetime2(3) NOT NULL,
        last_synced_at           datetime2(3) NOT NULL
            CONSTRAINT DF_document_draft_last_synced_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        deleted_at               datetime2(3) NULL,
        row_version              rowversion NOT NULL,

        CONSTRAINT PK_document_draft PRIMARY KEY CLUSTERED (draft_id),
        CONSTRAINT FK_document_draft_owner FOREIGN KEY (owner_user_id)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT CK_document_draft_status CHECK
            (business_status IN ('DRAFT', 'PREPARED', 'COMPLETED', 'CANCELLED', 'DELETED')),
        CONSTRAINT CK_document_draft_schema_version CHECK (schema_version > 0),
        CONSTRAINT CK_document_draft_client_revision CHECK (client_revision > 0),
        CONSTRAINT CK_document_draft_line_count CHECK (line_count >= 0),
        CONSTRAINT CK_document_draft_total_quantity CHECK (total_quantity >= 0),
        CONSTRAINT CK_document_draft_payload_json CHECK (ISJSON(payload_json) = 1),
        CONSTRAINT CK_document_draft_deleted_at CHECK
            ((business_status = 'DELETED' AND deleted_at IS NOT NULL)
             OR (business_status <> 'DELETED' AND deleted_at IS NULL))
    );

    CREATE INDEX IX_document_draft_owner_status_time
        ON dbo.document_draft(owner_user_id, business_status, client_updated_at DESC, draft_id)
        INCLUDE (document_type, source_document_no, line_count, total_quantity, last_synced_at);

    CREATE INDEX IX_document_draft_admin_status_time
        ON dbo.document_draft(business_status, client_updated_at DESC, draft_id)
        INCLUDE (owner_user_id, owner_display_name, document_type, source_document_no,
                 line_count, total_quantity, device_id, last_synced_at);

    CREATE INDEX IX_document_draft_operation_group
        ON dbo.document_draft(operation_group_id)
        WHERE operation_group_id IS NOT NULL;

    CREATE TABLE dbo.document_draft_event
    (
        event_id                  bigint IDENTITY(1,1) NOT NULL,
        draft_id                 nvarchar(100) NOT NULL,
        event_code               varchar(30) NOT NULL,
        previous_status          varchar(20) NULL,
        current_status           varchar(20) NOT NULL,
        actor_user_id            nvarchar(100) NOT NULL,
        device_id                nvarchar(100) NULL,
        client_revision          bigint NULL,
        request_id               nvarchar(100) NOT NULL,
        created_at               datetime2(3) NOT NULL
            CONSTRAINT DF_document_draft_event_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),

        CONSTRAINT PK_document_draft_event PRIMARY KEY CLUSTERED (event_id),
        CONSTRAINT FK_document_draft_event_draft FOREIGN KEY (draft_id)
            REFERENCES dbo.document_draft(draft_id),
        CONSTRAINT FK_document_draft_event_actor FOREIGN KEY (actor_user_id)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT CK_document_draft_event_code CHECK
            (event_code IN ('CREATED', 'STATUS_CHANGED', 'DELETED', 'RESTORED'))
    );

    CREATE INDEX IX_document_draft_event_draft_time
        ON dbo.document_draft_event(draft_id, created_at DESC, event_id DESC)
        INCLUDE (event_code, previous_status, current_status, actor_user_id, client_revision);

    CREATE TABLE dbo.client_device_state
    (
        user_id                   nvarchar(100) NOT NULL,
        device_id                nvarchar(100) NOT NULL,
        device_name              nvarchar(200) NULL,
        device_model             nvarchar(200) NULL,
        app_version              nvarchar(50) NULL,
        local_draft_count        int NOT NULL,
        pending_sync_count       int NOT NULL,
        failed_sync_count        int NOT NULL,
        last_seen_at             datetime2(3) NOT NULL,
        last_successful_sync_at  datetime2(3) NULL,
        created_at               datetime2(3) NOT NULL
            CONSTRAINT DF_client_device_state_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at               datetime2(3) NOT NULL
            CONSTRAINT DF_client_device_state_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version              rowversion NOT NULL,

        CONSTRAINT PK_client_device_state PRIMARY KEY CLUSTERED (user_id, device_id),
        CONSTRAINT FK_client_device_state_user FOREIGN KEY (user_id)
            REFERENCES dbo.application_user(user_id),
        CONSTRAINT CK_client_device_state_counts CHECK
            (local_draft_count >= 0 AND pending_sync_count >= 0 AND failed_sync_count >= 0)
    );

    CREATE INDEX IX_client_device_state_last_seen
        ON dbo.client_device_state(last_seen_at DESC, user_id, device_id)
        INCLUDE (device_name, device_model, app_version, local_draft_count,
                 pending_sync_count, failed_sync_count, last_successful_sync_at);

    IF NOT EXISTS
    (
        SELECT 1
        FROM dbo.system_setting
        WHERE setting_scope = 'GLOBAL'
          AND document_type IS NULL
          AND setting_key = 'draft_retention_policy'
    )
    BEGIN
        INSERT INTO dbo.system_setting
            (setting_scope, document_type, setting_key, setting_name,
             value_json, is_enabled, setting_version, updated_by)
        VALUES
            ('GLOBAL', NULL, 'draft_retention_policy', N'草稿保留',
             N'{"activeMode":"PERMANENT","activeDays":null,"closedMode":"DAYS","closedDays":90}',
             1, 1, N'migration-009');
    END;

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('009', N'建立多端草稿、设备同步状态和草稿状态事件。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
