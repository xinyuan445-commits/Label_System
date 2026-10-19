/*
  迁移 017：电脑端产品标签打印。
  仅准备，未执行。不得由服务启动、构建或测试自动执行。
  执行前需独立审核与授权；不包含 U8/TraceBack 库修改。
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

IF NOT EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id='016')
    THROW 50001,N'缺少前置迁移 016。',1;
IF EXISTS (SELECT 1 FROM dbo.schema_migration WHERE migration_id='017')
    THROW 50002,N'迁移 017 已执行，停止。',1;
IF OBJECT_ID(N'dbo.label_print_template',N'U') IS NOT NULL
    THROW 50003,N'打印结构已存在，先核对迁移。',1;
IF EXISTS (SELECT label_id FROM dbo.traceback_sync_task
           WHERE event_phase='PRINT_TARGET' GROUP BY label_id HAVING COUNT_BIG(*)>1)
    THROW 50004,N'存在重复首次打印登记任务，停止。',1;

BEGIN TRY
    BEGIN TRANSACTION;
    CREATE TABLE dbo.label_print_asset
    (
        id bigint IDENTITY PRIMARY KEY,
        sha256 binary(32) NOT NULL UNIQUE,
        media_type varchar(100) NOT NULL,
        storage_relative_path nvarchar(500) NOT NULL,
        size_bytes bigint NOT NULL CHECK(size_bytes>0 AND size_bytes<=5242880),
        width_px int NOT NULL CHECK(width_px>0),
        height_px int NOT NULL CHECK(height_px>0),
        created_by nvarchar(100) NOT NULL,
        created_at datetime2(3) NOT NULL DEFAULT(DATEADD(HOUR,8,SYSUTCDATETIME()))
    );
    CREATE TABLE dbo.label_print_template
    (
        id bigint IDENTITY PRIMARY KEY,
        name nvarchar(200) NOT NULL,
        draft_canvas_json nvarchar(max) NOT NULL CHECK(ISJSON(draft_canvas_json)=1),
        width_mm decimal(9,3) NOT NULL CHECK(width_mm>0 AND width_mm<=1000),
        height_mm decimal(9,3) NOT NULL CHECK(height_mm>0 AND height_mm<=1000),
        published_version_id bigint NULL,
        is_enabled bit NOT NULL DEFAULT(1),
        created_by nvarchar(100) NOT NULL,
        updated_by nvarchar(100) NOT NULL,
        created_at datetime2(3) NOT NULL DEFAULT(DATEADD(HOUR,8,SYSUTCDATETIME())),
        updated_at datetime2(3) NOT NULL DEFAULT(DATEADD(HOUR,8,SYSUTCDATETIME())),
        row_version rowversion NOT NULL
    );
    CREATE TABLE dbo.label_print_template_version
    (
        id bigint IDENTITY PRIMARY KEY,
        template_id bigint NOT NULL REFERENCES dbo.label_print_template(id),
        version_no int NOT NULL CHECK(version_no>0),
        canvas_json nvarchar(max) NOT NULL CHECK(ISJSON(canvas_json)=1),
        width_mm decimal(9,3) NOT NULL CHECK(width_mm>0 AND width_mm<=1000),
        height_mm decimal(9,3) NOT NULL CHECK(height_mm>0 AND height_mm<=1000),
        variable_contract_json nvarchar(max) NOT NULL CHECK(ISJSON(variable_contract_json)=1),
        content_sha256 binary(32) NOT NULL,
        renderer_version nvarchar(100) NOT NULL,
        font_manifest_json nvarchar(max) NOT NULL CHECK(ISJSON(font_manifest_json)=1),
        created_by nvarchar(100) NOT NULL,
        created_at datetime2(3) NOT NULL DEFAULT(DATEADD(HOUR,8,SYSUTCDATETIME())),
        CONSTRAINT UQ_print_template_version UNIQUE(template_id,version_no),
        CONSTRAINT UQ_print_template_version_owner UNIQUE(id,template_id)
    );
    ALTER TABLE dbo.label_print_template ADD CONSTRAINT FK_print_published_version
        FOREIGN KEY(published_version_id,id)
        REFERENCES dbo.label_print_template_version(id,template_id);
    CREATE TABLE dbo.label_print_template_version_asset
    (
        version_id bigint NOT NULL REFERENCES dbo.label_print_template_version(id),
        asset_id bigint NOT NULL REFERENCES dbo.label_print_asset(id),
        PRIMARY KEY(version_id,asset_id)
    );
    CREATE TABLE dbo.label_print_device
    (
        id uniqueidentifier PRIMARY KEY,
        name nvarchar(100) NOT NULL,
        public_key nvarchar(max) NOT NULL,
        public_key_sha256 binary(32) NOT NULL,
        protocol_version int NOT NULL CHECK(protocol_version=2),
        is_enabled bit NOT NULL DEFAULT(1),
        paired_by nvarchar(100) NOT NULL,
        owner_user_id nvarchar(100) NOT NULL,
        created_at datetime2(3) NOT NULL DEFAULT(DATEADD(HOUR,8,SYSUTCDATETIME())),
        updated_at datetime2(3) NOT NULL DEFAULT(DATEADD(HOUR,8,SYSUTCDATETIME())),
        row_version rowversion NOT NULL,
        CONSTRAINT UQ_print_device_key UNIQUE(public_key_sha256)
    );
    CREATE TABLE dbo.label_print_rule_binding
    (
        singleton_id tinyint PRIMARY KEY CHECK(singleton_id=1),
        rule_version_id bigint NOT NULL REFERENCES dbo.label_code_rule_version(id),
        updated_by nvarchar(100) NOT NULL,
        updated_at datetime2(3) NOT NULL DEFAULT(DATEADD(HOUR,8,SYSUTCDATETIME())),
        row_version rowversion NOT NULL
    );
    /* 不自动选择或改写任何现有规则，不打开打印功能。 */
    ALTER TABLE dbo.business_operation ADD
        print_job_id uniqueidentifier NULL,
        print_scope nvarchar(500) NULL,
        print_phase varchar(30) NULL,
        print_printer nvarchar(200) NULL,
        print_template_version_id bigint NULL REFERENCES dbo.label_print_template_version(id),
        print_device_id uniqueidentifier NULL REFERENCES dbo.label_print_device(id),
        print_manifest_relative_path nvarchar(500) NULL,
        print_manifest_sha256 binary(32) NULL,
        print_manifest_json nvarchar(max) NULL;
    ALTER TABLE dbo.business_operation_line ADD
        print_output_id uniqueidentifier NULL,
        print_kind varchar(10) NULL,
        print_origin_line_id bigint NULL REFERENCES dbo.business_operation_line(id),
        print_output_phase varchar(30) NULL,
        print_snapshot_json nvarchar(max) NULL;
    ALTER TABLE dbo.inventory_flow ADD print_output_line_id bigint NULL;
    ALTER TABLE dbo.business_operation_event ADD
        operation_line_id bigint NULL,
        print_attempt_id uniqueidentifier NULL,
        event_key varchar(200) NULL;

    /* ALTER 新列之后另批编译其约束和索引，仍在同一事务内。 */
    EXEC sys.sp_executesql N'
    CREATE UNIQUE INDEX UX_print_job ON dbo.business_operation(print_job_id)
        WHERE print_job_id IS NOT NULL;
    CREATE INDEX IX_print_recovery ON dbo.business_operation(print_phase,updated_at)
        INCLUDE(print_job_id,operator_id) WHERE print_phase IS NOT NULL;
    ALTER TABLE dbo.business_operation ADD CONSTRAINT CK_print_operation CHECK
      ((print_job_id IS NULL AND print_phase IS NULL AND print_scope IS NULL)
       OR (print_job_id IS NOT NULL AND print_phase IS NOT NULL AND operation_type IN (''PRINT'',''REPRINT'')
       AND erp_status=''NOT_REQUIRED'' AND print_device_id IS NOT NULL AND print_scope IS NOT NULL
       AND print_printer IS NOT NULL AND print_template_version_id IS NOT NULL
       AND print_phase IN (''PREPARING'',''READY'',''DISPATCH_AUTHORIZED'',''AWAITING_CONFIRMATION'',
       ''RECONCILIATION_REQUIRED'',''TRACEBACK_PENDING'',''COMPLETED'',''FAILED_NO_OUTPUT'',''CANCELLED'')));
    ALTER TABLE dbo.business_operation ADD CONSTRAINT CK_print_manifest CHECK
      ((print_manifest_relative_path IS NULL AND print_manifest_sha256 IS NULL AND print_manifest_json IS NULL)
       OR (print_manifest_relative_path IS NOT NULL AND print_manifest_sha256 IS NOT NULL
       AND print_manifest_json IS NOT NULL AND ISJSON(print_manifest_json)=1));
    CREATE UNIQUE INDEX UX_print_output ON dbo.business_operation_line(print_output_id)
        WHERE print_output_id IS NOT NULL;
    ALTER TABLE dbo.business_operation_line ADD CONSTRAINT CK_print_line CHECK
      ((print_output_id IS NULL AND print_kind IS NULL AND print_output_phase IS NULL AND print_snapshot_json IS NULL AND print_origin_line_id IS NULL)
       OR (print_output_id IS NOT NULL AND print_kind IS NOT NULL AND print_kind IN (''FIRST'',''REPRINT'')
       AND label_id IS NOT NULL AND pallet_id IS NULL AND requested_label_count=1
       AND requested_label_count IS NOT NULL AND printed_label_count IS NOT NULL
       AND printed_label_count IN (0,1) AND print_snapshot_json IS NOT NULL AND ISJSON(print_snapshot_json)=1
       AND (print_kind<>''FIRST'' OR print_origin_line_id IS NOT NULL)
       AND print_output_phase IS NOT NULL AND print_output_phase IN (''PREPARING'',''READY'',''AUTHORIZED'',''SUBMITTED'',''UNKNOWN'',
        ''FAILED_NO_OUTPUT'',''CONFIRMED'',''CANCELLED'')
       AND ((print_output_phase=''CONFIRMED'' AND printed_label_count=1 AND line_status=''SUCCESS'')
         OR (print_output_phase<>''CONFIRMED'' AND printed_label_count=0))));
    ALTER TABLE dbo.inventory_flow ADD CONSTRAINT FK_print_flow_output
      FOREIGN KEY(print_output_line_id,operation_id) REFERENCES dbo.business_operation_line(id,operation_id);
    CREATE UNIQUE INDEX UX_print_flow_output ON dbo.inventory_flow(print_output_line_id)
      WHERE print_output_line_id IS NOT NULL;
    CREATE UNIQUE INDEX UX_print_label_first ON dbo.inventory_flow(label_id)
      WHERE operation_type=''PRINT'' AND print_output_line_id IS NOT NULL;
    ALTER TABLE dbo.inventory_flow ADD CONSTRAINT CK_print_flow_zero CHECK
      (print_output_line_id IS NULL OR (operation_line_id IS NOT NULL AND operation_line_id=print_output_line_id
       AND object_type=''PRODUCT_LABEL'' AND operation_type IN (''PRINT'',''REPRINT'') AND change_qty=0));
    ALTER TABLE dbo.business_operation_event ADD CONSTRAINT FK_print_event_line
      FOREIGN KEY(operation_line_id,operation_id) REFERENCES dbo.business_operation_line(id,operation_id);
    CREATE UNIQUE INDEX UX_print_event_key ON dbo.business_operation_event(event_key)
      WHERE event_key IS NOT NULL;
    CREATE INDEX IX_print_attempt ON dbo.business_operation_event(operation_id,print_attempt_id,id)
      WHERE print_attempt_id IS NOT NULL;
    CREATE UNIQUE INDEX UX_print_initial_registration ON dbo.traceback_sync_task(label_id)
      WHERE event_phase=''PRINT_TARGET'';
    ALTER TABLE dbo.traceback_sync_task ADD CONSTRAINT CK_print_target_identity CHECK
      (event_phase<>''PRINT_TARGET'' OR (label_id IS NOT NULL AND operation_id IS NOT NULL
       AND operation_line_id IS NOT NULL AND NULLIF(traceback_code,N'''') IS NOT NULL
       AND pda_order_uuid IS NOT NULL AND pda_goods_uuid IS NOT NULL));
    ALTER TABLE dbo.business_operation_line DROP CONSTRAINT CK_business_operation_line_selection_source;
    ALTER TABLE dbo.business_operation_line ADD CONSTRAINT CK_business_operation_line_selection_source CHECK
      (selection_source IS NULL OR selection_source IN (''LABEL_SCAN'',''PALLET_SCAN'',''PRINT_TARGET'',''PRINT_REPRINT''));
    ';
    EXEC sys.sp_executesql N'CREATE TRIGGER dbo.TR_print_version_immutable
      ON dbo.label_print_template_version AFTER UPDATE,DELETE AS
      BEGIN SET NOCOUNT ON; THROW 50010,N''已发布的模板版本不可修改或删除。'',1; END;';
    EXEC sys.sp_executesql N'CREATE TRIGGER dbo.TR_print_asset_immutable
      ON dbo.label_print_asset AFTER UPDATE,DELETE AS
      BEGIN SET NOCOUNT ON; THROW 50011,N''打印图片不可覆盖或删除。'',1; END;';
    EXEC sys.sp_executesql N'CREATE TRIGGER dbo.TR_print_version_asset_immutable
      ON dbo.label_print_template_version_asset AFTER UPDATE,DELETE AS
      BEGIN SET NOCOUNT ON; THROW 50012,N''模板版本图片关联不可修改或删除。'',1; END;';
    /* 操作事件和成功流水是唯一业务账本；没有新增 print_jobs/print_records。 */
    INSERT dbo.system_function(function_code,function_name,function_kind,group_code,group_name,
      group_sort,function_sort,icon_code,is_enabled,updated_by)
    SELECT code,name,'QUERY','printing',N'标签打印',50,n,'document',0,N'migration-017'
    FROM (VALUES
      ('print.view',N'打印工作台',10),('print.first',N'首次打印',20),
      ('print.reprint',N'标签补打',30),('print.confirm',N'打印结果登记',40),
      ('print.reconcile',N'打印异常恢复',50),('print.template.manage',N'打印模板管理',60),
      ('print.device.manage',N'打印设备管理',70)) s(code,name,n)
    WHERE NOT EXISTS(SELECT 1 FROM dbo.system_function f WHERE f.function_code=s.code);
    INSERT INTO dbo.schema_migration(migration_id,description)
        VALUES('017',N'电脑端打印模板、规则单选及逐份幂等登记结构；默认不启用打印。');
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
