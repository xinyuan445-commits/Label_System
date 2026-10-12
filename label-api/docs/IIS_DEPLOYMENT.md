# Label System Business API IIS 部署

## 固定资源

| 项目 | 值 |
| --- | --- |
| IIS 站点 | `LabelSystemApi` |
| 应用池 | `LabelSystemApiPool` |
| 端口 | `9890` |
| 发布版本 | `D:\LabelSystem\Api\releases\yyyyMMdd_HHmmss` |
| 外置配置 | `D:\LabelSystem\Config\label-api.json` |
| 操作凭证 | `D:\LabelSystem\OperationArchive` |

每次部署创建新的版本目录并把站点切换到该目录。旧版本不自动删除，便于确认后回退；配置和操作凭证位于版本目录之外，不随程序包覆盖。

## 服务器前置条件

- IIS 已安装。
- 安装与目标框架一致的 Microsoft ASP.NET Core Hosting Bundle。
- 9890 端口未被其他站点占用。
- `LabelSystemApiPool` 对当前版本目录只读，对操作凭证目录可修改，对外置配置只读。

安装 Hosting Bundle 可能需要刷新 IIS 服务，应安排在允许短暂连接中断的时间执行。后续普通版本部署只停止和启动 `LabelSystemApi` 自身站点及应用池。

## 外置配置

外置配置必须包含：

- `ConnectionStrings.LabelSystem`：开发、测试或生产标签库连接串。
- `U8Gateway.BaseUrl` 和 `U8Gateway.ApiKey`。
- `LabelSystem.Environment`、U8 账套、年度和数据库名。
- `Archive.RootPath`。
- H5 的 CORS 来源。
- `ManagementAccess.PasswordHash` 和短时授权分钟数；只保存哈希，不保存管理密码明文。

密码和 API Key 不得写入仓库、发布包、H5 或部署文档。正式切换前必须保持 `U8WritesEnabled=false`；开发登录只能在 development 环境开启。

## 发布和部署

```powershell
.\scripts\Publish-Iis.ps1
.\scripts\Deploy-Iis.ps1 -Server 172.16.1.159 -ConfigPath <本机忽略目录中的配置文件>
```

服务器已安全配置且只更新程序时，可省略 `ConfigPath`，脚本会复用服务器外置配置。

部署脚本会检查 Hosting Bundle 和端口、复制新版本、设置权限、切换站点并调用 `/api/v1/health`。健康检查必须同时满足 `status=ready` 和 `databaseReady=true`。

`LabelSystemApiPool` 使用 `AlwaysRunning`，空闲超时为 0；站点启用预加载。development 会话仍为进程内状态，部署、手工回收、服务器重启或其他进程终止仍会使现有令牌失效。

## 回退

1. 停止 `LabelSystemApi`。
2. 把站点物理路径改回上一个 `releases` 目录。
3. 启动应用池和站点。
4. 验证健康接口和数据库环境。

回退程序版本不回退数据库迁移。数据库变更必须保持向后兼容，无法兼容时另写经过验证的回退迁移。
