# IIS 独立部署

## 目标

- 站点：`U8Gateway`
- 应用池：`U8GatewayPool`
- 目录：`D:\U8Gateway\site`
- 端口：`9880`
- .NET CLR：v4.0，集成模式，32 位
- 配置：站点 `App_Data`，DPAPI LocalMachine 加密
- 操作凭证：`D:\U8GatewayData\operation-archive`

该站点与 `Default Web Site`、`PDA`、`MaterialOutboundApi` 使用不同站点、应用池、物理目录和端口。部署脚本不得修改其他站点绑定或文件。

## 发布包

在开发机执行：

```powershell
.\u8-gateway\scripts\Publish-IisPackage.ps1
```

ASP.NET Web API 项目不能直接使用当前 `dotnet publish` 目录作为 IIS 根目录。标准包把 `Global.asax`、`Web.config` 和 `Admin` 放在站点根目录，程序集统一放入 `bin`。

后续更新只替换程序文件，必须保留：

```text
D:\U8Gateway\site\App_Data\settings.json
D:\U8GatewayData\operation-archive
```

## 应用池权限

`IIS AppPool\U8GatewayPool` 对站点程序目录需要读取和执行权限，对 `App_Data` 与操作凭证目录需要修改权限。应用池设置：

```text
managedRuntimeVersion = v4.0
managedPipelineMode = Integrated
enable32BitAppOnWin64 = true
loadUserProfile = true
startMode = AlwaysRunning
```

## 验收

部署后依次检查：

```text
GET http://127.0.0.1:9880/api/v1/health
GET http://127.0.0.1:9880/admin/config
```

保存配置后，再从服务器本机测试 SQL、U8 登录和只读单据查询。首次部署与程序更新均保持 `enableTestWrites=false`；只有 888 单据写入验收窗口才允许临时开启，008 永远不得加入写入白名单。
