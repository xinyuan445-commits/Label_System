# 已确认盘点JSON修复工具

仅显式用于LabelSystem_Dev / 888，复用正式盘点JSON构造器；不由服务启动或业务上传调用。

```powershell
# 默认预览；参数只含服务器现有外置配置路径及操作ID，不传口令。
CountTraceBackJsonRepair.exe <开发外置配置路径> 159
# 预览确认目标后，明确执行已有JSON修复。
CountTraceBackJsonRepair.exe <开发外置配置路径> 159 --apply
```

要求成功盘点操作、全部差异任务完成、归档哈希一致、旧流水仍为最新且余额符合原事件。跨库Serializable事务只更新原Goods/Order的JSON，比较原内容防止覆盖并发修改，并追加业务操作修复审计。

不会创建/修改U8盘点单，不插入OnlyCode、不改sum，不调整本地标签余额，不改已完成任务载荷。重复预览应为0项变更。诊断只输出操作ID、变更计数及异常类型，不输出连接串或凭据。
