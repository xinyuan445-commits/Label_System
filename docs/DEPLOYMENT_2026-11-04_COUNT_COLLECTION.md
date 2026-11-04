# 2026-11-04 盘点采集部署记录

用户授权直接部署并启动服务。程序基于本地提交 `f28a7c6`，另包含本次发现的共享互斥锁权限修复与APK版本更新；未推送GitHub。

## 已发布范围

| 组件 | 运行位置 / 版本 | 结果 |
| --- | --- | --- |
| 正式Gateway | 159:9880，0.11.0.0，008 / UFDATA_008_2021 | running；真实未审核盘点单列表及5行明细读取成功 |
| 正式Business API | 159:9890，0.12.0，LabelSystem_Prod / 008 | ready；登录、盘点权限/列表/明细/核验成功，canSubmit=false |
| 开发Gateway | 159本机:9881，0.11.0.0，888 / UFDATA_888_2021 | running；真实原单PDD26101316的9行明细读取成功 |
| 开发Business API | 159:9891，配置版本0.12.0-count-dev，程序集0.12.0 | ready；登录、盘点接口及核验成功，保留原打印配置 |
| H5 | 172.16.10.45:5175，0.2.0-dev.82 | 已后台启动；页面/运行配置HTTP200；9890对该来源CORS204 |
| APK / 桥 | PX101A，0.5.5 / 111 | 签名/品牌校验通过，覆盖安装并启动；桥health正常，包装脚本包含offline-collection-ready |

H5运行配置仍连接9890正式008；客户端environment=development及APK地址档位不改变服务器账套。没有切换数据库、ERP映射或业务写入开关。两套Business API仅更新配置版本，其余原配置保留；Gateway加密settings内容不变。共享程序包含打印路由，但008没有安装017或开放正式打印工作流，不能据程序升级认定生产打印可用。

四个对应IIS站点/应用池均为Started，应用池设AlwaysRunning、空闲超时0，站点启用自动启动及预加载；H5使用现有后台启动脚本，本机重启后仍按根目录启动脚本开启5175，不新增开机任务。

## 验证与边界

- 888先验证，随后更新008。两个环境均完成真实原盘点单读取；Business API使用构造的非真实标签码进行只读核验，返回LABEL_INVALID，没有创建标签镜像、盘点操作或U8单据。
- 本轮没有执行数据库迁移、盘点回填、U8/旧三表写入或打印。用户另行要求恢复正式admin登录，已使用现有PBKDF2-SHA256/210000迭代格式更新凭据、清除锁定并写PASSWORD_RESET审计；验证登录后退出。密码不记入本文或发布包。
- API重启使旧进程内会话失效，需要重新登录。APK使用相同包名/签名覆盖安装，未清除应用数据或浏览器采集记录。
- 只开放原单选择、本机采集和联网核验；盘点ERP上传仍关闭。无网冷启动、完整应盘范围、原单回填及差异生效仍未完成。
- 未查看截图，未做物理重复扫码/声音、实际断网续扫或整套盘点现场验收；安装和HTTP检查不代替这些测试。

## 部署发现及修复

新Gateway在不同IIS应用池启动时，原Global互斥锁的默认ACL只允许创建池访问，导致UnauthorizedAccessException。继续使用同一Global锁串行U8操作，创建时给IIS_IUSRS仅Synchronize/Modify权限；复用时只请求这两个权限，避免要求FullControl。现存旧锁由服务器管理员补齐同等权限，没有更改U8权限或写入门禁。

InventoryCountSmokeTest新增共享锁权限及受限句柄复用测试，通过；无SQL/COM调用。Gateway构建通过。IIS停止/启动之间等待应用池真正到达目标状态，避免Stopping状态立即Start失败。运行时错误排查仅在loopback开发站临时使用RemoteOnly，完成后恢复原Web.config。

C盘构建期间空间不足。旧/失败APK临时构建目录移至 `D:\LabelSystemBuildCache\20261104`，保留安装包、签名和设备备份；未删除业务资料。

## 发布目录与回退

- 正式API当前：`D:\LabelSystem\Api\releases\20261104_count_collection`；上一版：`D:\LabelSystem\Api\releases\20261019_095703`。
- 正式Gateway继续使用：`D:\U8Gateway\site`；程序文件回退副本：`D:\U8Gateway\backups\20261104_count_collection`。App_Data和外部凭证目录未覆盖。
- 开发API/Gateway当前：`D:\LabelSystem\DevPrinting\releases\20261104_count_collection\api` / `gateway`；上一版分别为 `20261020_104510_print_api\api` / `20261019_145739\gateway`。
- 正式API外置配置仍为 `D:\LabelSystem\Config\label-api.json`；开发仍为 `D:\LabelSystem\DevPrinting\Config\label-api.dev.json`。没有将密码、连接串或API Key拷入仓库。

回退时停止对应站点/应用池并等待Stopped，恢复上版程序路径或Gateway程序文件，然后启动并验证健康、来源账套和配置。程序回退不撤销用户要求的凭据恢复，也不回退业务数据库。

## 同日后续：页面规范及盘点格式对齐

H5升级0.2.0-dev.83并重启本机5175：统一固定页头/底栏、表头和扫码分步、独立选单/记录/原单明细及数量编辑。新增根UI_DESIGN.md、AGENTS.md和页面设计记录，区分规范、实际实现与待办。

执行实际Vue脚本及fake-indexeddb的5项页面流程测试，与原13项存储/桥测试共18项通过；类型检查/生产构建通过。运行时版本dev.83、首页、盘点Vue模块及启动模块均HTTP200，API9890 ready、Gateway9880 running。未查看截图、操作真实扫码或清除设备记录。

本轮沿用原5175来源和9890正式008连接，未发布API/Gateway/APK、执行迁移或业务写入。PDA已有页面不强制自动刷新，保存当前输入后重新打开功能查看新版；实机视觉、键盘/滚动及物理扫码仍单独验收。


## 同日后续：盘点直接复用共用制单组件（dev.84）

H5升级0.2.0-dev.84，重启本项目5175进程并通过原启动脚本后台启动。九类制单直接使用同一个OutboundDocumentView，删除独立盘点页；源单选择、明细、数量确认和单据管理复用现有组件。盘点原单只按单号/日期查询、选择一张整单，显示日期、仓库及条数，不显示合并/明细模式、选单类型或产品查询。

盘点数量确认后写入普通草稿IndexedDB，落盘成功才反馈；旧记录幂等导入普通草稿，原始缓存保留。完成做单在线自动重新核验原单与TraceBack标签，离线保留完整Code和明确数量待后续复核。共用草稿同步新增原子版本保护，防止旧后台响应覆盖新数量。没有独立采集页或手工核验阶段。

验证23项自动测试通过（共用组件流程/SSR渲染及同步并发保护10项，原模型/缓存/壳13项），类型检查和生产构建通过。运行配置与编译模块版本dev.84，首页、业务入口、共用制单和选单模块HTTP200；Business API9890 ready/0.12.0，Gateway9880 running/0.11.0.0。H5仍为172.16.10.45:5175，连接9890正式008；本次未发布API/Gateway/APK，没有SQL迁移或U8/旧三表写入。

ERP盘点原单回填及差异落账尚未实现，管理页盘点上传禁用；完成做单仅保存标准待上传草稿。未强制刷新PDA，用户保存当前输入后重新打开功能；物理扫码、声音、断网及实际视觉/滚动仍待现场验收。未查看截图、清空存储或推送GitHub。


## 同日后续：原单盈亏类别（dev.85 / API0.12.1 / Gateway0.11.1）

修复盘点表头遗漏盘盈类别/盘亏类别：原单document返回cIRdCode、cORdCode及对应Rd_Style名称，H5选单回填并只读显示，使用原单名称不依赖档案预加载；草稿恢复、换单/清除保持一致。类别值纳入完整源指纹，旧基线需保留数据后重新选择同一原单。

先888后008部署。开发API新路径D:\LabelSystem\DevPrinting\releases\20261104_201007_count_categories\api，配置版本0.12.1-count-dev；生产API新路径D:\LabelSystem\Api\releases\20261104_201014_count_categories\api，配置版本0.12.1。两者程序集0.12.1，沿用原web.config和外置配置，仅更新配置版本。Gateway保留原物理目录，仅更新Api/Contracts/U8三个DLL至0.11.1；App_Data、加密配置及API Key不改。

服务停止后更新并启动，健康检查通过。两个环境的真实原单GET均返回盘盈107/盘盈入库、盘亏207/盘亏出库；验证过程只读，无迁移、无U8/旧三表业务写入。H5已重启5175到dev.85，运行版本和共用制单模块HTTP200，API9890 ready、Gateway9880 running；APK不变。IIS池重启后原登录会话可能失效，重新登录即可。

H5 24项自动测试和类型/构建通过；API盘点21项测试、Gateway只读合同测试及两端Release发布构建通过。备份位于D:\LabelSystem\backups\20261104_201007_count_categories_888及20261104_201014_count_categories_008，保存前API路径、配置及三个Gateway旧DLL；回退时停池后恢复，启动验证。配置备份只留服务器。ERP盘点上传仍禁用；实机视觉/扫码待验，未截图或清除设备数据，未推送GitHub。
