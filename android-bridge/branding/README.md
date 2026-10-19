# APK 图标和启动展示

`app-icon.svg` 为蓝底、白色标签与条码、绿色确认标记的矢量源文件。`preview.png` 为 512 像素预览；Android 48/72/96/144/192 像素图标位于 `label_system/miniapp/android/nativeResources/res/mipmap-*`。`startup.xml` 为浅灰底应用图标和名称的启动布局。

图标导出使用 `scripts/render_apk_branding.mjs` 与 `sharp`。当前桌面依赖路径由 Codex 的 workspace dependencies 提供，通过进程变量 `LABEL_SYSTEM_NODE_MODULES` 指定；未指定时按 Node 本地依赖查找。SVG 和生成的 PNG 均保存于仓库，APK 与签名材料继续被忽略。

`build_ct58c_apk.ps1` 在资源解码后调用 `apply_apk_branding.ps1`：替换全部图标密度和两种壳启动布局，清空 `wxa_brand_hint_donut`，将 SDK 底部品牌图替换为透明图，并隐藏对应底部视图。保留框架视图 ID、隐私确认、实际加载流程和扫码桥。使用既有固定包名 APK 作基包时只重建资源，保留第三方运行时 DEX；桥 DEX 单独编译并替换，旧 manifest 桥注册先去重。`apktool.yml` 和 manifest 同时更新版本，防止复用旧基包时回退版本号。

`verify_apk_branding.ps1` 校验 APK 版本、全部图标像素、启动文字清空、桥接组件唯一注册及语音资源。PNG 会被 Android 资源编译优化，因此比较解码像素而不是文件哈希。每次构建签名成功后自动执行，失败不复制最终发布包。

本次可移除 Donut 展示，但容器仍需真实初始化，不能把去除文字理解为完全取消加载。资源变更在 APK `0.5.3 / 109` 生效，仅更新 H5 不会改变已安装的图标或原生启动页。
