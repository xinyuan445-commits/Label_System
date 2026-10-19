# 2026-10-19 现有开发库017迁移及打印工作台部署

> 历史部署证据：本报告中的9891 H5及/printing/入口已撤销。用户明确产品是`New_Label_Print_System/`原独立系统，当前入口18003、复用Label system账号权限，888只读。017保留；当前结果及未完成的017任务/回执适配见[独立系统验收](../../New_Label_Print_System/docs/LabelSystem开发验收.md)。下列旧入口和功能验证不表示当前入口仍可使用。

状态：已部署到开发环境供测试。使用既有 `LabelSystem_Dev` 和888；生产008的配置、程序、站点路径与绑定保持不变。没有启动真实打印。

## 测试入口

1. 打开 [开发标签系统](http://172.16.1.159:9891/#/login)，使用原开发库账号登录。
2. 从“标签打印”入口进入，或登录后打开 [打印工作台](http://172.16.1.159:9891/printing/)。
3. 可测试模板编辑、保存、发布、预览、物料查询、待打印列表及规则设置。已发布“开发预览模板 76.2×50.8”（模板1/版本1），可先用它做示例预览。

页面显示“打印发送已关闭，可编辑模板和预览”。生成首打、补打及发送按钮关闭，服务端也拒绝新投递；没有安装、启动或配对真实打印助手。现有待打印标签1条，打印任务0，正式Code登记0。当前打印规则未自动选定，可在设置中选择现有规则；设置和预览不会占正式编码。

## 017是什么

017是标签数据库第17次结构升级，执行文件为 `docs/database/052_label_print_phase1.sql`；文件编号052是仓库脚本排序，`schema_migration.migration_id`记录017。

- 增加6张技术表：模板、模板发布版本、图片、版本图片关联、设备配对、当前打印规则绑定。
- 在已有业务操作、明细、事件和库存流水中补充打印任务、逐份输出、原快照、尝试及恢复信息。
- 增加输出唯一、首打唯一、初始登记任务唯一和零数量约束，防止补打和重复回执再次落账。
- 复用已有用户、权限、编码规则和库存账本；脚本不创建ERP单据、不增减标签余额，也不改变旧TraceBack三表结构。

脚本头“仅准备”是编制时备注，执行后不改写脚本字节；实际安装状态以迁移记录和本报告为准。

## 备份、迁移及数据核对

仅对 `LabelSystem_Dev` 执行，连接打开后再次断言 `DB_NAME()`，确认已有016且尚无017。未执行跨环境批量迁移脚本。

迁移前完成 `COPY_ONLY + CHECKSUM` 完整备份及 `RESTORE VERIFYONLY WITH CHECKSUM`。备份文件为SQL默认备份目录中的 `LabelSystem_Dev_pre_print017_20261019_144919.bak`，保留供开发库恢复。

安装017并登记SHA-256：`8E6FEE2FE6CA541A4968710C4E1D6DAE9C357DF9A3818EE7026C8254CC53911F`。

| 项目 | 迁移前 | 部署及冒烟后 |
| --- | --- | --- |
| product_label | 3 | 3 |
| inventory_flow | 9 | 9 |
| business_operation | 8 | 8 |
| 新打印任务 | 0 | 0 |
| 正式Code登记 | 0 | 0 |

开发库7个打印功能码已启用，沿用现有管理员及权限机制。为验证测试入口，新增技术数据：预览模板1/版本1、2×2测试图片1；同步888物料缓存31,409条并记录同步状态。未生成业务单据、打印流水或TraceBack数量流水。

## 实际部署

| 组件 | 开发位置/配置 |
| --- | --- |
| 标签库 | `LabelSystem_Dev`，迁移001–017 |
| U8目标 | `UFDATA_888_2021` / 888；Gateway环境test，API环境development |
| H5、打印工作台、API | IIS `LabelSystemDevPrint` / `LabelSystemDevPrintPool`，9891同源提供 |
| Gateway | IIS `U8GatewayDevPrint` / `U8GatewayDevPrintPool`，仅回环9881 |
| 渲染器 | 仅回环18781，任务 `LabelSystemDevPrintRenderer`，Local Service身份；开机启动，失败重试 |
| 代码目录 | `D:\LabelSystem\DevPrinting\releases\20261019_145739`；后续HTTP兼容补丁仅更新该开发API和静态工作台 |
| 业务配置 | `D:\LabelSystem\DevPrinting\Config\label-api.dev.json`，与生产配置分离 |
| 归档 | `D:\LabelSystem\DevPrinting\printing-artifacts`、`operation-archive` |
| Cookie | `LabelSystem.DevPrint.Session`，避免同一IP不同端口的生产会话被覆盖 |
| 模块开关 | Enabled=true，AllowNewDispatch=false，RecoveryEnabled=false |
| 写门禁 | Business API U8WritesEnabled=false；Gateway总写入和生产写入均false |

部署工具：`label-print/deployment/Deploy-Development.ps1`，仅创建专用开发站点、应用池、配置和局域网9891防火墙规则；拒绝覆盖已存在站点，不调用原生产部署脚本。Python运行时及依赖独立放在开发发布目录，不修改服务器全局Python环境。渲染服务不连接数据库、不调用驱动。

本次只回收新建的开发API应用池。生产9890和9880站点及其应用池未停止或回收。六个生产配置/程序文件SHA-256、原路径、绑定和站点状态均与部署前一致；核对过程未读写008业务表。

## 实测结果

- 根H5、`/printing/`及API健康检查200；API返回development、数据库ready、U8写入关闭。前端运行配置使用同源地址，未指向生产API。
- 原开发管理员登录成功，收到独立开发Cookie；38项API纯测试通过，含默认/开发Cookie隔离。
- Gateway实际只读查询返回888、`UFDATA_888_2021`及31,409条物料；开发缓存同步SUCCESS，来源一致。
- 实际IIS接口保存、发布模板及调用独立Python渲染成功；PDF为1页、216×144pt，即76.2×50.8mm，含预览物料与数量12.5；正式Code和库存流水未增加。
- 实际图片上传、元数据SHA-256及二进制一致性验证通过。新增只读 `GET /api/v1/print/assets/{id}/metadata` 供模板包导出使用，不返回内部文件路径。
- 首打和补打POST均409拒绝，打印任务仍0；前端发送关闭时先查能力，不联系本机助手。
- 工作台7项测试及构建、H5构建通过。模板新建和请求号使用支持局域网HTTP的安全随机接口；图片哈希以服务端验算结果核对，去除HTTP下不可用的randomUUID/subtle依赖。错误图片哈希会阻止模板创建，可能留下未被模板引用的已校验技术图片，不影响库存。

脱敏机器证据见 [部署证据](2026-10-19-print-development-evidence.json)。此前54项首打/补打/故障恢复属于 [独立隔离验收](2026-10-19-print-phase1-isolated.md)；本轮开发环境没有重新发送或写三表来重跑这些场景。

## 未验证与运维

未查看页面截图或进行完整浏览器交互验收。实际Windows打印、扫码、HTTPS/浏览器本机网络访问策略、真实模板迁入及完整历史数据故障矩阵仍未验收。生产和测试库本轮未执行017。

自动审批拦截了删除未使用部署目录的操作，未提供更具体原因；调试阶段未引用的发布目录保留在开发根目录下，不运行、不影响当前站点。没有新建临时数据库。

需要暂停开发入口时，仅停止专用开发站点/应用池及渲染任务，保留017和开发数据；不要删除迁移记录或以回退为由操作生产库。源配置、签名私钥及渲染密钥受文件ACL保护，不归档到项目文档或上传。
