# APK 图标和启动展示

`app-icon.svg` 为蓝底、白色标签与条码、绿色确认标记的矢量源文件。`preview.png` 为 512 像素预览；Android 48/72/96/144/192 像素图标位于 `label_system/miniapp/android/nativeResources/res/mipmap-*`。`startup.xml` 为浅灰底应用图标和名称的启动布局。

图标导出使用 `scripts/render_apk_branding.mjs` 与 `sharp`。当前桌面依赖路径由 Codex 的 workspace dependencies 提供，通过进程变量 `LABEL_SYSTEM_NODE_MODULES` 指定；未指定时按 Node 本地依赖查找。SVG 和生成的 PNG 均保存于仓库，APK 与签名材料继续被忽略。

`build_ct58c_apk.ps1` 在资源解码后调用 `apply_apk_branding.ps1`：替换全部图标密度和两种壳启动布局，清空 `wxa_brand_hint_donut`，将 SDK 底部品牌图替换为透明图，并隐藏对应底部视图。保留框架视图 ID、隐私确认、实际加载流程和扫码桥。使用既有固定包名 APK 作基包时只重建资源，保留第三方运行时 DEX；桥 DEX 单独编译并替换，旧 manifest 桥注册先去重。`apktool.yml` 和 manifest 同时更新版本，防止复用旧基包时回退版本号。

`verify_apk_branding.ps1` 校验 APK 版本、全部图标像素、启动文字清空、桥接组件唯一注册及语音资源。PNG 会被 Android 资源编译优化，因此比较解码像素而不是文件哈希。每次构建签名成功后自动执行，失败不复制最终发布包。

本次可移除 Donut 展示，但容器仍需真实初始化，不能把去除文字理解为完全取消加载。资源变更在 APK `0.5.3 / 109` 生效，仅更新 H5 不会改变已安装的图标或原生启动页。

## 0.5.4 启动过渡动画

`StartupBrandView.java` 在原生启动布局以 Canvas 绘制同款图标，正常动画倍率下 720ms 完成淡入 / 轻微放大、扫描线、对勾显现。进程内只播一次，离开视图或窗口隐藏时取消；后续 SDK 加载层使用相同图标静态交接，避免跨进程重复播放。Android 系统关闭动画时显示完成态，不以动画控制加载或导航。系统动画开关接口参考 [Android ValueAnimator](https://developer.android.com/reference/android/animation/ValueAnimator#areAnimatorsEnabled())。

`apply_apk_branding.ps1` 保留 SDK 进度、头像、名称及提示视图的原 ID 和对象，在其上增加静态品牌展示层；可选合规提示仍位于展示层上方。第三方 DEX 不修改，不延长 SDK 启动。

桥包装页内嵌 `assets/startup-overlay.html`，背景和图标与原生一致、不重播扫描动画。H5 `0.2.0-dev.81` 首路由就绪并经过两帧后，只向 `http://127.0.0.1:18765` 发送 `app-ready`；包装页校验 iframe 窗口、当前 URL 来源及消息类型，收到后立即开始 120ms 淡出，150ms 内隐藏，不强制等满原生动画。遮罩不接管触摸，减少动态效果设置下取消 CSS 过渡。连接失败立即交回原离线操作；10 秒未收到就绪消息会移除遮罩并提示重试，不把加载较慢等同于业务失败。旧 H5 不发送 ready 时也会走该超时出口，完整衔接需同步部署新 H5。

验证：`cd web; npm run test:startup` 覆盖提前就绪、重复消息、来源校验、地址变化、离线、超时、双帧通知和浏览器直开。APK 校验新增三个启动布局的自定义视图、桥 DEX 类及 HTML 资源检查。必须安装 APK `0.5.4 / 110`，仅更新 H5 不会获得原生动画。
