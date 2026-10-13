# 标签管理系统数据库设计说明

状态：结构、业务设置、U8 基础资料缓存、用户权限、漫游数据、本地密码凭据、用户管理审计、多端草稿同步及标签 Code 规则已创建；三套环境最新迁移编号均为 `011`
数据库：Microsoft SQL Server 2019 Standard（15.0.2170.1）
实例：`172.16.1.159` 默认实例
更新时间：2026-10-12

## 1. 文件与执行顺序

| 顺序 | 文件 | 用途 |
| --- | --- | --- |
| 1 | `000_create_database.sql` | 创建独立业务数据库并设置基础数据库选项 |
| 2 | `001_label_business_schema_draft.sql` | 创建业务表、约束、索引及 SQL Server 扩展说明 |
| 3 | `010_install_schema_all_environments.sql` | 用 sqlcmd 将 001 安装到三个环境 |
| 4 | `011_record_initial_migration_checksum.sql` | 保存初始迁移脚本 SHA-256 |
| 5 | `002_seed_settings_and_source_reservation_index.sql` | 写入默认设置、修正标量 JSON 校验并增加源单占用索引 |
| 6 | `012_install_migration_002_all_environments.sql` | 用 sqlcmd 将 002 安装到三个环境 |
| 7 | `013_record_migration_002_checksum.sql` | 保存 002 迁移脚本 SHA-256 |
| 8 | `003_cache_u8_rd_style.sql` | 建立 U8 出入库类别缓存和基础资料同步状态 |
| 9 | `014_install_migration_003_all_environments.sql` | 用 sqlcmd 将 003 安装到三个环境 |
| 10 | `015_record_migration_003_checksum.sql` | 保存 003 迁移脚本 SHA-256 |
| 11 | `004_fix_reference_data_sync_time_constraint.sql` | 修正基础资料重复同步的时间约束 |
| 12 | `016_install_migration_004_all_environments.sql` | 用 sqlcmd 将 004 安装到三个环境 |
| 13 | `017_record_migration_004_checksum.sql` | 保存 004 迁移脚本 SHA-256 |
| 14 | `005_cache_u8_warehouse_position_department.sql` | 建立 U8 仓库、仓位和部门缓存 |
| 15 | `018_install_migration_005_all_environments.sql` | 用 sqlcmd 将 005 安装到三个环境 |
| 16 | `019_record_migration_005_checksum.sql` | 保存 005 迁移脚本 SHA-256 |
| 17 | `006_create_users_permissions_preferences.sql` | 建立应用用户、功能权限和用户漫游偏好 |
| 18 | `022_install_migration_006_all_environments.sql` | 用 sqlcmd 将 006 安装到三个环境 |
| 19 | `023_record_migration_006_checksum.sql` | 保存 006 迁移脚本 SHA-256 |
| 20 | `007_add_local_user_credentials.sql` | 在应用用户表增加本地密码哈希和登录锁定字段 |
| 21 | `024_install_migration_007_all_environments.sql` | 用 sqlcmd 将 007 安装到三个环境 |
| 22 | `025_record_migration_007_checksum.sql` | 保存 007 迁移脚本 SHA-256 |
| 23 | `008_create_user_admin_audit.sql` | 建立用户管理审计记录 |
| 24 | `026_install_migration_008_all_environments.sql` | 用 sqlcmd 将 008 安装到三个环境 |
| 25 | `027_record_migration_008_checksum.sql` | 保存 008 迁移脚本 SHA-256 |
| 26 | `009_create_synced_document_drafts.sql` | 建立多端草稿、设备状态、草稿事件及默认保留策略 |
| 27 | `028_install_migration_009_all_environments.sql` | 用 sqlcmd 将 009 安装到三个环境 |
| 28 | `029_record_migration_009_checksum.sql` | 保存 009 迁移脚本 SHA-256 |
| 29 | `031_create_label_code_rules.sql` | 建立本地标签 Code 规则、不可变版本、流水和统一登记 |
| 30 | `032_install_migration_010_all_environments.sql` | 用 sqlcmd 将 010 安装到三个环境 |
| 31 | `033_record_migration_010_checksum.sql` | 保存 010 迁移脚本 SHA-256 |
| 32 | `034_allow_shared_inventory_label_rule.sql` | 允许产品标签与托盘共用一条 Code 规则 |
| 33 | `035_install_migration_011_all_environments.sql` | 用 sqlcmd 将 011 安装到三个环境 |
| 34 | `036_record_migration_011_checksum.sql` | 保存 011 迁移脚本 SHA-256 |
| 35 | `020_database_operations.sql` | 配置 Query Store、备份、校验、维护和容量检查 |
| 36 | `021_add_long_transaction_monitor.sql` | 已部署环境补充长事务监测，新环境安全跳过 |
| 37 | `030_restore_drill.sql` | 从最近生产完整备份恢复临时库、校验并自动清理 |

脚本使用 UTF-8 保存。通过旧版 `sqlcmd` 执行时必须增加 `-f 65001`，并使用 `-b` 在 SQL 错误时返回失败；连接密码通过安全部署配置或 `SQLCMDPASSWORD` 提供，不写入命令脚本。

已经建立的环境：

| 环境 | 数据库名 | 恢复模式 | 数据文件 | 日志文件 | 用途 |
| --- | --- | --- | --- | --- | --- |
| development | `LabelSystem_Dev` | SIMPLE | 初始 512MB；增长 128MB；上限 20GB | 初始 256MB；增长 64MB；上限 4GB | 本地开发 |
| test | `LabelSystem_Test` | SIMPLE | 初始 1GB；增长 256MB；上限 40GB | 初始 512MB；增长 128MB；上限 8GB | PDA、API 和 U8 测试账套联调 |
| production | `LabelSystem_Prod` | FULL | 初始 4GB；增长 512MB；上限 180GB | 初始 1GB；增长 256MB；上限 40GB | 正式业务 |

数据文件位于 `D:\LabelSystem\Data`，日志文件位于 `D:\LabelSystem\Log`。排序规则固定为 `Chinese_PRC_CI_AS`，兼容级别为 150；与当前 U8 账套保持一致。`AUTO_CLOSE`、`AUTO_SHRINK`、`TRUSTWORTHY` 和跨库所有权链接关闭，页校验使用 `CHECKSUM`，并启用快照隔离和读已提交快照。

三个环境使用不同数据库。按当前项目决定，数据库 owner 和应用连接账号使用 `sa`；密码不得进入业务表、代码仓库、事件 JSON 或前端包。后续如调整安全策略，再通过新增迁移和部署配置切换到最小权限账号。

## 2. 数据库边界

本数据库保存：

- 产品标签、托盘自身当前余额和不可变库存流水。
- 托盘料号配置、托盘自身库存和装卸历史。
- 用户提交的业务操作和明细。
- U8 单据、表体行和货位行的关联标识。
- 打印、补打以及失败重试过程；成功结果进入库存流水，过程结果进入操作事件。
- 操作过程事件和异常核对状态。
- 非敏感的全局及单据级业务配置。
- 系统功能目录、显示分组、顺序及全局启停状态。
- U8 出入库类别、仓库、仓位和部门的本地只读缓存及同步状态。
- 应用用户、ERP 制单人映射、直接功能权限和用户漫游偏好。

本数据库不保存：

- U8 全部业务表或全部 XML 字段副本。
- SQL Server、U8、Windows 或打印服务密码。
- 未脱敏的连接串、令牌和敏感技术信息。
- 可以从标签关系实时汇总出的托盘总数量。

U8 完整请求、返回和回查过程另存独立 TXT 操作凭证；数据库通过操作编号、请求编号、相对路径和 SHA-256 与凭证关联。

## 3. 统一规则

### 3.1 主键、时间和并发

- 内部主键统一使用 `bigint IDENTITY`。
- 除非外部接口明确要求其他时区，所有日期和时间统一使用北京时间（UTC+8）。
- 数据库使用 `datetime2(3)` 直接保存北京时间，默认值由 `DATEADD(HOUR, 8, SYSUTCDATETIME())` 生成，不依赖服务器或 PDA 的本地时区。
- API 使用 ISO 8601 并携带 `+08:00`；页面直接按北京时间显示，不再对数据库业务时间执行 UTC 转换。
- 可变主表使用 SQL Server `rowversion` 防止并发覆盖。
- `created_at` 由数据库提供默认值；`updated_at` 由应用在每次更新时维护。
- 历史流水和事件只追加，不修改、不删除。

### 3.2 数量和单位

- 数量统一使用 `decimal(18,6)`。
- `product_label.current_qty` 是标签当前余额的唯一可信值。
- 当前阶段一个物料只使用一个标签计量单位，不支持单位换算。
- 业务操作明细和库存流水必须使用相同的 `unit_code`。
- `change_qty` 是有符号变化量；减少为负，位置和托盘操作为零。
- 标签变更后数量必须满足“变更后 = 变更前 + 变化量”，且不允许小于零。
- 单位不同的请求必须拒绝，不能静默按 1:1 处理。
- 后续需要单位换算时，在业务操作明细增加输入单位、输入数量和换算率；库存流水仍只保存统一库存单位下的实际结果。

### 3.3 多设备冲突

- 查询允许继续，查询结果应显示标签或托盘正在处理中。
- 改变数量、状态、仓库、货位、托盘关系或打印状态的操作必须取得业务锁。
- 默认不设置优先级，先成功取得锁者先处理；失败请求不自动排队。
- 一次操作涉及多个资源时，必须按固定顺序在一个短事务内取得全部锁，失败则整体回滚。
- 提交时再次检查 `locked_operation_id`、`row_version` 和剩余数量。
- 锁超时只进入核对流程，不能自动抢锁。U8 结果未知时使用 `NEEDS_RECONCILIATION` 并继续阻止冲突写入。
- 相同 `client_request_id` 的重复请求返回原操作状态，不重复执行。

### 3.4 库存身份

料号、批号、单位、仓库、货位和库存属性不放在产品标签主表，使用最新有效库存流水确定当前状态。ERP 库存对账只按仓库、料号、货位和批号汇总；单位必须预先校验一致但不参与分组，自由项和其他属性暂不参与对账。`inventory_identity_hash` 使用这四个对账字段生成。

产品标签当前位置通过 `product_label.last_flow_id` 直接关联 `inventory_flow.id`，读取最新流水的 `to_warehouse_code` 和 `to_location_code`。托盘同理由 `pallet.last_flow_id` 定位。每条已生效流水都保存完整的操作后状态；不改变位置的补打、装卸托等流水也必须复制当前仓库和货位。只有出库、报废或作废后不再有当前位置时，操作后位置才允许为空。应用不得仅使用 `MAX(created_at)` 判断最新位置。

货位是否必填由 U8 仓库的货位管理设置决定。启用货位管理的仓库必须提供有效货位，未启用的仓库允许货位为空；来源仓库和目标仓库分别校验。扫描托盘进行调拨或货位调整时，必须同时锁定并移动托盘自身及全部当前在托有效产品标签，不能只更新托盘或部分在托标签。

### 3.5 ERP 实时对账

业务数据库不复制 ERP 表头、表体或库存表。对账 API 实时读取指定账套中的 ERP 单据或现存量，再使用本地保存的 ERP 单据 ID、表体行 ID 和四项库存身份与业务操作、库存流水比较。ERP 单据行从未发生本地打印或业务操作时，也必须通过 ERP 查询结果的左连接显示为本地数量 0。

当前不建立打印需求表和对账结果表。对账结果按请求实时生成；需要留痕时只记录对账操作、筛选条件、汇总结果和异常事件，不保存整份 ERP 数据副本。

## 4. 表关系

```text
business_operation
├─ business_operation_line
├─ business_operation_event
├─ inventory_flow
│  ├─ product_label
│  ├─ pallet
│  ├─ business_operation_line
│  ├─ from_pallet ────── pallet
│  └─ to_pallet ──────── pallet
└─ pallet_label_relation
   ├─ pallet
   └─ product_label

pallet_material ───────── pallet
product_label.last_flow_id ── inventory_flow
pallet.last_flow_id ───────── inventory_flow
business_operation.reversal_of_operation_id ── business_operation
inventory_flow.reversal_of_flow_id ── 同一库存对象的原流水

system_function.parent_function_code ── 对应的业务功能
schema_migration ───────── 已执行的数据库结构版本

label_code_rule
└─ label_code_rule_version
   ├─ label_code_sequence
   └─ label_code_registry
      ├─ product_label（可选）
      └─ pallet（可选）
```

打印不单独建表。成功的首次打印和补打由 `inventory_flow` 记录，操作人、设备和原因由 `business_operation` 记录；包装规格、计划张数和成功张数由业务操作明细记录，打印机、模板、任务号、失败返回和重试过程由 `business_operation_event` 按顺序记录。每成功物理输出一张标签，必须写入一条该标签的 `PRINT` 或 `REPRINT` 流水；失败或结果未知的尝试不得写成成功流水。

## 5. 表与字段

### 5.1 `business_operation` 业务操作主表

一笔提交、审核、打印、标签或托盘作业一条记录。保存操作人、设备、幂等、U8 关联、锁、操作凭证和最终结果。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `id` | bigint identity | 否 | 内部主键 |
| `operation_no` | nvarchar(50) | 否 | 系统生成的唯一业务操作编号 |
| `submission_group_id` | varchar(100) | 是 | 同一次 H5 提交拆成多个业务操作时共用的组编号 |
| `operation_type` | varchar(50) | 否 | 操作类型，如打印、拆分、合并、数量调整、U8 新增或审核 |
| `status` | varchar(30) | 否 | 操作状态 |
| `source_document_type` | varchar(50) | 是 | 来源单据类型快照 |
| `source_document_id` | nvarchar(100) | 是 | 来源单据内部标识 |
| `source_document_no` | nvarchar(100) | 是 | 来源单据编号 |
| `erp_environment` | varchar(20) | 是 | U8 环境：development、test、production |
| `erp_account_id` | nvarchar(20) | 是 | U8 账套号，如 008、888 |
| `erp_year` | smallint | 是 | U8 业务年度 |
| `erp_database_name_snapshot` | nvarchar(128) | 是 | U8 账套数据库名快照，不含连接信息 |
| `erp_document_type` | varchar(50) | 是 | U8 单据类型 |
| `erp_document_id` | nvarchar(100) | 是 | U8 单据内部 ID |
| `erp_document_no` | nvarchar(100) | 是 | U8 单据编号 |
| `erp_operation_type` | varchar(30) | 是 | ADD、EDIT、AUDIT、UNAUDIT、DELETE、RED 等 |
| `erp_version_before` | nvarchar(100) | 是 | U8 操作前版本，如 `ufts` |
| `erp_version_after` | nvarchar(100) | 是 | U8 回查确认后的版本 |
| `erp_status` | varchar(30) | 否 | U8 处理状态，默认 NOT_REQUIRED |
| `erp_confirmed_at` | datetime2(3) | 是 | U8 回查确认时间 |
| `operator_id` | nvarchar(100) | 否 | 操作人唯一标识 |
| `operator_name_snapshot` | nvarchar(100) | 否 | 操作人名称快照 |
| `device_id` | nvarchar(100) | 是 | 设备系统标识 |
| `device_code` | nvarchar(100) | 是 | 设备业务编码或资产编号 |
| `device_model` | nvarchar(100) | 是 | 设备型号，如 CT58C |
| `client_request_id` | varchar(100) | 否 | 客户端唯一幂等编号 |
| `request_payload_hash` | char(64) | 是 | 规范化请求的 SHA-256 |
| `approval_status` | varchar(20) | 否 | 内部审核状态 |
| `approver_id` | nvarchar(100) | 是 | 内部审核人标识 |
| `approver_name_snapshot` | nvarchar(100) | 是 | 审核人名称快照 |
| `approved_at` | datetime2(3) | 是 | 内部审核完成时间 |
| `reason` | nvarchar(500) | 是 | 操作原因或业务备注 |
| `reversal_of_operation_id` | bigint | 是 | 被撤销、红冲或反向处理的原操作 ID |
| `lock_acquired_at` | datetime2(3) | 是 | 成功取得业务锁的时间 |
| `heartbeat_at` | datetime2(3) | 是 | 持锁操作最近心跳时间 |
| `lock_expires_at` | datetime2(3) | 是 | 进入超时核对的时间；到期不能自动抢锁 |
| `archive_relative_path` | nvarchar(500) | 是 | 独立 TXT 操作凭证相对路径 |
| `archive_status` | varchar(20) | 否 | 凭证状态，默认 PENDING |
| `archive_sha256` | char(64) | 是 | 完成态凭证文件 SHA-256 |
| `error_code` | varchar(100) | 是 | 最终错误代码 |
| `error_message` | nvarchar(2000) | 是 | 最终错误摘要 |
| `created_at` | datetime2(3) | 否 | 创建时间 |
| `updated_at` | datetime2(3) | 否 | 最后更新时间 |
| `completed_at` | datetime2(3) | 是 | 完成时间 |
| `row_version` | rowversion | 否 | 并发版本 |

操作状态：`DRAFT`、`PENDING`、`WRITING_ERP`、`PENDING_APPROVAL`、`SUCCESS`、`FAILED`、`CANCELLED`、`REVERSED`、`NEEDS_RECONCILIATION`。

关键约束：`operation_no`、`client_request_id` 唯一；反向操作直接关联原操作；摘要字段只允许十六进制 SHA-256。

### 5.2 `business_operation_line` 业务操作明细表

保存用户提交的实际明细。即使是没有绑定标签的纯 U8 单据操作，也可以保存料号、数量、仓库、货位及 U8 表体对应关系。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `id` | bigint identity | 否 | 内部主键 |
| `operation_id` | bigint | 否 | 所属业务操作 |
| `line_no` | int | 否 | 操作内从 1 开始的明细行号 |
| `line_status` | varchar(20) | 否 | PENDING、SUCCESS、FAILED、SKIPPED |
| `label_id` | bigint | 是 | 绑定的产品标签；纯 U8 单据明细为空 |
| `pallet_id` | bigint | 是 | 绑定的托盘；与 `label_id` 互斥 |
| `selection_source` | varchar(20) | 是 | LABEL_SCAN 或 PALLET_SCAN |
| `source_pallet_id` | bigint | 是 | 选择标签时所属的托盘；托盘扫码时必填 |
| `inventory_qty_snapshot` | decimal(18,6) | 是 | 扫码或提交时看到的标签或托盘库存数量快照 |
| `source_line_id` | nvarchar(100) | 是 | 来源单据表体行 ID |
| `source_line_no` | nvarchar(50) | 是 | 来源单据表体行号 |
| `erp_line_id` | nvarchar(100) | 是 | U8 目标表体行 ID，通常对应 autoid |
| `erp_line_no` | nvarchar(50) | 是 | U8 目标表体行号 |
| `erp_position_id` | nvarchar(100) | 是 | U8 货位明细 ID |
| `material_code` | nvarchar(100) | 否 | 物料编码快照 |
| `batch_no` | nvarchar(100) | 是 | 批号快照 |
| `manufacture_date` | date | 是 | 生产日期 |
| `expiry_date` | date | 是 | 失效日期 |
| `serial_no` | nvarchar(200) | 是 | 序列号 |
| `inventory_attributes_json` | nvarchar(max) | 是 | 自由项及不固定批次属性 JSON |
| `attribute_schema_version` | int | 否 | 属性 JSON 结构版本 |
| `inventory_identity_hash` | char(64) | 是 | 库存身份 SHA-256 |
| `unit_code` | nvarchar(50) | 否 | 用户或单据使用的计量单位；当前不支持换算 |
| `requested_qty` | decimal(18,6) | 否 | 本次申请处理的非负数量 |
| `package_spec_snapshot` | nvarchar(200) | 是 | 本次打印使用的包装规格文字快照 |
| `package_qty` | decimal(18,6) | 是 | 标准包装下每张标签对应的产品数量 |
| `requested_label_count` | int | 是 | 计划打印张数 |
| `printed_label_count` | int | 是 | 打印工具确认成功的实际张数 |
| `from_warehouse_code` | nvarchar(100) | 是 | 来源仓库 |
| `from_location_code` | nvarchar(100) | 是 | 来源货位 |
| `to_warehouse_code` | nvarchar(100) | 是 | 目标仓库或完成后仓库 |
| `to_location_code` | nvarchar(100) | 是 | 目标货位或完成后货位 |
| `remark` | nvarchar(500) | 是 | 用户提交的明细备注 |
| `error_code` | varchar(100) | 是 | 明细错误代码 |
| `error_message` | nvarchar(2000) | 是 | 明细错误摘要 |
| `created_at` | datetime2(3) | 否 | 创建时间 |
| `updated_at` | datetime2(3) | 否 | 最后更新时间 |
| `row_version` | rowversion | 否 | 并发版本 |

关键约束：同一操作的 `line_no` 唯一；数量不得为负；属性必须是合法 JSON。产品标签和托盘最多绑定一种；绑定库存对象时必须保存选择来源和库存数量快照。通过托盘选择产品标签时必须保存来源托盘；托盘自身做单时 `requested_qty` 固定为 1。

`inventory_qty_snapshot` 和 `requested_qty` 含义不同。例如产品标签扫码时余额为 100、本次申请使用 60，两个字段分别保存 100 和 60。前者只还原用户提交现场，后者保存业务请求；最终实际执行前后数量只写入 `inventory_flow.qty_before` 和 `inventory_flow.qty_after`。

打印数量按两种口径查询：`printed_label_count` 是打印工具确认成功的张数，成功首次打印的产品总数量由本明细关联的 `inventory_flow` 中 `PRINT` 流水合计。补打、拆分和合并产生的新纸张不能重复增加 ERP 入库打印数量。

已有 U8 单据来源位置与标签最新位置不一致时，该明细使用 `line_status = FAILED`、`error_code = LABEL_LOCATION_MISMATCH`，`error_message` 保存标签当前位置和 U8 来源位置；不得生成已生效库存流水，也不得静默修改 U8 单据。

### 5.3 `product_label` 产品标签主表

一个标签码永久一条记录。物料、批次、仓库和货位从 `last_flow_id` 指向的最新有效流水取得，不在主表重复维护。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `id` | bigint identity | 否 | 内部主键 |
| `label_code` | nvarchar(100) | 否 | 全局唯一标签码，作废后也不得复用 |
| `current_qty` | decimal(18,6) | 否 | 当前剩余数量，默认 0 |
| `status` | varchar(30) | 否 | 标签状态 |
| `last_flow_id` | bigint | 是 | 同一标签最新已生效流水 ID，也是当前库存身份和位置的指针 |
| `locked_operation_id` | bigint | 是 | 当前持有业务锁的操作 ID |
| `created_at` | datetime2(3) | 否 | 创建时间 |
| `updated_at` | datetime2(3) | 否 | 最后更新时间 |
| `row_version` | rowversion | 否 | 并发版本 |

标签状态：`PENDING_PRINT`、`ACTIVE`、`USED`、`SPLIT`、`MERGED`、`FROZEN`、`VOID`。处理中只使用 `locked_operation_id`，不再设置重复的 `LOCKED` 状态。

所有状态均允许重新输出原标签，但只有已有成功 `PRINT` 流水的标签才记为 `REPRINT`，且不改变状态、余额或托盘关系。尚未首次打印成功的 `PENDING_PRINT` 使用原任务完成首次打印，成功流水仍为 `PRINT`。非 `ACTIVE` 状态补打必须由应用执行二次确认。

关键约束：标签码唯一；当前数量不得小于零；`last_flow_id` 必须属于当前标签。

### 5.4 `pallet_material` 托盘料号配置表

维护哪些 U8 存货编码属于托盘。加入本表并启用的料号只能打印成托盘，不能同时打印为普通产品标签。存货名称、规格和单位从 U8 查询，不在本地重复维护。

产品料号与托盘料号由业务保证互斥，核心标签业务仍为 10 张表。迁移 010 另建 `label_code_registry` 统一约束产品、托盘及外部 Code；正式发码服务必须先登记唯一哈希。扫码意外同时命中两类对象时，应用按数据异常拒绝处理并记录事件。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `id` | bigint identity | 否 | 内部主键 |
| `material_code` | nvarchar(100) | 否 | U8 存货编码，唯一 |
| `is_enabled` | bit | 否 | 是否允许继续打印新托盘 |
| `remark` | nvarchar(500) | 是 | 维护备注 |
| `updated_by` | nvarchar(100) | 否 | 最后维护人标识 |
| `created_at` | datetime2(3) | 否 | 创建时间 |
| `updated_at` | datetime2(3) | 否 | 最后更新时间 |
| `row_version` | rowversion | 否 | 并发版本 |

关键约束：`material_code` 唯一。已经被托盘引用的配置不能物理删除；停用只阻止以后打印，不影响历史托盘。

### 5.5 `pallet` 托盘主表

托盘和产品标签分开管理。一个成功打印的托盘标签对应一条记录。托盘保存自身库存数量 0 或 1，但不保存所装产品总数量；所装产品通过当前在托标签实时汇总。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `id` | bigint identity | 否 | 内部主键 |
| `pallet_code` | nvarchar(100) | 否 | 打印生成的全局唯一托盘标签码 |
| `material_code` | nvarchar(100) | 否 | 托盘对应的 U8 存货编码 |
| `batch_no` | nvarchar(100) | 是 | 托盘生产批号 |
| `manufacture_date` | date | 是 | 托盘生产日期 |
| `unit_code` | nvarchar(50) | 否 | 托盘计量单位 |
| `current_qty` | decimal(18,6) | 否 | 托盘自身库存数量，只能是 0 或 1 |
| `status` | varchar(20) | 否 | ACTIVE、FROZEN、OUT_OF_STOCK、SCRAPPED、VOID |
| `last_flow_id` | bigint | 是 | 托盘自身最新已生效库存流水 |
| `current_warehouse_code` | nvarchar(100) | 是 | 托盘自身当前仓库 |
| `current_location_code` | nvarchar(100) | 是 | 托盘自身当前货位 |
| `last_operation_id` | bigint | 是 | 最后改变状态或位置的操作 ID |
| `locked_operation_id` | bigint | 是 | 当前持有业务锁的操作 ID |
| `created_at` | datetime2(3) | 否 | 创建时间 |
| `updated_at` | datetime2(3) | 否 | 最后更新时间 |
| `row_version` | rowversion | 否 | 并发版本 |

关键约束：托盘码唯一；托盘料号必须存在于 `pallet_material`；`ACTIVE`、`FROZEN` 数量为 1，其余状态数量为 0；数量为 0 时仓库和货位必须为空。处理中只使用 `locked_operation_id`，不设置重复的 `LOCKED` 状态。空托或已装托根据当前 `pallet_label_relation` 计算，不存入 `status`。

托盘标签以一次在库生命周期为边界。托盘出库后原记录永久保持 `OUT_OF_STOCK`，原标签码不得重新启用。实体托盘返回企业时，使用同一 U8 托盘料号重新打印全新标签码，创建新的 `pallet` 记录及首次 `PRINT` 流水；这不是旧标签补打。

### 5.6 `inventory_flow` 库存流水表

产品标签和托盘自身共用一张库存流水表。每次数量、位置、装卸托或成功打印新增一条流水，只追加，不直接修改或删除；产品标签和托盘主表仍然独立。流水必须保存完整的操作前后位置；不改变位置的流水沿用上一条操作后位置。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `id` | bigint identity | 否 | 内部主键 |
| `operation_id` | bigint | 否 | 产生流水的业务操作 |
| `operation_line_id` | bigint | 是 | 对应的业务操作明细，且必须属于同一操作 |
| `object_type` | varchar(20) | 否 | PRODUCT_LABEL 或 PALLET |
| `label_id` | bigint | 是 | 产品标签 ID；托盘流水为空 |
| `pallet_id` | bigint | 是 | 托盘 ID；产品标签流水为空 |
| `object_sequence_no` | int | 否 | 当前库存对象内部连续流水序号 |
| `relation_group_no` | int | 是 | 同一操作中拆分或合并的分组号 |
| `flow_role` | varchar(10) | 否 | NORMAL、SOURCE、TARGET |
| `operation_type` | varchar(50) | 否 | 打印、使用、拆分、合并、调整、装卸托等 |
| `reversal_of_flow_id` | bigint | 是 | 同一库存对象被反向处理的原流水 ID |
| `unit_code` | nvarchar(50) | 否 | 库存计量单位；当前不支持换算 |
| `qty_before` | decimal(18,6) | 否 | 变更前库存余额 |
| `change_qty` | decimal(18,6) | 否 | 有符号变化量，减少为负 |
| `qty_after` | decimal(18,6) | 否 | 变更后库存余额 |
| `material_code` | nvarchar(100) | 否 | 料号快照 |
| `batch_no` | nvarchar(100) | 是 | 批号快照 |
| `manufacture_date` | date | 是 | 生产日期快照 |
| `expiry_date` | date | 是 | 失效日期快照 |
| `serial_no` | nvarchar(200) | 是 | 序列号快照 |
| `inventory_attributes_json` | nvarchar(max) | 是 | 自由项及不固定批次属性 JSON |
| `attribute_schema_version` | int | 否 | 属性 JSON 结构版本 |
| `inventory_identity_hash` | char(64) | 是 | 仓库、料号、货位、批号规范化后的 SHA-256 |
| `from_warehouse_code` | nvarchar(100) | 是 | 变化前仓库 |
| `from_location_code` | nvarchar(100) | 是 | 变化前货位 |
| `to_warehouse_code` | nvarchar(100) | 是 | 变化后仓库 |
| `to_location_code` | nvarchar(100) | 是 | 变化后货位 |
| `from_pallet_id` | bigint | 是 | 产品标签变化前所属托盘；托盘自身流水为空 |
| `to_pallet_id` | bigint | 是 | 产品标签变化后所属托盘；托盘自身流水为空 |
| `remark` | nvarchar(500) | 是 | 库存流水备注 |
| `created_at` | datetime2(3) | 否 | 流水生效时间 |

关键约束：每条流水必须且只能绑定产品标签或托盘之一；同一库存对象流水序号唯一；前后数量必须平衡且不得为负；托盘自身数量只能为 0 或 1；数量发生变化时必须关联业务操作明细；反向流水必须由应用校验为同一库存对象；拆分和合并使用 `relation_group_no` 区分多组关系。

### 5.7 `pallet_label_relation` 托盘标签关系表

记录每次装托和卸托。`unloaded_at` 为空表示当前仍在托盘中。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `id` | bigint identity | 否 | 内部主键 |
| `pallet_id` | bigint | 否 | 托盘 ID |
| `label_id` | bigint | 否 | 标签 ID |
| `loaded_operation_id` | bigint | 否 | 装托操作 ID |
| `unloaded_operation_id` | bigint | 是 | 卸托操作 ID |
| `loaded_at` | datetime2(3) | 否 | 装托时间 |
| `unloaded_at` | datetime2(3) | 是 | 卸托时间，为空表示当前关系 |

关键约束：一张标签最多只有一条未卸托关系；卸托操作和卸托时间必须同时有值；卸托时间不得早于装托时间。

### 5.8 `business_operation_event` 业务操作事件表

按顺序保存 H5、PDA、业务 API、U8、打印机和数据库的处理过程。核心业务字段使用普通列；网关请求编号、APK/H5/桥接版本、单次 U8 返回和核对过程等技术信息保存在对应事件中。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `id` | bigint identity | 否 | 内部主键 |
| `operation_id` | bigint | 否 | 所属业务操作 |
| `sequence_no` | int | 否 | 操作内从 1 开始的事件序号 |
| `stage` | varchar(50) | 否 | REQUEST_RECEIVED、U8_CALLED、DATABASE_COMMITTED 等阶段 |
| `source` | varchar(30) | 否 | H5、PDA、BUSINESS_API、U8_GATEWAY、U8、PRINTER、DATABASE、SYSTEM |
| `event_type` | varchar(50) | 否 | 事件类型 |
| `event_code` | varchar(100) | 是 | 事件或错误代码 |
| `result` | varchar(20) | 否 | INFO、SUCCESS、FAILED、UNKNOWN |
| `request_id` | varchar(100) | 是 | 当前阶段请求编号 |
| `message` | nvarchar(2000) | 是 | 简短事件摘要 |
| `event_data_json` | nvarchar(max) | 是 | 脱敏后的非固定事件数据 |
| `payload_hash` | char(64) | 是 | 事件有效载荷 SHA-256 |
| `duration_ms` | bigint | 是 | 阶段耗时，毫秒 |
| `created_at` | datetime2(3) | 否 | 事件时间 |

关键约束：同一操作的事件序号唯一；JSON 必须合法；耗时不得为负。

### 5.9 `system_setting` 系统配置表

保存非敏感全局设置和单据级覆盖设置。数据库、U8 或服务器密码不进入此表。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `id` | bigint identity | 否 | 内部主键 |
| `setting_scope` | varchar(20) | 否 | GLOBAL 或 DOCUMENT |
| `document_type` | varchar(50) | 是 | 单据级配置的单据类型；全局配置为空 |
| `setting_key` | varchar(100) | 否 | 程序识别的稳定配置键 |
| `setting_name` | nvarchar(100) | 否 | 配置中文名称 |
| `value_json` | nvarchar(max) | 否 | 布尔、数字、字符串或结构化 JSON 值 |
| `is_enabled` | bit | 否 | 配置记录是否启用 |
| `setting_version` | int | 否 | 每次修改递增的业务版本 |
| `remark` | nvarchar(500) | 是 | 配置维护备注 |
| `updated_by` | nvarchar(100) | 否 | 最后修改人标识 |
| `created_at` | datetime2(3) | 否 | 创建时间 |
| `updated_at` | datetime2(3) | 否 | 最后更新时间 |
| `row_version` | rowversion | 否 | 并发版本 |

关键约束：全局配置的 `document_type` 必须为空；单据配置必须指定 `document_type`；同一作用域、单据类型和配置键唯一；`value_json` 必须是合法 JSON。

迁移 002 已写入 35 条默认设置：7 条全局设置、9 类单据各 3 条通用设置，以及材料出库的“允许超领”设置。SQL Server 2019 对 JSON 标量不能直接通过 `ISJSON` 判定，迁移使用显式布尔、数字、字符串和对象/数组组合校验，API 仍可统一读写 JSON 值。

当前主要默认设置：

| 作用域 | 配置键 | 名称 | 默认值 |
| --- | --- | --- | --- |
| GLOBAL | `allow_direct_use_of_palletized_label` | 托盘内标签允许单独使用 | `false` |
| DOCUMENT | `include_pallet_in_document_by_default` | 做单时默认包含托盘自身 | `true` |
| DOCUMENT | `auto_upload_on_complete` | 完成自动上传 | `false` |
| DOCUMENT | `auto_approve_after_upload` | 上传自动审核 | `false` |
| DOCUMENT / material-outbound | `allow_over_issue` | 允许超领 | `false` |

### 5.10 `system_function` 系统功能表

保存系统功能目录和全局启停状态。功能开关不是普通 JSON 设置，不放入 `system_setting`；功能编码作为 H5、API 和后续权限表之间的稳定关联键。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `id` | bigint identity | 否 | 内部主键 |
| `function_code` | varchar(100) | 否 | 稳定且唯一的功能编码 |
| `function_name` | nvarchar(100) | 否 | 功能显示名称 |
| `function_kind` | varchar(20) | 否 | DOCUMENT、APPROVAL 或 QUERY |
| `parent_function_code` | varchar(100) | 是 | 审核功能对应的业务功能编码 |
| `group_code` | varchar(50) | 否 | 分组稳定编码 |
| `group_name` | nvarchar(100) | 否 | 分组显示名称 |
| `group_sort` | int | 否 | 分组显示顺序 |
| `function_sort` | int | 否 | 组内显示顺序 |
| `icon_code` | varchar(50) | 是 | H5 已知图标编码 |
| `is_enabled` | bit | 否 | 全局是否启用 |
| `remark` | nvarchar(500) | 是 | 维护备注 |
| `updated_by` | nvarchar(100) | 否 | 最后修改人标识 |
| `created_at` | datetime2(3) | 否 | 创建时间，北京时间 |
| `updated_at` | datetime2(3) | 否 | 最后更新时间，北京时间 |
| `row_version` | rowversion | 否 | 并发版本 |

初始写入 9 个做单功能、9 个审核功能和 1 个标签查询功能。全局关闭后所有用户均不可显示或访问；不删除记录和复用功能编码。后续用户权限表引用 `function_code`，用户只能在全局开启且自身具有权限的范围内使用功能。

### 5.11 `u8_rd_style_cache` U8 出入库类别缓存表

保存从当前环境所连接 U8 账套 `Rd_Style` 读取的完整类别快照。同步不会物理删除旧编码；本次未出现的记录改为停用，以保留历史解释能力。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `rd_style_code` | nvarchar(10) | 否 | U8 `cRdCode`，主键 |
| `rd_style_name` | nvarchar(24) | 否 | U8 `cRdName` |
| `rd_grade` | tinyint | 否 | U8 `iRdGrade` |
| `direction` | varchar(10) | 否 | IN 或 OUT；分别对应 `bRdFlag` 1 和 0 |
| `is_terminal` | bit | 否 | U8 `bRdEnd`，是否末级类别 |
| `opposite_code` | nvarchar(80) | 是 | U8 `cOppHead` |
| `is_retail` | bit | 否 | U8 `bRetail` |
| `source_row_version` | binary(8) | 是 | U8 `pubufts` 快照 |
| `is_active` | bit | 否 | 最近一次完整同步是否仍存在 |
| `last_seen_at` | datetime2(3) | 否 | 最近一次在 U8 快照中出现时间 |
| `created_at` / `updated_at` | datetime2(3) | 否 | 本地创建和更新时间 |
| `row_version` | rowversion | 否 | 本地并发版本 |

业务选择默认只读取 `is_active=1` 且 `is_terminal=1` 的记录；完整缓存保留非末级分类。

### 5.12 `u8_warehouse_cache` U8 仓库缓存表

保存 U8 `Warehouse` 中未设置停用日期的仓库。主键为 `warehouse_code`；同时保存名称、有效标志、最近出现时间、本地创建/更新时间和行版本。没有仓位的有效仓库同样保留。

### 5.13 `u8_position_cache` U8 仓位缓存表

保存 U8 `Position` 仓位及所属仓库。`position_code` 为主键，`warehouse_code` 外键指向仓库缓存；选择索引为 `(warehouse_code, is_active, position_code)`。同步缺失项只停用，不删除。

### 5.14 `u8_department_cache` U8 部门缓存表

保存 U8 `Department` 中未设置停用日期的部门。主键为 `department_code`；同时保存名称、有效标志、最近出现时间、本地创建/更新时间和行版本。

### 5.15 `reference_data_sync_state` 基础资料同步状态表

按同步项记录手工同步状态、数量、开始/完成/成功时间、操作人和简短错误。当前稳定键为 `rd-styles`、`warehouse-positions` 和 `departments`。

状态限定为 `NEVER`、`RUNNING`、`SUCCESS` 和 `FAILED`。同步失败只更新状态，不清空上一次成功缓存。迁移 004 允许 `last_success_at` 保留上一轮成功时间，同时仍要求本轮完成时间不得早于本轮开始时间、成功时间不得晚于最近完成时间。

### 5.16 `schema_migration` 结构迁移记录表

记录每套数据库已经成功执行的结构迁移。当前已执行 `001` 至 `009`；正式建表完成后，后续结构调整只能新增迁移编号，不修改已执行脚本。

| 字段 | 类型 | 可空 | 说明 |
| --- | --- | --- | --- |
| `migration_id` | varchar(100) | 否 | 全局唯一迁移编号 |
| `description` | nvarchar(500) | 否 | 迁移用途 |
| `script_checksum` | char(64) | 是 | 脚本 SHA-256；当前 `001` 至 `009` 均已记录 |
| `applied_at` | datetime2(3) | 否 | 成功执行时间，北京时间 |
| `applied_by` | sysname | 否 | 执行迁移的 SQL 登录名 |

### 5.17 `application_user` 应用用户表

保存应用身份，不保存明文密码。`user_id` 是业务和审计使用的稳定主键，`login_name` 唯一；`display_name` 用于页面及本地业务快照。`erp_operator_code`、`erp_operator_name` 必须同时为空或同时有值，真实 U8 写入时由 Business API 读取该映射生成制单人。`auth_source` 当前允许 `DEVELOPMENT`、`LOCAL`、`EXTERNAL`。

主要字段还包括 `is_admin`、`is_enabled`、`last_login_at`、创建/更新人、北京时间和 `row_version`。迁移 007 在同一表增加 `password_hash`、`password_salt`、`password_iterations`、失败次数、锁定时间和密码修改时间；只保存 PBKDF2-SHA256 加盐派生值。本地账号连续 5 次失败锁定 15 分钟。development 当前另有两个已启用管理员账号；测试库和生产库只安装字段，不复制开发账号。

### 5.18 `user_function_permission` 用户功能权限表

主键为 `(user_id, function_code)`，分别外键关联用户和 `system_function`。`permission_state` 仅允许 `ALLOW`、`DENY`；无记录表示继承。`data_scope_json` 可空且必须为合法 JSON，当前使用 `version=1` 和 `rdStyles={mode,codes}` 保存该用户在对应做单功能可使用的出入库类别；空值或没有 `rdStyles` 表示全部。部门、仓库和货位当前不进入用户数据范围。指定范围会同时保留一条 `ALLOW` 记录，恢复“默认”且无范围时删除记录。权限变更记录更新人、时间和行版本。

### 5.19 `user_preference` 用户漫游偏好表

主键为 `(user_id, preference_key)`，`value_json` 必须为合法 JSON 对象或数组。每次更新递增 `preference_version`，并保存可选设备号、北京时间和行版本。稳定键包括常用功能 `workspace.favorites` 和业务中心显示模式 `workspace.homeMode`；显示模式值为 `{"mode":"compact|detailed"}`。无期望行版本的首次并发写入采用最后写入覆盖，显式携带行版本时仍执行并发冲突校验。设备地址、扫描器和声音等设备属性不放入本表。

### 5.20 `user_admin_audit` 用户管理审计表

记录新增/修改用户、密码重置、账号解锁、会话撤销、漫游数据重置和功能权限修改。每条记录保存操作人、目标用户、请求编号、客户端地址、User-Agent、北京时间以及不含敏感凭据的前后快照。密码、密码哈希、盐值和令牌不得进入审计 JSON。

除会话撤销外，用户管理写操作与审计记录在同一数据库事务内提交。会话撤销发生在 API 进程内，撤销数量随后写入审计记录。

### 5.21 `document_draft` 多端草稿最新快照

每个 `draft_id` 只保存一个最新 JSON 正文。`owner_user_id`、`device_id`、`document_type`、`business_status`、日期、源单、仓库、部门、明细数和总数量作为普通字段保存，便于权限过滤和索引查询；各单据差异字段保存在 `payload_json`。`schema_version` 管理 JSON 结构演进，`client_revision` 和 `payload_hash` 用于幂等与并发冲突检测。

状态限制为 `DRAFT`、`PREPARED`、`COMPLETED`、`CANCELLED`、`DELETED`。软删除设置 `deleted_at` 并保留正文到保留期结束；服务端不保存每次字段修改前后的 JSON。

### 5.22 `document_draft_event` 草稿状态事件

只记录创建、状态变化、删除和恢复，包括前后状态、操作人、设备、客户端修订号、请求号和时间，不保存草稿正文、密码或令牌。清理草稿时先清理对应事件。

### 5.23 `client_device_state` PDA 同步状态

以用户和设备为联合主键，保存设备名称、型号、H5 版本、最近在线时间、最后成功同步时间及本地草稿、待同步、失败数量。该表是设备最后一次主动上报的快照，不代表离线设备的实时内容。

### 5.24 `label_code_rule` 标签 Code 规则主档

保存稳定规则编号、名称、业务对象类型、解析优先级和启停状态。对象类型限制为产品标签、托盘、产品/托盘通用或外部标签。优先级数值越小越先匹配；同优先级同时命中必须由服务端拒绝，不允许静默选择。通用规则只表示解析格式，统一登记仍必须保存最终对象类型。

### 5.25 `label_code_rule_version` 标签 Code 规则版本

每个规则按递增版本保存完整 JSON 规则、SHA-256 和状态。状态为草稿、启用、历史停用或阻断；同一规则最多一个草稿和一个启用版本。启用版本不可原位修改，历史版本保留用于旧标签解析。

### 5.26 `label_code_sequence` 标签 Code 流水

按规则版本和分区键保存最后分配流水号，并使用行版本保护并发。该表只为后续正式发码预留；本阶段不消耗流水。正式实现必须在分配流水与统一登记的同一数据库事务内完成。

### 5.27 `label_code_registry` Code 统一登记

保存 Code 的 SHA-256 唯一哈希、原始值、标准化值、业务对象、规则版本、账套快照、解析 JSON 和登记状态。产品标签或托盘建立后可以关联对应主表；外部标签不关联本地业务对象。本阶段不写入该表，后续正式发码和外部码首次登记必须保证哈希全局唯一。

## 6. 关键索引与查询

| 查询 | 主要索引或约束 |
| --- | --- |
| 标签扫码查询 | `product_label.label_code` 唯一约束 |
| 托盘料号判断 | `pallet_material.material_code` 唯一约束 |
| 托盘标签查询 | `pallet.pallet_code` 唯一约束 |
| 托盘存货、批号和在库状态 | `pallet(material_code, batch_no, current_qty)` |
| 产品标签完整流水 | `inventory_flow(label_id, object_sequence_no)` 过滤唯一索引 |
| 托盘自身完整流水 | `inventory_flow(pallet_id, object_sequence_no)` 过滤唯一索引 |
| 拆分、合并关系 | `inventory_flow(operation_id, relation_group_no, flow_role)` |
| 当前托盘内容 | `pallet_label_relation(pallet_id, label_id) WHERE unloaded_at IS NULL` |
| 标签当前所属托盘 | `pallet_label_relation(label_id) WHERE unloaded_at IS NULL` 唯一索引 |
| 标签参与的业务明细 | `business_operation_line(label_id, created_at)` |
| 托盘扫码产生的业务明细 | `business_operation_line(source_pallet_id, created_at)` |
| 服务端源单明细占用 | `business_operation_line(source_line_id, operation_id)` 过滤索引 |
| U8 单据追溯 | 环境、账套、年度、单据类型、单据号组合索引 |
| 标签打印历史 | `inventory_flow(label_id, object_sequence_no)` 中的 PRINT、REPRINT 流水 |
| 打印失败和重试过程 | `business_operation_event(operation_id, sequence_no)` |
| 待处理或异常操作 | `business_operation(status, created_at)` |
| 用户单据状态列表 | `business_operation.submission_group_id` 聚合，按 `operator_id`、`operation_type`、日期和状态过滤 |
| ERP 库存对账 | `inventory_flow(to_warehouse_code, material_code, to_location_code, batch_no)` |
| 已启用功能列表 | `system_function(is_enabled, group_sort, function_sort)` |
| 可选出入库类别 | `u8_rd_style_cache(direction, is_active, is_terminal, rd_style_code)` |
| 可选仓库与仓位 | `u8_warehouse_cache` 主键过滤、`u8_position_cache(warehouse_code, is_active, position_code)` |
| 可选部门 | `u8_department_cache(is_active, department_code)` |
| 登录用户 | `application_user(login_name)` 唯一约束及 `(is_enabled, login_name)` 索引 |
| 用户有效功能 | `user_function_permission(user_id, function_code)` 主键和功能反查索引 |
| 用户漫游偏好 | `user_preference(user_id, preference_key)` 主键 |
| 用户管理审计 | `(target_user_id, created_at)`、`(actor_user_id, created_at)` 索引 |
| 用户草稿列表 | `(owner_user_id, business_status, client_updated_at)`；管理员列表使用 `(business_status, client_updated_at)` |
| PDA 最近状态 | `(last_seen_at, user_id, device_id)` |
| 规则编号 | `label_code_rule.rule_code` 唯一约束 |
| 当前规则版本 | 每条规则的草稿和启用状态分别使用过滤唯一索引 |
| 流水并发分配 | `label_code_sequence(rule_version_id, partition_key)` 唯一约束及行版本 |
| Code 全局登记 | `label_code_registry.code_hash` 唯一约束；按产品、托盘和规则版本建立过滤索引 |

标签成功打印总次数为该标签 `PRINT` 与 `REPRINT` 流水条数之和，补打次数只统计 `REPRINT`。同一请求把同一个标签输出多张时，必须按成功张数写入多条 `REPRINT` 流水，且数量应与对应业务明细的 `printed_label_count` 一致。打印任务数使用不同的 `operation_id` 统计，不能代替标签实际打印张数。

位置类业务在创建 U8 单据前，从最新流水读取来源仓库和货位并保存到业务明细；用户选择的目的位置保存到目标字段。U8 写入及所需审核成功后，才允许在同一业务数据库事务中插入新流水并更新 `last_flow_id`。已有 U8 单据必须校验其来源位置与最新流水一致，不一致时拒绝提交，不得静默覆盖 U8 单据。

托盘整体调拨或货位调整时，应用必须在同一操作中为托盘自身及全部当前在托有效产品标签分别写入位置流水。只移动部分产品时必须先卸托，不能让实体托盘与其在托产品形成不同当前位置。

调拨、货位调整和盘点的服务器准备阶段复用现有 `business_operation`、`business_operation_line`、标签/托盘锁和事件表，无需新增迁移。该阶段只保存最新库存快照、来源和目标位置并建立短暂 `PENDING` 锁，不生成 `inventory_flow`；H5 不再把它保存为可操作的业务阶段。盘点产品标签的 `requested_qty` 可为零或高于 `inventory_qty_snapshot`；托盘自身仍受固定数量 1 的约束。

采购到货、委外到货、采购入库、发货单和销售出库预提交同样不新增表。每条扫码数量分配记录在 `business_operation_line.source_line_id`、`source_line_no` 和 `requested_qty`；跨来源单据时建立多个操作并共用 `submission_group_id`。相同标签可因同料来源行拆分出现多条操作明细，但锁只指向业务组根操作。现有来源行占用索引继续用于跨设备并发校验。

材料出库表头不由用户输入仓库。提交时从标签或托盘最新流水读取实际仓库，按仓库拆成多条 `business_operation` 和多张 U8 材料出库单。拆出的操作共用 `submission_group_id`，但各自保存独立 `client_request_id`、U8 单号、状态和操作凭证；部分成功时只对成功仓库组生成库存流水。

单据管理的执行状态以 `business_operation` 为准，`document_draft.business_status` 只保存草稿阶段摘要。直接上传请求内部可短暂建立 `PENDING`，但必须在同一服务调用中进入写入或被取消释放；普通写入前错误不得留下长期待处理锁。客户端补传时服务端按关联业务组重新计算状态，不能用旧 JSON 覆盖真实业务终态。只有外部写入结果不明确时保留 `NEEDS_RECONCILIATION` 供核对。

库存对象查询同时返回最新 `unit_code`。材料出库预提交不接受 H5 推断或默认单位，服务端把请求单位与产品标签最新库存流水或托盘主记录再次比较；不同单位直接拒绝，不做换算。本次为 API 合同补充，不新增数据库字段或迁移。

## 7. 备份、校验与维护

- 完整备份：三套数据库每天 01:30，压缩、校验和，并立即执行 `RESTORE VERIFYONLY`。
- 日志备份：生产库每 15 分钟一次，目标 RPO 为 15 分钟。
- 保留：完整备份和日志备份保留 30 天；草稿按全局 `draft_retention_policy` 清理，默认活跃草稿永久、已完成/取消/删除记录 90 天，均可改为自定义天数或永久；其他业务和审计流水当前不自动清理。
- 完整性检查：每周日 03:00 对三套数据库执行完整 `DBCC CHECKDB`。
- 索引和统计：每周日 04:00 更新统计信息；页数不少于 1000 且碎片率达到 10% 的索引执行整理或重建。
- 容量检查：每 15 分钟检查标签库和 tempdb 所在盘；剩余低于 20% 或 50GB 时作业失败并写入 SQL Server 错误日志。
- 长事务：每 5 分钟检查一次，标签系统数据库中的用户事务超过 5 分钟时作业失败并写入错误日志。
- Query Store：开发、测试、生产分别限制为 256MB、512MB、1024MB，用于慢查询、等待和执行计划追踪。
- 死锁：复用 SQL Server 默认运行的 `system_health` 扩展事件会话采集 `xml_deadlock_report`，不另建重复会话。
- SQL Server 实例内存上限保持现有 13107MB；服务器物理内存为 32GB，当前无需调整。
- 首次完整备份已完成并通过校验，生产日志备份链已启动。
- 每季度执行一次恢复演练，使用 `030_restore_drill.sql` 恢复到固定隔离临时库，核对完整性、表数量和迁移版本后自动删除临时库。恢复演练不得覆盖任何现有环境。首次演练已于 2026-10-11 完成；迁移 002 后又重新生成完整备份并完成恢复验证，恢复 11 张用户表、最新迁移 `002`，`DBCC CHECKDB` 通过，临时库已删除。

SQL Agent 作业统一使用 `LabelSystem_` 前缀，owner 为 `sa`。当前未配置邮件或短信收件人；作业失败可在 SQL Server Agent 历史和错误日志查看，后续确定通知渠道后补充主动通知。TLS 需要服务器证书和维护窗口，数据库已创建但不在本次数据库脚本中强行切换连接加密。

## 8. 尚待确认

- 正式产品标签和托盘标签分别启用哪些规则，以及流水分区策略。
- 正式发码、打印和 `label_code_registry` 登记的事务流程。
- 各单据哪些库存自由项需要提升为普通检索列。
- 单位换算功能及物料单位换算来源；当前所有数量必须使用同一标签单位。
- 条码打印阶段“打印尾数”的页面交互、权限和默认开关。
- 角色聚合、用户角色关系和外部身份，待正式认证方案确定后补充；当前本地凭据、锁定/解锁和直接用户权限已落地。

## 9. 变更要求

业务规则调整时，应同时更新：

1. 根目录 `BUSINESS_LOGIC.md`。
2. 本数据库说明。
3. 对应的 SQL 迁移脚本。
4. API 请求、响应及错误码合同。

迁移 001 至 011 已经在开发、测试和生产库执行。以后只允许使用新增迁移脚本修改结构，禁止直接修改已经执行过的历史迁移文件；每次执行都必须写入 `schema_migration` 并同步更新本说明。
