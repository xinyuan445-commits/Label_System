# 现有开发库与888真实SQL打印验收

这是显式联调工具，不属于普通测试、应用启动或数据库迁移。仅允许`LabelSystem_Dev / UFDATA_888_2021`，不创建SQL Server数据库，不调用U8 COM，不读取生产008，不开放现有站点写门禁。

## 执行入口

从上层项目根目录，取得明确的开发SQL验收授权后执行：

```powershell
./label-api/tools/PrintDevelopmentAcceptance/Invoke-ExistingDevelopmentAcceptance.ps1
```

需要现有017结构、888物料缓存及有效档案、开发账号权限、已配置的WinRM，以及本地.NET8/net48、Python环境、Go和前端依赖。私有SQL连接从专用开发配置读入进程内存，不作为参数输出，也不读取生产配置；遇到账套/数据库不匹配或现有公共开发门禁已开启时停止。

工具不是可直接部署的Business API：仅在本机随机回环端口运行临时宿主，配置中的发送/写入许可只作用于这个临时宿主；结束即停止。原9891/9881/18003及严格渲染服务不重启、不改配置。

## 实际调用链

`New_Label_Print_System/frontend/src/lib/labelSystem.sql-acceptance.test.ts`显式执行原`durable/ls/sendPrint/recoverPrint` → 本机原Flask应用及其身份/权限/白名单/transport → 当前源码Business API → 真实标签库与真实888三表。模板也经原草稿创建和正式发布适配。

本机助手调用重定向到受密钥保护的回环测试桥，使用**产品仓库**Go test二进制。进入RPC之前已强制替换打印机枚举与驱动；不绑定9110、不启动正式助手、不触碰Windows打印队列。普通`npm test`和`go test`会跳过显式联调入口。

Flask验收身份/草稿采用按run区分的共享内存技术模型，连接分池支持并发；它不是新建SQL Server业务库。草稿ID在测试中按run区分，不能用每次重置的ID冒充既有持久设计稿。

## 写入边界与保留

- 每次生成12位runId，批次为`ACPT<runId>`，来源号以`DEVPRINT-<runId>-`开头。仅直接插入本轮合成的“来源业务已落账”夹具，不执行ERP制单，也不修改Inventory、Warehouse、Position、CurrentStock等档案或库存表。
- 临时启用一条专用验收规则并绑定唯一当前规则，创建专用模拟设备；结束恢复原绑定并停用验收规则/设备。正式序号使用独立namespace；不消耗用户原规则的流水。
- 三表写桥只注册TraceBack控制器，仅允许本轮批次的初始登记/验证及快照查询，拒绝其他U8接口和非本轮条码。
- SQL故障使用只匹配**一个本轮确定性Goods UUID**的临时AFTER INSERT触发器，使OnlyCode失败并验证同事务Order/Goods回滚；不使用全表拒写约束，不改索引。finally只在名称及定义归属匹配时删除该触发器。
- 新表和三表的已登记验收记录、Code、零数量流水、事件和归档保留供审核，不删除、回收编码或伪造未发送。异常未决任务仍保留，不能为“清理测试”解除未知锁。
- 14张表原有行的前后SHA256逐项比对；Order/Goods按本轮UUID精确排除新增行，不依赖其不存在的自增主键，不用跨表NOT IN扫描大表。
- 不并行运行多次此工具。原规则绑定发生第三方变更时停止恢复，不覆盖其他管理员选择。机器断电或强制终止后按本轮私有归属文件核对触发器、绑定、设备和未决任务，禁止通配符清理。

## 结果及恢复

私有运行目录：`.tools/print-development-acceptance/<runId>/`，受限ACL包含测试私钥、DPAPI设置、日志和原始快照，不可整体提交或分享。可分享的是脱敏`docs/test-builds/*print-development-sql-evidence.json`及项目报告。

主要证据：原前端日志、11个具名检查点、原有行摘要、逐标签余额/PRINT/REPRINT/registry/PRINT_TARGET/三表关联计数、模拟驱动调用次数、故障触发器和规则恢复状态。前端原函数、Flask、SQL是真实链路；浏览器DOM、员工安装、本机网络策略及硬件输出不在该工具通过范围。

2026-10-19完成run `4eec1364e4a3`的业务检查后，Windows GBK无法输出Vitest成功符号，原runner因此非零退出。SQL和前端检查均已通过，finally清理完成。已修正stdout UTF-8；通过`New_Label_Print_System/deployment/recover_completed_development_acceptance.py`核对原日志、11检查点、14摘要及只读终态后恢复脱敏报告，未为输出错误重复执行SQL写入，原失败状态文件保留。

当前结果见[独立系统真实SQL验收](../../../New_Label_Print_System/docs/LabelSystem真实SQL验收.md)。旧`PrintAcceptance`影子库工具与本工具分开，本次没有运行旧建库工具。
