# 电脑端标签打印接入方案

2026-10-19正常模式更新：当前状态与操作以[正常打印、PDF及数据清理](../New_Label_Print_System/docs/LabelSystem正常打印与数据清理.md)为准。888已取消模拟运行，使用本机0.7.1助手；PDF目标可正式首打、补打及回执恢复。左侧仅“标签打印”，四项功能在页内。测试任务与标签及对应888旧三表登记已清理；保留编码占号及流水，清理完成时打印任务0条，后续以实际记录为准，原业务保留。生产008未动，未发送实体打印。

以下既往接入记录中涉及模拟、禁打、0.7.0及清理前数量的段落属于此前阶段；当前部署与操作以以上更新为准。核心业务防重合同继续有效。

2026-10-19后续实测和清理边界见[独立系统联调复核](../New_Label_Print_System/docs/LabelSystem联调复核.md)及[复用清单](PRINT_REUSE_AND_CLEANUP.md)。现有开发HTTP与模拟恢复已分别记录，其他真实SQL矩阵与硬件验收仍未完成（首打/补打/丢回执/三表失败恢复已通过）；本轮没有迁移、新建数据库、删除代码、生产008操作或真实打印。

更新日期：2026-10-19
最新：原New_Label_Print_System独立应用18003已补齐规则管理/当前规则、017正式发布、目标/任务/原码补打和V2回执恢复接口；实际验证规则读取/预览、带图片正式发布和重复发布复用，真实发送与业务写入保持关闭。原54项隔离证据与新版真实SQL全过程验收分开记录。见[接入实施](../New_Label_Print_System/docs/LabelSystem接入实施.md)。

## 1. 文档范围与基线

目标：将历史打印仓库中的模板设计、标签渲染、本机打印助手及回执恢复能力接入当前 Label system，首期处理已有待打印产品标签和原标签补打，保证不重复落账并兼容旧 TraceBack 三表。

用户后续已授权现有开发库升级及888开发部署。隔离54项验收后，又在既有开发库执行017、启用打印功能码并部署独立站点；仅回收专用开发API池，生产008服务不重启/切换，无真实打印，未提交或上传。见 [开发记录](PRINT_PHASE1_IMPLEMENTATION.md)、[开发部署](test-builds/2026-10-19-print-development.md) 与 [隔离验收](test-builds/2026-10-19-print-phase1-isolated.md)。

| 项目 | 阅读基线 | 说明 |
| --- | --- | --- |
| 当前 Label system | 1eb233d | 本地源码基线；专用开发API运行已验证，生产打印能力未更新 |
| 打印系统源码 | `New_Label_Print_System/`，来源基线6f09c78 | 已纳入Label_System主仓库统一维护；仍保持独立部署边界 |
| 上游参考 | [AmigoUK/BarcodeLabelGen](https://github.com/AmigoUK/BarcodeLabelGen) | 参考设计器与渲染能力，不用上游助手替代定制回执协议 |
| 当前数据库 | 仓库包含迁移001–017 | 临时库001–017验收后清理；既有LabelSystem_Dev随后按授权安装017并保留，本次修正未再次迁移 |

配套文档：

- [第一阶段数据库与接口规格](PRINT_PHASE1_SPEC.md)
- [第一阶段验收清单](PRINT_PHASE1_ACCEPTANCE.md)
- [当前业务规则](../BUSINESS_LOGIC.md)
- [当前数据库设计](database/DATABASE_DESIGN.md)
- [TraceBack 共存方案](TRACEBACK_COEXISTENCE_PLAN.md)

第 8 节的确认规则保持有效；实现字段与接口以配套规格和源码为准。源码完成不表示运行环境已改变，真实数据库、设备和部署验收另行记录。

用户于2026-10-19完成D1–D7确认：发送成功自动登记；历史标签核验后原码补打；数量来自单据、补打不占首打数量；三表登记未完成时保留占用并只恢复写表；多规则保存、全局单一当前打印规则；旧确认入口不能绕过新流程；相关处理完成后才释放对应占用。后续授权已扩大到本地开发和隔离迁移联调，生产及真实打印仍禁止。本轮临时库已清理，后续常规联调优先使用既有开发库，破坏性故障另行隔离。

## 2. 第一阶段范围

纳入：

1. 无标签红字退料、部分调拨、部分货位调整已经生成的 PENDING_PRINT 产品标签。
2. 本地有成功 PRINT 记录的产品标签补打。
3. 历史 TraceBack 标签在首次打印事实、原始内容可核验后的补打；证据不足时只进入核对。
4. 模板、图片、发布版本、严格渲染、客户端任务提交、发送结果登记、异常恢复和首次三表登记。

暂不纳入：

- 托盘标签、装卸托和托盘生命周期。
- 普通入库及历史无标签库存的任意制签。
- 一个待打印目标重新按包装拆成多张不同 Code。首期一张待打印目标对应一个首打输出。
- 通过打印再次提交、审核或冲销 U8 业务单据。
- 无可靠硬件反馈时自动推断物理出纸成功。

补打支持同一 Code 多份，每份独立记录成功输出。首打仅允许一份；额外纸张在首打及兼容登记完成后通过补打办理。

## 3. 职责与调用关系

~~~mermaid
flowchart LR
    PC[电脑端打印工作台] --> API[现有 Business API]
    API --> DB[(标签数据库)]
    API --> R[内部 Python 渲染服务]
    API --> GW[现有 U8 Gateway]
    GW --> TB[(旧 TraceBack 三表)]
    PC --> A[本机 Windows 助手]
    A --> P[本机打印机]
    A -->|提交结果| PC
    PC -->|发送结果回执| API
~~~

- 电脑端保留`New_Label_Print_System/`原React/Konva设计器及Flask应用，独立部署；账号和有效功能码由Label system提供，业务账本经Business API管理，原Vue H5/PDA及Android扫码壳继续承担原职责。Flask的模板技术数据不能直接替代017不可变模板版本。
- Business API 是打印业务写入口：账号、权限、标签锁、正式编码、不可变快照、成功流水、恢复及同步任务。
- Python 只接收已冻结的模板、变量、资源并输出渲染结果，不直连 U8，不生成正式 Code，不修改库存。
- Gateway 是旧三表写入口，沿用当前账套绑定、生产写开关、白名单与审计。
- 助手只接受绑定本机设备的打印任务、记录提交结果和提供原任务查询，不持有数据库凭据，不自行登记库存。
- 旧三表仍是迁移期已有标签的实时来源。当前余额按完整 Code 汇总，位置取最新流水关联的 Goods。

## 4. 复用与改造清单

| 模块 | 源码已确认 | 拟处理 |
| --- | --- | --- |
| 毫米画布、对象编辑、撤销、图层 | 已有 | 复用；改接口适配和现有登录接入 |
| .blg-template.json 导入导出 | 已有图片封装、SHA256 和资源 ID 重映射 | 复用协议；持久化到标签数据库，保留来源信息 |
| 模板版本 | 手动版本、自动覆盖、最近 30 版 | 增加 rowversion 校验和不可变发布版本；已引用版本不得按 30 版规则删除 |
| 动态变量和 PDF 渲染 | 已有 | 冻结业务时间、字段和资源；正式渲染失败关闭 |
| Windows 驱动打印 | 已有 | 复用基础能力；增加设备绑定、逐张输出身份与保守恢复 |
| 本机任务账本 | 已有同任务查回执与阻止重投 | 保留；补跨环境隔离、版本和恢复合同 |
| 浏览器恢复 | 已有单个本地待确认指针 | 改为服务器可恢复；在任何打印动作之前持久化 |
| 旧仓库 Code、数量及三表写入 | 自行生成 SN、直接正数量登记 | 不接入；替换为当前 API/Gateway |
| 旧补打查询 | 限定 SN、打印平台和五段码 | 改为完整 Code 和历史首次打印证据 |
| 用户、物料、仓位和权限 | 两个项目各有实现 | 复用当前 Label system，不再复制用户和业务档案 |
| 通用 ZPL 队列 | 与定制驱动包路径并存 | 首期不接入库存打印链路，避免两条投递通道重复输出 |

尺寸读取模板的 width_mm/height_mm。仓库示例规格不是现场业务模板清单；实际历史模板、图片、字体和打印快照不随源码导入。

## 5. 不重复落账的数量约定

| 场景 | 业务提交时的既有落账 | 首打本地变化 | 首次旧三表登记 |
| --- | --- | --- | --- |
| 无标签红字退料 Q | 新目标余额 Q，退料流水 +Q | PRINT：Q → Q，change_qty=0 | 新 Code +Q，固定事件仅一次 |
| 部分移动 Q | 来源 -Q，目标 +Q | 目标 PRINT：Q → Q，change_qty=0 | 目标新 Code +Q；不重做来源 -Q |
| 完整移动 | 原标签已更新位置 | 不生成新的目标首打 | 不因打印再写移动流水 |
| 补打 N 份 | 原余额及状态不变 | N 条 REPRINT，全部 change_qty=0 | 不产生 PRINT_TARGET，不加正流水 |
| 回执重交或同步重试 | 已提交结果保持 | 不新增成功流水或张数 | 返回原结果，禁止第二次 +Q |

数量来源为单据明细及其已经生成的待打印目标，不另行录入一套数量。例如单据应打 100、累计首次登记 60，则剩余应打 40；原标签补打不增加累计首次登记量。已有目标首打的本地库存变化为零，不能为了统计首打数量而再写一条 +Q。按 D1，成功张数统计改为数据发送成功并完成本地登记的份数，不再宣称是实际出纸张数。

数量计算按来源明细和单位分别核对，不用整张单据表头数量代替具体目标数量。发送结果未知或三表待登记的原任务继续占用对应目标，不能把它重新当成未打印数量再次安排。

## 6. 首打、补打及恢复流程

### 6.1 首打

预览不取号 → 显式开始首打 → 本地事务锁目标并分配 Code → 冻结并严格渲染 → 授权本机投递 → 助手确认数据发送/驱动接受成功 → 自动在本地事务写 PRINT 和 PRINT_TARGET 任务 → Gateway 三表幂等登记 → 本地确认同步并释放打印锁。无需操作员确认出纸。

按已确认 D4，发送成功并完成本地登记后可将标签置 ACTIVE，但三表登记未完成时必须保留打印锁和原同步任务，所有后续业务仍拒绝使用。界面显示“登记未完成”，三表确认成功后才显示整个任务完成并解除对应占用。ACTIVE 不能独立等同于可用。标签镜像字段的约束处理见第一阶段规格，不伪造旧流水 ID。

Code 一经分配保留给同一标签，不因失败、取消、超时、规则换版或跨日重新取号。未知输出不得自动重投。

### 6.2 补打

核验完整 Code 和首次打印事实 → 取得标签锁 → 冻结原始标签内容 → 为每份生成独立 outputId → 发送成功回执自动登记 → 每份各写一条零数量 REPRINT → 释放打印锁。

不改变余额、业务状态、位置、托盘关系、正式 Code 和首次登记次数。流水中的库存身份、数量和位置取确认时受锁保护的当前状态；纸面内容取原打印快照，两者不能混用。

### 6.3 回执恢复

恢复顺序为“服务端任务 → 原设备账本 → 原输出结果 → 原同步事件”。浏览器 localStorage 只是恢复指针，不是账本。

按已确认 D1，驱动接受/数据发送成功即为业务登记触发点；不检测纸张，不要求人工确认出纸，不以缺纸、卡纸等物理结果作为登记前置条件。仍须区分“收到成功回执”和“发送是否成功尚未知”，避免网络超时导致重复发送。

同一回执重复提交必须返回原 flowId；同一 Code 首次登记重复提交必须返回原旧流水 ID。任何 UNKNOWN 都不能通过超时释放锁、清除浏览器记录或换电脑重新打印来消除。

正常流程发送成功后自动写表，不要求用户另点“同步”。三表写入失败、已提交但响应丢失或本地完成记录失败时，只恢复原事件的写表与确认，不再次发送打印数据、不重新取号、不重复计算首打数量。

## 7. 已确认的实现缺口

- PrintRepository.Prepare.cs已实现正式取号，开发9891已部署但关闭新投递；预览不分配。生产未更新。
- 当前 issued-labels/confirm 不是完整打印入口，不能分配 Code，也不完成首次三表登记。
- Gateway 虽列出 PRINT_TARGET，但当前要求旧流水 ID 大于零并读取来源 Goods，不能登记从未出现的新 Code。
- 旧渲染器会跳过部分对象异常、缺失图片及条码；正式输出必须改为校验失败则不允许投递。
- 助手没有可靠的逐张出纸证明；整批调用失败可能已经部分出纸。
- 旧文件账本使用进程内锁、读取有 30 天期限，不能作为新系统多实例业务账本。
- 旧仓库测试限制 998 写入、008 只读；当前系统测试 888、生产受控 008，不能直接照搬旧连接配置或绕过门禁。

## 8. 已确认业务规则（2026-10-19）

以下 D1–D7 均已获用户确认。验收清单中的相应用例仍全部待执行，不能把“规则已确认”标成“功能已完成”。

| 编号 | 已确认规则 | 实施要求 |
| --- | --- | --- |
| D1 | 已确认：发送成功即登记 | 不确认实际出纸；助手发送/驱动接受成功后自动登记，无人工出纸确认步骤 |
| D2 | 已确认：历史标签核验后补打 | 使用独立历史证明事件识别已首打，不伪造本次 PRINT，不产生首次正账；异常数据继续核对 |
| D3 | 已确认：数量来自单据，补打不占首打额度 | 应打 100、累计首次登记 60，剩余 40；补打不改变这三个数；已有业务落账不因打印重做，未决原任务不能重新安排 |
| D4 | 已确认：三表登记未完成只恢复写表 | 正常自动登记；异常显示“登记未完成”并保留相关占用，不能用于后续业务；只补原写表事件，不重新发送打印数据 |
| D5 | 原则已确认：多规则保存、打印单选 | 复用现有规则表，全局仅一条当前打印规则；历史规则继续识别/补打，已有任务保留原版本；具体规则内容及兼容性尚未核验 |
| D6 | 已确认：旧确认入口不能绕过新打印流程 | 新任务只能凭发送成功回执自动登记；旧接口不能直接标记完成、激活或解锁新任务，历史任务保留受控恢复路径 |
| D7 | 已确认：完成对应处理后再释放占用 | 自动加锁和解锁；A 完成不能连带释放未完成的 B；按标签及业务组核对所有未决输出与同步依赖，禁止按超时一概解锁 |

第一阶段资料已同步；临时库重点场景及既有开发库017升级、9891/888开发部署已验证。真实规则/模板和完整历史矩阵仍待核验，不表示D1–D7尚未确认。既有迁移不改写，生产和测试本轮未升级017。D1替代旧稿人工出纸确认，后续继续同步业务、数据库、API、方案和验收资料。

## 9. 源码证据与验证边界

以下为仓库根相对路径；这是阅读定位，不表示本轮运行测试：

| 结论 | 代码或文档位置 |
| --- | --- |
| 无 Code 目标已有数量 | label-api/src/LabelSystem.Api/MaterialOutboundExecutionRepository.cs，CompleteGeneratedLabelAsync、CreatePendingProductLabelAsync、CompletePartialMoveAsync |
| 旧快照权威口径 | u8-gateway/src/U8Gateway.U8/TraceBack/TraceBackSnapshotReader.cs |
| 新增 PRINT_TARGET 实现 | label-api/src/LabelSystem.Api/Printing/PrintRepository.Receipts.cs；u8-gateway/src/U8Gateway.U8/TraceBack/TraceBackInitialRegistration.cs |
| 当前确认接口边界 | label-api/src/LabelSystem.Api/Program.cs，issued-labels/confirm；MaterialOutboundRepository.cs，ConfirmIssuedLabelAsync |
| 当前同步与根锁释放 | label-api/src/LabelSystem.Api/TraceBackSyncService.cs，FinalizeOperationAsync |
| 真实表结构、约束 | docs/database/001_label_business_schema_draft.sql、031_create_label_code_rules.sql、043_defer_product_label_code_until_print.sql、046_add_traceback_mirror_and_sync_tasks.sql |
| 模板及版本 | New_Label_Print_System/frontend/src/editor/types.ts；backend/app/services/templates.py、template_versions.py、templates_io.py |
| 渲染风险 | New_Label_Print_System/backend/app/services/batch_render.py、pdf_renderer.py、barcodes.py、placeholders.py |
| 旧写入及补打局限 | New_Label_Print_System/backend/app/services/bill_printing.py、label_reprinting.py |
| 回执恢复 | New_Label_Print_System/frontend/src/lib/clientLabelPrint.ts；backend/app/services/client_label_print.py；connector/labeljobs.go |
| 驱动结果语义 | New_Label_Print_System/connector/labeldriver_windows.go、print-label-pages.ps1 |
| 已有测试源码 | New_Label_Print_System/backend/tests/test_client_label_print.py、test_label_reprinting.py；frontend/src/lib/clientLabelPrint.test.ts；connector/labeljobs_test.go |

已验证：54项重点隔离断言；后续既有开发库备份校验/017安装、独立9891/888实际IIS登录、模板发布、PDF预览、图片摘要和物料同步。生产配置仍008，未切换。未验证：实际U8制单、真实助手/驱动、浏览器完整交互、出纸/扫码、完整故障矩阵及开发备份实际还原。见开发部署和隔离报告，不能用合成测试代替现场。

本次未执行 AOCI 校验，索引可能落后于源码；本文未使用导入项目索引代替当前源码核查。
