# Label System API

## 审核接口（0.5.2）

- `GET /api/v1/approval-documents`：登录用户查询 U8 审核列表；H5 单据编码由 Business API 映射为 Gateway 编码。
- `GET /api/v1/approval-documents/{documentType}/{id}`：查询真实 U8 表头和明细。
- `POST /api/v1/approval-documents/{documentType}/{voucherCode}/audit`
- `POST /api/v1/approval-documents/{documentType}/{voucherCode}/unaudit`

操作请求携带 `clientRequestId`、`expectedVersion`、原因和设备版本信息。Business API 使用当前会话和服务端 ERP 映射重建操作人；development 已对测试账套 888 开放审核和弃审，操作后必须由 Gateway 回查确认状态。`expectedVersion` 用于调用前的 U8 SQL 并发校验；库存 USERPCO 状态操作另由 Gateway `Load` 当前单据并传入组件格式时间戳，二者都通过后才允许执行。

用户管理接口要求管理员角色或当前会话短时管理权限；当前管理员不能停用自己或取消自己的管理员身份，系统也不允许停用或降级最后一个启用管理员。密码不进入用户列表、编辑响应或审计记录。
Base path：`/api/v1`

响应统一包含：`success`、`code`、`message`、`data`、`requestId`、`serverTime`。`serverTime` 使用北京时间并携带 `+08:00`。

除健康检查和登录外，接口需要：

```http
Authorization: Bearer <session-token>
```

登录成功的 `data` 包含 `token`、`expiresAt`、`expiresInSeconds` 和 `user`；`user` 包含稳定用户 ID、显示名称和角色。development 会话有效期为 12 小时并保存在 API 进程内，应用池重启后失效。客户端使用 `expiresInSeconds` 建立本机会话，避免设备时钟偏差；启动时使用 `/auth/me` 验证缓存会话，主动退出调用 `/auth/logout`，受保护接口返回 `401 AUTH_REQUIRED` 时必须清除本地会话。

H5 统一携带 `X-Request-ID`、`X-H5-Version` 和 `X-Bridge-Version`。development CORS 只允许已登记的 H5 来源，不使用通配来源；当前已登记 CT58C 开发来源 `http://172.16.10.45:5173` 和实际容器来源 `http://172.16.10.45:5174`。

## 接口

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/health` | 服务、环境和数据库状态 |
| POST | `/auth/login` | development 管理员或启用的本地密码账号登录 |
| POST | `/auth/logout` | 退出 |
| GET | `/auth/me` | 当前用户 |
| GET/POST | `/auth/management-access` | 查询或验证当前会话的短时管理权限 |
| GET | `/functions` | 当前启用功能 |
| GET | `/admin/functions` | 全部功能及行版本 |
| PUT | `/admin/functions/{code}` | 更新功能启停 |
| GET/POST | `/admin/users` | 查询或建立应用用户 |
| PUT | `/admin/users/{userId}` | 更新显示名、ERP 制单人映射和状态 |
| PUT | `/admin/users/{userId}/password` | 管理员重置本地账号密码 |
| GET | `/admin/users/{userId}/functions` | 查询用户功能权限及数据范围 |
| PUT | `/admin/users/{userId}/functions/{functionCode}` | 设置继承、允许或拒绝 |
| POST | `/admin/users/{userId}/unlock` | 清除登录失败次数和锁定 |
| POST | `/admin/users/{userId}/sessions/revoke` | 撤销该用户的全部当前会话 |
| POST | `/admin/users/{userId}/preferences/reset` | 清除该用户的漫游数据 |
| GET | `/admin/users/audit?userId=...` | 查询用户管理操作记录 |
| GET/PUT | `/users/me/preferences/{key}` | 读取或保存当前用户漫游数据 |
| GET | `/settings?documentType=...` | 全局设置和单据设置 |
| PUT | `/admin/settings/{scope}/{key}` | 新增或更新设置 |
| GET | `/inventory-objects/lookup?code=...` | 查产品标签或托盘 |
| GET | `/label-code-rules/capabilities` | 查询规则字段、片段和支持码制 |
| GET | `/label-code-rules/catalog` | 下发启用规则及可解析历史版本目录 |
| POST | `/label-codes/parse` | 按规则目录解析标签 Code |
| GET/POST | `/admin/label-code-rules` | 管理员查询或新建规则及 V1 草稿 |
| POST | `/admin/label-code-rules/preview` | 校验规则并生成、回解析样例 |
| PUT | `/admin/label-code-rules/{ruleId}/draft` | 保存当前草稿或从历史创建下一版本草稿 |
| POST | `/admin/label-code-rules/{ruleId}/versions/{versionId}/activate` | 启用草稿版本并停用原启用版本 |
| POST | `/admin/label-code-rules/{ruleId}/versions/{versionId}/retire` | 停止当前版本继续生成 |
| GET | `/reference-data/rd-styles?direction=...&terminalOnly=...` | 查询本地出入库类别缓存 |
| GET | `/reference-data/warehouse-positions` | 查询本地仓库与仓位缓存 |
| GET | `/reference-data/departments` | 查询本地部门缓存 |
| GET | `/reference-data/purchase-types` | 查询采购类型缓存 |
| GET | `/reference-data/sales-types` | 查询销售类型缓存 |
| GET | `/reference-data/currencies` | 查询币种及最新汇率缓存 |
| GET | `/reference-data/materials?keyword=...&limit=...` | 从 U8 实时按编号或名称查询物料 |
| GET | `/admin/data-sync/status` | 查询基础资料同步状态 |
| POST | `/admin/data-sync/rd-styles` | 从 U8 同步出入库类别 |
| POST | `/admin/data-sync/warehouse-positions` | 从 U8 同步仓库与仓位 |
| POST | `/admin/data-sync/departments` | 从 U8 同步部门 |
| POST | `/admin/data-sync/document-master-data` | 从 U8 同步采购类型、销售类型和币种 |
| PUT | `/drafts/{draftId}` | 新增或覆盖当前用户的最新草稿 JSON |
| GET | `/drafts?documentType=...&status=...` | 查询当前用户草稿；管理员可加 `allUsers=true` |
| GET | `/drafts/{draftId}` | 查询草稿正文；其他用户正文只允许管理员角色查看 |
| DELETE | `/drafts/{draftId}` | 当前创建人软删除草稿 |
| POST | `/drafts/device-heartbeat` | 上报当前用户在本设备的草稿同步数量 |
| GET | `/admin/draft-devices` | 管理员查询 PDA 最近上报状态 |
| GET | `/source-documents` | 代理 U8 源单查询 |
| POST | `/source-documents/lines` | 代理 U8 整单明细 |
| POST | `/source-documents/material-requirements` | 代理 U8 领料需求 |
| POST | `/operations/material-outbound/submit?autoApprove=false` | 有生产订单的材料出库直接上传 ERP |
| POST | `/operations/documents/{documentType}/submit?autoApprove=false` | 通用单据直接上传 ERP |
| GET | `/operations/groups?documentType=...&status=...&operatorId=...&startDate=...&endDate=...&skip=...&take=...` | 分页查询业务组；管理员可加 `allUsers=true` |
| GET | `/operations/groups/{groupId}` | 查询拆单组 |
| POST | `/operations/groups/{groupId}/issued-labels/{labelCode}/confirm` | 确认无标签退料生成的实体标签已贴附并启用 |
| POST | `/operations/groups/{groupId}/validate` | 材料出库写入前校验；不调用 U8 写接口 |
| POST | `/operations/groups/{groupId}/execute` | 创建人执行材料出库拆组；请求体可带 `autoApprove` |
| POST | `/operations/groups/{groupId}/heartbeat` | 续期业务锁 |
| POST | `/operations/groups/{groupId}/cancel` | 取消未写 U8 的待处理组 |

`autoApprove=true` 对九类单据统一生效。服务端先确认新增、回写 ERP 单号并提交本地库存事务，再以新增返回的版本审核；审核明确失败时业务组停在 `PENDING_APPROVAL`，可继续审核但不得重新新增，审核结果未知时进入 `NEEDS_RECONCILIATION`。首次审核沿用固定 `:auto-audit` 幂等后缀；明确失败后的继续审核按失败事件数追加递增序号，既不重放新增，也不会被上一次审核的幂等结果覆盖。

无源单红字材料出库可在通用提交接口的 `lines` 中使用 `objectType=UNLABELED_MATERIAL`，并提供 `materialCode`、`unitCode`、`requestedQty`、`batchNo`、`warehouseCode`、`locationCode`。该类型只允许 `material-outbound + RED`，且必须填写退料原因。提交成功返回的业务组包含 `issuedLabels`；新标签初始为 `PENDING_PRINT`，只有确认实体标签已贴附后才变为 `ACTIVE`。确认接口要求业务组已经完成，重复确认同一已启用标签按幂等成功返回。

H5 只调用 `submit`，用户点击一次“上传 ERP”后由 Business API 在同一请求内完成校验、幂等建档、拆组、锁定和执行，不向用户保留普通 `PENDING` 步骤。写入总开关关闭、ERP 制单人缺失或建档前校验失败时直接返回错误，不创建业务操作、来源占用或标签锁；建档后的写入前校验失败会立即取消临时操作并释放锁。只有已经调用 U8 且结果不明确时保留 `NEEDS_RECONCILIATION` 审计和锁。旧 `/prepare` 路由只返回 `409 DIRECT_SUBMIT_REQUIRED`，不得再建立待处理组。

普通用户调用 `POST /auth/management-access` 验证管理密码后，当前会话获得 10 分钟管理权限，可访问 `/admin/*`；该权限不改变用户角色，也不增加 `/functions` 返回的业务功能。管理密码仅在服务器外置配置保存哈希，客户端不保存。管理员角色不需要再次验证。

`/source-documents*` 和 `/inventory-objects/lookup` 要求 H5 通过 `X-Function-Code` 传入当前单据或 `label-query` 功能编码，Business API 按当前账号有效功能复核后才读取数据。待处理组只有创建人可以续期或取消；管理员角色可以跨用户查询，短时管理会话不扩大业务组范围。

九类单据执行前均重新读取不可变操作归档并校验 SHA-256，按当前用户 ERP 制单人生成每个拆组请求；材料出库另重新读取 U8 生产订单领料需求。执行状态从 `PENDING` 进入 `WRITING_ERP`；只有 Gateway 返回已回查确认的 U8 单据后，才在一个本地事务中保存 ERP 单号和行号、完成对应库存动作并释放组锁。取号、字段校验或 COM 参数绑定等确认发生在写入调用前的失败，把同组未执行拆组一并结束为失败并释放锁，允许修正后安全重试；超时、连接中断、保存后回查未确认或本地提交不明确记为 `NEEDS_RECONCILIATION` 并保留锁，同时停止本组后续拆组，禁止直接重试。当前不支持材料/销售数量单据的托盘上传和自动审核。

所有 `submit` 入口通过会话、功能权限、ERP 制单人和类别门禁后，会在建档和锁定前检查 Business API 的 `U8WritesEnabled`。当前 development 为 `true`；Gateway 仅允许 `test` 环境、`UFDATA_888_2021` 数据库和 888 账套，九类 `create` 已开放。单独的 `execute`、`heartbeat` 和 `cancel` 仅保留给旧组恢复和运维核对，不再作为 H5 正常业务步骤。

## 标签 Code 规则

规则 `spec` 当前 `schemaVersion=1`。`symbologies` 支持 Code 128、Code 39、Code 93、EAN-13/8、UPC-A/E、ITF-14/ITF、Codabar、QR、Data Matrix、PDF417、Aztec、GS1-128 和 GS1 Data Matrix。片段类型为固定内容、分隔符、业务字段、日期、流水号和校验位；自定义分隔符长度为 1 至 8 个可打印字符。

业务字段包括账套号、标签类型、料号、批号、数量、单位、生产日期、源单号和标签唯一值。字段可以配置 `expectedValue` 限定值；数量字段可配置 `decimalScale` 0 至 6 位，预览会规范化，扫码值必须严格保留对应位数。可变长度字段后必须有固定内容、分隔符或末尾校验位；字段内容可能包含分隔符时应选择百分号编码。规则支持 SHA-256 前 8 位或 EAN Mod-10 校验，载体自身的长度和字符限制仍会单独校验。

规则对象类型 `INVENTORY_OBJECT` 表示产品标签和托盘共用格式，解析接口不根据格式猜测最终对象；调用方应继续通过统一登记或库存查询确定实际类型。development 的 `INV008_STANDARD` V1 使用 `008|料号|批号|两位小数数量|10 位标签唯一值`，支持 Code 128、QR、Data Matrix、PDF417 和 Aztec。

新规则只产生 V1 草稿。启用版本不可原位修改；保存新草稿会生成下一版本，启用时把原启用版本转为历史停用。目录包含当前启用和历史版本，使旧标签仍可解析。同一规则优先启用版本，再按版本号回退；不同规则按较小优先级数值优先，同优先级同时命中返回 `409 LABEL_CODE_RULE_AMBIGUOUS`。账套校验命中但不一致返回 `422 ERP_ACCOUNT_MISMATCH`，优先于其他宽泛规则的成功结果。

目录响应包含内容指纹 `catalogVersion`，规则元数据、版本状态或规则 JSON 变化都会生成新值。当前 H5 按环境缓存目录；服务端解析为正式结果，离线解析尚未接入扫码链路。本阶段不分配正式流水、不登记 Code、不创建产品或托盘标签。

## 草稿同步

草稿使用客户端生成的稳定 `draftId` 和递增 `clientRevision`。同一修订号和相同内容重复提交按幂等处理；较旧修订或同一修订号的不同内容返回 `409 DRAFT_VERSION_CONFLICT`。服务端只保存最新 `payload` JSON 和 SHA-256 摘要，不保存中间内容版本；创建、业务状态变化、恢复和删除记录到 `document_draft_event`，事件不含正文。

`payload` 必须是 JSON 对象，当前单次上限为 8 MiB 字符，足以容纳正常的数百行明细。常用检索字段包括创建人、设备、单据类型、业务状态、日期、源单号、仓库、部门、明细数和总数量，单独保存并建立索引。正文携带 `schemaVersion`，当前为 `1`。

业务状态为 `DRAFT`、`PREPARED`、`COMPLETED`、`CANCELLED` 或 `DELETED`。`PREPARED` 必须带有效业务组编号，并验证该业务组属于当前用户。软删除只允许草稿创建人执行；管理员跨用户能力为查看，不冒充员工编辑或删除。短时管理密码不授予跨用户草稿或设备状态权限。

`client_device_state` 保存用户与设备组合的最近在线时间、最后成功同步时间、应用版本，以及本地草稿、待同步和失败数量。设备关闭或离线期间服务器无法获知即时变化，管理页面必须显示最后上报时间。

全局设置 `draft_retention_policy` 分别定义活跃草稿和已结束记录的 `DAYS` 或 `PERMANENT`。永久使用 `null` 天数表示；天数范围为 1 至 36500。后台清理服务每 6 小时执行一次，先删除事件再删除到期最新快照，不处理 PDA 尚未上传的数据。

## 单据状态闭环

业务组列表以 `business_operation` 为准，不用草稿状态推测执行结果。返回组级聚合状态、是否终态、创建人、来源单号、明细数、总数量、锁到期时间、异常摘要，以及每个拆组操作的业务状态、ERP 状态、审核状态、ERP 单号和异常。普通用户只能读取自己的组；只有管理员角色可使用 `allUsers=true` 或按其他操作人筛选。

组内状态完全一致时返回该状态；混合状态按待核对、写入中、待审核和部分完成的优先级聚合。只有组内所有操作均为 `SUCCESS`、`FAILED`、`CANCELLED` 或 `REVERSED` 才标记终态；`NEEDS_RECONCILIATION` 明确是非终态，H5 必须放入独立“待核对”页签，不能显示在“已结束”。预提交和取消会同步服务端草稿状态；客户端再次上传草稿时，服务端按关联业务组重算 `PREPARED`、`COMPLETED` 或 `CANCELLED`，禁止旧客户端把已结束单据降回草稿。

## 基础资料同步

`POST /admin/data-sync/rd-styles` 需要管理员角色或当前会话短时管理权限。Business API 通过 U8 Gateway 只读获取 `Rd_Style` 完整快照，校验编码、名称、方向和重复项后，在单一事务中更新 `u8_rd_style_cache`。新出现的编码新增，已有编码更新，本次未出现的旧编码标记为停用；同步失败不清空上一次成功缓存。

`POST /admin/data-sync/warehouse-positions` 和 `POST /admin/data-sync/departments` 采用相同的管理员权限、完整读取、先校验后事务替换和失败保留旧缓存规则。仓位同步同时维护仓库与仓位缓存；无仓位的有效仓库仍可作为仓库选项。

`POST /admin/data-sync/document-master-data` 一次读取并事务替换采购类型、销售类型和币种缓存。采购、销售类型同时保存其关联出入库类别；币种保存 U8 名称、编码、本币标志和最新可用汇率。同步失败时三组旧缓存全部保留，不允许只更新其中一组。

重复同步开始时保留上一轮 `last_success_at`，本轮完成后再更新成功时间。数据库唯一键或检查约束冲突返回 `409 DATABASE_CONFLICT`，不再误报为数据库连接失败。

`GET /admin/data-sync/status` 返回四个同步项的状态、条数和最近成功时间。所有 `/reference-data/*` 查询都只读取标签数据库缓存；类别的 `direction` 可为 `IN` 或 `OUT`，`terminalOnly=true` 只返回末级类别。接口不实时查询 U8，也不调用 U8 写能力。

## 用户、权限与漫游数据

development 管理员密码校验仍受环境开关保护；`LOCAL` 用户使用 `application_user` 内的 PBKDF2-SHA256 加盐派生值校验，数据库不保存明文密码。密码不设最低长度或字符组合强度，单字符允许；只禁止空密码并保留 128 字符技术上限。连续 5 次失败锁定 15 分钟。成功后从启用用户生成会话，后续每个认证请求再次检查账号状态。客户端提交的操作人 ID 和姓名会被当前会话用户覆盖，不能伪造业务操作人。

`GET /functions` 按全局功能开关、用户类型和 `user_function_permission` 计算。管理员无直接记录时默认允许全部已启用功能，普通用户无直接记录时默认禁止；记录值可为 `ALLOW` 或 `DENY`，显式值优先。审核查询、审核操作和单据预提交均在服务端按功能编码再次校验，前端隐藏入口不作为授权依据。

当前 `dataScope` 只定义做单功能的出入库类别范围：`{"version":1,"rdStyles":{"mode":"SELECTED","codes":["..."]}}`。空值或没有 `rdStyles` 表示全部；`SELECTED` 至少包含一个当前方向下有效末级类别。保存指定范围时，无记录继承会落为 `ALLOW`；恢复默认且范围为空时删除直接权限记录。部门、仓库和货位不做用户范围限制。

`GET /reference-data/rd-styles` 携带 `X-Function-Code` 时先校验功能权限，再按当前用户类别范围过滤；不携带时供管理端读取完整缓存。通用和材料出库专用预提交都会再次校验类别存在、方向和用户范围，越权返回 `403 RD_STYLE_FORBIDDEN`，无效类别返回 `400 RD_STYLE_INVALID`。材料出库专用合同同时携带 `departmentCode` 和 `categoryCode`。

用户列表同时返回失败次数、锁定时间和密码修改时间。管理员可解锁账号、强制退出全部会话、重置漫游数据并查看操作记录；密码重置也撤销该用户当前会话。新增/修改用户、密码重置、解锁、会话撤销、漫游重置和直接权限修改写入 `user_admin_audit`，前后快照不包含凭据或令牌。

用户漫游接口使用稳定偏好键、JSON 值、版本和行版本。当前正式键为常用功能 `workspace.favorites` 和业务中心显示模式 `workspace.homeMode`；后者使用 `{"mode":"compact|detailed"}` 对象，现有用户缺省为 `detailed`。不存在时返回 `404 PREFERENCE_NOT_FOUND`。携带 `expectedRowVersion` 时不一致返回 `409 DATA_CHANGED`；不携带时按最后写入覆盖，首次并发创建会在唯一键竞争后自动转为更新。

真实 U8 写入不得沿用请求的 `operatorName`。写入前必须按当前会话用户解析 `erp_operator_code`、`erp_operator_name`，把 ERP 姓名传给 Gateway 并写入 `cmaker`；未配置返回 `409 ERP_OPERATOR_NOT_CONFIGURED`。development 已开放 888 写入，该映射现在是新增、审核和弃审的强制门禁。

## 材料出库预提交

库存查询的每个产品标签及托盘对象均返回 `materialCode/materialName`、`unitCode/unitName`、`warehouseCode/warehouseName`、`locationCode/locationName`、可用数量、状态和行版本。仓库和货位名称读取本地 U8 缓存；物料和单位名称读取库存流水属性快照，旧流水没有名称时返回空名称并由客户端回退显示编码。查询托盘时，`lines` 是托内产品标签，`pallet` 是托盘自身；H5 不得把托内标签改写为 `PALLET`。

选择生产订单时，专用预提交表头必须带单据日期、`documentDirection=BLUE|RED`、部门、出库类别和可选生产批号；每条分配必须带来源生产订单、生产订单行、子件需求、产品、材料、标签或托盘、正常数量和超领数量。服务端不会相信 H5 的汇总结果，而是重新读取 U8 领料需求并校验：

- 来源行和子件仍然存在，料号和产品归属未变化。
- 正常领用不超过当前可领数量。
- 超领开关已启用，并且同料所有正常待领数量已经分配完成。
- 产品标签或托盘存在、状态有效、没有被其他操作锁定。
- 提交数量不超过标签当前余额。
- 请求单位与标签最新库存流水或托盘主记录一致。
- 托内标签通过托盘扫码使用，或全局“托内标签单独使用”开关已开启。
- 来源明细没有被其他未完成业务占用。

通过后按“生产订单内部 ID + 标签当前仓库”拆成多个 `business_operation`，单号只用于展示和一致性复核；各操作共同使用请求的 `clientRequestId` 作为 `submission_group_id`。标签和相关托盘统一锁到组内第一条操作，避免一个标签跨拆单组被并发使用。

旧准备接口曾只创建 `PENDING` 记录；当前正常 H5 只使用单次提交。旧组的取消和心跳仅用于兼容恢复；进入 U8 写流程后禁止直接取消，结果不明确时保留锁等待核对。

有生产订单来源的材料出库请求必须提交 `categoryCode=202`。H5 在选单后自动固定该值，Business API 在预提交时拒绝其他类别；无源单材料出库仍按用户选择的有效出库类别处理。

执行参照生产订单的材料出库时，Gateway 请求表头同时携带 `iproorderid` 和 `cmpocode`，表体携带 `impoids`、`cmocode`、`imoseq` 和 `iopseq`。前者用于 U8 表头展示和整单来源关联，后者用于逐行追溯子件需求。

## 通用单据预提交

通用提交的 `header` 保存日期、`documentDirection=BLUE|RED`、仓库、目标仓库、来源/目标货位、部门、业务类别和可选源单类型；`lines` 保存对象类型、标签或托盘码、来源托盘、料号、单位、本次数量及可选来源单据行。服务端重新读取最新库存并校验对象状态、业务锁、料号、单位、仓库和货位。所有单据都允许不传来源字段；只要请求携带任一来源字段，就必须提交完整来源关系并启用来源校验。红字请求已映射 U8 红字标识和相反数量，当前只允许在 888 分单据验收。

单据字段开关使用现有设置接口保存。DOCUMENT 范围键 `field_input_rules` 的值为字段键到 `{ "visible": boolean, "required": boolean }` 的 JSON 对象；字段定义由 H5 当前版本提供。H5 按设置控制九类制单的字段显示、输入和完整必填校验，Business API 同时按当前单据设置复核仓库、目标仓库、目标货位、部门和业务类别等可配置表头必填项。

DOCUMENT 范围布尔键 `auto_add_scanned_detail` 控制 H5 扫码结果是否直接加入明细，默认 `true`。值为 `false` 或存在无法自动取得的必填表体字段时，H5 先打开明细确认页；该键只控制录入交互，不降低 Business API 对标签身份、库存位置、余额、来源关系和必填字段的复核。

`field_input_rules` 是影响全部用户的公共单据规则。读取随登录设置接口开放，写入必须由真实管理员账号执行；临时管理密码授权不能修改该键。

通用提交的 `header.additionalFields`、`lines[].additionalFields`，以及有源材料出库请求的同名字段保存当前启用的扩展字段。值使用扁平 JSON 对象，仅允许字符串、数字、布尔值或空值；每个对象最多 128 项、64 KiB，字段名最多 100 字符，单个字符串最多 4000 字符。扩展字段随原请求写入操作前 TXT 归档；受支持的采购、销售、调拨、盘点专用字段及 U8 自定义项在执行时映射到对应单据合同。

- 采购到货、委外到货、采购入库、发货单和销售出库选择源单后，在预提交时重新读取 U8 当前源单行，校验来源单号、行、料号和可用数量；按来源单据拆成独立业务操作。未选源单时不执行来源复核和占用。
- 材料出库未选生产订单时走通用预提交并按标签实际仓库拆组；选择生产订单时使用专用材料出库预提交合同。
- 同一标签数量跨多个同料源单行时，H5 按可用量拆分请求明细；服务端按来源行分别复核和占用。
- 委外订单源单已通过测试账套只读验证；委外到货单本身仍没有历史样例，不能据此开放写入。

- 调拨要求调出、调入仓库不同，且每个对象按当前完整数量处理。
- 货位调整要求目标货位与当前位置不同，且每个对象按当前完整数量处理。
- 调拨和货位调整涉及托盘时，必须同时提交托盘自身及全部当前在托有效产品标签。
- 盘点允许产品标签实盘数量为零或高于当前账面数量；托盘自身数量固定为 1。
- 总开关开放后，通过校验的请求只在单次提交内部短暂进入 `PENDING/WRITING_ERP`，不向用户暴露待处理阶段。采购到货、委外到货、采购入库和发货单只保存 ERP 关联，不重复改变已有标签余额；销售出库按方向改变余额；调拨和货位调整写 `change_qty=0` 的位置流水；盘点按实盘数写差异流水。

## 并发和版本

- 功能和设置更新必须提交 `expectedRowVersion`。
- 行版本不一致返回 `409 DATA_CHANGED`。
- 标签或托盘被占用返回 `409 LABEL_LOCKED` 或 `409 PALLET_LOCKED`。
- 调拨或货位调整未整托提交返回 `409 WHOLE_PALLET_REQUIRED`。
- 当前仓库或货位变化返回 `409 WAREHOUSE_CHANGED` 或 `409 LOCATION_CHANGED`。
- U8 源单变化或数量不足返回 `409 SOURCE_LINE_CHANGED` 或 `409 SOURCE_QUANTITY_EXCEEDED`。
- 源单明细被占用返回 `409 SOURCE_LINE_RESERVED`。
- 幂等编号复用但请求内容不同返回 `409 IDEMPOTENCY_CONFLICT`。

明确失败重试使用新的 `clientRequestId`，长度不得超过 64。服务端草稿只在旧组全部为 `FAILED`、ERP 状态全部为 `FAILED`、没有 ERP 单号且没有库存流水时允许改绑到新组；成功、待核对、写入中或含库存流水的组一律拒绝改绑。网络超时等客户端无法确认结果时不得生成新编号，应沿用当前编号查询原组。

材料出库 888 验证组 `aec2b446-b5f2-496d-9b2b-b4b36f611231` 已完成两仓拆单：`CLCKD20263349` 对应 KML_KSW 两行共 10，`CLCKD20263350` 对应 KML_ZK 一行 3。两张单均未审核；Business API 保存 U8 主表 ID、表体 ID 和货位行 ID后才提交 3 条本地库存流水。

## 尚未开放

- U8 修改、删除和生产账套写入；九类新增、审核、弃审及红字新增当前仅供 888 联调，仍需逐类验收。
- 生产用户数据库、角色及数据权限。
- 标签正式发码、流水分配、统一登记、打印和首次库存流水。
- H5 使用缓存规则执行离线解析及联网后的登记校验。
- 托盘装箱、装托、拆箱和卸托扫码接口，以及托内产品标签合计校验；托盘仅作为容器，托内物料不得缺少产品标签。
- 托盘内容变化后的旧 Code 处置和重新发码策略。
- 云端租户、客户许可序列号和规则分发。
- 管理员异常解锁和 U8 结果未知核对页面。
