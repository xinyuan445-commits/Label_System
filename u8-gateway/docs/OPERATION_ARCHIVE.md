# 操作凭证档案

## 1. 定位

操作凭证是独立于业务数据库的第二记录，用于核对 API 响应、U8 实际结果和数据库提交结果。它不是普通调试日志，也不能作为不经核对就自动重放 U8 操作的依据。

## 2. 存储位置

生产默认放在网站目录之外：

```text
D:\U8GatewayData\operation-archive\
```

开发默认放在：

```text
u8-gateway\runtime-data\operation-archive\
```

开发运行目录、生产档案和原始 U8 XML 均禁止提交 Git。生产目录只授予 IIS 应用池身份写入、管理员读取权限，不能作为 IIS 静态目录发布。

## 3. 目录和文件名

```text
operation-archive/
├─ .index/                        clientRequestId 幂等状态索引
└─ 2026-10-10/
   ├─ 材料出库单_material-outbound/
   │  ├─ 08_35_12_126_add.txt
   │  ├─ 09_16_05_482_edit.txt
   │  ├─ 10_02_46_309_verify.txt
   │  └─ 10_30_18_117_unverify.txt
   └─ 盘点单_inventory-count/
```

时间使用服务器配置的业务时区，文件名格式为 `HH_mm_ss_fff_操作类型.txt`。同一毫秒重名时追加 `_02`、`_03`，不能覆盖已有文件。

## 4. 当前已实现的网关写入顺序

```text
业务后端生成 clientRequestId
-> 网关创建凭证并写入 RECEIVED
-> 追加 VALIDATED
-> 追加 U8_WRITING
-> 调用 U8
-> 追加 U8_RETURNED
-> 回查 U8
-> 追加 U8_CONFIRMED 或 RESULT_UNKNOWN
-> 追加 SUCCESS 或 RECONCILIATION_REQUIRED
```

创建凭证、写入 `VALIDATED` 或写入 `U8_WRITING` 失败时，网关禁止调用 U8。U8 调用后凭证追加失败时，网关不得返回成功，而是返回 `RECONCILIATION_REQUIRED`，并提示禁止直接重试。每个阶段使用追加写、立即刷新到磁盘；程序异常时允许最后一行不完整，恢复程序只读取完整事件。

当前 U8 Gateway 不负责标签系统业务数据库事务，因此它的 `SUCCESS` 只表示 U8 已回查确认且网关凭证写入完成。以后接入标签系统业务后端时，端到端流程还必须增加业务数据库 `PENDING`、`DATABASE_COMMITTED` 和最终业务成功状态；H5 只能以业务后端的最终结果为准。

## 5. 幂等处理

每个 `clientRequestId` 在 `.index` 中对应一条状态记录，索引文件名使用编号的 SHA-256，不直接把外部输入作为文件名。

- 首次请求原子取得执行权，才允许进入 U8。
- 相同编号、相同内容并发提交时，只有一个请求执行，其余返回处理中。
- 相同编号、不同内容返回 `IDEMPOTENCY_CONFLICT`。
- 已成功请求再次提交时返回第一次保存的单据结果，不再次调用 U8。
- 状态不明确或等待核对的请求返回 `RECONCILIATION_REQUIRED`，不能自动重放。

可通过 `GET /api/v1/operations/{clientRequestId}` 查询当前状态。人工自动核对接口要等各类写适配器能够准确判断新增、审核、弃审和删除结果后再开放，当前不根据文件内容直接重放。

## 6. 内容

凭证保存记录版本、环境、账套、服务版本、请求编号、幂等编号、操作人、设备、APK/H5/桥版本、单据类型、操作类型、目标单据、预期版本、原因、请求 SHA-256、脱敏后的完整业务请求、U8 返回、U8 回查结果、最终状态和错误信息。业务数据库接入后再追加数据库结果。

禁止保存 API Key、登录令牌、SQL 密码、U8 密码和连接字符串。保存前必须递归脱敏常见密钥字段。

## 7. 核对与恢复

恢复时比较本地凭证、业务数据库和 U8 实际单据，状态包括：

```text
CONSISTENT
U8_MISSING
DATABASE_MISSING
ARCHIVE_INCOMPLETE
RESULT_UNKNOWN
```

任何恢复动作都必须先查询 U8，并继续使用原 `clientRequestId`；不能从 TXT 直接生成新幂等编号重放。

## 8. 真正备份

本机档案与 IIS、数据库位于同一硬盘时仍有单点风险。生产阶段应每天生成文件清单和 SHA-256，并复制到另一块磁盘或受控 NAS。保留期限和恢复演练频率在上线前确认。

## 9. 当前验证

`tools/OperationJournalSmokeTest` 已验证：

- 同一业务内容在 JSON 属性顺序不同时得到相同 SHA-256；
- 业务内容变化后摘要随之变化；
- 目录和文件按日期、单据及操作类型创建；
- 多线程追加不会产生损坏的 JSON 行；
- 24 个相同并发请求只有一个能够取得执行权；
- 同编号不同内容会冲突，成功后的相同请求会返回原结果；
- 密码、令牌、授权头和连接信息会递归脱敏；
- 首条记录固定为 `RECEIVED`。

测试只使用临时目录，不连接 U8，也不写测试或生产账套。
