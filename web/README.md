# H5 本地开发

```powershell
npm install
npm run dev
```

默认监听 `0.0.0.0:5173`。本机可访问 `http://localhost:5173`，同一局域网的 PDA 使用电脑的局域网 IP。development 环境使用 `admin/admin` 登录。

当前 CT58C 测试地址为 `http://172.16.10.45:5173/`。电脑 IP 变化后，需要同步修改 APK 启动配置页中的开发地址。

运行时配置位于 `public/runtime-config.json`。test 和 production 必须关闭 `enableMockLogin`、`enableMockScanner` 和 `enableVConsole`。

```powershell
npm run typecheck
npm run build
```
