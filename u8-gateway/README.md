# U8 Gateway

2026-10-19正常模式更新：当前状态与操作以[正常打印、PDF及数据清理](../New_Label_Print_System/docs/LabelSystem正常打印与数据清理.md)为准。888已取消模拟运行，使用本机0.7.1助手；PDF目标可正式首打、补打及回执恢复。左侧仅“标签打印”，四项功能在页内。测试任务与标签及对应888旧三表登记已清理；保留编码占号及流水，清理完成时打印任务0条，后续以实际记录为准，原业务保留。生产008未动，未发送实体打印。

以下既往接入记录中涉及模拟、禁打、0.7.0及清理前数量的段落属于此前阶段；当前部署与操作以以上更新为准。核心业务防重合同继续有效。

2026-10-19打印复核：H5打印入口已撤销，独立18003继续复用Business API及Gateway三表兼容服务，严格渲染器保留。旧代码清理边界见[清单](../docs/PRINT_REUSE_AND_CLEANUP.md)，当前联调及未验收项见[复核](../New_Label_Print_System/docs/LabelSystem联调复核.md)。

最新开发部署：独立U8GatewayDevPrint/应用池在127.0.0.1:9881运行，精确连接888/UFDATA_888_2021，总写入和生产写入均关闭。实际物料快照返回31409条，Business API已同步至LabelSystem_Dev。既有9880/008站点、配置和程序未改变，见 [开发部署](../docs/test-builds/2026-10-19-print-development.md)。

2026-10-19打印兼容接口已在独立测试宿主和888逻辑账套影子库完成真实SQL联调：初始三表登记、OnlyCode故障整体回滚、提交后丢响应的原事件恢复、历史标签核验补打均通过。未发布到既有IIS，未操作008或U8 COM，临时库已清理。见 [验收报告](../docs/test-builds/2026-10-19-print-phase1-isolated.md)；测试桥入口为tools/PrintAcceptanceBridge，仅注册追溯控制器。

`0.10.0` 新增只读 `GET /api/v1/reference-data/materials/snapshot`，单次查询完整读取 `Inventory.dEDate IS NULL` 且料号非空的档案，附带来源账套、数据库和条数；上限 200,000 条，超限明确拒绝。原 `GET /reference-data/materials` 继续实时查询，供 Business API 上传前复核。缓存及同步状态由 Business API 保存。

## 账套写入

`0.9.2` 增加受控生产写入门禁：Gateway 只有同时开启总写入和生产写入、环境为 `production`、当前数据库与账套严格命中白名单，并输入 `ENABLE PRODUCTION 账套 数据库` 时才能写 U8 和 TraceBack。Business API 保留第二层独立开关。配置模板仍默认只读，应用模板不会自动开启生产写入。本版同时修正 Business API 外置 JSON 已有节点在模板切换时未被更新的问题，并让 U8 连接测试返回可安全展示的登录失败原因。

`0.9.0` 在本机部署管理页增加可独立编辑的“开发 / 888”和“生产 / 008”配置模板。读取或保存模板不改变运行配置，只有输入包含模板、账套和数据库的确认文字后才能应用；应用前先测试 U8 SQL 和标签数据库连接，并备份 Gateway 与 Business API 配置。模板不保存密码，生产模板固定关闭两层写入和开发登录。

`0.8.1` 取消管理密码长度和强度限制。首次配置仍必须设置非空密码；已有配置可在本机部署管理页填写“新管理密码”进行更换，留空则保持原值。

`0.8.0` 将原 Gateway 本机配置页扩展为“标签系统部署管理”：统一显示 Gateway、Business API、标签数据库迁移和双层写入状态；可测试并保存 Gateway/U8 与 Business API/标签数据库配置，执行 TraceBack 三表只读检查。密码不回显，保存前自动备份并记录不含敏感信息的审计。

`0.7.1` 修正调拨和货位调整写入最新 TraceBackGoods 时只更新位置编码、仍保留旧仓库或旧货位名称的问题。全量移动及带目标位置的事件现在从当前 U8 基础档案解析名称，并同步更新 Goods JSON 的仓库、货位编码和名称；生产账套仍硬阻断。

`0.7.0` 在既有 TraceBack 产品标签快照上增加测试账套同步事件：材料出库原标签负/正流水、调拨和货位调整全量零流水及部分移动来源负流水，均在账套单一事务中写入 Order、Goods 和 OnlyCode。事件使用确定性 UUID 幂等恢复，生产账套硬阻断；托盘仍不进入旧三表。

`0.5.8` 将调拨审核视为一个联动状态：先审核调拨单，再审核 U8 生成的其他出库单和其他入库单；弃审按其他入库单、其他出库单、调拨单的顺序执行。每一步都按实际状态跳过已完成项并回查，因此明确失败后可以安全重试。审核列表只有在三类单据全部审核后才显示“已审核”。

`0.5.7` 修正 USERPCO 新增取号后的表头回写：普通库存单写 `ccode`，调拨单写 `ctvcode`，货位调整写 `cvouchcode`，盘点单写 `ccvcode`。库存新增继续按完整 13 参数签名调用 `VoucherCO.Insert`，审核和弃审按完整签名调用 `Verify` / `UnVerify`；状态操作先用 USERPCO `Load` 读取组件格式的 `ufts`。测试账套 888 的九类新增、审核和弃审保持开放，修改和删除仍关闭。服务器写入总开关同时受 `test` 环境、`UFDATA_888_2021` 数据库和 888 账套限制。
状态：9 类单据字段结构已建立；除委外到货外，8 类单据已通过测试账套运行时读取验证。当前运行配置已切换到生产 008；新增、审核和弃审只能在双层开关、独立生产门禁和精确白名单全部命中时执行，修改和删除仍关闭。

`u8-gateway` 是标签系统全新建设的 U8 IIS 集成服务。它不复用
`U8_COM_Code` 的业务代码、配置和追溯表，只参考其中已经验证的 U8 COM
调用边界。

## 服务边界

```text
PDA / H5
  -> 标签系统业务后端
     -> U8 Gateway（本项目）
        -> U8 COM / 受控 U8 SQL 查询与 TraceBack 同步
```

- H5 不直接持有 U8 Gateway 的密钥。
- 标签、托盘、打印和标签流水不写入本项目。
- 本项目负责 U8 基础资料查询、单据校验、新增、修改、审核、弃审、受控删除和结果回查。
- 所有网关写操作先建立本地操作凭证并写入 `U8_WRITING`，再调用 U8。
- U8 Gateway 只有在 U8 回查和本地凭证完成后才返回网关成功；以后由标签系统业务后端继续完成业务数据库提交，H5 只接收业务后端的最终结果。

## 目录

```text
u8-gateway/
├─ docs/                         设计、字段采集和运维规则
├─ document-contracts/           每种 U8 单据的机器可读字段合同
├─ src/
│  ├─ U8Gateway.Api/             ASP.NET Web API / IIS 入口
│  ├─ U8Gateway.Contracts/       稳定 API 合同
│  ├─ U8Gateway.Core/            通用流程、状态机和业务接口
│  ├─ U8Gateway.Metadata/        单据类型和字段元数据
│  ├─ U8Gateway.Persistence/     配置、幂等、操作凭证和审计
│  ├─ U8Gateway.UserpcoBridge/   USERPCO 与系统 ADODB 7.0 隔离桥接
│  └─ U8Gateway.U8/              U8 COM 与 SQL 适配层
├─ tests/                        不连接真实 U8 的自动化测试
└─ runtime-data/                 仅开发机运行数据，禁止提交
```

## 当前单据范围

| 编码 | 单据 |
| --- | --- |
| `purchase-arrival` | 采购到货 |
| `subcontract-arrival` | 委外到货 |
| `purchase-receipt` | 采购入库 |
| `sales-delivery` | 发货单 |
| `sales-outbound` | 销售出库单 |
| `material-outbound` | 材料出库单 |
| `transfer` | 调拨单 |
| `location-adjustment` | 货位调整 |
| `inventory-count` | 盘点单 |

这些编码先稳定下来。9 类单据均已从目标 U8 16.0 生产账套只读采集原生 XML；销售发货使用 `USSAServer.clsSystem` 初始化销售上下文，并由 `clsVouchLoad.GetVoucherDataWEB` 读取。新网关的编译运行时已直接回读测试账套 888 中除委外到货外的 8 类历史单据，字段结构与合同一致，因此这 8 类已开放只读查询能力。委外到货按当前决定暂缓实现；其余单据的编号类型、中文字段名、条件必填和可写规则完成测试账套写入验证前，合同仍保持 `draft`。

当前代码已建立 JSON 与 U8 ADO 持久化 XML 的双向编解码层，并把表头、表体和货位分开处理。9 类草稿合同共登记 2,584 个字段，已嵌入元数据服务供 schema 接口读取；第一版新增/修改候选策略、服务端默认值、字段白名单、无写入校验和基于原单的 PATCH XML 合并已经实现。采购、销售、库存三组适配器及统一单据路由已经建立，九类新增、审核和弃审已对 888 放行。写路由已接入追加式操作凭证、`clientRequestId` 幂等门禁和操作状态查询。修改和删除仍不从 API 放行。服务器另有测试写入总开关、环境限制、数据库白名单和账套白名单四重门禁。

源单查询支持生产订单、采购订单、采购到货、销售订单、发货单和委外订单：`GET /api/v1/source-documents` 返回当前可用明细行，`POST /api/v1/source-documents/lines` 重新读取整张来源单据；生产订单另由 `POST /api/v1/source-documents/material-requirements` 返回子件计划量、累计已领量和剩余量。所有查询使用固定表和参数，不接受客户端表名或字段名，不写 U8。六类源单搜索和整单明细读取已在测试账套通过只读验证；委外到货单本身仍没有历史样例，不能据此开放写入。

基础资料接口只读查询 U8：`GET /api/v1/reference-data/rd-styles` 读取 `Rd_Style`，`GET /api/v1/reference-data/warehouse-positions` 读取未停用仓库及其仓位，`GET /api/v1/reference-data/departments` 读取未停用部门，`GET /api/v1/reference-data/materials` 按编号或名称实时查询在用物料，`GET /api/v1/reference-data/document-master-data` 读取采购类型、销售类型、币种及最新汇率。测试账套已验证物料查询返回库存单位和批次管理标志；缓存由 Business API 负责，H5 不直接调用 Gateway。

`tools/CodecSmokeTest` 用于验证 XML 往返、合同装载、能力开关、操作映射和适配器登记；`tools/NativeReadSmokeTest` 用于在 U8 服务器上验证实际读取；`tools/OperationJournalSmokeTest` 用于验证凭证摘要、脱敏、并发追加和并发幂等；`tools/WriteSafetySmokeTest` 验证错误环境、数据库或账套不能进入写适配器。这些自动测试都不会写账套。

## 相关文档

- [API 设计](docs/API_DESIGN.md)
- [单据操作矩阵](docs/OPERATION_MATRIX.md)
- [字段采集](docs/FIELD_DISCOVERY.md)
- [新增与修改字段规则](docs/WRITE_CONTRACTS.md)
- [只读运行时验证](docs/READ_VALIDATION_2026-10-10.md)
- [操作凭证](docs/OPERATION_ARCHIVE.md)
- [服务器配置](docs/SERVER_CONFIGURATION.md)
- [IIS 独立部署](docs/IIS_DEPLOYMENT.md)
- [2026-10-11 部署记录](docs/DEPLOYMENT_2026-10-11.md)
- [字段合同](document-contracts/README.md)
- [U8 来源映射](document-contracts/u8-source-map.json)

## TraceBack 三表接口

Gateway `0.7.1` 提供 `GET /api/v1/traceback/labels/{code}` 和 `POST /api/v1/traceback/labels/snapshots`。批量请求最多 100 个完整 Code，响应顺序与去重后的请求顺序一致。读取前校验三张表及必要关联字段；负余额、数量无效、Goods 缺失或重复、JSON 无效和 Code 身份冲突均返回结构化异常状态。Order 关联异常只使来源字段留空，不猜测业务来源。

`POST /api/v1/traceback/labels/sync-events` 接收单个稳定同步事件。Gateway 先锁定事件和 Order UUID，复核原 Code 的最新流水 ID 与余额，再在一个 SQL 事务中写入缺失的 Order、Goods 和 OnlyCode；相同 Goods UUID 的完全一致重试直接返回已完成结果，不一致则返回冲突。写接口只允许 `UFDATA_888_2021`，其他账套在连接数据库前即返回 `TRACEBACK_WRITE_ACCOUNT_BLOCKED`。诊断用 `validateOnly=true` 会完整执行并回滚，不保留旧表数据。

当前不包含托盘。部分移动的目标标签和无标签红字在首次打印前保持无 Code 的 `PENDING_PRINT`，因此本阶段只写来源事件，打印目标正流水待打印平台接入。详细口径见根目录 `docs/TRACEBACK_COEXISTENCE_PLAN.md`。
# 开发中：初始打印登记（2026-10-19）

已增加initial-registrations及只读预检、历史打印证据读取接口，专用开发9881已部署但禁写；生产9880未更新。
原读快照仍按完整 Code 汇总、最新流水 Goods 定位。首打初始正数使用独立路径；
旧 sync-events 继续处理来源事件，不负责创建不存在的新 Code。
三表写入采用稳定 UUID、服务端重算载荷摘要和同事务校验，重放返回原 flowId。
PRINT/REPRINT 的本地零数量登记由 Business API 负责，补打不调用此写入。
环境、生产开关、精确账套/数据库白名单及内部认证继续有效，没有迁移或改写旧表结构。
本轮只运行纯合同冒烟/构建，未调用真实业务接口，未操作 888/008 数据。
合同及限制见 [打印规格](../docs/PRINT_PHASE1_SPEC.md)、[开发记录](../docs/PRINT_PHASE1_IMPLEMENTATION.md)。
