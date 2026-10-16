# U8 Gateway API 设计

## 单据基础档案只读接口（0.4.0）

`GET /api/v1/reference-data/document-master-data` 使用固定 SQL 读取 `PurchaseType`、`SaleType`、`foreigncurrency` 和 `exch`，返回采购类型、销售类型、币种及最新可用汇率。接口不接受客户端表名、字段名或 SQL，不写 U8；888 已只读验证返回 16 个采购类型、11 个销售类型和 4 个币种。

## 九类新增候选运行时（0.3.9）

库存单据适配器已为六类库存单据实现 `USERPCO.VoucherCO.GetNewVouchCode + Insert`；采购适配器为采购到货和委外到货实现 `VoucherCO_PU.GetVoucherNO + VoucherSave`；销售适配器为发货单实现 `VoucherCO_SA.GetVoucherNO + Save`。保存后均按单号解析 ID，再使用既有只读加载器回查表头、明细、货位和版本；未回查确认不得返回成功。请求继续经过字段白名单、操作凭证、幂等记录和四重写入门禁。测试环境九类单据的 `create`、`verify`、`unverify` 已开放；`update` 和 `delete` 仍关闭。服务器总开关只允许 `UFDATA_888_2021` 和 888 账套。

库存类单据自动取号先尝试组件单据类型，再尝试合同登记的 `numberType`。888 当前映射为采购入库 `24`、销售出库 `0303`、材料出库 `0412`、调拨 `0304`、货位调整 `0313`、盘点 `0307`。每次取号使用新的表头 DOM；取得单号后重新生成表头，并按单据合同写入单号字段：普通库存单为 `ccode`、调拨为 `ctvcode`、货位调整为 `cvouchcode`、盘点为 `ccvcode`，随后才调用 `Insert`。取号失败发生在 `Insert` 前，必须返回明确失败，不得误记为结果未知。

`0.4.3` 曾按旧组件示例尝试 7 参数签名；真实 U8 16.0 USERPCO 类型库要求 13 参数。`0.5.2` 已由独立桥接程序集使用系统 ADODB 7.0 和完整 13 参数签名调用新增，`0.5.3` 将库存审核和弃审的 `Verify` / `UnVerify` 也移入同一桥接并传递完整引用参数。`0.5.5` 在审核、弃审和删除前统一调用 USERPCO `Load`，从返回的表头 XML 取得组件格式 `ufts` 并作为时间戳传入状态方法；API 的 `expectedVersion` 仍用于 SQL 并发前置校验，但其 `0x...` 文本不能直接代替 USERPCO 时间戳。单据内部 ID 在 U8 当前范围内按 32 位整数封送，不能把 .NET `Int64` 直接传给 VB6，否则会被拒绝为不支持的 Automation 类型。调用前的参数或依赖错误、U8 明确返回 `false` 且回查状态未变化时返回明确失败并保留原始业务消息；已经进入调用但回查无法确认结果时才保留核对状态并禁止直接重放。

调拨单审核在 `0.5.8` 起包含 U8 自动生成单据的联动状态。审核顺序为调拨单、关联其他出库单、关联其他入库单；弃审顺序相反。关联关系固定使用调拨单号匹配 `RdRecord09`、`RdRecord08` 的业务号或来源单号。每一步先读当前状态、只执行缺失动作、再通过审核人字段回查；部分成功返回明确的关联单错误并允许安全重试，全部三类状态一致后才返回成功。审核查询也按该组合状态划分 `pending`/`audited`。

## 审核单据只读接口（0.3.7）

- `GET /api/v1/approval-documents?documentType=&status=&documentNo=&startDate=&endDate=&limit=`：按 U8 当前状态返回单据摘要，`status` 为 `pending` 或 `audited`，`limit` 最大 100。
- `GET /api/v1/approval-documents/{documentType}/{id}`：返回表头和全部明细。
- 支持的稳定类型：`purchase-arrival`、`subcontract-arrival`、`purchase-receipt`、`sales-delivery`、`sales-outbound`、`material-outbound`、`transfer`、`location-adjustment`、`inventory-count`。
- `version` 为 U8 `ufts` 的 `0x...` 十六进制值，用于后续审核/弃审并发校验；读取接口不调用 U8 COM 写操作。
## 1. 设计原则

通用的是认证、路径、响应、幂等、状态、错误码和字段描述方式；每种 U8 单据保留独立字段合同、校验器和适配器。禁止通过 API 接收任意数据库表名、列名或原始 SQL。

“完整字段”表示所有已确认字段都被登记并分类，不表示所有字段都允许调用方写入。字段分为：

- 表头、表体、货位或辅助明细；
- 固定必填、条件必填、非必填；
- 新增可写、修改可写、只读；
- 调用方提供、服务端默认、U8 派生、U8 系统维护。

H5 和标签业务后端只提交 JSON。U8 Gateway 根据单据字段合同，把 JSON 分别转换成表头、表体、货位或其他辅助明细的 ADO 持久化 XML，再交给对应 U8 CO 组件；查询所得 XML 也必须先解析成 JSON 后返回。原始 XML 只允许用于网关内部诊断和受控归档，不作为普通业务接口。

## 2. 统一接口

```text
GET    /api/v1/document-types
GET    /api/v1/document-types/{type}/schema

POST   /api/v1/documents/{type}/search
GET    /api/v1/documents/{type}/{voucherCode}
POST   /api/v1/documents/{type}/validate?operation=add|edit
POST   /api/v1/documents/{type}
PATCH  /api/v1/documents/{type}/{voucherCode}

POST   /api/v1/documents/{type}/{voucherCode}/verify
POST   /api/v1/documents/{type}/{voucherCode}/unverify
POST   /api/v1/documents/{type}/{voucherCode}/delete

GET    /api/v1/operations/{clientRequestId}
POST   /api/v1/operations/{clientRequestId}/reconcile
```

操作状态查询已经实现。`reconcile` 必须由单据适配器根据操作类型回查 U8 后判断；当前仍不允许直接重放凭证内容，结果不明确时必须先人工核对。

单据类型列表只返回编码、名称、合同状态和能力摘要；完整字段仅由单个单据的 `schema` 接口返回，避免列表接口携带数千个字段。

未完成字段采集和真实账套验收的单据类型不得开放写接口。

## 3. 写操作公共字段

每次新增、修改、审核、弃审和删除必须携带：

```text
clientRequestId
operatorId
deviceId
environment
documentType
operationType
```

单据数据统一使用以下层级：

```json
{
  "header": {},
  "lines": [],
  "positions": [],
  "auxiliary": {}
}
```

`positions` 用于库存货位明细；`auxiliary` 为以后确有证据的其他辅助层级预留。调用方使用字段合同中的稳定字段名，网关通过 `u8Field` 映射到 XML 属性名。

服务端保存规范化请求的 SHA-256。同一 `clientRequestId` 和同一内容返回原结果；同一编号配合不同内容返回冲突。

幂等状态使用操作档案目录下的独立索引保存，并使用跨线程、跨进程的独占文件锁串行更新。同一个编号的并发请求只有一个能够进入 U8；`RESULT_UNKNOWN` 和 `RECONCILIATION_REQUIRED` 状态不能自动重试。

当前已从脱敏结构证据生成 9 类草稿合同，合计登记 2,584 个字段。除暂缓的委外到货外，8 类单据已通过测试账套 888 的编译运行时读取验证并开放查询能力。第一版新增/修改候选策略已单独登记并合并到 schema，可以在不调用 U8 保存接口的情况下校验字段和生成默认值；九类新增、审核和弃审已开放用于 888 联调，修改和删除仍关闭。任何写请求还必须同时通过测试写入总开关、`test` 环境、限定数据库和限定账套检查。

## 4. 修改规则

- 修改使用 PATCH 语义，只修改明确传入的字段。
- 未传字段保持原值；传空是否表示清空由字段合同的 `clearable` 决定。
- 必须携带查询时获得的 `expectedVersion`。
- 服务端先通过 U8 COM 加载原单，再应用变更并保存。
- 表体和货位变更使用 `_action=add|update|delete`；修改或删除必须携带 schema 中的稳定匹配键。
- 原单 XML 的 schema、系统字段、时间戳和未修改字段必须原样保留，行操作写入 `editprop=A|M|D`。
- 已审核、存在下游关联或受工作流控制的单据默认拒绝修改。
- 保存后必须回读目标字段；结果不明确时不能盲目重试。

## 5. 删除规则

删除不是数据库 DELETE。只有单据适配器明确支持，且单据未审核、无下游关联、没有已生效库存流水时，才调用 U8 正式删除能力。删除请求必须记录操作人、设备、原因和幂等编号。

## 6. 成功判定

U8 Gateway 只有以下条件全部成立才返回网关 `SUCCESS`：

1. U8 操作返回成功；
2. 再次查询 U8，确认实际单据或状态符合预期；
3. 本地操作凭证写入完成事件。

标签系统业务后端收到网关成功后再提交库存流水等业务数据；只有业务数据库提交也成功，业务后端才能向 H5 返回最终成功。网关成功不能直接当作 H5 端到端成功。

中间状态包括：

```text
RECEIVED
VALIDATED
U8_WRITING
U8_CREATED
U8_CONFIRMED
DATABASE_COMMITTED
SUCCESS
FAILED
RESULT_UNKNOWN
RECONCILIATION_REQUIRED
```

其中 `DATABASE_COMMITTED` 属于后续标签系统业务后端的端到端流程，当前 U8 Gateway 不伪造该阶段。

## 7. 基础资料接口

H5 不直接查询 U8 数据库。按单据需要提供仓库、货位、存货、批号和结存、计量单位、供应商、客户、部门、业务员、收发类别、采购订单、销售订单、生产订单及来源单据明细查询。

源单只读查询路由：

```text
GET  /api/v1/source-documents
POST /api/v1/source-documents/lines
POST /api/v1/source-documents/material-requirements
GET  /api/v1/reference-data/rd-styles
GET  /api/v1/reference-data/warehouse-positions
GET  /api/v1/reference-data/departments
GET  /api/v1/reference-data/materials?keyword=&limit=
GET  /api/v1/reference-data/document-master-data
```

`GET` 支持 `production-order`、`purchase-order`、`purchase-arrival`、`sales-order`、`sales-delivery` 和 `outsourcing-order`，按关键字、单据日期范围和条数查询当前可用明细；`POST /lines` 按来源单据内部 ID 重新读取有效明细。采购和销售读取器从固定候选字段中按目标账套实际结构解析主键、行号、审核、关闭和累计执行数量，只生成固定 SQL 并对业务条件使用参数，不接受客户端表名、字段名或 SQL 文本。

`POST /material-requirements` 只适用于生产订单，根据最多 1000 个生产订单明细内部 ID 返回子件分配 ID、产品、物料、计划量、累计已领量、剩余量和批次控制标志。生产订单审核和关闭规则保持原有验证口径。

六类源单搜索和整单明细读取已通过服务器测试账套只读验证，包含委外订单源单。委外到货单本身仍没有历史样例，原有未验收边界不变。标签系统业务后端负责持有网关密钥并转发稳定 JSON；H5 不直接持有密钥或访问 U8 数据库。

`GET /reference-data/rd-styles` 使用固定 SQL 读取 `dbo.Rd_Style`，将 `bRdFlag=1` 映射为 `IN`、`bRdFlag=0` 映射为 `OUT`，并返回 `bRdEnd` 末级标志。接口不接受表名、字段名或 SQL 参数，不写 U8；测试账套已只读验证 52 条记录。

`GET /reference-data/warehouse-positions` 使用固定 SQL 将 `dbo.Warehouse` 左连接 `dbo.Position`，只读取 `dWhEndDate IS NULL` 的仓库；没有仓位的仓库仍返回一行。`GET /reference-data/departments` 使用固定 SQL 读取 `dbo.Department` 中 `dDepEndDate IS NULL` 的部门。两者都不接受客户端 SQL，不写 U8；测试账套已只读验证 45 个仓库、475 个仓位和 50 个部门。

`GET /reference-data/materials` 使用固定、参数化 SQL 查询 `dbo.Inventory`，`keyword` 同时模糊匹配 `cInvCode` 和 `cInvName`，`limit` 限制在 1–100。返回物料编码、名称、规格、库存主计量单位和是否批次管理；不接受客户端表名、列名或 SQL。版本 `0.5.6` 已用测试账套 888 的两个真实料号完成只读验证。

`GET /reference-data/document-master-data` 使用固定 SQL 读取 `PurchaseType`、`SaleType`、`foreigncurrency` 和 `exch`，返回采购类型、销售类型、币种及各币种最新可用汇率。接口不接受任意 SQL，不写 U8；888 已验证返回 16、11、4 条。

写接口合同中的 `operatorName` 映射到 U8 `cmaker`。Gateway 不访问标签系统用户表，因此 Business API 在调用写接口前必须用当前应用用户的 ERP 制单人映射覆盖该字段；不得透传 H5 姓名或使用 Gateway 默认制单人替代。ERP 映射缺失时请求应在 Business API 层拒绝。

## 8. USERPCO 写入兼容边界

库存 USERPCO 与采购、销售 CO 使用的 ADODB 互操作版本不同。`U8Gateway.UserpcoBridge` 单独引用服务器系统 ADODB 7.0 和 `Interop.USERPCO`，其余适配器继续使用原 ADODB 2.6，禁止在同一程序集内强制统一版本。

`VoucherCO.Insert` 按 U8 16.0 的 13 参数签名调用，消息文档以空输出参数传入，两个校验标志初始为 `true`，货位文档使用空 DOM，具体货位由表体 `cposition` 交给 U8 生成。新增返回后先按 USERPCO 返回的单据 ID 查询主表，再回退按预取单号查询；只有数据库确认存在才返回成功。

U8 登录许可饱和返回 `U8_LICENSE_UNAVAILABLE`；反射或参数绑定发生在保存前时返回明确失败。COM 调用抛错后仍按预取单号和返回 ID 进行短时回查，查到单据按成功处理，无法判断是否写入才进入 `RECONCILIATION_REQUIRED`。

## 9. TraceBack 标签读取与同步接口（0.7.0）

`GET /api/v1/traceback/labels/{code}` 查询单张，`POST /api/v1/traceback/labels/snapshots` 使用 `{"codes":[...]}` 查询最多 100 张。读取规则为：按完整 `traceBackCode` 汇总 `TraceBackOnlyCode.sum`，取最大 `id` 关联 `TraceBackGoods`，再按 `pdaOrderUuid` 关联 `TraceBackOrder`。响应包含 Code、SN、余额、最新流水 ID、Goods/Order UUID、物料、批次、单位、仓库、货位、来源业务、PDA/ERP 单号、身份摘要、状态和异常列表。

状态为 `AVAILABLE`、`USED_UP`、`RECONCILIATION_REQUIRED` 或 `NOT_FOUND`。负余额、数量不可转换、Goods 缺失或重复、JSON 无效、必要身份字段缺失、Code 与 Goods 料号或批号冲突均进入待核对。Order 关联缺失或不唯一时来源字段留空，不猜测业务来源。结构缺失返回 `503 TRACEBACK_CONTRACT_INVALID`，SQL 查询失败返回 `503 TRACEBACK_QUERY_FAILED`，请求为空、超过 100 张或 Code 过长返回 `400 TRACEBACK_CODES_INVALID`。

`POST /api/v1/traceback/labels/sync-events` 写入一个稳定事件，请求字段包括 `eventKey`、`eventRole`、完整 `code`、有符号 `quantity`、`expectedLatestFlowId`、`expectedBalance`、确定性 `orderUuid`/`goodsUuid`、业务与 ERP 单号、操作人和可选目标位置。事件角色固定为：`MATERIAL_ISSUE` 负数、`MATERIAL_RETURN` 正数、`FULL_MOVE` 零、`PARTIAL_SOURCE` 负数、`PRINT_TARGET` 正数。正数退回、打印目标和全量移动必须提供目标仓库。

Gateway 使用 `sp_getapplock` 和 `Serializable` 事务，先复核最新流水 ID、余额、Goods 唯一性及 JSON，再原子写入 `TraceBackOrder`、`TraceBackGoods`、`TraceBackOnlyCode`。相同确定性 Goods UUID 且内容完全一致的重试返回 `alreadyApplied=true`；内容不同返回 `TRACEBACK_EVENT_CONFLICT`。来源变化返回 `TRACEBACK_SNAPSHOT_CHANGED`，写后负余额返回 `TRACEBACK_NEGATIVE_BALANCE`。`validateOnly=true` 只用于部署诊断，完整执行后回滚并且 `snapshot` 为空。

写接口在代码中硬限制数据库为 `UFDATA_888_2021`，其他数据库返回 `TRACEBACK_WRITE_ACCOUNT_BLOCKED`；它仍受全局内部 API Key 保护，不接受客户端 SQL。托盘不进入接口。部分移动和无标签红字的目标正流水要等正式打印生成 Code 后才能调用，本阶段尚未接入打印入口。

888 已完成数据量、重复值、真实字段类型和实际执行计划核查；由于关联键均为 `varchar(3500)`，本次未创建原字段索引，后续哈希索引方案见根目录 `docs/TRACEBACK_COEXISTENCE_PLAN.md`。
