# H5 本地开发

```powershell
npm install
npm run dev
```

默认地址为 `http://localhost:5173`。development 环境使用 `admin/admin` 登录。

运行时配置位于 `public/runtime-config.json`。test 和 production 必须关闭 `enableMockLogin`、`enableMockScanner` 和 `enableVConsole`。

```powershell
npm run typecheck
npm run build
```
