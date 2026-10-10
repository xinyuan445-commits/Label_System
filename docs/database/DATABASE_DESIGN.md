# 标签管理系统数据库设计说明

状态：评审稿，尚未冻结，不得直接用于生产环境
数据库：Microsoft SQL Server
默认开发库：`LabelSystem_Dev`
更新时间：2026-10-11

## 1. 文件与执行顺序

| 顺序 | 文件 | 用途 |
| --- | --- | --- |
| 1 | `000_create_database.sql` | 创建独立业务数据库并设置基础数据库选项 |
| 2 | `001_label_business_schema_draft.sql` | 创建业务表、约束、索引及 SQL Server 扩展说明 |

建库脚本默认只创建 `LabelSystem_Dev`。执行前可在脚本顶部修改数据库名、环境和恢复模式。

建议环境命名：

| 环境 | 数据库名 | 恢复模式 | 用途 |
| --- | --- | --- | --- |
| development | `LabelSystem_Dev` | SIMPLE | 本地开发 |
| test | `LabelSystem_Test` | SIMPLE | PDA、API 和 U8 测试账套联调 |
| production | `LabelSystem_Prod` | FULL | 正式业务；必须配置完整备份和日志备份 |

三个环境必须使用不同数据库。应用连接账号使用最小权限专用账号，不使用 `sa`。数据库密码、U8 密码、令牌和连接字符串不进入业务表、代码仓库或事件 JSON。

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

产品料号与托盘料号由业务保证互斥，因此不增加产品与托盘共用的标签编码登记表，数据库仍为 9 张业务表。发码服务必须避免产品标签码与托盘标签码重复；扫码意外同时命中两类对象时，应用按数据异常拒绝处理并记录事件。

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

当前已确定的托盘标签使用配置：

| 作用域 | 配置键 | 名称 | 默认值 |
| --- | --- | --- | --- |
| GLOBAL | `allow_direct_use_of_palletized_label` | 托盘内标签允许单独使用 | `false` |
| DOCUMENT | `include_pallet_in_document_by_default` | 做单时默认包含托盘自身 | `true` |

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
| U8 单据追溯 | 环境、账套、年度、单据类型、单据号组合索引 |
| 标签打印历史 | `inventory_flow(label_id, object_sequence_no)` 中的 PRINT、REPRINT 流水 |
| 打印失败和重试过程 | `business_operation_event(operation_id, sequence_no)` |
| 待处理或异常操作 | `business_operation(status, created_at)` |
| ERP 库存对账 | `inventory_flow(to_warehouse_code, material_code, to_location_code, batch_no)` |
| 已启用功能列表 | `system_function(is_enabled, group_sort, function_sort)` |

标签成功打印总次数为该标签 `PRINT` 与 `REPRINT` 流水条数之和，补打次数只统计 `REPRINT`。同一请求把同一个标签输出多张时，必须按成功张数写入多条 `REPRINT` 流水，且数量应与对应业务明细的 `printed_label_count` 一致。打印任务数使用不同的 `operation_id` 统计，不能代替标签实际打印张数。

位置类业务在创建 U8 单据前，从最新流水读取来源仓库和货位并保存到业务明细；用户选择的目的位置保存到目标字段。U8 写入及所需审核成功后，才允许在同一业务数据库事务中插入新流水并更新 `last_flow_id`。已有 U8 单据必须校验其来源位置与最新流水一致，不一致时拒绝提交，不得静默覆盖 U8 单据。

托盘整体调拨或货位调整时，应用必须在同一操作中为托盘自身及全部当前在托有效产品标签分别写入位置流水。只移动部分产品时必须先卸托，不能让实体托盘与其在托产品形成不同当前位置。

材料出库表头不由用户输入仓库。提交时从标签或托盘最新流水读取实际仓库，按仓库拆成多条 `business_operation` 和多张 U8 材料出库单。拆出的操作共用 `submission_group_id`，但各自保存独立 `client_request_id`、U8 单号、状态和操作凭证；部分成功时只对成功仓库组生成库存流水。

## 7. 尚待确认

- 新产品标签码的正式编码规则和生成服务。
- 各单据哪些库存自由项需要提升为普通检索列。
- 单位换算功能及物料单位换算来源；当前所有数量必须使用同一标签单位。
- 条码打印阶段“打印尾数”的页面交互、权限和默认开关。
- 正式数据库容量、数据文件增长策略、备份保留期限和恢复演练频率。
- 用户、角色、用户功能权限和管理员异常解锁表，待认证权限方案确定后补充；用户功能权限必须引用 `system_function.function_code`。
- 表结构评审通过后的版本化迁移工具及正式迁移编号。

## 8. 变更要求

业务规则调整时，应同时更新：

1. 根目录 `BUSINESS_LOGIC.md`。
2. 本数据库说明。
3. 对应的 SQL 迁移脚本。
4. API 请求、响应及错误码合同。

评审稿通过前不执行生产建表；正式上线后只允许使用新增迁移脚本修改结构，禁止直接修改已经执行过的历史迁移文件。
