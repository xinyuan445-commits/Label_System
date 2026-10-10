# U8 Gateway API 设计

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

操作状态查询已经实现。`reconcile` 必须由单据适配器根据操作类型回查 U8 后判断，当前写能力尚未开放，因此该接口仍保留为后续能力，不允许直接重放凭证内容。

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

当前已从脱敏结构证据生成 9 类草稿合同，合计登记 2,584 个字段。除暂缓的委外到货外，8 类单据已通过测试账套 888 的编译运行时读取验证并开放查询能力。第一版新增/修改候选策略已单独登记并合并到 schema，可以在不调用 U8 保存接口的情况下校验字段和生成默认值；候选规则尚未完成 888 写入验收，因此新增、修改和状态操作能力仍关闭。任何写请求还必须同时通过测试写入总开关、`test` 环境、限定数据库和限定账套检查。

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

H5 不直接查询 U8 数据库。后续按单据需要提供仓库、货位、存货、批号和结存、计量单位、供应商、客户、部门、业务员、收发类别、采购订单、销售订单、生产订单及来源单据明细查询。
