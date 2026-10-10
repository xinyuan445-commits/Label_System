# U8 互操作程序集

本目录保存新网关编译所需的 U8 COM 互操作程序集，不包含 U8 业务代码或配置。

程序集来自目标服务器 U8 16.0 环境，并与该服务器上的已安装组件配套使用。当前编译依赖包括：

- `Interop.U8Login.dll`
- `ADODB.dll`
- `Info_PU.dll`
- `Interop.MSXML2.dll`
- `Interop.VoucherCO_PU.dll`
- `Scripting.dll`
- `SysInfo.dll`
- `VoucherVerify.dll`

2026-10-10 已在测试账套 888 完成采购、销售和库存三组运行时读取验证。`ADODB.dll` 必须随 API 输出复制，不能只依赖项目引用的间接传播。

运行时仍要求 IIS 服务器已正确安装并注册 U8 客户端组件，应用程序池启用 32 位应用程序，并启用旧版 CLR 激活兼容设置。这些程序集不能代替 U8 安装；替换 U8 补丁或互操作程序集后必须重新执行 8 类单据读取验证。
