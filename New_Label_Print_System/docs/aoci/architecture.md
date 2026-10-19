# 项目架构与定位

2026-10-19正常模式更新：当前状态与操作以[正常打印、PDF及数据清理](../LabelSystem正常打印与数据清理.md)为准。888已取消模拟运行，使用本机0.7.1助手；PDF目标可正式首打、补打及回执恢复。左侧仅“标签打印”，四项功能在页内。测试任务与标签及对应888旧三表登记已清理；保留编码占号及流水，清理完成时打印任务0条，后续以实际记录为准，原业务保留。生产008未动，未发送实体打印。

以下既往接入记录中涉及模拟、禁打、0.7.0及清理前数量的段落属于此前阶段；当前部署与操作以以上更新为准。核心业务防重合同继续有效。

最新接入实测见[真实SQL验收](../LabelSystem真实SQL验收.md)：现有LabelSystem_Dev/888首打、补打、丢回执、三表失败及提交后丢响应恢复通过，发送为强制模拟；新表PRINT/REPRINT总数量变化为0，补打/恢复不增加旧正数。保留验收记录后全库8/22/20、任务6/Code5，公共门禁仍关闭；历史全矩阵、安装/浏览器/硬件另验。

最新Label system接入状态见[实施与验收](../LabelSystem接入实施.md)。

范围仅为 BarcodeLabelGen。AOCI 可选，未安装时按普通文档、源码和测试开发；使用其 CLI、MCP 和辅助脚本时必须显式指定本目录，不得自动向上定位到 labelPrint 的 Git 根目录。初始化不创建嵌套 Git 仓库。本文以当前源码为依据；旧版上游文档仅供参考。交接入口为根 README、项目说明、docs/README 与项目复用指南。

## 运行组成

新增Label system开发模式：原React/Flask独立应用运行18003，`standalone_labelsystem.py`读私有配置，`auth/labelsystem.py`转交9891校验账号与有效权限。SQLite保留设计草稿、素材和身份引用；正式版本、任务及回执转接已接入Business API/017。888业务写入和真实发送保持关闭；旧打印、队列及agent被统一关闭。见[接入边界](../LabelSystem接入边界.md)及[开发验收](../LabelSystem开发验收.md)。下列通用模式保留兼容。

- `backend/local_server.py`：Windows 本地试运行入口，读取项目 `.env`，使用 SQLAlchemy SQLite 数据库 `.local-data/pilot.db`；标签图片、PDF、上传文件与密钥保存在 `.local-data`。启动只补齐缺失用户和规格，不清空已有模板。通过 `LABEL_HOSTS` / `LABEL_PORT` 显式绑定地址，提供 API 和 `frontend/dist` 静态页面。
- `start.ps1`：从自身目录启动本机服务，检查配置、同步后端依赖，缺少前端产物或指定 BuildFrontend 时安装并构建前端；不清空现有数据。
- `backend/app/factory.py`、`config.py`、`db/session.py`：Flask 应用、会话、认证、业务路由和 SQLAlchemy 引擎。Compose 部署还保留 PostgreSQL / Redis 路径，不能把它与本地 SQLite 运行方式混为一谈。
- `frontend/src/App.tsx`：React 路由。首页 `/`、单据 `/bills`、库存 `/stock`、补打 `/reprint`、模板 `/templates`、设计 `/templates/:id/edit`、生成记录 `/history`、打印机 `/devices`、用户 `/admin/users`。设计页独立全屏，库存页使用视口内布局。
- `frontend/src/editor/`：Zustand 管理画布状态，Konva 绘制对象；支持文本、条码、图片、线条、方框与表格、纸张规格、方向、缩放、属性修改、自动保存和版本历史。
- `connector/`：Go 本地打印连接器及移动端核心，支持网络/本地队列、ZPL 检查、服务端任务轮询和本机 API。新增客户端 Windows 驱动打印；单据、库存和补打使用此助手，后台不再派发到服务器队列。

## 主要调用链

| 业务 | 前端 | API 与服务 |
| --- | --- | --- |
| 单据查询与首页待打印 | BillPrintPage / DashboardPage | routes/u8.py → services/u8_bills.py → services/u8_sql/*.sql |
| 库存查询与打印 | StockPrintPage | routes/u8.py → u8_inventory.py → bill_printing.py |
| 流水号补打 | ReprintPage | routes/u8.py → label_reprinting.py → pdf_renderer.py / client_label_print.py |
| 标签生成、打印、登记 | 三个打印页面 | routes/u8.py → client_label_print.py → 浏览器手动确认 / 本机 connector 驱动回执 → complete → confirm_run |
| 模板与设计 | TemplatesPage / EditorPage / editor/useAutosave | hooks/useTemplates → routes/templates.py → services/templates.py / template_versions.py |
| 打印机设置 | LocalPrintersPage | useLocalPrinters / localPrinterSettings / localAgent；全部通过访问电脑的助手发现，桌面入口转发中央网页 |
| 生成记录 | HistoryPage | routes/history.py → generated_files.py；文件字节位于 pdfs 目录 |
| 用户与权限 | admin/UsersPage / ProtectedRoute / AppLayout | routes/admin.py、auth/decorators.py → users.py → User / UserDisabledPage / UserHiddenPage |

## 持久化边界

模板、模板版本、用户、上次成功登录时间、常用单据、权限、菜单显示配置和生成记录索引属于本地应用数据库。模板共用时仍保留创建者字段，不代表访问按创建者隔离。打印机默认/隐藏列表属于当前浏览器 localStorage；默认单据和库存模板使用按用户区分的 localStorage 键。更换浏览器时这些偏好不会自动跟随。

U8 查询及追溯登记使用 SQL Server / pyodbc，不能用 SQLite 替代 U8 业务数据。AOCI 第一版只索引源码、SQL 和脱敏结构文档，不启用数据库连接器，不读取真实单据行、用户数据或凭据。

客户端安装、任务回执及恢复见 `docs/client-printing.md`。服务端打印包和回执状态存于原批次目录；客户端任务账本在用户缓存目录，不能用服务器队列代替客户端成功回执。
