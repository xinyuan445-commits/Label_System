# 标签管理系统 H5

## 本地开发

```powershell
npm install
npm run dev
```

默认监听 `0.0.0.0:5173`。本机可访问 `http://localhost:5173`，同一局域网的 PDA 使用电脑的局域网 IP。development 环境使用 `admin/admin` 登录。

当前 CT58C 测试地址为 `http://172.16.10.45:5173/`。电脑 IP 变化后，在登录页进入“设置 > 地址设置”修改开发地址。

运行时配置位于 `public/runtime-config.json`。test 和 production 必须关闭 `enableMockLogin`、`enableMockScanner` 和 `enableVConsole`。

```powershell
npm run typecheck
npm run build
```

## 当前页面

```text
/                    业务中心
/favorites           常用功能管理
/functions           全部功能
/work/:code           业务功能页
/login                登录
/settings             设置中心
/settings/address     地址设置
/settings/scanner     扫码测试
/settings/sound       扫码提示音测试
```

业务中心只显示常用功能和“全部功能”入口。常用功能最多选择 6 个，当前按用户名保存在浏览器 `localStorage`；清除应用数据、卸载应用或更换设备后不会保留。以后如需多设备同步，应迁移到后端用户偏好接口。

全部功能当前包含采购管理、销售管理、库存管理、审核管理和信息查询。具体业务页面仍为占位页面，后续按稳定功能编码逐项接入。
