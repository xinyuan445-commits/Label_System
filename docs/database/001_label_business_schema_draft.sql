/*
    标签管理系统业务数据库结构（评审稿）

    适用数据库：Microsoft SQL Server
    状态：仅供字段和关系评审，尚未冻结，禁止直接用于生产环境。
    时间字段：全部直接保存北京时间（UTC+8），精确到毫秒。
    数量规则：当前阶段每个物料只使用一个标签计量单位，数量精度 decimal(18,6)，不做单位换算。
    安全规则：本库不得保存 SQL 密码、U8 密码、令牌或完整连接字符串。

    执行特性：
    1. 脚本不创建数据库，请先切换到明确的目标数据库。
    2. 任一同名业务表已经存在时立即停止。
    3. 全部建表、约束、索引和说明在同一事务内完成，失败时整体回滚。
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF EXISTS
(
    SELECT 1
    FROM sys.tables AS t
    INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
    WHERE s.name = N'dbo'
      AND t.name IN
      (
          N'business_operation',
          N'pallet_material',
          N'business_operation_line',
          N'product_label',
          N'pallet',
          N'inventory_flow',
          N'pallet_label_relation',
          N'business_operation_event',
          N'system_function',
          N'system_setting'
      )
)
BEGIN
    THROW 50001, N'目标数据库中已经存在同名业务表，脚本已停止，未执行任何建表操作。', 1;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    /* 一笔提交、打印、审核、标签或托盘作业的操作主记录。 */
    CREATE TABLE dbo.business_operation
    (
        id                          bigint IDENTITY(1,1) NOT NULL,
        operation_no                nvarchar(50) NOT NULL,
        submission_group_id         varchar(100) NULL,
        operation_type              varchar(50) NOT NULL,
        status                      varchar(30) NOT NULL,

        source_document_type        varchar(50) NULL,
        source_document_id          nvarchar(100) NULL,
        source_document_no          nvarchar(100) NULL,

        erp_environment             varchar(20) NULL,
        erp_account_id              nvarchar(20) NULL,
        erp_year                    smallint NULL,
        erp_database_name_snapshot  nvarchar(128) NULL,
        erp_document_type           varchar(50) NULL,
        erp_document_id             nvarchar(100) NULL,
        erp_document_no             nvarchar(100) NULL,
        erp_operation_type          varchar(30) NULL,
        erp_version_before          nvarchar(100) NULL,
        erp_version_after           nvarchar(100) NULL,
        erp_status                  varchar(30) NOT NULL CONSTRAINT DF_business_operation_erp_status DEFAULT ('NOT_REQUIRED'),
        erp_confirmed_at            datetime2(3) NULL,

        operator_id                 nvarchar(100) NOT NULL,
        operator_name_snapshot      nvarchar(100) NOT NULL,
        device_id                   nvarchar(100) NULL,
        device_code                 nvarchar(100) NULL,
        device_model                nvarchar(100) NULL,

        client_request_id           varchar(100) NOT NULL,
        request_payload_hash        char(64) NULL,

        approval_status             varchar(20) NOT NULL CONSTRAINT DF_business_operation_approval_status DEFAULT ('NOT_REQUIRED'),
        approver_id                 nvarchar(100) NULL,
        approver_name_snapshot      nvarchar(100) NULL,
        approved_at                 datetime2(3) NULL,

        reason                      nvarchar(500) NULL,
        reversal_of_operation_id    bigint NULL,

        lock_acquired_at            datetime2(3) NULL,
        heartbeat_at                datetime2(3) NULL,
        lock_expires_at             datetime2(3) NULL,

        archive_relative_path       nvarchar(500) NULL,
        archive_status              varchar(20) NOT NULL CONSTRAINT DF_business_operation_archive_status DEFAULT ('PENDING'),
        archive_sha256              char(64) NULL,

        error_code                  varchar(100) NULL,
        error_message               nvarchar(2000) NULL,
        created_at                  datetime2(3) NOT NULL CONSTRAINT DF_business_operation_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at                  datetime2(3) NOT NULL CONSTRAINT DF_business_operation_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        completed_at                datetime2(3) NULL,
        row_version                 rowversion NOT NULL,

        CONSTRAINT PK_business_operation PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_business_operation_operation_no UNIQUE (operation_no),
        CONSTRAINT UQ_business_operation_client_request_id UNIQUE (client_request_id),
        CONSTRAINT FK_business_operation_reversal
            FOREIGN KEY (reversal_of_operation_id) REFERENCES dbo.business_operation(id),
        CONSTRAINT CK_business_operation_status CHECK
        (
            status IN
            (
                'DRAFT', 'PENDING', 'WRITING_ERP', 'PENDING_APPROVAL',
                'SUCCESS', 'FAILED', 'CANCELLED', 'REVERSED',
                'NEEDS_RECONCILIATION'
            )
        ),
        CONSTRAINT CK_business_operation_erp_environment CHECK
        (
            erp_environment IS NULL OR erp_environment IN ('development', 'test', 'production')
        ),
        CONSTRAINT CK_business_operation_erp_year CHECK
        (
            erp_year IS NULL OR erp_year BETWEEN 2000 AND 9999
        ),
        CONSTRAINT CK_business_operation_erp_status CHECK
        (
            erp_status IN ('NOT_REQUIRED', 'PENDING', 'WRITING', 'SUCCESS', 'FAILED', 'UNKNOWN')
        ),
        CONSTRAINT CK_business_operation_approval_status CHECK
        (
            approval_status IN ('NOT_REQUIRED', 'PENDING', 'SUCCESS', 'FAILED', 'CANCELLED')
        ),
        CONSTRAINT CK_business_operation_archive_status CHECK
        (
            archive_status IN ('PENDING', 'COMPLETE', 'FAILED', 'MISSING')
        ),
        CONSTRAINT CK_business_operation_request_hash CHECK
        (
            request_payload_hash IS NULL OR request_payload_hash NOT LIKE '%[^0-9A-Fa-f]%'
        ),
        CONSTRAINT CK_business_operation_archive_hash CHECK
        (
            archive_sha256 IS NULL OR archive_sha256 NOT LIKE '%[^0-9A-Fa-f]%'
        ),
        CONSTRAINT CK_business_operation_lock_times CHECK
        (
            (
                lock_acquired_at IS NULL
                AND heartbeat_at IS NULL
                AND lock_expires_at IS NULL
            )
            OR
            (
                lock_acquired_at IS NOT NULL
                AND lock_expires_at IS NOT NULL
                AND lock_expires_at >= lock_acquired_at
                AND (heartbeat_at IS NULL OR heartbeat_at >= lock_acquired_at)
            )
        )
    );

    CREATE INDEX IX_business_operation_erp_document
        ON dbo.business_operation
        (
            erp_environment,
            erp_account_id,
            erp_year,
            erp_document_type,
            erp_document_no
        );

    CREATE INDEX IX_business_operation_status_created_at
        ON dbo.business_operation(status, created_at DESC);

    CREATE INDEX IX_business_operation_submission_group
        ON dbo.business_operation(submission_group_id, id)
        WHERE submission_group_id IS NOT NULL;

    CREATE INDEX IX_business_operation_reversal
        ON dbo.business_operation(reversal_of_operation_id)
        WHERE reversal_of_operation_id IS NOT NULL;

    /* 维护哪些 U8 存货编码专用于打印和管理托盘。 */
    CREATE TABLE dbo.pallet_material
    (
        id                          bigint IDENTITY(1,1) NOT NULL,
        material_code               nvarchar(100) NOT NULL,
        is_enabled                  bit NOT NULL CONSTRAINT DF_pallet_material_is_enabled DEFAULT (1),
        remark                      nvarchar(500) NULL,
        updated_by                  nvarchar(100) NOT NULL,
        created_at                  datetime2(3) NOT NULL CONSTRAINT DF_pallet_material_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at                  datetime2(3) NOT NULL CONSTRAINT DF_pallet_material_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version                 rowversion NOT NULL,

        CONSTRAINT PK_pallet_material PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_pallet_material_code UNIQUE (material_code)
    );

    /* 保存用户提交的业务明细以及它与 U8 表体行、货位行的对应关系。 */
    CREATE TABLE dbo.business_operation_line
    (
        id                          bigint IDENTITY(1,1) NOT NULL,
        operation_id                bigint NOT NULL,
        line_no                     int NOT NULL,
        line_status                 varchar(20) NOT NULL CONSTRAINT DF_business_operation_line_status DEFAULT ('PENDING'),

        label_id                    bigint NULL,
        pallet_id                   bigint NULL,
        selection_source            varchar(20) NULL,
        source_pallet_id            bigint NULL,
        inventory_qty_snapshot      decimal(18,6) NULL,

        source_line_id              nvarchar(100) NULL,
        source_line_no              nvarchar(50) NULL,
        erp_line_id                 nvarchar(100) NULL,
        erp_line_no                 nvarchar(50) NULL,
        erp_position_id             nvarchar(100) NULL,

        material_code               nvarchar(100) NOT NULL,
        batch_no                    nvarchar(100) NULL,
        manufacture_date            date NULL,
        expiry_date                 date NULL,
        serial_no                   nvarchar(200) NULL,
        inventory_attributes_json   nvarchar(max) NULL,
        attribute_schema_version    int NOT NULL CONSTRAINT DF_business_operation_line_attribute_schema DEFAULT (1),
        inventory_identity_hash     char(64) NULL,

        unit_code                   nvarchar(50) NOT NULL,
        requested_qty               decimal(18,6) NOT NULL,
        package_spec_snapshot       nvarchar(200) NULL,
        package_qty                 decimal(18,6) NULL,
        requested_label_count       int NULL,
        printed_label_count         int NULL,

        from_warehouse_code         nvarchar(100) NULL,
        from_location_code          nvarchar(100) NULL,
        to_warehouse_code           nvarchar(100) NULL,
        to_location_code            nvarchar(100) NULL,
        remark                      nvarchar(500) NULL,

        error_code                  varchar(100) NULL,
        error_message               nvarchar(2000) NULL,
        created_at                  datetime2(3) NOT NULL CONSTRAINT DF_business_operation_line_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at                  datetime2(3) NOT NULL CONSTRAINT DF_business_operation_line_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version                 rowversion NOT NULL,

        CONSTRAINT PK_business_operation_line PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_business_operation_line_no UNIQUE (operation_id, line_no),
        CONSTRAINT UQ_business_operation_line_id_operation UNIQUE (id, operation_id),
        CONSTRAINT FK_business_operation_line_operation
            FOREIGN KEY (operation_id) REFERENCES dbo.business_operation(id),
        CONSTRAINT CK_business_operation_line_no CHECK (line_no > 0),
        CONSTRAINT CK_business_operation_line_status CHECK
        (
            line_status IN ('PENDING', 'SUCCESS', 'FAILED', 'SKIPPED')
        ),
        CONSTRAINT CK_business_operation_line_requested_qty CHECK (requested_qty >= 0),
        CONSTRAINT CK_business_operation_line_package_qty CHECK
        (
            package_qty IS NULL OR package_qty > 0
        ),
        CONSTRAINT CK_business_operation_line_label_counts CHECK
        (
            (requested_label_count IS NULL OR requested_label_count >= 0)
            AND (printed_label_count IS NULL OR printed_label_count >= 0)
        ),
        CONSTRAINT CK_business_operation_line_selection_source CHECK
        (
            selection_source IS NULL OR selection_source IN ('LABEL_SCAN', 'PALLET_SCAN')
        ),
        CONSTRAINT CK_business_operation_line_inventory_binding CHECK
        (
            (
                label_id IS NULL
                AND pallet_id IS NULL
                AND selection_source IS NULL
                AND source_pallet_id IS NULL
                AND inventory_qty_snapshot IS NULL
            )
            OR
            (
                label_id IS NOT NULL
                AND pallet_id IS NULL
                AND selection_source IS NOT NULL
                AND inventory_qty_snapshot IS NOT NULL
                AND inventory_qty_snapshot >= 0
                AND (selection_source <> 'PALLET_SCAN' OR source_pallet_id IS NOT NULL)
            )
            OR
            (
                label_id IS NULL
                AND pallet_id IS NOT NULL
                AND selection_source = 'PALLET_SCAN'
                AND source_pallet_id IS NULL
                AND inventory_qty_snapshot IS NOT NULL
                AND inventory_qty_snapshot IN (0, 1)
                AND requested_qty = 1
            )
        ),
        CONSTRAINT CK_business_operation_line_dates CHECK
        (
            expiry_date IS NULL OR manufacture_date IS NULL OR expiry_date >= manufacture_date
        ),
        CONSTRAINT CK_business_operation_line_attributes_json CHECK
        (
            inventory_attributes_json IS NULL OR ISJSON(inventory_attributes_json) = 1
        ),
        CONSTRAINT CK_business_operation_line_identity_hash CHECK
        (
            inventory_identity_hash IS NULL OR inventory_identity_hash NOT LIKE '%[^0-9A-Fa-f]%'
        )
    );

    CREATE INDEX IX_business_operation_line_material
        ON dbo.business_operation_line(material_code, batch_no);

    CREATE INDEX IX_business_operation_line_erp_line
        ON dbo.business_operation_line(operation_id, erp_line_id, erp_position_id);

    /* 每个产品标签永久保留一条主记录，只保存当前数量和控制状态。 */
    CREATE TABLE dbo.product_label
    (
        id                          bigint IDENTITY(1,1) NOT NULL,
        label_code                  nvarchar(100) NOT NULL,
        current_qty                 decimal(18,6) NOT NULL CONSTRAINT DF_product_label_current_qty DEFAULT (0),
        status                      varchar(30) NOT NULL,
        last_flow_id                bigint NULL,
        locked_operation_id         bigint NULL,

        created_at                  datetime2(3) NOT NULL CONSTRAINT DF_product_label_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at                  datetime2(3) NOT NULL CONSTRAINT DF_product_label_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version                 rowversion NOT NULL,

        CONSTRAINT PK_product_label PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_product_label_label_code UNIQUE (label_code),
        CONSTRAINT FK_product_label_locked_operation
            FOREIGN KEY (locked_operation_id) REFERENCES dbo.business_operation(id),
        CONSTRAINT CK_product_label_current_qty CHECK (current_qty >= 0),
        CONSTRAINT CK_product_label_status CHECK
        (
            status IN ('PENDING_PRINT', 'ACTIVE', 'USED', 'SPLIT', 'MERGED', 'FROZEN', 'VOID')
        )
    );

    CREATE INDEX IX_product_label_locked_operation
        ON dbo.product_label(locked_operation_id)
        WHERE locked_operation_id IS NOT NULL;

    /* 托盘标签、存货身份和当前库存状态；所装产品及其数量不在本表重复保存。 */
    CREATE TABLE dbo.pallet
    (
        id                          bigint IDENTITY(1,1) NOT NULL,
        pallet_code                 nvarchar(100) NOT NULL,
        material_code               nvarchar(100) NOT NULL,
        batch_no                    nvarchar(100) NULL,
        manufacture_date            date NULL,
        unit_code                   nvarchar(50) NOT NULL,
        current_qty                 decimal(18,6) NOT NULL CONSTRAINT DF_pallet_current_qty DEFAULT (1),
        status                      varchar(20) NOT NULL,
        last_flow_id                bigint NULL,
        current_warehouse_code      nvarchar(100) NULL,
        current_location_code       nvarchar(100) NULL,
        last_operation_id           bigint NULL,
        locked_operation_id         bigint NULL,
        created_at                  datetime2(3) NOT NULL CONSTRAINT DF_pallet_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at                  datetime2(3) NOT NULL CONSTRAINT DF_pallet_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version                 rowversion NOT NULL,

        CONSTRAINT PK_pallet PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_pallet_pallet_code UNIQUE (pallet_code),
        CONSTRAINT FK_pallet_material
            FOREIGN KEY (material_code) REFERENCES dbo.pallet_material(material_code),
        CONSTRAINT FK_pallet_last_operation
            FOREIGN KEY (last_operation_id) REFERENCES dbo.business_operation(id),
        CONSTRAINT FK_pallet_locked_operation
            FOREIGN KEY (locked_operation_id) REFERENCES dbo.business_operation(id),
        CONSTRAINT CK_pallet_current_qty CHECK (current_qty IN (0, 1)),
        CONSTRAINT CK_pallet_status CHECK
        (
            status IN ('ACTIVE', 'FROZEN', 'OUT_OF_STOCK', 'SCRAPPED', 'VOID')
        ),
        CONSTRAINT CK_pallet_status_quantity CHECK
        (
            (status IN ('ACTIVE', 'FROZEN') AND current_qty = 1)
            OR
            (status IN ('OUT_OF_STOCK', 'SCRAPPED', 'VOID') AND current_qty = 0)
        ),
        CONSTRAINT CK_pallet_out_of_stock_location CHECK
        (
            current_qty = 1
            OR (current_warehouse_code IS NULL AND current_location_code IS NULL)
        )
    );

    CREATE INDEX IX_pallet_material_batch
        ON dbo.pallet(material_code, batch_no, current_qty);

    CREATE INDEX IX_pallet_locked_operation
        ON dbo.pallet(locked_operation_id)
        WHERE locked_operation_id IS NOT NULL;

    /* 明细表先于标签和托盘主表创建，因此在两个目标表建立后补充外键和扫码查询索引。 */
    ALTER TABLE dbo.business_operation_line
        ADD CONSTRAINT FK_business_operation_line_label
            FOREIGN KEY (label_id) REFERENCES dbo.product_label(id);

    ALTER TABLE dbo.business_operation_line
        ADD CONSTRAINT FK_business_operation_line_pallet
            FOREIGN KEY (pallet_id) REFERENCES dbo.pallet(id);

    ALTER TABLE dbo.business_operation_line
        ADD CONSTRAINT FK_business_operation_line_source_pallet
            FOREIGN KEY (source_pallet_id) REFERENCES dbo.pallet(id);

    CREATE INDEX IX_business_operation_line_label
        ON dbo.business_operation_line(label_id, created_at DESC)
        WHERE label_id IS NOT NULL;

    CREATE INDEX IX_business_operation_line_pallet
        ON dbo.business_operation_line(pallet_id, created_at DESC)
        WHERE pallet_id IS NOT NULL;

    CREATE INDEX IX_business_operation_line_source_pallet
        ON dbo.business_operation_line(source_pallet_id, created_at DESC)
        WHERE source_pallet_id IS NOT NULL;

    /* 产品标签和托盘自身共用的不可变库存流水；数量、库存身份及位置均为发生时快照。 */
    CREATE TABLE dbo.inventory_flow
    (
        id                          bigint IDENTITY(1,1) NOT NULL,
        operation_id                bigint NOT NULL,
        operation_line_id           bigint NULL,
        object_type                 varchar(20) NOT NULL,
        label_id                    bigint NULL,
        pallet_id                   bigint NULL,
        object_sequence_no          int NOT NULL,
        relation_group_no           int NULL,
        flow_role                   varchar(10) NOT NULL CONSTRAINT DF_inventory_flow_flow_role DEFAULT ('NORMAL'),
        operation_type              varchar(50) NOT NULL,
        reversal_of_flow_id         bigint NULL,

        unit_code                   nvarchar(50) NOT NULL,
        qty_before                  decimal(18,6) NOT NULL,
        change_qty                  decimal(18,6) NOT NULL,
        qty_after                   decimal(18,6) NOT NULL,

        material_code               nvarchar(100) NOT NULL,
        batch_no                    nvarchar(100) NULL,
        manufacture_date            date NULL,
        expiry_date                 date NULL,
        serial_no                   nvarchar(200) NULL,
        inventory_attributes_json   nvarchar(max) NULL,
        attribute_schema_version    int NOT NULL CONSTRAINT DF_inventory_flow_attribute_schema DEFAULT (1),
        inventory_identity_hash     char(64) NULL,

        from_warehouse_code         nvarchar(100) NULL,
        from_location_code          nvarchar(100) NULL,
        to_warehouse_code           nvarchar(100) NULL,
        to_location_code            nvarchar(100) NULL,
        from_pallet_id              bigint NULL,
        to_pallet_id                bigint NULL,
        remark                      nvarchar(500) NULL,
        created_at                  datetime2(3) NOT NULL CONSTRAINT DF_inventory_flow_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),

        CONSTRAINT PK_inventory_flow PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_inventory_flow_id_label UNIQUE (id, label_id),
        CONSTRAINT UQ_inventory_flow_id_pallet UNIQUE (id, pallet_id),
        CONSTRAINT FK_inventory_flow_operation
            FOREIGN KEY (operation_id) REFERENCES dbo.business_operation(id),
        CONSTRAINT FK_inventory_flow_operation_line
            FOREIGN KEY (operation_line_id, operation_id)
            REFERENCES dbo.business_operation_line(id, operation_id),
        CONSTRAINT FK_inventory_flow_label
            FOREIGN KEY (label_id) REFERENCES dbo.product_label(id),
        CONSTRAINT FK_inventory_flow_pallet
            FOREIGN KEY (pallet_id) REFERENCES dbo.pallet(id),
        CONSTRAINT FK_inventory_flow_reversal
            FOREIGN KEY (reversal_of_flow_id) REFERENCES dbo.inventory_flow(id),
        CONSTRAINT FK_inventory_flow_from_pallet
            FOREIGN KEY (from_pallet_id) REFERENCES dbo.pallet(id),
        CONSTRAINT FK_inventory_flow_to_pallet
            FOREIGN KEY (to_pallet_id) REFERENCES dbo.pallet(id),
        CONSTRAINT CK_inventory_flow_object CHECK
        (
            (object_type = 'PRODUCT_LABEL' AND label_id IS NOT NULL AND pallet_id IS NULL)
            OR
            (object_type = 'PALLET' AND label_id IS NULL AND pallet_id IS NOT NULL)
        ),
        CONSTRAINT CK_inventory_flow_sequence CHECK (object_sequence_no > 0),
        CONSTRAINT CK_inventory_flow_relation_group CHECK (relation_group_no IS NULL OR relation_group_no > 0),
        CONSTRAINT CK_inventory_flow_role CHECK (flow_role IN ('NORMAL', 'SOURCE', 'TARGET')),
        CONSTRAINT CK_inventory_flow_balance CHECK
        (
            qty_before >= 0
            AND qty_after >= 0
            AND qty_after = qty_before + change_qty
        ),
        CONSTRAINT CK_inventory_flow_pallet_quantity CHECK
        (
            object_type <> 'PALLET'
            OR
            (
                qty_before IN (0, 1)
                AND qty_after IN (0, 1)
                AND change_qty IN (-1, 0, 1)
                AND from_pallet_id IS NULL
                AND to_pallet_id IS NULL
            )
        ),
        CONSTRAINT CK_inventory_flow_quantity_line CHECK
        (
            change_qty = 0 OR operation_line_id IS NOT NULL
        ),
        CONSTRAINT CK_inventory_flow_dates CHECK
        (
            expiry_date IS NULL OR manufacture_date IS NULL OR expiry_date >= manufacture_date
        ),
        CONSTRAINT CK_inventory_flow_attributes_json CHECK
        (
            inventory_attributes_json IS NULL OR ISJSON(inventory_attributes_json) = 1
        ),
        CONSTRAINT CK_inventory_flow_identity_hash CHECK
        (
            inventory_identity_hash IS NULL OR inventory_identity_hash NOT LIKE '%[^0-9A-Fa-f]%'
        )
    );

    ALTER TABLE dbo.product_label
        ADD CONSTRAINT FK_product_label_last_flow
        FOREIGN KEY (last_flow_id, id) REFERENCES dbo.inventory_flow(id, label_id);

    ALTER TABLE dbo.pallet
        ADD CONSTRAINT FK_pallet_last_flow
        FOREIGN KEY (last_flow_id, id) REFERENCES dbo.inventory_flow(id, pallet_id);

    CREATE UNIQUE INDEX UX_inventory_flow_label_sequence
        ON dbo.inventory_flow(label_id, object_sequence_no)
        WHERE label_id IS NOT NULL;

    CREATE UNIQUE INDEX UX_inventory_flow_pallet_sequence
        ON dbo.inventory_flow(pallet_id, object_sequence_no)
        WHERE pallet_id IS NOT NULL;

    CREATE INDEX IX_inventory_flow_operation
        ON dbo.inventory_flow(operation_id, relation_group_no, flow_role);

    CREATE INDEX IX_inventory_flow_operation_line
        ON dbo.inventory_flow(operation_line_id)
        WHERE operation_line_id IS NOT NULL;

    CREATE INDEX IX_inventory_flow_reversal
        ON dbo.inventory_flow(reversal_of_flow_id)
        WHERE reversal_of_flow_id IS NOT NULL;

    CREATE INDEX IX_inventory_flow_material_batch
        ON dbo.inventory_flow(material_code, batch_no, created_at DESC);

    CREATE INDEX IX_inventory_flow_reconciliation
        ON dbo.inventory_flow(to_warehouse_code, material_code, to_location_code, batch_no)
        INCLUDE (object_type, qty_after);

    CREATE INDEX IX_inventory_flow_inventory_identity
        ON dbo.inventory_flow(inventory_identity_hash)
        WHERE inventory_identity_hash IS NOT NULL;

    /* 产品标签装入和移出托盘的历史关系。 */
    CREATE TABLE dbo.pallet_label_relation
    (
        id                          bigint IDENTITY(1,1) NOT NULL,
        pallet_id                   bigint NOT NULL,
        label_id                    bigint NOT NULL,
        loaded_operation_id         bigint NOT NULL,
        unloaded_operation_id       bigint NULL,
        loaded_at                   datetime2(3) NOT NULL CONSTRAINT DF_pallet_label_relation_loaded_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        unloaded_at                 datetime2(3) NULL,

        CONSTRAINT PK_pallet_label_relation PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_pallet_label_relation_pallet
            FOREIGN KEY (pallet_id) REFERENCES dbo.pallet(id),
        CONSTRAINT FK_pallet_label_relation_label
            FOREIGN KEY (label_id) REFERENCES dbo.product_label(id),
        CONSTRAINT FK_pallet_label_relation_loaded_operation
            FOREIGN KEY (loaded_operation_id) REFERENCES dbo.business_operation(id),
        CONSTRAINT FK_pallet_label_relation_unloaded_operation
            FOREIGN KEY (unloaded_operation_id) REFERENCES dbo.business_operation(id),
        CONSTRAINT CK_pallet_label_relation_unload_pair CHECK
        (
            (unloaded_operation_id IS NULL AND unloaded_at IS NULL)
            OR
            (unloaded_operation_id IS NOT NULL AND unloaded_at IS NOT NULL)
        ),
        CONSTRAINT CK_pallet_label_relation_time CHECK
        (
            unloaded_at IS NULL OR unloaded_at >= loaded_at
        )
    );

    CREATE UNIQUE INDEX UX_pallet_label_relation_current_label
        ON dbo.pallet_label_relation(label_id)
        WHERE unloaded_at IS NULL;

    CREATE INDEX IX_pallet_label_relation_current_pallet
        ON dbo.pallet_label_relation(pallet_id, label_id)
        WHERE unloaded_at IS NULL;

    CREATE INDEX IX_pallet_label_relation_label_history
        ON dbo.pallet_label_relation(label_id, loaded_at DESC);

    /* 记录一次业务操作的技术过程事件；核心业务字段不得只放在 JSON 中。 */
    CREATE TABLE dbo.business_operation_event
    (
        id                          bigint IDENTITY(1,1) NOT NULL,
        operation_id                bigint NOT NULL,
        sequence_no                 int NOT NULL,
        stage                       varchar(50) NOT NULL,
        source                      varchar(30) NOT NULL,
        event_type                  varchar(50) NOT NULL,
        event_code                  varchar(100) NULL,
        result                      varchar(20) NOT NULL,
        request_id                  varchar(100) NULL,
        message                     nvarchar(2000) NULL,
        event_data_json             nvarchar(max) NULL,
        payload_hash                char(64) NULL,
        duration_ms                 bigint NULL,
        created_at                  datetime2(3) NOT NULL CONSTRAINT DF_business_operation_event_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),

        CONSTRAINT PK_business_operation_event PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_business_operation_event_sequence UNIQUE (operation_id, sequence_no),
        CONSTRAINT FK_business_operation_event_operation
            FOREIGN KEY (operation_id) REFERENCES dbo.business_operation(id),
        CONSTRAINT CK_business_operation_event_sequence CHECK (sequence_no > 0),
        CONSTRAINT CK_business_operation_event_source CHECK
        (
            source IN ('H5', 'PDA', 'BUSINESS_API', 'U8_GATEWAY', 'U8', 'PRINTER', 'DATABASE', 'SYSTEM')
        ),
        CONSTRAINT CK_business_operation_event_result CHECK
        (
            result IN ('INFO', 'SUCCESS', 'FAILED', 'UNKNOWN')
        ),
        CONSTRAINT CK_business_operation_event_json CHECK
        (
            event_data_json IS NULL OR ISJSON(event_data_json) = 1
        ),
        CONSTRAINT CK_business_operation_event_hash CHECK
        (
            payload_hash IS NULL OR payload_hash NOT LIKE '%[^0-9A-Fa-f]%'
        ),
        CONSTRAINT CK_business_operation_event_duration CHECK
        (
            duration_ms IS NULL OR duration_ms >= 0
        )
    );

    CREATE INDEX IX_business_operation_event_type
        ON dbo.business_operation_event(event_type, created_at DESC);

    /* 系统功能目录及全局启停状态；后续用户权限表引用 function_code。 */
    CREATE TABLE dbo.system_function
    (
        id                          bigint IDENTITY(1,1) NOT NULL,
        function_code               varchar(100) NOT NULL,
        function_name               nvarchar(100) NOT NULL,
        function_kind               varchar(20) NOT NULL,
        parent_function_code        varchar(100) NULL,
        group_code                  varchar(50) NOT NULL,
        group_name                  nvarchar(100) NOT NULL,
        group_sort                  int NOT NULL,
        function_sort               int NOT NULL,
        icon_code                   varchar(50) NULL,
        is_enabled                  bit NOT NULL CONSTRAINT DF_system_function_is_enabled DEFAULT (1),
        remark                      nvarchar(500) NULL,
        updated_by                  nvarchar(100) NOT NULL,
        created_at                  datetime2(3) NOT NULL CONSTRAINT DF_system_function_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at                  datetime2(3) NOT NULL CONSTRAINT DF_system_function_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version                 rowversion NOT NULL,

        CONSTRAINT PK_system_function PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_system_function_code UNIQUE (function_code),
        CONSTRAINT FK_system_function_parent
            FOREIGN KEY (parent_function_code) REFERENCES dbo.system_function(function_code),
        CONSTRAINT CK_system_function_kind
            CHECK (function_kind IN ('DOCUMENT', 'APPROVAL', 'QUERY')),
        CONSTRAINT CK_system_function_sort
            CHECK (group_sort >= 0 AND function_sort >= 0),
        CONSTRAINT CK_system_function_parent
            CHECK (parent_function_code IS NULL OR parent_function_code <> function_code)
    );

    CREATE INDEX IX_system_function_display
        ON dbo.system_function(is_enabled, group_sort, function_sort);

    INSERT INTO dbo.system_function
    (
        function_code, function_name, function_kind, parent_function_code,
        group_code, group_name, group_sort, function_sort, icon_code,
        is_enabled, updated_by
    )
    VALUES
    ('purchase-arrival', N'采购到货', 'DOCUMENT', NULL, 'purchase', N'采购管理', 10, 10, 'receive', 1, N'SYSTEM'),
    ('outsourcing-arrival', N'委外到货', 'DOCUMENT', NULL, 'purchase', N'采购管理', 10, 20, 'truck', 1, N'SYSTEM'),
    ('purchase-inbound', N'采购入库', 'DOCUMENT', NULL, 'purchase', N'采购管理', 10, 30, 'warehouse', 1, N'SYSTEM'),
    ('delivery-note', N'发货单', 'DOCUMENT', NULL, 'sales', N'销售管理', 20, 10, 'document', 1, N'SYSTEM'),
    ('sales-outbound', N'销售出库单', 'DOCUMENT', NULL, 'sales', N'销售管理', 20, 20, 'dispatch', 1, N'SYSTEM'),
    ('material-outbound', N'材料出库单', 'DOCUMENT', NULL, 'inventory', N'库存管理', 30, 10, 'material', 1, N'SYSTEM'),
    ('transfer-order', N'调拨单', 'DOCUMENT', NULL, 'inventory', N'库存管理', 30, 20, 'transfer', 1, N'SYSTEM'),
    ('location-adjustment', N'货位调整', 'DOCUMENT', NULL, 'inventory', N'库存管理', 30, 30, 'location', 1, N'SYSTEM'),
    ('inventory-count', N'盘点单', 'DOCUMENT', NULL, 'inventory', N'库存管理', 30, 40, 'inventory', 1, N'SYSTEM'),
    ('label-query', N'标签查询', 'QUERY', NULL, 'query', N'信息查询', 50, 10, 'query', 1, N'SYSTEM');

    INSERT INTO dbo.system_function
    (
        function_code, function_name, function_kind, parent_function_code,
        group_code, group_name, group_sort, function_sort, icon_code,
        is_enabled, updated_by
    )
    VALUES
    ('approval-purchase-arrival', N'采购到货审核', 'APPROVAL', 'purchase-arrival', 'approval', N'审核管理', 40, 10, 'approve', 1, N'SYSTEM'),
    ('approval-outsourcing-arrival', N'委外到货审核', 'APPROVAL', 'outsourcing-arrival', 'approval', N'审核管理', 40, 20, 'approve', 1, N'SYSTEM'),
    ('approval-purchase-inbound', N'采购入库审核', 'APPROVAL', 'purchase-inbound', 'approval', N'审核管理', 40, 30, 'approve', 1, N'SYSTEM'),
    ('approval-delivery-note', N'发货单审核', 'APPROVAL', 'delivery-note', 'approval', N'审核管理', 40, 40, 'approve', 1, N'SYSTEM'),
    ('approval-sales-outbound', N'销售出库单审核', 'APPROVAL', 'sales-outbound', 'approval', N'审核管理', 40, 50, 'approve', 1, N'SYSTEM'),
    ('approval-material-outbound', N'材料出库单审核', 'APPROVAL', 'material-outbound', 'approval', N'审核管理', 40, 60, 'approve', 1, N'SYSTEM'),
    ('approval-transfer-order', N'调拨单审核', 'APPROVAL', 'transfer-order', 'approval', N'审核管理', 40, 70, 'approve', 1, N'SYSTEM'),
    ('approval-location-adjustment', N'货位调整审核', 'APPROVAL', 'location-adjustment', 'approval', N'审核管理', 40, 80, 'approve', 1, N'SYSTEM'),
    ('approval-inventory-count', N'盘点单审核', 'APPROVAL', 'inventory-count', 'approval', N'审核管理', 40, 90, 'approve', 1, N'SYSTEM');

    /* 保存非敏感业务开关；全局值和单据级覆盖值共用一张表。 */
    CREATE TABLE dbo.system_setting
    (
        id                          bigint IDENTITY(1,1) NOT NULL,
        setting_scope               varchar(20) NOT NULL,
        document_type               varchar(50) NULL,
        setting_key                 varchar(100) NOT NULL,
        setting_name                nvarchar(100) NOT NULL,
        value_json                  nvarchar(max) NOT NULL,
        is_enabled                  bit NOT NULL CONSTRAINT DF_system_setting_is_enabled DEFAULT (1),
        setting_version             int NOT NULL CONSTRAINT DF_system_setting_version DEFAULT (1),
        remark                      nvarchar(500) NULL,
        updated_by                  nvarchar(100) NOT NULL,
        created_at                  datetime2(3) NOT NULL CONSTRAINT DF_system_setting_created_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        updated_at                  datetime2(3) NOT NULL CONSTRAINT DF_system_setting_updated_at DEFAULT (DATEADD(HOUR, 8, SYSUTCDATETIME())),
        row_version                 rowversion NOT NULL,

        CONSTRAINT PK_system_setting PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_system_setting_scope_key UNIQUE (setting_scope, document_type, setting_key),
        CONSTRAINT CK_system_setting_scope CHECK
        (
            (setting_scope = 'GLOBAL' AND document_type IS NULL)
            OR
            (setting_scope = 'DOCUMENT' AND document_type IS NOT NULL)
        ),
        CONSTRAINT CK_system_setting_value_json CHECK (ISJSON(value_json) = 1),
        CONSTRAINT CK_system_setting_version CHECK (setting_version > 0)
    );

    /* 表和字段说明写入 SQL Server 扩展属性，可在 SSMS 中直接查看。 */
    DECLARE @Descriptions TABLE
    (
        table_name  sysname NOT NULL,
        column_name sysname NULL,
        description nvarchar(4000) NOT NULL
    );

    INSERT INTO @Descriptions(table_name, column_name, description)
    VALUES
    (N'business_operation', NULL, N'业务操作主表：一笔提交、审核、打印、标签或托盘作业一条记录，保存操作人、设备、单据、幂等、归档和最终结果。'),
    (N'business_operation', N'id', N'业务操作内部主键。'),
    (N'business_operation', N'operation_no', N'业务操作编号，系统生成且全局唯一。'),
    (N'business_operation', N'submission_group_id', N'同一次 H5 提交按仓库等规则拆成多个业务操作时共用的组编号。'),
    (N'business_operation', N'operation_type', N'操作类型，如标签打印、拆分、合并、数量调整、装托、U8 新增或审核。'),
    (N'business_operation', N'status', N'业务操作状态；NEEDS_RECONCILIATION 表示 U8、业务库或归档结果不一致，必须人工或任务核对。'),
    (N'business_operation', N'source_document_type', N'来源单据类型快照，可为空。'),
    (N'business_operation', N'source_document_id', N'来源单据内部标识快照，可为空。'),
    (N'business_operation', N'source_document_no', N'来源单据编号快照，可为空。'),
    (N'business_operation', N'erp_environment', N'U8 环境快照：development、test 或 production。'),
    (N'business_operation', N'erp_account_id', N'U8 账套号快照，如 008、888。'),
    (N'business_operation', N'erp_year', N'U8 业务年度快照。'),
    (N'business_operation', N'erp_database_name_snapshot', N'U8 账套数据库名称快照，不包含服务器密码或连接串。'),
    (N'business_operation', N'erp_document_type', N'U8 单据类型编码。'),
    (N'business_operation', N'erp_document_id', N'U8 单据内部 ID。'),
    (N'business_operation', N'erp_document_no', N'U8 单据编号。'),
    (N'business_operation', N'erp_operation_type', N'U8 操作类型，如 ADD、EDIT、AUDIT、UNAUDIT、DELETE、RED。'),
    (N'business_operation', N'erp_version_before', N'U8 操作前版本值，如 ufts 的字符串表示。'),
    (N'business_operation', N'erp_version_after', N'U8 回查确认后的版本值。'),
    (N'business_operation', N'erp_status', N'U8 处理状态。UNKNOWN 表示调用结果暂时无法确认。'),
    (N'business_operation', N'erp_confirmed_at', N'U8 实际结果回查确认时间，北京时间。'),
    (N'business_operation', N'operator_id', N'操作人唯一标识。'),
    (N'business_operation', N'operator_name_snapshot', N'操作发生时的操作人名称快照。'),
    (N'business_operation', N'device_id', N'设备系统标识，可为空。'),
    (N'business_operation', N'device_code', N'设备业务编码或资产编号，可为空。'),
    (N'business_operation', N'device_model', N'设备型号快照，如 CT58C。'),
    (N'business_operation', N'client_request_id', N'客户端幂等编号，相同编号不得重复产生业务结果。'),
    (N'business_operation', N'request_payload_hash', N'规范化业务请求的 SHA-256，用于识别同一幂等编号下内容是否变化。'),
    (N'business_operation', N'approval_status', N'内部审核状态，与 U8 审核状态分开保存。'),
    (N'business_operation', N'approver_id', N'内部审核人唯一标识。'),
    (N'business_operation', N'approver_name_snapshot', N'内部审核发生时的审核人名称快照。'),
    (N'business_operation', N'approved_at', N'内部审核完成时间，北京时间。'),
    (N'business_operation', N'reason', N'操作原因或业务备注。是否必填由配置和操作类型决定。'),
    (N'business_operation', N'reversal_of_operation_id', N'本操作所撤销、红冲或反向处理的原业务操作 ID。'),
    (N'business_operation', N'lock_acquired_at', N'本操作成功取得标签或托盘业务锁的时间，北京时间。'),
    (N'business_operation', N'heartbeat_at', N'持锁操作最近一次正常处理心跳时间，北京时间。'),
    (N'business_operation', N'lock_expires_at', N'锁进入超时核对的时间，北京时间；到期不能自动抢锁或覆盖原操作。'),
    (N'business_operation', N'archive_relative_path', N'独立 TXT 操作凭证的相对路径，不保存服务器根路径。'),
    (N'business_operation', N'archive_status', N'独立操作凭证状态。'),
    (N'business_operation', N'archive_sha256', N'完成态操作凭证文件的 SHA-256。'),
    (N'business_operation', N'error_code', N'业务操作最终错误代码。'),
    (N'business_operation', N'error_message', N'业务操作最终错误摘要，不写密码、令牌或连接串。'),
    (N'business_operation', N'created_at', N'记录创建时间，北京时间。'),
    (N'business_operation', N'updated_at', N'记录最后更新时间，北京时间，由应用在更新时维护。'),
    (N'business_operation', N'completed_at', N'操作完成时间，北京时间。'),
    (N'business_operation', N'row_version', N'SQL Server 行版本，用于并发更新判断，不代表业务时间。'),

    (N'pallet_material', NULL, N'托盘料号配置表：维护哪些 U8 存货编码专用于托盘标签，存货名称、规格和单位仍从 U8 查询。'),
    (N'pallet_material', N'id', N'托盘料号配置内部主键。'),
    (N'pallet_material', N'material_code', N'U8 存货编码；启用后该料号只能打印为托盘，不能打印为普通产品标签。'),
    (N'pallet_material', N'is_enabled', N'是否允许继续使用该料号打印新托盘；停用不影响历史托盘。'),
    (N'pallet_material', N'remark', N'托盘料号维护备注。'),
    (N'pallet_material', N'updated_by', N'最后维护人唯一标识。'),
    (N'pallet_material', N'created_at', N'配置创建时间，北京时间。'),
    (N'pallet_material', N'updated_at', N'配置最后更新时间，北京时间。'),
    (N'pallet_material', N'row_version', N'SQL Server 行版本，用于防止并发覆盖。'),

    (N'business_operation_line', NULL, N'业务操作明细表：保存用户提交的标签或托盘扫码来源、本次申请处理数量、库存属性、仓库货位以及 U8 表体和货位行对应关系。'),
    (N'business_operation_line', N'id', N'业务操作明细内部主键。'),
    (N'business_operation_line', N'operation_id', N'所属业务操作 ID。'),
    (N'business_operation_line', N'line_no', N'业务操作内从 1 开始的明细行号。'),
    (N'business_operation_line', N'line_status', N'明细处理状态。'),
    (N'business_operation_line', N'label_id', N'本明细绑定的产品标签 ID；纯 U8 单据明细可为空。'),
    (N'business_operation_line', N'pallet_id', N'本明细绑定的托盘 ID；与 label_id 互斥。'),
    (N'business_operation_line', N'selection_source', N'库存对象选择来源：LABEL_SCAN 扫描产品标签，PALLET_SCAN 扫描托盘。'),
    (N'business_operation_line', N'source_pallet_id', N'选择产品标签时它所属的托盘 ID；通过托盘扫码选择产品时必填。'),
    (N'business_operation_line', N'inventory_qty_snapshot', N'扫码或提交时用户看到的产品标签或托盘库存数量快照，不代表最终执行前数量。'),
    (N'business_operation_line', N'source_line_id', N'来源单据表体行内部 ID。'),
    (N'business_operation_line', N'source_line_no', N'来源单据表体行号。'),
    (N'business_operation_line', N'erp_line_id', N'U8 目标单据表体行内部 ID，通常对应 autoid。'),
    (N'business_operation_line', N'erp_line_no', N'U8 目标单据表体行号。'),
    (N'business_operation_line', N'erp_position_id', N'U8 货位明细内部 ID。'),
    (N'business_operation_line', N'material_code', N'存货或物料编码快照。'),
    (N'business_operation_line', N'batch_no', N'批号快照，可为空。'),
    (N'business_operation_line', N'manufacture_date', N'生产日期快照，可为空。'),
    (N'business_operation_line', N'expiry_date', N'失效日期快照，可为空。'),
    (N'business_operation_line', N'serial_no', N'序列号快照，可为空。'),
    (N'business_operation_line', N'inventory_attributes_json', N'自由项及不固定批次属性的 JSON 快照，不保存密码或技术连接信息。'),
    (N'business_operation_line', N'attribute_schema_version', N'库存属性 JSON 结构版本。'),
    (N'business_operation_line', N'inventory_identity_hash', N'规范化库存身份字段的 SHA-256，用于合并、混托及对账比较。'),
    (N'business_operation_line', N'unit_code', N'用户或单据使用的计量单位编码；当前阶段不支持单位换算。'),
    (N'business_operation_line', N'requested_qty', N'本次申请处理的非负数量；托盘明细固定为 1，不等同于库存数量快照。'),
    (N'business_operation_line', N'package_spec_snapshot', N'本次打印使用的包装规格文字快照；非打印明细为空。'),
    (N'business_operation_line', N'package_qty', N'标准包装下每张标签对应的产品数量；尾数标签可以小于此数量。'),
    (N'business_operation_line', N'requested_label_count', N'本次计划打印的标签张数。'),
    (N'business_operation_line', N'printed_label_count', N'打印工具明确确认成功的实际张数；不代表新增库存数量。'),
    (N'business_operation_line', N'from_warehouse_code', N'来源仓库编码。'),
    (N'business_operation_line', N'from_location_code', N'来源货位编码；来源仓库启用 U8 货位管理时必填。'),
    (N'business_operation_line', N'to_warehouse_code', N'目标仓库编码或业务完成后的仓库编码。'),
    (N'business_operation_line', N'to_location_code', N'目标货位编码或业务完成后的货位编码；目标仓库启用 U8 货位管理时必填。'),
    (N'business_operation_line', N'remark', N'用户提交的明细备注。'),
    (N'business_operation_line', N'error_code', N'该明细处理失败时的错误代码；例如标签与 U8 来源位置不一致使用 LABEL_LOCATION_MISMATCH。'),
    (N'business_operation_line', N'error_message', N'该明细处理失败时的错误摘要；位置冲突时保存标签当前位置与 U8 来源位置。'),
    (N'business_operation_line', N'created_at', N'明细创建时间，北京时间。'),
    (N'business_operation_line', N'updated_at', N'明细最后更新时间，北京时间。'),
    (N'business_operation_line', N'row_version', N'SQL Server 行版本，用于并发更新判断。'),

    (N'product_label', NULL, N'产品标签主表：一个标签码永久一条记录，只保存身份、当前数量、状态及并发控制信息。'),
    (N'product_label', N'id', N'产品标签内部主键。'),
    (N'product_label', N'label_code', N'产品标签码，全局唯一，作废或用完后也不得重复使用。'),
    (N'product_label', N'current_qty', N'当前剩余数量，是标签余额的唯一当前值，不允许小于零。'),
    (N'product_label', N'status', N'标签状态；处理中锁定只使用 locked_operation_id，不再使用 LOCKED 状态。所有状态可重新输出原标签，但补打不得改变该状态。'),
    (N'product_label', N'last_flow_id', N'该标签最新已生效流水 ID，也是查询当前库存身份和位置的精确指针。'),
    (N'product_label', N'locked_operation_id', N'当前占用该标签的未完成业务操作 ID；为空表示未锁定。'),
    (N'product_label', N'created_at', N'标签记录创建时间，北京时间。'),
    (N'product_label', N'updated_at', N'标签记录最后更新时间，北京时间。'),
    (N'product_label', N'row_version', N'SQL Server 行版本，用于防止多设备并发覆盖。'),

    (N'pallet', NULL, N'托盘主表：每个成功打印的托盘标签一条记录，保存托盘自身库存状态，不保存所装产品总数量。'),
    (N'pallet', N'id', N'托盘内部主键。'),
    (N'pallet', N'pallet_code', N'打印生成的唯一托盘标签码；不建立独立号码池。出库后不得重新启用，实体托盘返回企业时生成新码和新记录。'),
    (N'pallet', N'material_code', N'托盘对应的 U8 存货编码，必须存在于托盘料号配置。'),
    (N'pallet', N'batch_no', N'托盘生产批号，可为空。'),
    (N'pallet', N'manufacture_date', N'托盘生产日期，可为空。'),
    (N'pallet', N'unit_code', N'托盘计量单位编码，当前每个托盘标签数量固定为 1。'),
    (N'pallet', N'current_qty', N'托盘自身当前库存数量，只能为 0 或 1，不是托盘内产品数量。'),
    (N'pallet', N'status', N'托盘库存状态：ACTIVE、FROZEN、OUT_OF_STOCK、SCRAPPED 或 VOID；OUT_OF_STOCK 不恢复为 ACTIVE。'),
    (N'pallet', N'last_flow_id', N'该托盘自身最新已生效库存流水 ID。'),
    (N'pallet', N'current_warehouse_code', N'托盘自身当前仓库编码；出库、报废或作废后为空。'),
    (N'pallet', N'current_location_code', N'托盘自身当前货位编码；出库、报废或作废后为空。'),
    (N'pallet', N'last_operation_id', N'最后改变托盘状态或位置的业务操作 ID。'),
    (N'pallet', N'locked_operation_id', N'当前占用该托盘的未完成业务操作 ID。'),
    (N'pallet', N'created_at', N'托盘记录创建时间，北京时间。'),
    (N'pallet', N'updated_at', N'托盘记录最后更新时间，北京时间。'),
    (N'pallet', N'row_version', N'SQL Server 行版本，用于防止多设备并发覆盖。'),

    (N'inventory_flow', NULL, N'库存流水表：产品标签和托盘自身共用，只追加不修改不删除，保存数量、打印和位置操作的完整前后状态快照。每成功物理打印一张写入一条 PRINT 或 REPRINT 流水。'),
    (N'inventory_flow', N'id', N'库存流水内部主键。'),
    (N'inventory_flow', N'operation_id', N'产生本流水的业务操作 ID。'),
    (N'inventory_flow', N'operation_line_id', N'对应的业务操作明细 ID；必须与本流水属于同一操作。'),
    (N'inventory_flow', N'object_type', N'库存对象类型：PRODUCT_LABEL 产品标签或 PALLET 托盘。'),
    (N'inventory_flow', N'label_id', N'被影响的产品标签 ID；托盘流水为空。'),
    (N'inventory_flow', N'pallet_id', N'被影响的托盘 ID；产品标签流水为空。'),
    (N'inventory_flow', N'object_sequence_no', N'当前库存对象内部连续流水序号，用于可靠排序和缺号检查。'),
    (N'inventory_flow', N'relation_group_no', N'同一操作内拆分或合并的关系组号；批量包含多组时用于区分。'),
    (N'inventory_flow', N'flow_role', N'流水角色：NORMAL、SOURCE 或 TARGET。'),
    (N'inventory_flow', N'operation_type', N'库存业务类型，如 PRINT、CONSUME、SPLIT、MERGE、PALLET_IN、PALLET_OUT。'),
    (N'inventory_flow', N'reversal_of_flow_id', N'本流水直接反向处理的原流水 ID；应用必须校验为同一库存对象。'),
    (N'inventory_flow', N'unit_code', N'库存对象数量使用的计量单位编码；当前阶段不支持单位换算。'),
    (N'inventory_flow', N'qty_before', N'变更前库存余额。'),
    (N'inventory_flow', N'change_qty', N'有符号变化量；减少为负，位置或装卸托操作为零。'),
    (N'inventory_flow', N'qty_after', N'变更后库存余额，必须等于对应主表生效后的当前余额。'),
    (N'inventory_flow', N'material_code', N'流水发生时的料号快照。'),
    (N'inventory_flow', N'batch_no', N'流水发生时的批号快照。'),
    (N'inventory_flow', N'manufacture_date', N'流水发生时的生产日期快照。'),
    (N'inventory_flow', N'expiry_date', N'流水发生时的失效日期快照。'),
    (N'inventory_flow', N'serial_no', N'流水发生时的序列号快照。'),
    (N'inventory_flow', N'inventory_attributes_json', N'流水发生时的自由项及不固定属性 JSON 快照；当前不参与库存对账分组。'),
    (N'inventory_flow', N'attribute_schema_version', N'库存属性 JSON 结构版本。'),
    (N'inventory_flow', N'inventory_identity_hash', N'仓库、料号、货位和批号规范化后的 SHA-256，用于库存对账分组。'),
    (N'inventory_flow', N'from_warehouse_code', N'变化前仓库编码。'),
    (N'inventory_flow', N'from_location_code', N'变化前货位编码。'),
    (N'inventory_flow', N'to_warehouse_code', N'变化后仓库编码；查询当前库存时通过主表 last_flow_id 读取该字段。不改变位置的流水仍复制当前值。'),
    (N'inventory_flow', N'to_location_code', N'变化后货位编码；查询当前库存时通过主表 last_flow_id 读取该字段。不改变位置的流水仍复制当前值。'),
    (N'inventory_flow', N'from_pallet_id', N'产品标签变化前所属托盘 ID；托盘自身流水为空。'),
    (N'inventory_flow', N'to_pallet_id', N'产品标签变化后所属托盘 ID；托盘自身流水为空。'),
    (N'inventory_flow', N'remark', N'库存流水业务备注。'),
    (N'inventory_flow', N'created_at', N'流水生效并写入时间，北京时间。'),

    (N'pallet_label_relation', NULL, N'托盘标签关系历史表：记录标签每次装托和卸托；未卸托记录表示当前关系。'),
    (N'pallet_label_relation', N'id', N'托盘标签关系内部主键。'),
    (N'pallet_label_relation', N'pallet_id', N'托盘 ID。'),
    (N'pallet_label_relation', N'label_id', N'产品标签 ID。'),
    (N'pallet_label_relation', N'loaded_operation_id', N'建立装托关系的业务操作 ID。'),
    (N'pallet_label_relation', N'unloaded_operation_id', N'关闭装托关系的业务操作 ID。'),
    (N'pallet_label_relation', N'loaded_at', N'装托时间，北京时间。'),
    (N'pallet_label_relation', N'unloaded_at', N'卸托时间，北京时间；为空表示当前仍在该托盘中。'),

    (N'business_operation_event', NULL, N'业务操作事件表：按顺序保存 H5、PDA、业务 API、U8、打印机和数据库的过程事件及非固定诊断数据。'),
    (N'business_operation_event', N'id', N'业务操作事件内部主键。'),
    (N'business_operation_event', N'operation_id', N'所属业务操作 ID。'),
    (N'business_operation_event', N'sequence_no', N'业务操作内从 1 开始的事件序号。'),
    (N'business_operation_event', N'stage', N'处理阶段，如 REQUEST_RECEIVED、U8_CALLED、U8_VERIFIED、DATABASE_COMMITTED。'),
    (N'business_operation_event', N'source', N'事件来源组件。'),
    (N'business_operation_event', N'event_type', N'事件类型。'),
    (N'business_operation_event', N'event_code', N'事件或错误代码。'),
    (N'business_operation_event', N'result', N'事件结果级别。'),
    (N'business_operation_event', N'request_id', N'该阶段外部或内部请求编号。'),
    (N'business_operation_event', N'message', N'简短事件摘要。'),
    (N'business_operation_event', N'event_data_json', N'结构不固定的脱敏事件数据 JSON；不得保存密码、令牌和连接串。'),
    (N'business_operation_event', N'payload_hash', N'该事件有效载荷的 SHA-256。'),
    (N'business_operation_event', N'duration_ms', N'该阶段耗时，毫秒。'),
    (N'business_operation_event', N'created_at', N'事件写入时间，北京时间。'),

    (N'system_function', NULL, N'系统功能目录表：保存稳定功能编码、显示分组、顺序、图标、关联业务功能及全局启停状态。'),
    (N'system_function', N'id', N'系统功能内部主键。'),
    (N'system_function', N'function_code', N'稳定功能编码，同时作为前后端功能识别和后续权限关联键。'),
    (N'system_function', N'function_name', N'功能显示名称。'),
    (N'system_function', N'function_kind', N'功能类型：DOCUMENT 做单、APPROVAL 审核、QUERY 查询。'),
    (N'system_function', N'parent_function_code', N'审核功能关联的原业务功能编码；其他功能为空。'),
    (N'system_function', N'group_code', N'显示分组稳定编码。'),
    (N'system_function', N'group_name', N'显示分组名称。'),
    (N'system_function', N'group_sort', N'分组显示顺序。'),
    (N'system_function', N'function_sort', N'功能在分组内的显示顺序。'),
    (N'system_function', N'icon_code', N'H5 已知图标编码，不保存任意 HTML 或脚本。'),
    (N'system_function', N'is_enabled', N'全局是否启用；关闭后所有用户均不可显示或访问。'),
    (N'system_function', N'remark', N'功能维护备注。'),
    (N'system_function', N'updated_by', N'最后修改人唯一标识。'),
    (N'system_function', N'created_at', N'功能记录创建时间，北京时间。'),
    (N'system_function', N'updated_at', N'功能记录最后更新时间，北京时间。'),
    (N'system_function', N'row_version', N'SQL Server 行版本，用于全局功能开关并发更新。'),

    (N'system_setting', NULL, N'系统业务配置表：保存非敏感全局设置及单据级覆盖设置，不保存数据库或 U8 密码。'),
    (N'system_setting', N'id', N'系统配置内部主键。'),
    (N'system_setting', N'setting_scope', N'配置作用域：GLOBAL 全局或 DOCUMENT 单据级。'),
    (N'system_setting', N'document_type', N'单据级配置对应的单据类型；全局配置必须为空。'),
    (N'system_setting', N'setting_key', N'稳定的配置键，由程序识别。'),
    (N'system_setting', N'setting_name', N'配置项中文名称。'),
    (N'system_setting', N'value_json', N'配置值 JSON，可保存布尔值、数字、字符串或结构化值。'),
    (N'system_setting', N'is_enabled', N'配置记录是否启用。'),
    (N'system_setting', N'setting_version', N'配置业务版本，每次修改递增。'),
    (N'system_setting', N'remark', N'配置维护备注。'),
    (N'system_setting', N'updated_by', N'最后修改人唯一标识。'),
    (N'system_setting', N'created_at', N'配置创建时间，北京时间。'),
    (N'system_setting', N'updated_at', N'配置最后更新时间，北京时间。'),
    (N'system_setting', N'row_version', N'SQL Server 行版本，用于配置并发更新判断。');

    DECLARE
        @TableName sysname,
        @ColumnName sysname,
        @Description nvarchar(4000);

    DECLARE description_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT table_name, column_name, description
        FROM @Descriptions
        ORDER BY table_name, CASE WHEN column_name IS NULL THEN 0 ELSE 1 END, column_name;

    OPEN description_cursor;
    FETCH NEXT FROM description_cursor INTO @TableName, @ColumnName, @Description;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF @ColumnName IS NULL
        BEGIN
            EXEC sys.sp_addextendedproperty
                @name = N'MS_Description',
                @value = @Description,
                @level0type = N'SCHEMA',
                @level0name = N'dbo',
                @level1type = N'TABLE',
                @level1name = @TableName;
        END;
        ELSE
        BEGIN
            EXEC sys.sp_addextendedproperty
                @name = N'MS_Description',
                @value = @Description,
                @level0type = N'SCHEMA',
                @level0name = N'dbo',
                @level1type = N'TABLE',
                @level1name = @TableName,
                @level2type = N'COLUMN',
                @level2name = @ColumnName;
        END;

        FETCH NEXT FROM description_cursor INTO @TableName, @ColumnName, @Description;
    END;

    CLOSE description_cursor;
    DEALLOCATE description_cursor;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
