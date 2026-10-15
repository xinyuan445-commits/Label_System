/*
    迁移 015：增加 TraceBack 产品标签镜像版本和可恢复同步任务。
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

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '014')
    THROW 50001, N'缺少前置迁移 014。', 1;

IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id = '015')
    THROW 50002, N'迁移 015 已执行，脚本停止。', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    ALTER TABLE dbo.product_label ADD
        mirror_source varchar(20) NOT NULL
            CONSTRAINT DF_product_label_mirror_source DEFAULT ('LOCAL'),
        traceback_account_id nvarchar(20) NULL,
        traceback_latest_flow_id bigint NULL,
        traceback_balance decimal(18,6) NULL,
        traceback_identity_hash char(64) NULL,
        traceback_sync_status varchar(30) NOT NULL
            CONSTRAINT DF_product_label_traceback_sync_status DEFAULT ('NOT_APPLICABLE'),
        traceback_synced_at datetime2(3) NULL;

    /* 新增列和引用这些列的约束、索引不能在同一静态批次中编译，保留在当前事务内动态创建。 */
    EXEC sys.sp_executesql N'
        ALTER TABLE dbo.product_label ADD
            CONSTRAINT CK_product_label_mirror_source
                CHECK (mirror_source IN (''LOCAL'', ''TRACEBACK'')),
            CONSTRAINT CK_product_label_traceback_flow
                CHECK (traceback_latest_flow_id IS NULL OR traceback_latest_flow_id > 0),
            CONSTRAINT CK_product_label_traceback_balance
                CHECK (traceback_balance IS NULL OR traceback_balance >= 0),
            CONSTRAINT CK_product_label_traceback_hash
                CHECK (traceback_identity_hash IS NULL OR
                       traceback_identity_hash NOT LIKE ''%[^0-9A-F]%'' AND
                       LEN(traceback_identity_hash) = 64),
            CONSTRAINT CK_product_label_traceback_sync_status
                CHECK (traceback_sync_status IN
                       (''NOT_APPLICABLE'', ''SYNCED'', ''STALE'', ''PENDING'', ''FAILED'', ''UNKNOWN'')),
            CONSTRAINT CK_product_label_traceback_source_fields
                CHECK (
                    mirror_source = ''LOCAL'' AND traceback_account_id IS NULL
                        AND traceback_latest_flow_id IS NULL AND traceback_balance IS NULL
                        AND traceback_identity_hash IS NULL AND traceback_sync_status = ''NOT_APPLICABLE''
                        AND traceback_synced_at IS NULL
                    OR
                    mirror_source = ''TRACEBACK'' AND label_code IS NOT NULL
                        AND traceback_account_id IS NOT NULL
                        AND traceback_latest_flow_id IS NOT NULL
                        AND traceback_balance IS NOT NULL
                        AND traceback_identity_hash IS NOT NULL
                        AND traceback_sync_status <> ''NOT_APPLICABLE''
                        AND traceback_synced_at IS NOT NULL
                );

        CREATE INDEX IX_product_label_traceback_cursor
            ON dbo.product_label(mirror_source, traceback_latest_flow_id)
            INCLUDE(label_code, traceback_sync_status, traceback_synced_at)
            WHERE mirror_source = ''TRACEBACK'';';

    CREATE TABLE dbo.traceback_sync_task
    (
        id bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_traceback_sync_task PRIMARY KEY,
        event_key varchar(200) NOT NULL,
        operation_id bigint NULL,
        operation_line_id bigint NULL,
        label_id bigint NULL,
        event_phase varchar(30) NOT NULL,
        event_role varchar(40) NOT NULL,
        traceback_code nvarchar(100) NULL,
        pda_order_uuid uniqueidentifier NULL,
        pda_goods_uuid uniqueidentifier NULL,
        erp_document_type varchar(50) NULL,
        erp_document_no nvarchar(100) NULL,
        payload_json nvarchar(max) NOT NULL,
        payload_hash char(64) NOT NULL,
        status varchar(20) NOT NULL
            CONSTRAINT DF_traceback_sync_task_status DEFAULT ('PENDING'),
        attempt_count int NOT NULL
            CONSTRAINT DF_traceback_sync_task_attempt_count DEFAULT (0),
        next_retry_at datetime2(3) NULL,
        last_attempt_at datetime2(3) NULL,
        last_error_code varchar(100) NULL,
        last_error_message nvarchar(2000) NULL,
        completed_at datetime2(3) NULL,
        created_at datetime2(3) NOT NULL
            CONSTRAINT DF_traceback_sync_task_created_at
                DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at datetime2(3) NOT NULL
            CONSTRAINT DF_traceback_sync_task_updated_at
                DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version rowversion NOT NULL,
        CONSTRAINT UQ_traceback_sync_task_event_key UNIQUE(event_key),
        CONSTRAINT FK_traceback_sync_task_operation
            FOREIGN KEY(operation_id) REFERENCES dbo.business_operation(id),
        CONSTRAINT FK_traceback_sync_task_operation_line
            FOREIGN KEY(operation_line_id) REFERENCES dbo.business_operation_line(id),
        CONSTRAINT FK_traceback_sync_task_label
            FOREIGN KEY(label_id) REFERENCES dbo.product_label(id),
        CONSTRAINT CK_traceback_sync_task_phase
            CHECK (event_phase IN ('ERP_SOURCE', 'PRINT_TARGET')),
        CONSTRAINT CK_traceback_sync_task_role
            CHECK (event_role IN
                   ('MATERIAL_ISSUE', 'MATERIAL_RETURN', 'FULL_MOVE', 'PARTIAL_SOURCE', 'PRINT_TARGET')),
        CONSTRAINT CK_traceback_sync_task_status
            CHECK (status IN ('PENDING', 'PROCESSING', 'COMPLETED', 'FAILED', 'UNKNOWN')),
        CONSTRAINT CK_traceback_sync_task_attempt_count
            CHECK (attempt_count >= 0),
        CONSTRAINT CK_traceback_sync_task_payload_json
            CHECK (ISJSON(payload_json) = 1),
        CONSTRAINT CK_traceback_sync_task_payload_hash
            CHECK (payload_hash NOT LIKE '%[^0-9A-F]%' AND LEN(payload_hash) = 64),
        CONSTRAINT CK_traceback_sync_task_completed
            CHECK ((status = 'COMPLETED' AND completed_at IS NOT NULL) OR
                   (status <> 'COMPLETED' AND completed_at IS NULL)),
        CONSTRAINT CK_traceback_sync_task_times
            CHECK ((last_attempt_at IS NULL OR last_attempt_at >= created_at) AND
                   (completed_at IS NULL OR completed_at >= created_at) AND
                   updated_at >= created_at)
    );

    CREATE INDEX IX_traceback_sync_task_recovery
        ON dbo.traceback_sync_task(status, next_retry_at, created_at)
        INCLUDE(event_key, operation_id, label_id, attempt_count, last_error_code);

    CREATE INDEX IX_traceback_sync_task_operation
        ON dbo.traceback_sync_task(operation_id, id)
        WHERE operation_id IS NOT NULL;

    CREATE INDEX IX_traceback_sync_task_label
        ON dbo.traceback_sync_task(label_id, id)
        WHERE label_id IS NOT NULL;

    INSERT INTO dbo.schema_migration(migration_id, description)
    VALUES ('015', N'增加 TraceBack 标签镜像版本与可恢复同步任务。');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

