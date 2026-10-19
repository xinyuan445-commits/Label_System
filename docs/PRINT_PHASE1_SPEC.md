# 打印接入第一阶段：数据库与接口实现合同

2026-10-19正常模式更新：当前状态与操作以[正常打印、PDF及数据清理](../New_Label_Print_System/docs/LabelSystem正常打印与数据清理.md)为准。888已取消模拟运行，使用本机0.7.1助手；PDF目标可正式首打、补打及回执恢复。左侧仅“标签打印”，四项功能在页内。测试任务与标签及对应888旧三表登记已清理；保留编码占号及流水，清理完成时打印任务0条，后续以实际记录为准，原业务保留。生产008未动，未发送实体打印。

以下既往接入记录中涉及模拟、禁打、0.7.0及清理前数量的段落属于此前阶段；当前部署与操作以以上更新为准。核心业务防重合同继续有效。

2026-10-19后续实测和清理边界见[独立系统联调复核](../New_Label_Print_System/docs/LabelSystem联调复核.md)及[复用清单](PRINT_REUSE_AND_CLEANUP.md)。现有开发HTTP与模拟恢复已分别记录，其他真实SQL矩阵与硬件验收仍未完成（首打/补打/丢回执/三表失败恢复已通过）；本轮没有迁移、新建数据库、删除代码、生产008操作或真实打印。

更新日期：2026-10-19
最新：原New_Label_Print_System独立应用18003已补齐规则管理/当前规则、017正式发布、目标/任务/原码补打和V2回执恢复接口；实际验证规则读取/预览、带图片正式发布和重复发布复用，真实发送与业务写入保持关闭。原54项隔离证据与新版真实SQL全过程验收分开记录。见[接入实施](../New_Label_Print_System/docs/LabelSystem接入实施.md)。

上位方案：[接入方案](PRINT_INTEGRATION_PLAN.md)
验证依据：[验收清单](PRINT_PHASE1_ACCEPTANCE.md)

决策更新（2026-10-19）：D1–D7保持已确认口径，发送成功自动登记、不确认出纸、历史核验补打、数量来自单据、三表只恢复原事件、规则全局单选、旧入口及占用保护。后续已授权隔离迁移及模拟联调；生产008和真实打印仍禁止。

## 1. 必须成立的不变量

| 编号 | 不变量 |
| --- | --- |
| I1 | 已有待打印目标首打、任何补打及恢复的本地 change_qty 均为 0；原业务数量流水不重做 |
| I2 | 每个 outputId 最多一条成功 PRINT/REPRINT；每个计划发送副本一个身份，重交回执不产生新身份 |
| I3 | 每个新目标标签最多一个首次 PRINT_TARGET 事件；事件键不随任务、设备、尝试次数或恢复请求变化 |
| I4 | 正式 Code、序号和 registry 只在显式开始首打时分配，且同事务；预览和待打印查询不分配 |
| I5 | Code 一经分配不回收；没有成功 PRINT 的失败首打继续原任务与原 Code |
| I6 | UNKNOWN 不等于失败，不自动解锁或重投；任何人工结论均有证据和审计 |
| I7 | 首打发送成功但旧库登记未完成时必须保留打印锁；ACTIVE 不代表可用于后续业务，原登记确认完成才解除相关限制 |
| I8 | 补打保留当前业务状态、余额、位置、托盘关系和原 Code；不得创建 PRINT_TARGET |
| I9 | 历史外部已打印事实与本次实际输出分开记录，不伪造本次 PRINT 或增加库存 |
| I10 | 回执、同步响应和重试先校验环境、账套、主体、原任务、逐张身份及有效载荷，不靠前端判断 |

成功依据为驱动接受/打印数据发送成功的回执。缺纸、卡纸及真实出纸不在本阶段判断范围内；不开发物理检测或人工出纸确认流程。回执丢失仍查询原任务以防重复发送。

### 1.1 单据数量与重复登记

- 首打数量来源为单据明细及该明细已经生成的待打印目标，不提供脱离来源单据的额外数量账。
- 按来源明细、目标标签和单位核对：单据应打 100、已首次登记 60、无其他未决任务时，剩余 40；补打任意原标签不增加首次登记量，剩余仍为 40。
- 发送成功后的同一 outputId 只计一次首次登记量；回执重交、三表补写和本地完成恢复均不重复累计。
- 发送结果未知或三表登记未完成的原任务继续占用目标，不把其数量释放给新首打；已有任务恢复使用原 Code 和原输出身份。
- 三表登记未完成时，保留“本地已登记、三表待登记”的事实，不能回退成本次未打印再发送。
- 本阶段目标库存已在原业务提交中落账，首次登记量不通过再次增加库存来实现；库存流水仍为零数量 PRINT/REPRINT。

## 2. 数据库变更（脚本已准备，未安装）

### 2.1 迁移原则

- 017脚本为docs/database/052_label_print_phase1.sql；普通纯测试仅解析SQL，不执行DDL；已在临时库及现有LabelSystem_Dev显式安装，生产和测试未升级。
- 实施时以实际 schema_migration 和最新仓库为基线，通过新增迁移完成；不修改 001–016 历史脚本。
- 新增列默认 NULL，旧业务行保留现状；打印专属约束只针对新协议行。
- 不新增独立打印业务记录表，不复制 ERP 业务表，不新增库存余额副本。
- 建库、索引、回填和发布分开验收；先只读检查历史重复，再创建唯一约束，发现异常不得删除旧业务行凑通过。
- 新技术表统一保留北京时间、创建/修改人及适用的 rowversion；操作者复用 application_user.user_id。

### 2.2 新增技术表

以下表及字段已写入 017 脚本；不表示运行库已经存在。

| 表 | 核心字段与类型 | 约束与用途 |
| --- | --- | --- |
| label_print_template | id bigint；name nvarchar(200)；draft_canvas_json nvarchar(max)；width_mm/height_mm decimal(9,3)；published_version_id bigint NULL；is_enabled bit；row_version | 草稿 JSON 合法、毫米尺寸大于 0 且不大于 1000；更新必须带期望 rowversion |
| label_print_template_version | id bigint；template_id bigint；version_no int；canvas_json nvarchar(max)；width_mm/height_mm；variable_contract_json；content_sha256 binary(32)；renderer_version nvarchar(100)；font_manifest_json | 唯一(template_id,version_no)；发布后只读；操作引用版本，不引用可变草稿 |
| label_print_asset | id bigint；sha256 binary(32)；media_type varchar(100)；storage_relative_path nvarchar(500)；size_bytes bigint；width_px/height_px int；created_by nvarchar(100) | 唯一 sha256；同内容复用资源；内容不可覆盖；路径由服务端生成，不接受任意绝对路径 |
| label_print_template_version_asset | version_id bigint；asset_id bigint | 联合主键；外键禁止删除被发布版本引用的图片 |
| label_print_device | id、公钥及指纹、protocol_version=2、is_enabled、paired_by、owner_user_id、row_version | 管理员配对并指定启用使用人；新发送仅本人/管理员可用，停用后原签名回执仍可恢复，不覆盖原公钥 |
| label_print_rule_binding | singleton_id tinyint；rule_version_id bigint；updated_by nvarchar(100)；updated_at；row_version | 主键 singleton_id 且 CHECK singleton_id=1；外键到现有规则版本，只保存当前打印选择，不复制规则内容 |

补充约束：

- template.published_version_id 必须属于该模板；使用 (id,template_id) 唯一键与组合外键实施，建表后补循环引用。
- 发布前要求画布 stage 与模板宽高一致、变量合同完整、资源可解析、字体可用。
- 已被打印操作或历史归档引用的发布版本和文件禁止物理删除；停用模板不影响旧任务恢复。
- 原始 .blg-template.json 的资产 ID 必须重映射，不能直接使用旧数据库主键。
- 新协议每个输出必须有对应一页且仅一页文件；页数、outputId 集合与业务明细完全一致，空批次、重复 outputId 和未知对象类型不得正式投递。
- 图片默认沿用每张 5 MiB、每模板最多 20 张的边界；对象最多 50 个。资源去重不替代权限。
- 字体固定版本并归档哈希；模板“历史最多 30 版”规则不能清理已用于打印的发布版本。
- 若实际部署需区分设备可见用户，设备授权复用现有功能权限并校验配对归属；不将设备存在视为任意用户均可打印。
- 规则绑定在每套标签数据库中最多一行，表示该系统唯一的当前产品标签打印规则；无绑定时不能新建首打。不按模板、用户或打印机各选不同规则。
- 当前 label_code_rule_version 的 ACTIVE 唯一约束仅以 rule_id 为范围，不满足全局打印单选。绑定记录引用可用规则的启用版本，修改时事务锁定唯一记录并校验 rowversion；历史其他规则仍可用于识别旧标签。

### 2.3 扩展业务操作

business_operation 的 017 脚本增加：

| 列 | 类型 | 约定 |
| --- | --- | --- |
| print_job_id / print_scope | uniqueidentifier / nvarchar(500) NULL | 外部任务身份及不可变环境范围，非空任务过滤唯一 |
| print_printer / print_manifest_json | nvarchar(200) / nvarchar(max) NULL | 原队列及冻结清单；清单须合法 JSON |
| print_phase | varchar(30) NULL | 新打印操作的技术阶段，取值见第 3 节；不扩展现有 status 枚举 |
| print_template_version_id | bigint NULL | 外键到不可变发布版本 |
| print_device_id | uniqueidentifier NULL | 外键到指定助手；首期一个操作绑定一台设备、一个打印机 |
| print_manifest_relative_path | nvarchar(500) NULL | 不可变清单及文件包的位置，与现有 TXT 业务凭证字段分开 |
| print_manifest_sha256 | binary(32) NULL | 冻结清单哈希；路径与哈希必须同时存在或同时为空 |

复用现有 client_request_id 唯一索引、request_payload_hash、row_version、操作者、设备信息、reason 和 ERP 环境快照。operation_type 为 PRINT 或 REPRINT，erp_status=NOT_REQUIRED，不调用 U8 单据新增。

新打印操作由 print_phase 非空标识，历史 PRINT 操作不强行回填成新协议任务。

### 2.4 扩展业务明细：一个发送副本一条明细

business_operation_line 的 017 脚本增加：

| 列 | 类型 | 约定 |
| --- | --- | --- |
| print_output_id | uniqueidentifier NULL | 服务端生成，对外为 outputId；非空过滤唯一索引 |
| print_kind | varchar(10) NULL | FIRST / REPRINT |
| print_origin_line_id | bigint NULL | 首打外键指向原退料/部分移动明细；原明细 generated_label_id 必须等于本行 label_id |
| print_output_phase | varchar(30) NULL | PREPARING、READY、AUTHORIZED、SUBMITTED、UNKNOWN、FAILED_NO_OUTPUT、CONFIRMED、CANCELLED |
| print_snapshot_json | nvarchar(max) NULL | 原 Code、纸面数量、变量、日期及来源的完整冻结快照 |

约束与语义：

- 新协议字段成组出现；一个明细仅绑定一个产品 label_id，pallet_id=NULL。
- 为 selection_source 新增 PRINT_TARGET、PRINT_REPRINT 值；保留原扫描值，调整原 CHECK，不能伪装成 LABEL_SCAN。
- FIRST 的 print_origin_line_id 必填；REPRINT 的来源和原打印依据在不可变清单中保存。
- 每行 requested_label_count=1，printed_label_count 为 0 或 1。补打两份建立两行；汇总为发送成功登记份数，不作为实际出纸检测结果。
- requested_qty 保存本张纸面标签原始数量，不代表本次申请库存变化；inventory_qty_snapshot 保存准备时的当前余额。两者在补打时可以不同。
- `LABEL_SOURCE`明细保存生成标签的目标仓库、货位；拆包明细保存拆包时位置；FIRST/REPRINT明细把准备发送时的当前位置同时写入来源和目标字段。打印不产生位置移动。
- 纸面单位和历史原内容保存在清单；实际 REPRINT 业务明细及库存流水的单位、数量和位置必须使用补打时当前库存状态。补打冻结快照继续保存原首打纸面位置，因此两组位置可以不同。
- 新表中的打印来源、明细和流水必须独立保存物料、批次、仓库及货位，不得把读取`TraceBackGoods`作为补齐本地审计字段的长期前提。
- CONFIRMED 对应 printed_label_count=1 且 line_status=SUCCESS；未确认不得记成功张数。
- 每个首次输出和原业务明细关联不改写原业务金额、数量或 generated_label_id。
- 锁定期间禁止从其他操作改变该标签的库存状态；并发回执仍在标签锁内读取最新 last_flow_id。

### 2.5 扩展成功流水与事件

inventory_flow 的 017 脚本增加 print_output_line_id bigint NULL：

1. 通过 (print_output_line_id,operation_id) 外键指向现有明细组合唯一键。
2. 建立 print_output_line_id 非空的过滤唯一索引，确保一张输出最多一条成功流水。
3. 非空时要求 operation_line_id=print_output_line_id、object_type=PRODUCT_LABEL、operation_type 为 PRINT 或 REPRINT、change_qty=0。
4. 建立 label_id 的过滤唯一索引，范围为 operation_type=PRINT 且 print_output_line_id 非空，防止新协议为同一标签首打两次。
5. 对已有历史 PRINT，准备服务仍须在持有标签锁时检查；新索引不重写历史。旧确认接口不能绕过此检查。
6. 新成功流水保存当前完整库存身份和前后位置，qty_before=qty_after=当前余额，并更新 last_flow_id；不得用原纸面数量覆盖当前余额。

business_operation_event 的 017 脚本增加：

- operation_line_id bigint NULL：用组合外键校验归属操作。
- print_attempt_id uniqueidentifier NULL：同一次投递及其响应的固定身份。
- event_key varchar(200) NULL：全局非空过滤唯一，用于确认、投递授权、人工核对及历史基线事件去重。

事件保留 append-only。保存清单哈希、原签名授权及回执，用于原任务恢复；不保存会话令牌、设备私钥、数据库连接串或配置密钥。授权绑定原任务/设备/文件，仅五分钟内允许首次发送；历史回执恢复不受该发送时限影响。

同 event_key、同有效载荷返回已有结果；同键异载荷返回冲突。不能只依赖业务操作内部 sequence_no 去重。

### 2.6 复用编码与同步结构

不新增规则或 sequence 内容表，复用实际存在的 label_code_rule、label_code_rule_version、label_code_sequence，后者主键为 (rule_version_id,partition_key)。复用 label_code_registry 的 code_hash 唯一约束和 product_label 的非空 Code 唯一约束。全局打印单选通过技术绑定表实现，不把所有历史规则停用或删除。

traceback_sync_task 复用现有结构，017 新增约束与索引：

- event_phase=PRINT_TARGET 时，label_id、operation_id、operation_line_id、Code、稳定 Order/Goods UUID 必须非空。
- label_id 的过滤唯一索引，范围为 event_phase=PRINT_TARGET，确保一个目标只有一个首次兼容登记任务。
- 保留现有 event_key 全局唯一、payload_hash、状态及恢复字段。

product_label 不新增库存余额或“已打印数量”列。首次旧库登记前保持 mirror_source=LOCAL，全部 traceback 镜像字段保持现有 NULL/NOT_APPLICABLE 组合。

特别注意迁移 015 的 CHECK：LOCAL 标签不能直接写 traceback_sync_status=PENDING，TRACEBACK 标签又要求真实正数流水 ID。因此同步待办状态由 traceback_sync_task 和打印操作承载，不能填 0、伪造旧流水 ID 或提前切为 TRACEBACK。收到并验证旧库响应后，在同一本地事务填齐全部真实镜像字段并切为 TRACEBACK/SYNCED。

### 2.7 迁移与发布建议批次

| 批次 | 拟变更 | 发布前检查 |
| --- | --- | --- |
| A | 模板、版本、资产、版本资源引用、设备及唯一打印规则绑定 | 引用、权限、规则单选、并发保存与文件备份；打印写入口仍关闭 |
| B | 操作、逐张明细、事件和流水列及索引；扩展 selection_source CHECK | 历史数据不受损；新接口未放行前不产生新协议行 |
| C | PRINT_TARGET 专属约束及唯一索引 | 现有任务分布和 NULL 核验；Gateway 新建 Code 合同已验证 |
| 发布门禁 | 新客户端、API、Gateway、助手协议匹配 | 功能默认关闭；测试环境全项通过后再评审生产开启 |

存在打印任务后禁止通过删列、删流水或删除旧三表记录回滚。回退时关闭新投递，保留查询、结果确认、对账及同步恢复能力；数据库结构保留。

## 3. 状态、快照及幂等标识

### 3.1 操作阶段

| print_phase | business_operation.status | 业务含义 |
| --- | --- | --- |
| PREPARING / READY / DISPATCH_AUTHORIZED / AWAITING_CONFIRMATION | PENDING | 取号、渲染、授权或等待发送结果自动登记，不等待人工出纸确认 |
| RECONCILIATION_REQUIRED | NEEDS_RECONCILIATION | 发送结果未知、部分发送成功或回执冲突，保留相关标签锁 |
| TRACEBACK_PENDING | NEEDS_RECONCILIATION | 首打成功已有 PRINT，旧库待确认，禁止重投 |
| COMPLETED | SUCCESS | 所有输出已成功确认且应有同步全部完成 |
| FAILED_NO_OUTPUT | FAILED | 有明确未成功发送证据；可恢复原任务，不产生新 Code；状态名不代表纸张检测 |
| CANCELLED | CANCELLED | 授权前取消或确证未成功发送后取消；Code 不回收 |

print_phase和逐张阶段由017新列承载，已在临时库验证并安装到开发库；不写入旧status枚举。AWAITING_CONFIRMATION/SUBMITTED为保留阶段，当前助手直接返回发送结果，不等待人工操作。

部分输出确认后，未知输出仍留核对；剩余输出若明确 READY 或 FAILED_NO_OUTPUT，则保留其可继续的原阶段，不把它们误判为未知。整个操作不得提前 SUCCESS，只释放没有未决输出或同步依赖的标签锁。

界面按业务结果显示“登记未完成”或“完成”。发送成功但三表未确认时不能显示整个任务完成，不能提示用户重新打印；恢复动作自动处理原登记，用户无需增加一次出纸确认或手动同步步骤。

### 3.2 冻结清单

冻结资料分开保存：业务明细 print_snapshot_json 保存原 Code/SN、数量、单位、日期、位置、来源/规则版本及变量；操作保存 print_scope、模板、设备和打印机。ZIP 中 manifest.json 只含 version=2、scope、jobId、deviceId、printer、毫米尺寸、模板版本/摘要、字体清单和逐页 outputId/PNG/PDF 摘要；最终数据库清单额外保存 ZIP 路径/摘要与 manifestPath。恢复依赖这组数据库及文件归档，不把 ZIP 清单当作独立库存账本。

- 日期变量以 BusinessClock 的北京时间冻结；跨日恢复和补打不能重算。
- 首打数量来自已经落账的目标标签；不得相信客户端传入数量、Code、物料、位置或 ERP 账套。
- 原样补打优先使用原快照及文件；改变模板属于显式新版本输出，首期不开放静默替换。
- 历史原文件缺失时只能在原始身份、必填内容及历史证据均可核验后重建，并记录重建来源；不能把当前余额或当前位置默认为原纸面内容。
- 带旧 Code 的普通预览不取号；无 Code 首打预览使用明确非正式示例码，不可拿来登记或投递。

### 3.3 标识与唯一边界

| 标识 | 生成与范围 | 重试要求 |
| --- | --- | --- |
| clientRequestId | 客户端为一次显式操作生成 UUID，服务端映射为现有唯一 client_request_id | 同用户同规范化载荷返回原任务；异载荷 409；其他用户不得探测任务 |
| jobId | 服务端生成，稳定关联一个打印操作 | 恢复不创建替代 job |
| outputId | 服务端为每份纸张生成 UUID，绑定一条明细 | 查询、确认和重交一直沿用；不由助手重新生成 |
| attemptId | 服务端为一次投递授权生成 UUID | 网络重试只查原 attempt；新尝试必须已有未输出证据 |
| eventKey | 事件类型、环境身份及稳定对象标识规范化后 SHA256，再加短前缀，长度不超过 200 | 不以时间戳或重试次数改变语义事件身份 |
| firstRegistrationKey | 对 server namespace、环境、账套、年度、labelId 和 FIRST_REGISTRATION_V1 生成稳定键 | 独立于 jobId、attemptId、设备和确认请求；每标签只一个 |

首次三表事件的 Order/Goods UUID 由 firstRegistrationKey 和固定角色稳定派生，算法版本固定，碰撞及与旧值冲突必须拒绝。

serverInstanceId / server namespace 指持久的业务数据命名空间，不是进程 ID、机器名或每次部署重新生成的随机值。迁机、升级及原环境恢复必须保留；复制到另一隔离环境时显式分配新命名空间并禁止访问原生产账套。

请求和业务载荷的哈希必须使用版本化规范化算法：属性顺序固定、数量按统一十进制字符串、集合按 outputId 排序、编码按现有规则处理。网络请求号、到达时间和重试计数不参与业务身份；确认人变化单独审计，不能把同一张成功输出变成新库存事件。

### 3.4 编码分配事务

同一 Serializable 本地事务内：锁业务操作和标签 → 校验原业务成功及 generated_label_id → 校验未决打印与历史首打 → 读取并锁定唯一当前打印规则绑定 → 固定该规则版本及 label_code_sequence 分区 → 分配序号 → 生成并校验正式 Code → 写唯一 registry → 回填同一 product_label → 建打印明细和准备事件 → 提交。

Code 结构、数量格式、SN 长度和流水设置读取现有选中规则，不在打印模块另写固定五段码或固定 22 位 SN。当前产品表和 Gateway 的 100 字符上限、旧端字段解析以及载体编码能力仍须在规则投入打印前校验；不兼容时明确报告并评审适配，不能静默篡改规则。

可以保存多条规则，但同一系统同一时刻仅一条规则用于新首打。切换规则只影响之后的新任务，已分配 Code 的任务、失败重试及补打使用原版本和原 Code。具体规则内容、流水分区及跨版本序号唯一性尚未读取运行数据库核验，不能自行按年度或日期重置，也不能认为选择单一规则即可解决跨版本碰撞。

Gateway 查询旧 Code 是否已存在必须在投递前完成，失败关闭；本地事务不包跨服务 HTTP 调用。准备阶段可先持久分配 Code 并锁标签，再做旧库只读检查；发现旧码冲突即进入核对，不投递、不自动换 Code。

预检不能消除另一旧系统随后生成同码的风险。正式旧库登记仍须原子检查；跨系统序号空间未确定前，不开放新码生产首打。

## 4. Business API 合同（开发9891已部署，生产未更新）

### 4.1 通用规则

- 路径统一在 /api/v1 下，沿用现有 Bearer、API envelope、请求关联和错误结构。
- 功能码 print.view、print.first、print.reprint、print.confirm、print.reconcile、print.template.manage、print.device.manage 已接入全局启用及 ALLOW/DENY；017 默认全部关闭。
- 当前功能权限没有完整仓库权限模型；不能宣称已有仓库级隔离。首期按现有业务可见性、原操作归属与管理权限校验，不暗中扩大范围。
- 恢复、下载、回执、人工核对和同步重试同样校验用户启用状态、操作归属和相应功能权限。
- 新首打及兼容登记重试必须经过 Business API 写门禁和 Gateway 账套门禁。仅补打不调用 Gateway 写入，但仍有打印功能及环境门禁。
- 数量全程 .NET decimal(18,6)，冻结纸面数量用十进制字符串；浏览器首打请求不传数量。两层 .NET 内部 quantity 为精确 JSON 十进制数，不经 JS 计算。规则损失数量精度时拒绝。
- 首期一任务最多 100 个输出，配合当前 Gateway 批量快照上限；打印包不超过压缩 64 MiB、展开 128 MiB。超限在授权前拒绝，不静默截断。

### 4.2 接口列表

| 方法与路径 | 输入和返回要点 | 副作用及幂等 |
| --- | --- | --- |
| GET /print/capabilities | enabled、allowNewDispatch、protocol、服务端 SPKI 公钥、requiredMigration、上限及 DRIVER_ACCEPTED | 不分配 Code，复用登录 |
| GET /print/pending-labels | 最前 100 项：labelId、originLineId、数量、位置、十六进制 rowVersion、恢复任务 | 只读；分页/跨页搜索未实现 |
| POST /print/labels/resolve | 完整 Code；返回 label{labelId,rowVersion,code,quantity,status} 和 inventory | 实时旧三表快照及显式本地镜像，不创建 PRINT 或旧 +Q |
| POST /print/previews | templateVersionId、variables；返回示例 PDF，条码固定 PREVIEW | 不取号、不建任务；不作为发送回执 |
| POST /print/jobs/first | clientRequestId、templateVersionId、deviceId、printer、targets[labelId,originLineId,expectedRowVersion] | 200 PrintJob；同请求返回原任务；准备后校验旧库并严格渲染 |
| POST /print/jobs/reprint | clientRequestId、labelId、expectedRowVersion、copies、reason、confirmInactive | 200 PrintJob；服务端取原快照及原打印机，多份不同 outputId，不取新 Code/建同步任务 |
| GET /print/jobs | 最近 100 项，本人/管理员可见，绑定 scope | 跨浏览器恢复；无分页参数，不触发发送 |
| GET /print/jobs/{jobId} | PrintJob：phase、outputs、flowId、原设备、rowVersion、manifest、最后 attemptId/authorization | 恢复从原任务开始 |
| GET /print/jobs/{jobId}/bundle | 冻结 ZIP；/pages/{outputId} 下载原 PDF | 校验会话/归属/scope/文件哈希，下载不计成功 |
| POST /print/jobs/{jobId}/dispatch-authorizations | clientRequestId、expectedRowVersion、outputIds；设备及包哈希从服务端原任务取得 | 返回 Base64 payload/signature；attemptId 在签名载荷中，先持久授权再调用助手 |
| POST /print/jobs/{jobId}/receipts | {payload,signature}；载荷绑定原授权和 submitted/unknown/not_submitted | 逐份成功流水幂等，首打同事务 outbox，正常立即自动同步；未知不计成功 |
| POST /print/jobs/{jobId}/confirmations | 与 receipts 相同的原设备签名回执 | receipts 的恢复别名，不是人工出纸确认 |
| POST /print/jobs/{jobId}/reconciliations | clientRequestId、expectedRowVersion、reason、可选 receipt{payload,signature} | 管理员及 print.reconcile；记录核对，只有原设备有效签名可推进状态，不支持无签名强制成功/未发送 |
| POST /print/jobs/{jobId}/retry-sync | 空对象；按原任务查询未完成 PRINT_TARGET | Gateway 只处理原稳定事件；本地完成幂等，不调用助手/取号/新建 PRINT |
| POST /print/jobs/{jobId}/cancel | clientRequestId、expectedRowVersion | 只接受 PREPARING/READY/FAILED_NO_OUTPUT/CANCELLED 且没有成功流水，Code 保留 |
| POST /print/jobs/{jobId}/resume | clientRequestId、expectedRowVersion | 仅明确未输出的 FAILED_NO_OUTPUT/CANCELLED 原任务；重取标签锁，复用原 Code、清单及 outputId |

授权新 attempt 使用同一个 dispatch-authorizations 接口，但必须先通过核对将受影响输出置 FAILED_NO_OUTPUT。AUTHORIZED 后查不到本地账本不足以证明未出纸。

幂等接口先校验当前调用者权限，再查请求键/成功输出；确认重复且同内容时先返回原结果，不因原提交已经推进 rowversion 而错误拒绝。新动作才校验 expectedRowVersion。重复确认不重新投递或重新计算业务数量。

重新发起 first 时若同标签已有失败或取消的首打任务，返回原任务恢复入口，不能创建替代任务。resume 失败时不释放别人的锁；若标签已被其他业务合法改变则转核对。首期不支持跨设备重新投递，换电脑可以查询原任务，原设备无法核验时保留 UNKNOWN；换机接续另行设计。

模板接口已实现 GET/POST /print/templates、GET/PUT /{id}、POST /{id}/publish；资产 POST /print/assets 和 GET /{id}；条码示例 GET/POST /print/barcode-preview。旧 blg-template/v1 包的导入/导出在电脑端组合资源 API 完成，不存在独立服务器 import/export/versions 路由。草稿 CAS，发布版本及图片不可变；模板历史列表/跨版本恢复接口未实现。

当前打印规则由 GET /print/code-rule 查询、管理员 PUT /print/code-rule 用 ruleVersionId/expectedRowVersion 原子切换单行绑定。新首打不能任意指定规则，旧任务/补打不因切换而改码。

设备 GET/POST /print/devices，管理员 PUT /print/devices/{id} 以 expectedRowVersion/Enabled 停用或启用。登记公钥和使用人不可静默替换；密钥轮换和迁机未实现。原回执按原公钥验证。

### 4.3 核心错误与响应

| HTTP | 错误码示例 | 含义 |
| --- | --- | --- |
| 400 / 422 | PRINT_TARGET_INVALID、PRINT_RENDER_INVALID、PRINT_QUANTITY_UNREPRESENTABLE | 输入、原业务或渲染不满足合同 |
| 401 / 403 | SESSION_REQUIRED、PRINT_FORBIDDEN、PRINT_WRITES_BLOCKED | 会话、权限或写门禁 |
| 404 | PRINT_JOB_NOT_FOUND | 不存在或调用者不可见，避免泄露其他用户任务 |
| 409 | PRINT_REQUEST_CONFLICT、PRINT_TARGET_LOCKED、PRINT_RESULT_UNKNOWN、PRINT_RECEIPT_CONFLICT | 同键异内容、锁冲突或输出不可判定 |
| 409 | PRINT_ALREADY_CONFIRMED、PRINT_SOURCE_NOT_READY、PRINT_LEGACY_EVIDENCE_REQUIRED | 应返回可恢复任务/原结果，或等待业务/证据 |
| 409 | PRINT_PROTOCOL_MISMATCH、PRINT_CODE_CONFLICT | 协议或 Code 冲突，禁止继续投递 |
| 503 | PRINT_GATEWAY_UNAVAILABLE、PRINT_ARTIFACT_UNAVAILABLE | 外部依赖或归档缺失；不能转换为新任务 |

同内容重复回执 200 返回原 PrintJob；成功流水在 outputs[].flowId。响应另含 localRecorded、canRedispatch，phase=TRACEBACK_PENDING 时不可重投。不要以旧设计的 outputResults/tracebackStatus 作为已实现字段。

## 5. 助手协议与发送结果登记

首期已实现 driver-labels-v2：GET /status、/printers；POST /v2/print-labels；GET /v2/label-jobs/{jobId}/{attemptId}?scope=...。V2 模式禁用旧无签名打印端点、旧服务轮询和抓取；新工作台不降级 v1。

投递包包含不可变清单、页文件、attemptId、逐张 outputId、指定设备、环境身份与服务端签名。每次 attempt 只包含尚未成功确认且允许投递的输出。

设备使用配对公钥校验服务器授权，并以设备私钥签名回执；回执绑定 serverInstanceId、环境、账套、jobId、attemptId、manifestHash、设备公钥指纹、打印机及每张 outputId。签名只证明指定助手报告的事实，不证明纸张真实输出。

助手必须：

1. 仅监听回环地址，限制允许 Origin，校验协议、设备、环境、签名、时效、大小和每页哈希。
2. 以服务器/环境/设备/jobId/attemptId 隔离本地账本，跨进程互斥；在任何驱动调用前持久化投递意图。
3. 同 attempt 同包重交只返回已有结果；异包拒绝。已处理、处理中及未知 attempt 不调用驱动第二次。
4. 记录未提交、发送成功、未知和可取得的 spoolJobId；整包调用明确成功时可以按清单登记全部副本，无需改成物理逐张检测。
5. 重启恢复账本；损坏、遗失、设备换机或 spool 状态不可判定时报告 UNKNOWN，不伪造成功。
6. 发送调用报错且无法判断哪些数据成功发送时，保留未知并查询原任务；不能把失败调用的总页数直接登记成功。

按用户已确认 D1，发送成功回执通过验证即自动记成功流水并触发写表，不再要求人工选择“已实际输出”。物理缺纸、卡纸或是否真正出纸均不阻止已发送成功任务登记；若后来需要重新输出，走原 Code 补打。发送成功回执丢失时先查询原任务，恢复登记不得重新发送。

浏览器原生打印仍仅用于预览/导出。2026-10-19按用户要求，精确目标Microsoft Print to PDF已接入正式V2助手：保存文件并取得持久签名回执后按首打/补打合同登记。正式PDF输出会取号和登记，单纯预览不取号；不要求人工确认出纸。

## 6. 成功确认事务与旧三表合同

### 6.1 本地确认事务

在 Serializable 事务中按固定次序锁操作、目标标签、逐张明细，再锁相关成功流水和事件：

1. 验证操作者权限、期望版本、标签锁归属、attempt 和发送成功回执；正常流程由 receipts 自动进入本事务，不等待人工出纸确认。
2. 已有 print_output_line_id 成功流水且内容相同：返回原 flowId，不增加张数。
3. 同 outputId 异载荷：409 并记核对事件；不能覆盖已有成功，不能再次累计单据首打数量。
4. 读取当前 last_flow_id，复制当前完整库存身份和位置，插入对应 PRINT/REPRINT 零数量流水并更新指针。
5. 本行 printed_label_count 从 0 置 1，不能用无条件 +1；逐张阶段置 CONFIRMED。
6. FIRST：标签与 registry 置 ACTIVE，保留打印锁；以 firstRegistrationKey 插入唯一 PRINT_TARGET 任务，冻结 Quantity=目标原始 Q。
7. REPRINT：不改变原状态、余额或托盘关系，不建同步任务。
8. 插入唯一确认事件，原子提交。失败整笔回滚，后续用原输出身份重交。

首打发送成功后的恢复只处理原发送结果的登记及同步。即使标签仍被锁、UI 未收到响应，也不能再以“未完成”创建新首次输出；正常成功回执自动触发三表写入，不等待用户手动同步。

### 6.2 Gateway 首次登记接口（开发回环9881已部署，写门禁关闭）

已新增POST /api/v1/traceback/labels/initial-registrations，单次一个稳定初始事件；/validate只读检查。隔离影子库已调用，独立开发9881已部署但禁写，本轮真实888未调用登记。旧sync-events保留来源事件用途，不能用于首次建立不存在的Code。

请求字段：

| 字段组 | 要求 |
| --- | --- |
| 身份 | contractVersion=1、eventKey、payloadHash、稳定 orderUuid/goodsUuid；PRINT_TARGET 由此路由固定，不接收任意角色 |
| 目标账套 | 与服务器配置绑定的环境/账套/年度；数据库只能由服务端绑定，不能让调用方任意指定 |
| 初始断言 | expectedAbsent=true、expectedLatestFlowId=0、expectedBalance="0"；只适用于新接口 |
| 标签 | 完整 Code、SN、正数 Quantity、单位、物料、批次、日期及库存属性 |
| 位置与来源 | 目标仓库货位、原操作/明细/ERP 关联、首打操作与首打确认身份 |

处理顺序：

1. 全量执行当前生产开关、精确账套/数据库白名单和基础档案校验。
2. Serializable 旧库事务；按固定次序取得事件、完整 Code 与 Order 的互斥/谓词锁。
3. 先查稳定事件：三表及载荷完全匹配则返回原 flowId，不能再次写 +Q。即使该 Code 后续已有其他业务流水，也不能把同一首次事件再做一次。
4. 事件不存在时，按完整 Code 确认没有任何旧流水；已有其他来源、残缺关联、重复 UUID 或内容冲突一律拒绝。
5. 用已验证完整快照建立 Order、Goods 和一条 +Q 的 OnlyCode；保留旧字段兼容及来源标识，不能调用“读取已有 Code 来源 Goods”的分支。
6. 同事务提交三表。响应返回 eventKey、payloadHash、alreadyApplied、初始 flowId 及当前权威快照。

有效载荷比较必须覆盖物料、单位、批次、位置、数量、Code、来源和稳定关联，不能像旧打印仓库仅用三表 COUNT 相等判定幂等。摘要必须由 Gateway 根据规范化业务字段重新计算，不能直接信任请求中的 hash。

旧三表不在本阶段新增业务表或改历史行；是否需要索引另行只读评估，不能把应用锁等同于所有旧客户端共同遵守的跨系统协议。

### 6.3 本地同步确认事务

收到响应后验证稳定事件及初始流水，不用当前余额反推首次是否登记。随后在一个本地事务：

- 将原 task 标 COMPLETED。
- 验证当前权威快照与预期身份/位置/余额；填齐真实 TraceBack 镜像字段。
- 不再插入 PRINT，不增加 current_qty，不再调用通用镜像流程产生重复入账。
- 所有相关输出及依赖完成后操作置 SUCCESS，释放本操作可释放的标签锁。

上述处理失败或响应未知时，保留原任务及相关锁，界面为“登记未完成”。恢复仅提交/查询同一个首次登记事件并补齐本地结果，禁止调用打印助手、另取 Code、重复写 PRINT 或再次占用单据首打数量。

旧库已提交但响应丢失时，task 标 UNKNOWN；恢复重发同事件或查询同事件，拿到已应用结果后完成本地确认。

若旧客户端在旧库提交与本地确认之间已消费或移动该标签，则进入核对，不能用初始 Q 覆盖旧库最新余额，也不能再写 +Q。新系统锁不能阻止不遵循本协议的旧客户端，不能宣称两个数据库原子可见。

## 7. 补打与历史标签特别规则

- 本地有成功 PRINT：使用原快照及原 Code；多个 REPRINT 行分别确认，任何重复回执均不再增加流水条数。
- PENDING_PRINT 且没有成功 PRINT：返回原首打任务；不能进入补打或获得新 Code。
- 已有 PRINT 但首次同步未结束：只能恢复原同步，不允许用补打绕开锁。
- 非 ACTIVE 补打必须二次确认当前状态；输出成功后仍保留原状态。
- 旧三表标签缺本地 PRINT：先写唯一 LEGACY_FIRST_PRINT_BASELINE 事件，保存完整 Code、原始标签数量、历史初始关联/哈希及核验者。不新增虚构的历史 PRINT，不增加实际输出张数，不新增 +Q。
- 历史基线事件挂在稳定的历史核验操作上，语义唯一键绑定业务命名空间、账套和 labelId，独立于之后的补打任务及次数；不能每次补打重复创建基线。
- 原始数量允许从已验证的历史编码或初始打印记录恢复，不能用现有余额替代；SN 相同、Code 不同必须拒绝唯一匹配。
- D2 的历史核验后补打原则已获用户确认；历史内容不完整或证据有冲突时仍不自动放行，不把原则确认当成具体历史数据已核验。

## 8. 并发、恢复与旧入口保护

- 锁顺序统一，标签按 ID 升序；事务锁只覆盖短数据库操作，不包渲染、用户确认或 HTTP。
- product_label.locked_operation_id 保存持久业务锁；进程崩溃、API 重启和浏览器退出不代表锁可释放。
- 后台通过未完成操作和 task 扫描恢复；同时运行的恢复器需通过事务领取与 rowversion 校验，不能双重确认。
- 回执 UNKNOWN 的锁不按普通 TTL 自动释放；心跳超时只触发告警/核对。
- 同一标签可在一个补打操作内有多行，不能被另一个任务并发占用；释放前检查该标签所有未决输出和同步依赖。
- 当前 issued-labels/confirm 必须识别新打印协议对象并拒绝旁路确认；管理员也不能通过旧入口跳过新协议。
- 旧入口不能在缺少发送成功回执、三表登记未完成或打印锁仍有依赖时把新任务直接标成完成、激活或解锁。历史任务的受控恢复路径不能变成新任务的后门。
- 现有扫码、镜像、材料出库、调拨、货位调整、拆分、合并、装卸托和手工数量调整都必须检查打印锁；只修改打印页面不算完成。
- 统一 TraceBackSyncService.FinalizeOperationAsync 与其他路径的组锁释放检查；任一组内未决操作不能被其他操作提前解锁。A 已完成不能顺带释放未完成 B 的占用，逐标签释放也必须先检查该标签全部依赖；用户不需手工加锁或解锁。
- 清理浏览器恢复指针、下载文件或助手 UI 列表不改变服务端未决任务。

## 9. 实施状态及剩余前置条件

D1–D7均已确认，54项重点隔离断言通过；现有开发库已安装017并部署9891/888测试站点，模板/预览和888读取通过、发送禁用。生产未更新，无真实打印。真实规则/模板/旧数据、全部SQL故障点及硬件仍未完整验证，见开发部署及隔离报告，不将现场或完整清单标为通过。

上线前还需取得实际业务模板导出、引用图片及字体、原打印快照样本和目标现场硬件验证结果。资料核对不得包含或公开数据库密码、用户密码、设备私钥及完整敏感配置。


## 2026-10-19 正常PDF输出合同补充（覆盖此前仅导出限制）

精确打印目标Microsoft Print to PDF走017正式发送合同。本机助手保存校验过的真实PDF、持久化submitted回执后，首打记录零数量流水并恢复原旧三表登记；补打仍为零数量且不写三表正向数量。PDF成功只表示文件已保存。单纯预览仍不分配Code、不登记首打。补打可改用当前有效设备与目标，不能更换原Code或冻结标签内容。清理测试数据不回退正式编码流水，作废编码保持唯一登记；本轮结果见正常打印文档。
