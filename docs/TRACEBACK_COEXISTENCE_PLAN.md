# TraceBack 三表并行运行与迁移方案

更新日期：2026-10-15

## 1. 目标与范围

材料出库单、调拨单和货位调整单在新标签系统试运行期间，继续读取并写入 U8 账套中的旧追溯三表：

- `dbo.TraceBackOrder`
- `dbo.TraceBackGoods`
- `dbo.TraceBackOnlyCode`

过渡期内，旧三表是已有产品标签的实时来源；Label System 负责业务操作、并发锁、审计、待打印标签、打印规则以及本地查询镜像。新系统稳定并完成数据核对后，通过配置开关停止旧三表写入，再把产品标签权威来源切换到 Label System。

本阶段不处理托盘兼容。托盘、装托关系及托盘库存继续只由 Label System 管理，不读写旧三表。

### 1.1 第一阶段实施状态

截至 2026-10-15，第一阶段只读部分已部署：

- Gateway `0.6.0` 增加单张与批量快照查询，批量上限 100。
- Business API `0.9.0` 的产品标签扫码改为实时快照并建立本地镜像；托盘继续读本地表。
- 材料出库、调拨和货位调整在业务建档前复核一次，在调用 U8 前再复核一次。
- 迁移 015 已在 `LabelSystem_Dev`、`LabelSystem_Test`、`LabelSystem_Prod` 执行并校验脚本哈希。
- 本阶段没有旧三表写接口，不创建同步任务，不修改 888 或生产账套索引。

## 2. 888 账套实际数据核查

2026-10-15 对 `UFDATA_888_2021` 执行只读核查。最新可用旧追溯业务数据截至 2026-09-27；三表当时总量约为：

| 表 | 行数 |
| --- | ---: |
| `TraceBackOrder` | 29,555 |
| `TraceBackGoods` | 50,123 |
| `TraceBackOnlyCode` | 52,398 |

抽查结果：

- 材料出库 `CLCKD20263345` 写入一条 `-810`；`CLCKD20263332` 写入 `-1`；`CLCKD20263331` 两条标签分别写入 `-30`、`-10`。
- 调拨 `DBD26093200` 为全量移动，原 Code 写 `0` 并把最新 Goods 更新为目标位置。
- 调拨 `DBD26093199` 从原标签余额 420 中移动 380，原 Code 写 `-380` 并保留余额 40；打印平台随后在目标位置生成新 Code 并写 `+380`。
- 调拨 `DBD26093170` 对两张标签分别部分移动 45 和 3，打印平台随后生成对应目标标签。
- 货位调整 `0000000534` 三条标签均写 `0` 并更新到目标货位；`0000000533` 同样为全量位置变化。
- 货位调整 `1000002534` 同时存在部分移动的负流水和全量移动的零流水；部分移动数量在打印后以新 Code 正流水进入目标货位。

旧数据中还发现负余额、Goods 关联不完整，以及个别零数量位置流水后又生成新标签等异常。因此新系统不能无条件复制历史异常行为，必须把异常标签隔离为待核对。

## 3. 三表读取口径

### 3.1 精确扫码查询

产品标签扫码按完整 `traceBackCode` 查询：

1. 在 `TraceBackOnlyCode` 中读取该 Code 的全部流水。
2. `SUM(TRY_CONVERT(decimal(28,6), [sum]))` 为实时余额。
3. 最大 `id` 为最新追溯流水版本。
4. 通过最新流水的 `pdaGoodsUuid` 关联 `TraceBackGoods`，读取料号、名称、批号、单位、库存属性、仓库和货位。
5. 通过 `pdaOrderUuid` 关联 `TraceBackOrder`，读取来源业务、PDA 单号和 ERP 单号。
6. Code 中的账套、料号、批号、打印数量和 SN 只作为身份校验及缺失字段回退，当前余额不得使用 Code 内打印数量代替。

### 3.2 有效性判断

- 余额大于 0 才能作为可用产品标签。
- 余额等于 0 视为已用完。
- 余额小于 0、最新 Goods 缺失或不唯一、JSON 无法解析、Code 与 Goods 料号/批号冲突时，返回“标签数据待核对”，不得继续制单。
- 当前仓库货位取最新流水关联的 Goods。部分移动的负流水必须保留来源位置；全量调拨或货位调整的零流水必须保存目标位置。

### 3.3 本地镜像

`product_label` 和 `inventory_flow` 在过渡期是业务镜像，不是脱离旧三表的独立事实来源：

- 首次扫描旧标签时按 TraceBack 快照建立或刷新本地镜像。
- 本地保存最后同步的 `TraceBackOnlyCode.id`、余额、身份摘要和同步时间。
- 扫码确认及 U8 写入前必须再次查询旧表，比较最新流水 ID、余额和位置。
- 本地镜像用于业务锁、草稿、操作归档、待打印标签和新系统审计。
- 产品标签列表可用增量镜像查询；单张扫码和提交前复核必须读取旧表实时数据。

### 3.4 读取字段合同与异常分类

Gateway 启动查询时固定检查：`TraceBackOnlyCode(id, traceBackCode, sum, pdaGoodsUuid, pdaOrderUuid)`、`TraceBackGoods(pdaGoodsUuid)`、`TraceBackOrder(pdaOrderUuid)`。Goods 和 Order 的业务字段可以来自同名列或 JSON，但只映射物料、批次、单位、仓库、货位、来源业务、PDA 单号和 ERP 单号等白名单逻辑字段；原始 JSON 不向 Gateway 外暴露。

稳定异常编码包括：`TRACEBACK_QUANTITY_INVALID`、`TRACEBACK_NEGATIVE_BALANCE`、`TRACEBACK_GOODS_UUID_MISSING`、`TRACEBACK_GOODS_MISSING`、`TRACEBACK_GOODS_DUPLICATED`、`TRACEBACK_GOODS_JSON_INVALID`、`TRACEBACK_ORDER_JSON_INVALID`、`TRACEBACK_MATERIAL_MISSING`、`TRACEBACK_UNIT_MISSING`、`TRACEBACK_CODE_MATERIAL_CONFLICT`、`TRACEBACK_CODE_BATCH_CONFLICT`。任一异常都使状态变为 `RECONCILIATION_REQUIRED`。Order UUID 缺失、无匹配或不唯一时不猜测来源字段，但不单独改变库存身份和余额状态。

只读路由为：

- `GET /api/v1/traceback/labels/{code}`
- `POST /api/v1/traceback/labels/snapshots`，请求为 `{"codes":[...]}`

## 4. 三类单据写入规则

### 4.1 材料出库

- 蓝字领料：写一条 `TraceBackOrder`；每个原标签写 `TraceBackGoods` 和负数量 `TraceBackOnlyCode` 流水。
- 红字退料复用原标签：原 Code 追加正数量；Goods 保存用户最终选择的退回仓库和货位。
- 无标签红字退料：U8 成功后创建本地无 Code 的 `PENDING_PRINT` 标签；可以先登记兼容同步任务，但不得写空 Code。正式打印生成 Code 时写入追溯三表正流水。
- 审核和弃审不重复改变追溯流水。

### 4.2 调拨单

- 全量移动：原 Code 追加 `sum=0`；Goods 中保存调出、调入信息，并把库存位置保存为调入后的仓库货位。
- 部分移动：原 Code 追加负的移动数量，Goods 保持来源仓库货位；原标签余额留在来源位置。
- 目标部分在打印前只存在于本地 `PENDING_PRINT` 标签。正式打印生成新 Code 后，以正数量写入目标仓库货位。
- 一张本地草稿拆成多张 U8 调拨单时，每个 ERP 拆组分别建立 TraceBack Order 关联。

### 4.3 货位调整单

- 全量移动：原 Code 追加 `sum=0`；Goods 保存调整前后货位，库存位置保存目标货位。
- 部分移动：原 Code 追加负的移动数量并保持来源货位；打印新 Code 时把移动数量以正流水写入目标货位。
- 同一仓库内调整，不改变仓库编码。

## 5. 一致性、幂等和失败恢复

旧三表在过渡期是标签实时来源，因此旧表同步失败不能只作为普通警告：

1. U8 单据新增并回查确认。
2. Label System 在本地事务中保存 ERP 结果并创建不可丢失的 TraceBack 同步任务。
3. Gateway 使用一个 SQL 事务写入本次需要的 Order、Goods 和 Code 流水。
4. 旧表成功后，Business API 提交或刷新本地标签镜像并释放业务锁。
5. 旧表结果失败或未知时，操作显示“ERP 已生成，标签同步待恢复”，保留相关标签锁；禁止重新生成 U8 单据。
6. 恢复必须沿用原 ERP 结果和确定性 UUID，只补写缺失的 TraceBack 数据。

每个同步事件使用稳定事件键，建议由账套、业务操作 ID、操作明细 ID、事件角色和 ERP 单据标识组成。`pdaOrderUuid`、`pdaGoodsUuid` 使用确定性 UUID；Gateway 重试前按这些 UUID 核对已存在数据。一个同步包内的三表写入全部成功才提交。

部分移动存在两个阶段：

- U8 成功阶段：写原 Code 的负流水。
- 实际打印阶段：写新 Code 的正流水。

两个阶段使用不同事件键，但通过原业务操作、原明细和拆分关系组关联。

## 6. 服务边界

### Gateway

- 增加单张和批量 TraceBack 标签快照查询。
- 增加幂等的 TraceBack 同步包写入及结果查询。
- 在账套数据库内完成三表事务、字段长度校验、JSON 校验和确定性 UUID 核对。
- 不在响应、日志或仓库中暴露数据库密码和 Gateway API Key。

### Business API

- 负责标签查询编排、本地镜像、业务锁、同步任务、失败恢复和用户可见状态。
- 材料出库、调拨、货位调整提交前使用 Gateway 实时复核旧表。
- 保留现有本地标签表，不把旧表的大段 JSON 直接作为业务合同暴露给 H5。

### H5

- 扫码和标签查询继续调用 Business API。
- 正常流程不展示旧表名、UUID 或同步实现细节。
- 只显示必要状态：标签可用、已用完、待核对，以及“ERP 已生成，标签同步待恢复”。

## 7. 查询性能

888 当前仅有 `TraceBackOnlyCode.id` 聚集主键，三个常用关联键没有索引。2026-10-15 已保存代表性实际执行计划，查询使用 `TraceBackOnlyCode` 聚集索引扫描，估算子树成本 `0.93825`。数据质量核查结果：Goods UUID 无重复；Order UUID 有 11 组、22 行重复；无效数量 21 行；负余额 Code 164 个。

三个键的真实类型均为 `varchar(3500)`，超过普通非聚集索引键 1700 字节上限，因此没有在 888 创建原字段索引。测试账套候选方向为持久化 `SHA2_256` 计算列及“哈希 + 流水 ID”索引，同时在查询中保留完整原值复核以排除碰撞。该方案需要另行评估旧系统写入成本、索引空间和实际计划收益；生产账套不得直接套用。

不得未经执行计划、重复值和空间评估直接在生产账套建索引。批量同步以最新 `TraceBackOnlyCode.id` 为游标，避免反复全表扫描。

仓库提供 `docs/database/TRACEBACK_INDEX_ASSESSMENT_888.sql`，只读输出三表数据量、真实字段类型、现有索引、Goods/Order UUID 重复值、无效数量、负余额 Code，并执行一条带 Actual Plan 的代表性扫码查询。服务器保存的创建前计划为 `D:\LabelSystem\Diagnostics\traceback-scan-before-20261015_224745.sqlplan`，SHA-256 为 `D661CA671FF05FF9CB888A05D0C938E5E859B2DE3A2E57F82B65C8F0826B5FBC`。脚本只说明哈希索引候选方向，不执行 DDL。

Gateway 每次查询在同一次字段合同检查中读取 Code 和两个 UUID 键的真实 SQL 类型，并用 `varchar`、`nvarchar` 或 `uniqueidentifier` 的匹配参数绑定，避免因固定使用 Unicode 字符串而让 SQL Server 转换索引列。

## 8. 实施顺序

1. 已在本地固化三表必要字段合同、JSON 白名单映射和异常分类。
2. 迁移 015 已在开发、测试和生产库执行，新增本地 TraceBack 镜像版本与同步任务结构。
3. Gateway 只读快照接口和 Business API 扫码、两次复核已部署；888 批量读取 100 张正余额合法标签均返回 `AVAILABLE`，异常样本仍进入待核对。
4. 材料出库接入负数、红字正数及打印阶段正数流水。
5. 调拨接入全量零流水、部分负流水及打印阶段新 Code 正流水。
6. 货位调整接入全量零流水、部分负流水及打印阶段新 Code 正流水。
7. 增加同步状态、幂等恢复、管理员重试和新旧余额/位置对账。
8. 在 888 按全量、部分、红蓝字、断网、超时、重复请求和并发场景验收。
9. 生产启用双运行，观察并对账；达到退出条件后关闭旧表写入。

## 9. 退出旧表条件

- 连续观察期内没有未恢复同步任务。
- 新旧标签余额和位置对账无未解释差异。
- 所有在用 PDA 和打印入口均已切换到新系统。
- 不再存在绕过 Label System 写旧表的业务客户端。
- 完成旧表最终增量同步、备份和只读留档。
- 管理员关闭旧表写入开关后，新系统仍能独立完成扫码、制单、打印、拆分和查询。

旧表停写后先保留只读查询与对账能力，不立即删除兼容代码或历史数据。
