# 2026-10-11 IIS 部署记录

2026-10-13 15:17 已部署 `0.4.0`：新增采购类型、销售类型、币种及最新可用汇率固定只读接口，888 验证返回 16、11、4 条；Business API 首次同步成功。Release 构建通过，IIS 站点和应用池为 `Started`，外部健康检查版本为 `0.4.0.0`。九类新增、修改、审核、弃审、删除能力及服务器测试写入总开关保持关闭，未调用 U8 保存，未产生 888 单据。

2026-10-13 14:47 已部署 `0.3.9`：采购/委外到货增加 `VoucherCO_PU` 候选新增路径，发货单增加 `VoucherCO_SA` 候选新增路径；连同既有 `USERPCO.VoucherCO`，九类单据均具备生成单号、保存和按单号回查的候选运行时。采购入库和销售出库补齐来源到货/发货关联字段白名单。Debug、Release 构建及编解码合同、写入安全门禁、操作凭证烟测全部通过；IIS 站点和应用池为 `Started`，外部健康检查版本为 `0.3.9.0`。九类新增、修改、审核、弃审、删除能力全部保持 `false`，安全新增探针返回 `409 DOCUMENT_OPERATION_DISABLED`；服务器测试写入总开关未开启，未调用任何 U8 保存方法，未产生 888 单据。

2026-10-13 13:40 已部署 `0.3.8`：材料出库 `USERPCO.VoucherCO` 增加候选 `GetNewVouchCode + Insert + 回查` 运行时。编译、编解码合同和写入安全门禁测试通过；Business API 当前材料出库测试组的 2 个拆组、3 条明细通过 Gateway 无写入字段校验。材料出库 `create` 能力和服务器测试写入总开关均保持关闭，未调用 `Insert`，未产生 U8 单据。

2026-10-12 18:44 已部署 `0.3.7`：新增九类审核单据列表和详情只读接口，并把 U8 时间戳版本统一为十六进制文本。九类已审核单据搜索和详情读取全部通过，采购到货、材料出库和盘点待审数据读取通过。未调用审核、弃审或其他 U8 写接口，测试写入配置保持关闭。
## 部署结果

新 U8 Gateway 已作为独立 IIS 站点部署到 `172.16.1.159`：

```text
Site: U8Gateway
Application pool: U8GatewayPool
URL: http://172.16.1.159:9880
Physical path: D:\U8Gateway\site
Operation archive: D:\U8GatewayData\operation-archive
Environment: test
Database: UFDATA_888_2021
U8 account: (default)@888
Test writes: disabled
Service version: 0.4.0.0
```

原有 `Default Web Site`、`PDA` 和 `MaterialOutboundApi` 站点名称、目录和绑定未修改。

## 验收结果

- IIS 站点和应用池状态：`Started`；
- 外部健康检查：`running`、`configured=true`；
- SQL 连接测试：通过；
- U8 登录、初始化和注销测试：通过；
- 单据类型：9 类；
- 材料出库 schema 写入策略：`candidate-reference-derived`；
- 新增字段无写入校验：通过，服务端日期和制单人默认值正确；
- 服务器外访问管理页：403，符合仅本机管理要求；
- 真实新增接口：409 `DOCUMENT_OPERATION_DISABLED`，未调用 U8 写入。
- 生产订单源单、整单有效明细与子件查询：参数化 `SELECT` 验证通过；按订单日期筛选，只返回已审核且关闭日期、关闭时间为空的记录，不使用订单状态推断。
- 合并选单验证：订单 `SCDD26093014` 返回 11 条有效工单明细和 11 条领料子件；未执行 U8 写入。

首次启动发现 Web API 依赖的 Newtonsoft.Json 版本需要绑定重定向，已在 `Web.config` 固定为随发布包提供的 13.0.0.0，重新部署后启动正常。

2026-10-12 09:30 已部署 `0.3.5.0`，新增 `Rd_Style` 只读基础资料接口。IIS 站点和应用池均为 `Started`，接口验证返回 52 条类别，其中入库 18 条、出库 34 条；测试写入开关保持关闭。

2026-10-12 11:11 已部署 `0.3.6.0`，新增仓库、仓位和部门只读基础资料接口。通过 Business API 同步验证测试账套返回 45 个仓库、475 个仓位和 50 个部门；IIS 健康状态为 `running`，写入能力仍未开放。

## 凭据

新站点使用独立生成的管理密码和内部 API Key。开发机交接文件位于：

```text
u8-gateway/runtime-data/deployment/U8Gateway-credentials.txt
```

该目录已由 `.gitignore` 排除，不能上传 Git、粘贴到文档或写入 H5。后续标签系统业务后端通过安全配置读取 API Key。

## 尚未开放

新增、修改、审核、弃审和删除仍全部关闭。当前部署可用于健康检查、schema、无写入字段校验和已经验收的只读查询。下一阶段在 888 对每种单据逐项完成 CO 保存、修改、回读和失败场景测试，再按单据、按操作开放能力。008 保持只读。

2026-10-12 08:27 已部署 `0.3.4`。部署前把原站点完整备份到 `D:\U8Gateway\backups\20261012_082651`；站点和应用池启动正常。生产订单、采购订单、采购到货、销售订单、发货单和委外订单的搜索及整单明细读取均通过 Business API 代理验证。未调用任何写接口，测试写入配置未修改。
