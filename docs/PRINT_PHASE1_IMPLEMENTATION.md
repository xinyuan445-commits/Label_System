# 标签打印第一阶段开发记录

2026-10-19正常模式更新：当前状态与操作以[正常打印、PDF及数据清理](../New_Label_Print_System/docs/LabelSystem正常打印与数据清理.md)为准。888已取消模拟运行，使用本机0.7.1助手；PDF目标可正式首打、补打及回执恢复。左侧仅“标签打印”，四项功能在页内。测试任务与标签及对应888旧三表登记已清理；保留编码占号及流水，清理完成时打印任务0条，后续以实际记录为准，原业务保留。生产008未动，未发送实体打印。

以下既往接入记录中涉及模拟、禁打、0.7.0及清理前数量的段落属于此前阶段；当前部署与操作以以上更新为准。核心业务防重合同继续有效。

2026-10-19 20:53位置字段修复：`PrintRepository.Sources.cs`在新建来源标签及拆包明细时直接写仓库、货位，`PrintRepository.Prepare.cs`在FIRST/REPRINT准备时写当时当前位置；纸面冻结快照不改。代码已发布到888开发API版本`20261019_205347_normal`。DBD26103204的明细135、136已由原关联流水定向回填；除位置列及行版本外，新表其他数据、原Code、数量流水、规则流水和旧三表均未变化。没有新迁移、没有调用打印发送、没有操作008。详见[正常打印与数据清理](../New_Label_Print_System/docs/LabelSystem正常打印与数据清理.md)及[位置回填验收](../New_Label_Print_System/docs/acceptance/2026-10-19-DBD26103204-position-backfill.json)。

2026-10-19后续实测和清理边界见[独立系统联调复核](../New_Label_Print_System/docs/LabelSystem联调复核.md)及[复用清单](PRINT_REUSE_AND_CLEANUP.md)。现有开发HTTP与模拟恢复已分别记录，其他真实SQL矩阵与硬件验收仍未完成（首打/补打/丢回执/三表失败恢复已通过）；本轮没有迁移、新建数据库、删除代码、生产008操作或真实打印。

最新：原New_Label_Print_System独立应用18003已补齐规则管理/当前规则、017正式发布、目标/任务/原码补打和V2回执恢复接口；实际验证规则读取/预览、带图片正式发布和重复发布复用，真实发送与业务写入保持关闭。原54项隔离证据与新版真实SQL全过程验收分开记录。见[独立接入实施](../New_Label_Print_System/docs/LabelSystem接入实施.md)。以下原阶段状态按历史范围保留。

日期：2026-10-19。基线 main 1eb233d；本轮改动保留于本地工作区，未提交或上传。

当前产品主体为`New_Label_Print_System/`原独立系统，18003端口，复用Label system账号和权限。规则管理/单选、017模板发布、任务/补打和V2回执恢复代码已接；助手0.7.0。首打/补打/丢回执/三表失败恢复已通过真实SQL验收，其他矩阵待测，发送和U8写入仍关闭，见[联调复核](../New_Label_Print_System/docs/LabelSystem联调复核.md)。

早期独立部署曾遗漏规则页面及017适配，助手下载也曾漏带产物；这些缺口已逐项补代码并部署，不能继续标成当前未实现。修复前状态见[历史核对](../New_Label_Print_System/docs/LabelSystem接入核对.md)，0.6.1仅是下载修复阶段，已被0.7.0替代。H5内嵌入口撤销、9891静态托管关闭；公共API/Gateway/严格渲染仍复用，见[清理边界](PRINT_REUSE_AND_CLEANUP.md)。

以下为早期试验与数据库验收阶段记录，不表示新版独立链路已经全流程通过：

2026-10-19先完成54项隔离断言并清理临时库，随后按授权备份LabelSystem_Dev并安装017，部署9891工作台/API、回环9881 Gateway和18781渲染器。首打/补打发送及两层U8写入关闭，开发物料缓存31409条；模板1/版本1预览通过，业务记录仍3/9/8、打印任务和正式Code登记为0。详见 [开发部署](test-builds/2026-10-19-print-development.md) 与 [隔离验收](test-builds/2026-10-19-print-phase1-isolated.md)。017仍不由启动、构建或普通测试自动执行。
原有001–016未改写。独立导入仓库基线6f09c78，本轮已新增账号桥、888只读模式及专用开发入口，保留未提交修改。

## 1. 实现入口

| 能力 | 源码（相对项目根目录） |
| --- | --- |
| 当前产品入口 | New_Label_Print_System/frontend/src、backend/standalone_labelsystem.py、backend/app/auth/labelsystem.py |
| 早期试验工作台（非产品主体） | label-print/frontend/src/App.tsx；H5嵌入入口已撤销 |
| 当前原设计器与正式发布适配 | New_Label_Print_System/frontend/src/editor/、backend/app/services/labelsystem_templates.py |
| 严格 PDF/PNG 渲染 | label-print/renderer/label_renderer/service.py |
| 当前助手签名、落盘及恢复 | New_Label_Print_System/connector/labeljobs_v2.go；旧试验副本仍供隔离回归使用 |
| 路由及权限 | label-api/src/LabelSystem.Api/Printing/PrintEndpoints.cs |
| 单据校验、取号及冻结 | Printing/PrintRepository.Prepare.cs |
| 幂等流水及 outbox | Printing/PrintRepository.Receipts.cs |
| 取消、恢复、核对、历史基线 | Printing/PrintRepository.Recovery.cs、Printing/PrintRepository.Legacy.cs |
| 原登记自动恢复 | Printing/PrintRecoveryWorker.cs |
| 不可变归档 | Printing/PrintArtifacts.cs |
| 旧表初始登记/历史证据 | u8-gateway/src/U8Gateway.U8/TraceBack/TraceBackInitialRegistration.cs、TraceBackLegacyEvidence.cs |
| 旧入口/组锁保护 | MaterialOutboundRepository.ConfirmIssuedLabelAsync、TraceBackSyncRepository.FinalizeOperationAsync |

模板、图片、设备和规则绑定是技术表；库存事实继续保存在已有业务操作、明细、事件和流水。
没有新增打印数量账本、ERP 单据副本或第二套用户体系。

## 2. 防重账链路

1. **准备首打**：Serializable 校验来源业务成功、generated_label_id、数量/物料/单位/批次、版本和锁。
   同事务读取单选规则、取流水、登记唯一 Code、锁目标并冻结逐份快照。相同请求返回原任务。
2. **正式编码**：复用规则四表。分区为 PRINT: 加 namespace/环境/账套哈希，不按日期或年度重置；
   同分区跨版本取已有最大序号再加一。结构、精度、长度沿用引擎，不能表达原数量则拒绝。
3. **发送**：先归档文件及模板/字体摘要，再保存 attemptId 和服务端签名授权。
   助手验证签名、设备、原包、各页摘要和限额；调用驱动前 fsync UNKNOWN，成功后 fsync submitted。
   同 jobId/attemptId 返回原记录；跨进程文件锁防重入。
4. **本地登记**：只接受原设备签名和授权范围的回执；每份输出唯一成功流水。
   PRINT/REPRINT 的 qty_before=qty_after=当前余额、change_qty=0；成功张数置 1，不累加。
   首打同事务额外建唯一 PRINT_TARGET；补打没有该任务。
5. **三表登记**：稳定事件键绑定 scope+labelId，不随尝试、输出或恢复请求变化。
   Gateway 重算 UTF-8 长度前缀摘要，在 Serializable 事务内查 UUID、完整 Code、三表内容和档案。
   原事件已应用则返回原 flowId；不存在时才插 Order/Goods 和一条 +Q OnlyCode。
6. **收尾**：校验事件及权威快照后填真实游标，不改本地数量、不再写 PRINT。
   逐标签检查未确认输出和未完成任务，依赖完成才释放本操作占用；旧来源组也不因 A 完成提前全组解锁。
7. **异常恢复**：回执丢失查询原助手记录；三表失败/响应丢失只重发原事件。
   页面查询和可选后台恢复不调用驱动。锁、Code 和成功流水不因超时、关页面或备注清掉。

017 增加输出、首打、初始任务、事件键等唯一索引及零数量 CHECK。
LOCAL 登记待完成期间满足原 NULL/NOT_APPLICABLE 约束，不填假旧流水 ID；
完成才一起切 TRACEBACK/SYNCED。安装 017 后旧 issued-labels/confirm 拒绝待首打及新协议标签旁路。

## 3. 纯测试证据

| 检查 | 结果 | 说明 |
| --- | --- | --- |
| Business API 单元测试 | 36 通过 | 含既有纯测试；排除会建库/迁移的 MaterialCacheRepositoryTests |
| 017 SQL 语法 | 通过 | ScriptDom 解析外层和 4 个动态批次；没有连接 SQL 或执行 DDL |
| 不可变文件 | 通过 | 并发写入、篡改及越界路径 |
| Gateway 合同冒烟 | 通过 | 跨层摘要夹具、初始校验、写门禁在连接前拒绝 |
| Python 渲染 | 9 通过 | 80×50mm/300dpi、原数量100、确定性、缺变量/全码/字体等拒绝 |
| 前端恢复 | 5 通过 | 请求号保留、丢响应查原回执、同步不接触助手、未知任务不重发 |
| 原 H5 回归 | 11 通过 | 记住登录4项、启动桥接7项，均为纯测试 |
| Go 助手 | go test ./... 通过 | V2 新增 5 例模拟驱动；重启/重复、模糊失败、篡改包、明确未发送 |
| 构建 | API、Gateway、原 H5、打印工作台通过 | 未启动服务；编辑器分包加载 |

测试入口：label-api/tests/LabelSystem.Api.Tests/Print*Tests.cs、
u8-gateway/tools/TraceBackContractSmokeTest/Program.cs、
label-print/frontend/src/lib/printing.test.ts、label-print/renderer/tests/test_renderer.py、
label-print/connector/labeljobs_v2_test.go。
纯测试不等同数据库或现场验收。后续已另行完成重点隔离SQL验收，见报告及已更新清单；现场项仍未验证。

## 4. 边界与待验证

- 已在空临时库执行001–017；真实SQL首打、10个并发回执、补打及故障恢复通过。
  现有开发库017升级已通过、原业务行数量不变；未验证全部约束拒绝路径、性能压力和全部事务断点，DB01–DB15仍有未执行项。
- 已经隔离Gateway实际追溯接口写影子三表；未调用ERP制单、U8 COM或实际驱动。既有服务仍production/008，未切换或重启；没有操作008业务数据。
- 首打要求 Business API U8 写门禁和 Gateway 精确环境/账套/数据库白名单；
  新打印配置、自动恢复与功能码默认关闭。
- 列表只返回最近/最前 100 项，游标分页及跨页搜索尚未实现。
- 支持已有 6 类图元和条码绘图、PNG/JPEG 包迁入；SVG、任意 SQL 字段、ZPL/TSPL 迁入导出不在本阶段。
  尺寸最多 1000mm、3 位小数；未知/空变量拒绝渲染。须取得真实模板包、变量及字体核对。
- 历史核验只自动接受完整 Code 下最早正流水、唯一 Goods/Order、打印平台来源、完整原身份/单位/SN/单号；
  五段码数量也需等于初始正数。缺单位等遗留数据不能自动重建，不猜单位、不用当前余额代替原数量。
  历史样式由管理员选择重建模板，不宣称从旧三表恢复历史 PDF。
- 补打冻结原 Code/数量/模板/日期；零数量流水携带当前库存身份和位置。本阶段沿用原设备/打印机；
  尚无迁机或冻结任务换模板版本动作，准备失败保留原任务，不自动换 Code。
- 发送前核对助手的真实设备公钥指纹及服务器公钥；授权票据也绑定设备指纹。
  配置中的设备 ID 相同但私钥错误会在驱动调用前拒绝，不能先打印再发现回执无法验证。
- 部分成功时仅未发送输出可继续；未知输出保留核对，成功输出不再次授权。
  已拆分/合并标签的补打查询保留原业务状态，不因镜像查询改成 ACTIVE/USED；仍需集成验收。
- 助手驱动整批成功才认定整批发送；中途失败保留 UNKNOWN，不推测已出纸页数。
  日志遗失、驱动接受但成功回执未落盘等情况不能自动判定，保留锁和核对状态；
  管理员可留痕/补原签名回执，没有无签名强制成功或 TTL 解锁接口。
- 未提供安装器或自动部署；静态 /printing/、渲染服务、设备公钥及秘密配置需独立部署。
  HTTPS/本机访问、字体、标签尺寸、扫码可读性尚未现场验证。
- 正式规则及序号空间未读取运行库；跨版本单调序号不能代替旧系统发码隔离。
  旧客户端不遵守新应用锁时仍可能竞态，两个数据库不具有原子可见性。

现有开发库升级和开发站点部署已完成；破坏性故障用例继续独立隔离，真实设备及生产仍未执行。最新API纯测试38通过、工作台7通过，包含开发Cookie隔离及关闭发送不联系助手；实际IIS登录、模板发布/预览、图片摘要及888查询通过，浏览器完整交互仍待用户测试。

## 5. 本轮联调修复与工具

- `PrintArtifacts.Renderer` 改用明确Content-Length的UTF-8请求，修复Python HTTP服务拒绝分块JsonContent的问题；实际渲染链路通过。
- `label-api/tools/PrintAcceptance/` 显式执行迁移及联调、脱敏归档、校验临时库归属并删除。
- `u8-gateway/tools/PrintAcceptanceBridge/` 只注册实际追溯控制器，独立于现有IIS服务。
- `label-print/connector/acceptance_bridge_test.go` 在测试二进制中强制替换枚举和驱动，实际持久账本跨进程恢复，普通go test跳过显式宿主。
- 开发站点增加显式开启的同源静态文件托管和Session:CookieName；原部署默认行为不变。HTTP模板新建/请求号使用getRandomValues，图片导入对比服务端SHA-256，导出读取只读metadata；坏包不创建模板，可能留下未引用技术图片。
