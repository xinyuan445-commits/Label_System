# 打印公共服务与早期试验代码

最新：已使用产品仓库Go V2模拟驱动完成[既有开发库/888真实SQL验收](../New_Label_Print_System/docs/LabelSystem真实SQL验收.md)。严格渲染仍复用本目录源码，旧影子工具仍依赖早期助手副本；本轮没有删除这些服务或代码，也没有启动真实打印。

更新：2026-10-19。本目录不是当前产品界面，但不能整体删除。当前产品是 `New_Label_Print_System/` 原 React/Flask 独立系统，开发入口为18003，复用 Label system 账号和权限。

## 当前用途

| 目录或服务 | 当前状态 | 清理边界 |
| --- | --- | --- |
| `renderer/` | 严格PDF/PNG渲染公共服务，Business API仍调用；开发部署使用回环18781 | 保留源码、依赖、字体和测试 |
| `frontend/` | 早期精简工作台，H5菜单和iframe已撤销，9891静态托管关闭 | 可归档候选；先解除旧打包脚本依赖，本轮不删除 |
| `connector/` | 早期V2助手和隔离验收源；V2协议已移入独立仓库助手0.7.0 | 隔离回归桥仍依赖，暂保留；不能直接整目录删除 |
| `deployment/` | 曾部署共享API、Gateway和渲染器，也包含已撤销的嵌入界面部署逻辑 | 保留运行支持脚本；旧一键部署不再用于当前独立系统升级 |
| `../label-api/src/LabelSystem.Api/Printing/` | 正式模板、编码、任务、签名、零数量流水、回执与恢复 | 当前独立系统经Flask白名单转接复用，保留 |
| `../u8-gateway/src/U8Gateway.U8/TraceBack/` | 三表读取、首次登记、历史证据及失败恢复 | 与原业务共用，保留 |

原独立仓库已实际修改并部署，不能再描述为“保持原状”或“不部署Flask”。设计草稿和素材保存在其SQLite技术库；正式发布版本、打印事实和稳定同步任务由Business API/017管理，不建第二套库存账。旧独立模式的直接登记和队列入口在接入模式关闭。

## 运行与验证边界

- 18003提供独立React/Flask应用；9891仅提供开发Business API；9881为回环Gateway；18781为回环严格渲染器；员工助手监听本机9110。
- 9891的`/`、`/printing/`未认证实测返回401，没有提供旧页面。`Program.cs`可选静态托管代码仍存在，配置已关闭；服务器旧静态产物尚未删除。
- 当前开发使用现有`LabelSystem_Dev`和888，017已安装，本轮不重跑迁移、不新建数据库。新发送、U8写入和自动同步恢复保持关闭；没有启动员工助手或真实打印。
- 助手0.7.0的下载包包含V2公钥固定和禁打配置。模拟驱动测试与真实SQL、员工电脑安装分别记录，不能互相代替。

详见[独立系统实施](../New_Label_Print_System/docs/LabelSystem接入实施.md)、[本轮联调复核](../New_Label_Print_System/docs/LabelSystem联调复核.md)、[复用与清理清单](../docs/PRINT_REUSE_AND_CLEANUP.md)。此前54项隔离SQL断言见[历史报告](../docs/test-builds/2026-10-19-print-phase1-isolated.md)，不是新版独立系统的完整SQL验收。

## 业务规则

首打数量取来源单据和关联目标，业务提交已记的库存不再加减；PRINT/REPRINT为零数量流水。只有首次目标登记一次稳定PRINT_TARGET旧表事件；补打沿用原码和原快照，不新增旧表正数。预览不占正式编码；UNKNOWN保留占用并查询原任务，三表失败只恢复原事件，不再次调用助手。

正式模板、图片、字体、归档、未决任务和回执不能作为普通构建垃圾删除。D1按驱动接受发送登记，不等待或伪造出纸确认。完整规则见[规格](../docs/PRINT_PHASE1_SPEC.md)和[验收清单](../docs/PRINT_PHASE1_ACCEPTANCE.md)。

## 纯测试与维护

`renderer/`运行其pytest；原试验助手运行`go test ./...`。产品助手和前端的回归在`New_Label_Print_System/`执行。上述测试不执行迁移；`label-api/tools/PrintAcceptance/Invoke-IsolatedAcceptance.ps1`会另建并删除临时数据库，**本轮没有运行，也不作为现有开发环境的常规命令**。

当前独立系统升级使用其`deployment/Build-LabelSystemDevelopment.ps1`等脚本。不要运行本目录早期部署脚本来恢复9891打印界面。打印模板、编码、状态、协议、权限、部署及落账规则调整时，同步维护主项目和独立仓库业务资料；历史证据保留阶段名称和原始结果。
