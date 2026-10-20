# 标签打印系统 · BarcodeLabelGen

2026-10-20部署调整：独立网页和打印助手均运行在使用电脑本地；桌面入口为`http://127.0.0.1:18003/bills`，业务来源固定连接159的9891及888。159上的独立18003网页、计划任务、专用防火墙规则和`D:\NewLabelPrintDevelopment`已清理，9891/9881及数据库保留。安装、启动和迁移结果见[本地部署与159来源](docs/本地部署与159来源.md)。

2026-10-19正常模式更新：当前状态与操作以[正常打印、PDF及数据清理](docs/LabelSystem正常打印与数据清理.md)为准。888已取消模拟运行，使用本机0.7.1助手；PDF目标可正式首打、补打及回执恢复。左侧仅“标签打印”，四项功能在页内。测试任务与标签及对应888旧三表登记已清理；保留编码占号及流水，清理完成时打印任务0条，后续以实际记录为准，原业务保留。生产008未动，未发送实体打印。

以下既往接入记录中涉及模拟、禁打、0.7.0及清理前数量的段落属于此前阶段；当前部署与操作以以上更新为准。核心业务防重合同继续有效。

基于 BarcodeLabelGen 扩展的内网标签打印系统：网页设计模板，查询 U8 单据或库存，生成、打印和补打标签，登记追溯信息。

当前统一维护仓库：[xinyuan445-commits/Label_System](https://github.com/xinyuan445-commits/Label_System)，本项目位于`New_Label_Print_System/`目录并保持独立部署。原`New_Label_Print_System`仓库保留历史基线参考，不再作为当前代码提交目标。

当前代码版本为 `0.23.2`，本项目的定制功能记录在 [CHANGELOG.md](CHANGELOG.md) 的 Unreleased 部分。文档核对日期：2026-10-20。

## 从这里开始

- [项目说明](项目说明.md)：功能、架构、技术栈、业务规则和限制。
- [文档目录](docs/README.md)：当前文档与历史资料的入口。
- [项目复用指南](docs/项目复用指南.md)：在另一个项目中接入或抽取功能，不要求安装 AOCI。
- [部署与维护](docs/部署与维护.md)：配置、启动、数据库初始化、备份、升级和排错。
- [中文操作帮助](docs/HELP.zh.md)：员工日常使用。

## 当前Label system接入功能

- 首页及标签打印：017待打印目标、来源进度、原码补打、任务和恢复。
- 编码规则：规则管理、样例预览、唯一当前打印规则绑定；规则未选定时不能首打。
- 模板：保留原设计器，发布到017不可变版本，图片和变量经过规范化校验。
- 权限与记录：沿用Label system有效权限，正式编码/快照/零数量流水/三表同步统一由Business API管理。
- 助手0.7.2：正常本机V2签名授权、业务文件名PDF保存与持久回执；不要求人工确认出纸。

## 旧通用模式功能（当前接入不直接使用其写入口）

- 首页查看近 30 天未打印完成的 U8 单据，按明细及原始标签数量计算进度。
- 支持 11 类单据；盘点按实盘数量，负数出库按退回入库，调拨和货位调整结合已有 PDA 标签判断。
- 库存按仓库、货位、料号/名称、批号筛选，勾选并核对数量、包装方式后打印。
- 按 SN 恢复原标签补打；补打不生成新 SN，不新增首次打印追溯记录。
- 共享模板、可视化设计、变量、图片、版本历史，以及模板导入导出。
- 浏览器打印或可选 Windows 打印助手；助手读取访问电脑的本地打印机。
- 个人常用单据、生成记录、账号功能权限与菜单显示、浅色/深色主题。

## Windows 本机启动

Label system接入模式使用[本地安装脚本](deployment/Install-LabelSystemLocal.ps1)，复用Label system账号和159上的888数据。构建与安装命令见[本地部署与159来源](docs/本地部署与159来源.md)。安装后双击项目根目录的“启动标签打印系统.cmd”手动启动网页和助手，不随Windows登录自动运行。

下面的`start.ps1`只用于原通用模式开发，不带Label system账号、017正式任务和固定159来源，不能替代当前接入安装入口。

需要 Python 3.12、uv、Node.js 22；连接 U8 还需 Microsoft ODBC Driver 17 或 18。助手开发另需 Go 1.25。

以下命令都从本目录执行。首次复制配置，已有 `.env` 时保留原文件：

```powershell
Copy-Item .env.example .env
# 编辑 .env，填写本机配置和测试账套连接信息
.\start.ps1
```

启动脚本会检查配置、安装锁定的后端依赖，并在前端产物不存在时构建。改动前端后使用：

```powershell
.\start.ps1 -BuildFrontend
```

默认访问 `http://127.0.0.1:18003/`。初始管理员由 `LABEL_ADMIN_EMAIL` / `LABEL_ADMIN_PASSWORD` 控制；密码留空时启动日志会输出随机初始密码。已有账号不会被启动脚本重置。

本机模式把应用数据保存在 `.local-data`，包括 `pilot.db`、图片、上传文件、打印批次和生成文件。正常重启保留这些数据。**GitHub 源码不包含本机数据，换电脑需要另行备份恢复。**

## Docker 部署

```bash
docker compose up -d --build
docker compose exec web flask seed-admin
```

先在 `.env` 设置 `ADMIN_EMAIL`，可留空 `ADMIN_PASSWORD` 让命令生成初始密码。容器入口自动执行 Alembic 迁移；应用数据库使用 PostgreSQL 16，会话使用 Redis 7。默认仅绑定宿主机 `127.0.0.1:18003`。

本机 SQLite 和容器 PostgreSQL 是独立环境，不能通过复制 `pilot.db` 直接切换。Docker 及客户端助手安装包的部署限制见 [部署与维护](docs/部署与维护.md)。

## U8 边界

- 当前Label system接入固定使用`UFDATA_888_2021`，来源服务器为159；正式打印记录经159的Business API/017管理。
- `ufdata_998_2021`：测试查询、打印及三表追溯登记。
- `ufdata_008_2021`：正式账套，只读查询；应用阻止打印准备、补打与登记。
- `U8_BARCODE_ACCOUNT_CODE` 是条码中的账套段，默认 `008`，与实际连接数据库独立。
- 预览不登记；新打印收到客户端回执或浏览器人工确认后才登记。打印队列接收任务不代表实际出纸。

详细规则见 [业务规则](docs/aoci/business-rules.md)、[追溯格式](docs/u8-pdf-printing.md) 和 [客户端打印](docs/client-printing.md)。

## 开发检查

```powershell
cd backend
uv run --frozen pytest
cd ../frontend
npm test
npm run build
```

完整检查命令见 [部署与维护](docs/部署与维护.md)。本项目已作为主仓库子目录维护，目录内`.github/workflows`不会被GitHub自动作为主仓库工作流执行；主仓库CI接入另行维护。实际运行结果以主仓库Actions及本地验收记录为准。

## AOCI 可选维护

AOCI 用于辅助开发理解，不参与网页运行、数据库连接或打印。没有 AOCI 时，按 [AGENTS.md](AGENTS.md)、项目说明与业务文档开发即可。正式索引与可版本化配置随代码保留，二进制和本机运行状态不上传。

换电脑或抽取到新项目时，不复制 `.codex/config.toml` 的绝对路径，也不把原索引直接当作新项目的有效索引。详见 [AOCI 使用说明](docs/aoci/README.md)。

## 许可证与上游

本项目基于 [AmigoUK/BarcodeLabelGen](https://github.com/AmigoUK/BarcodeLabelGen)，原作者 Tomasz “Amigo” Lewandowski，继承 [GNU GPL-3.0](LICENSE)。复制、修改和分发时保留许可证及相关版权信息。
