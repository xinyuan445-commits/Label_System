# 盘点上传前的原生只读预检

此工具只执行U8登录和盘点Query，不调用Create、Update、审核或TraceBack写入。成功不等于盘点上传实写已验收。

在服务器本机以有权读取现有受保护配置的账号运行：

```powershell
InventoryCountUploadAcceptance.exe <Gateway的App_Data/settings.json> PDD26101311 <可选报告文件>
```

仅允许888/UFDATA_888_2021。凭据在内存读取，报告仅含阶段、来源/写入范围、原生读取状态、行数和少量公开表头；异常输出先替换已知秘密，不打印配置或连接串。正常返回后释放U8登录。报告应留服务器，不将配置副本或报告打包入仓库。

2026-11-05已成功只读回查1000行，并在未配置startup标志的独立进程验证Gateway自身的旧CLR激活兼容；实际首张上传按用户要求由用户操作，见[发布记录](../../../docs/DEPLOYMENT_2026-11-05_COUNT_UPLOAD.md)。
