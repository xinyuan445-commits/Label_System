# 打印复用与旧代码清理清单

2026-10-19正常模式更新：当前状态与操作以[正常打印、PDF及数据清理](../New_Label_Print_System/docs/LabelSystem正常打印与数据清理.md)为准。888已取消模拟运行，使用本机0.7.1助手；PDF目标可正式首打、补打及回执恢复。左侧仅“标签打印”，四项功能在页内。测试任务与标签及对应888旧三表登记已清理；保留编码占号及流水，清理完成时打印任务0条，后续以实际记录为准，原业务保留。生产008未动，未发送实体打印。

以下既往接入记录中涉及模拟、禁打、0.7.0及清理前数量的段落属于此前阶段；当前部署与操作以以上更新为准。核心业务防重合同继续有效。

核对日期：2026-10-19。当前产品为`New_Label_Print_System/`，前轮文档/只读复核后已补做现有开发库/888真实SQL验收；**没有删除代码、发布目录、数据库或运行服务**。仅精确清理一个本轮未发码未发送的失败夹具，已登记证据保留；不将“退出界面入口”等同于“整个目录不用了”。

## 必须保留的复用能力

| 位置 | 依赖证据与职责 | 结论 |
| --- | --- | --- |
| `label-api/src/LabelSystem.Api/Printing/` | `Program.cs`仍调用AddPrinting/MapPrinting；独立Flask的`routes/labelsystem_print.py`转接此API | 保留。正式编码、模板、任务、零数量流水、回执和恢复只有这一套权威实现 |
| `label-api`现有账号、规则、库存、操作/事件及同步实现 | 独立系统逐请求复核身份；017任务复用原业务表、事件与同步任务 | 保留。不能复制另一套用户或库存账替代 |
| `docs/database/052_label_print_phase1.sql`及迁移记录 | 017已安装在现有开发库；六张技术表和新增字段/唯一约束服务当前接入 | 保留且不改写已执行迁移，不反向删表 |
| `u8-gateway/src/U8Gateway.U8/TraceBack/` | 原业务读取/同步与打印首次登记、历史证据、稳定事件恢复共用 | 保留；不能随“打印移出H5”撤掉三表兼容 |
| `label-print/renderer/` | Business API渲染客户端仍调用严格渲染器；现有开发配置回环18781，正式PDF预览实测通过 | 保留源码、字体依赖、测试和部署支持 |
| `label-print/deployment/Run-Renderer.ps1` | 现有专用渲染任务使用其发布副本，源码仍用于维护 | 保留；不删除整个deployment目录 |
| `New_Label_Print_System/connector/` | V2协议已移入0.7.0；`dispatchV2Driver`仍复用`dispatchDriverLabels`，设备、Origin和本机代理共用基础设施 | 保留现用模块；不能按“旧驱动/旧助手”名称整批删除 |
| `New_Label_Print_System/backend/app/routes/u8.py`及旧服务 | 旧通用模式仍有导入和测试，接入模式在认证边界阻断写入口；单据/库存只读仍使用原代码 | 暂保留。关闭入口不代表依赖已消失 |
| 模板/图片、不可变归档、字体、私钥、设备账本和未决任务 | 重启、补打和UNKNOWN恢复依赖原身份、原文件和原证据 | 不作为构建垃圾清理，不覆盖或重建身份 |

## 可以列入后续清理的内容

| 候选 | 已确认状态 | 删除或归档前必须完成 |
| --- | --- | --- |
| `label-print/frontend/`早期精简界面 | 当前H5无菜单/iframe；18003使用`New_Label_Print_System/`中的React应用；9891静态托管关闭 | 从旧构建/部署与文档中解除引用，保留必要历史证据后可单独归档；无需连带删除renderer |
| API `Program.cs`可选DevelopmentWorkspace静态托管分支 | 源码仍存在但当前配置false；未认证旧页面地址返回401，无旧页面 | 明确9891永久只承担API后，在单独代码变更中删除分支并回归API；本轮未改 |
| 服务器旧`wwwroot/printing`及旧试验前端产物 | 托管关闭，物理文件仍保留 | 核对当前IIS物理路径、站点配置、回退备份及文件精确归属；只清静态产物，不碰API/渲染/归档 |
| `label-print/connector/`试验V2副本 | 产品已使用`New_Label_Print_System/connector/`版本，但旧隔离工具的agent-test构建说明仍以此为源 | 将隔离验收桥迁移到产品助手并验证协议兼容，再考虑去重；当前不能直接删除 |
| `label-print/deployment/Deploy-Development.ps1`、`Test-Development.ps1`旧嵌入部署/冒烟逻辑 | 前者仍写ServeStaticFiles=true，后者仍要求9891页面成功；均与当前独立部署不同 | 从公共API/Gateway/渲染部署中分离旧界面假设后归档；当前不用于升级，不能照旧执行 |
| 旧生成包、历史发布目录 | 单凭目录名或时间不能证明无依赖 | 核对任务/IIS/进程路径、未决任务、回退及备份后逐目录判断；本轮不删除 |

`label-api/tools/PrintAcceptance/Program.cs`仍明确使用`label-print/renderer`及构建出的agent-test.exe；其README给出的构建源是试验助手。新增`PrintDevelopmentAcceptance`已直接使用产品仓库Go模拟桥及旧严格渲染器，旧影子工具仍有独立依赖，未迁移完成，不能据新工具通过就删除旧助手副本。本轮未运行创建临时数据库的工具，也不为清理验证而新建数据库。

## 当前实际链路

员工浏览器 → 独立React/Flask 18003 → 当前用户身份的Business API 9891 → 现有标签库/严格渲染器/Gateway；未来允许发送时才通过员工本机9110 V2助手。正式记录不在Flask另落一套，兼容三表也不由原998直写链负责。

H5两个文件与原基线一致，相关入口撤回已确认；其他源文件和部署产物是否可删按上表分别判断。旧代码名字、被门禁阻断或已有另一份副本，都不足以证明可以安全删除。

本轮验证和保留限制见[独立系统联调复核](../New_Label_Print_System/docs/LabelSystem联调复核.md)。打印系统源码已纳入Label_System主仓库的`New_Label_Print_System/`目录；后续清理必须同步更新主项目及该目录的架构、业务、部署、接口和验收资料，保留历史证据，不把旧阶段结论改写成当前全流程通过。
