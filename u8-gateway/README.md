# U8 Gateway

## 测试账套写入

`0.5.8` 将调拨审核视为一个联动状态：先审核调拨单，再审核 U8 生成的其他出库单和其他入库单；弃审按其他入库单、其他出库单、调拨单的顺序执行。每一步都按实际状态跳过已完成项并回查，因此明确失败后可以安全重试。审核列表只有在三类单据全部审核后才显示“已审核”。

`0.5.7` 修正 USERPCO 新增取号后的表头回写：普通库存单写 `ccode`，调拨单写 `ctvcode`，货位调整写 `cvouchcode`，盘点单写 `ccvcode`。库存新增继续按完整 13 参数签名调用 `VoucherCO.Insert`，审核和弃审按完整签名调用 `Verify` / `UnVerify`；状态操作先用 USERPCO `Load` 读取组件格式的 `ufts`。测试账套 888 的九类新增、审核和弃审保持开放，修改和删除仍关闭。服务器写入总开关同时受 `test` 环境、`UFDATA_888_2021` 数据库和 888 账套限制。
状态：9 类单据字段结构已建立；除委外到货外，8 类单据已通过测试账套运行时读取验证。当前开放仅用于 888 联调，禁止用于生产账套。

`u8-gateway` 是标签系统全新建设的 U8 IIS 集成服务。它不复用
`U8_COM_Code` 的业务代码、配置和追溯表，只参考其中已经验证的 U8 COM
调用边界。

## 服务边界

```text
PDA / H5
  -> 标签系统业务后端
     -> U8 Gateway（本项目）
        -> U8 COM / U8 SQL 只读查询
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
