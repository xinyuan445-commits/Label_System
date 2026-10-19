# APK 构建记录

## 0.5.3 图标与启动展示更新

- 日期：2026-10-19；包名 `com.tencent.weauth`，versionCode `109`，桥协议 `pda-bridge-v1`
- 文件：`releases/ct58c-test/label-system-0.5.3-ct58c.apk`，36,730,500 字节
- SHA-256：`2952BC70061C119FFA6D7D0797C5E6B7DC846107D16C17202C9696580160A565`
- 展示：替换五档尺寸标签图标，两种壳启动布局使用应用图标和名称；清空 `Powered By Donut` 文字，隐藏 SDK 底部品牌图。实际初始化、隐私确认和扫码流程保留
- 构建：复用固定包名的 0.5.2，只解码并重建资源，保留第三方运行时 DEX；重新编译桥 DEX，去重 manifest 桥组件，同时更新 manifest 与 apktool 版本字段
- 校验：签名 v2/v3 通过；图标解码像素、空启动文字、桥唯一注册和语音资源验证通过。源码默认地址已包含 5175
- 安装：PX101A `4622564` 覆盖升级成功，未卸载或清除应用数据；应用进程运行，Bridge 返回 `0.5.3 / 109`，development、production 地址均为 `http://172.16.10.45:5175/`
- 边界：未查看设备截图；新图标和冷启动观感、物理扫码仍需用户实机验收。H5 元数据同步为 `0.2.0-dev.80`，不修改 API、数据库或 U8 数据
- 复现与安装记录：`android-bridge/branding/README.md`、`docs/test-builds/2026-10-19-px101a-apk-0.5.3.md`

## 2026-10-19 PX101A 安装验证

- 设备：PX101A，ADB 序列号 `4622564`，无线地址 `172.16.10.201`
- 安装包：复用 `0.5.2` CT58C 测试包，包名 `com.tencent.weauth`，versionCode `108`
- 安装结果：全新安装成功，用户已完成首次隐私确认；应用主进程和小程序容器均可启动
- 地址配置：设备 development、production 地址均已保存为 `http://172.16.10.45:5175/`，test 地址保持 `http://172.16.10.45:5174/`
- 原生验证：PDA Bridge 在 `127.0.0.1:18765` 正常启动，配置保存和重读一致；设备可连接 H5 5175 端口
- 兼容边界：该包原生适配以 CT58C/Urovo 为基准；PX101A 未发现 Urovo `ScanManager`，但通用扫码广播接收器已注册。物理扫码键、扫码广播格式、连续扫码及提示音仍需实机验收
- 构建边界：本次只安装既有 APK，没有重新构建或修改 APK 文件；`0.5.2` 二进制内的旧默认开发地址不变，新设备地址已通过 Bridge 配置覆盖为 5175

## 0.5.2 CT58C 测试包

- 日期：2026-10-14
- 包名：`com.tencent.weauth`
- versionCode：`108`
- 桥协议：`pda-bridge-v1`
- 文件：`releases/ct58c-test/label-system-0.5.2-ct58c.apk`
- 大小：36,726,093 字节
- SHA-256：`432F551CD3DC502241EE33FEFCCF6D25BE5036B7808E285DA19A2D464DFFF5F4`
- 签名校验：APK Signature Scheme v2、v3 通过
- 原生能力：九类制单页按需启用系统返回守卫；扫码步骤返回表头，表头步骤返回入口页，离开制单页后解除守卫
- 构建说明：微信开发者工具后台返回 `project not found`，本次复用 0.5.1 构建时留存的原始微信 APK 基包，重新注入当前桥代码并完成资源重建、固定包名、版本升级、签名和离线校验
- 安装：已在 CT58C `00772513210059` 原位覆盖安装，未清除应用数据
- 原生验证：应用启动正常；桥健康检查返回 `0.5.2/108`，包装页包含制单页返回监听与 `system-back` 事件；局域网 H5 为 `0.2.0-dev.63`
- 待用户验收：扫码页系统返回、表头页系统返回、存在未保存明细时的确认框，以及业务中心系统返回

## 0.5.1 CT58C 测试包

- 日期：2026-10-14
- 包名：`com.tencent.weauth`
- versionCode：`107`
- 桥协议：`pda-bridge-v1`
- 文件：`releases/ct58c-test/label-system-0.5.1-ct58c.apk`
- 大小：36,726,093 字节
- SHA-256：`B576F1CFFA5184E359795741BC33B7DCFC0B6174A35E6AC0EF1FD21302E035A3`
- 签名校验：APK Signature Scheme v2、v3 通过
- 原生能力：语音模式离线播报中文女声“成功/错误”；提示音模式成功一声短音、失败连续三声短音
- 离线校验：最终 APK 包含 `classes3.dex`、`assets/scan_success_zh_female.wav` 和 `assets/scan_error_zh_female.wav`
- 安装：已在 CT58C `00772513210059` 原位覆盖安装，原应用数据与地址设置保留
- 原生验证：桥健康检查返回 `0.5.1/107`；女声成功、女声错误、一声成功提示音和三声失败提示音请求均返回 `played=true`
- 待用户验收：设备实际听感及真实扫码业务场景

## 0.5.0 CT58C 测试包

- 日期：2026-10-14
- 包名：`com.tencent.weauth`
- versionCode：`106`
- 桥协议：`pda-bridge-v1`
- 文件：`releases/ct58c-test/label-system-0.5.0-ct58c.apk`
- 大小：36,787,505 字节
- SHA-256：`CE38833D9A0EE867BF5CA6E57CC58A2775C4AB78985FDF880E05D6C56D51473B`
- 签名校验：APK Signature Scheme v2、v3 通过
- 原生能力：按 H5 业务校验结果播放成功/失败提示音，或离线逐字母播报“OK”/“NG”；结果音串行且限制积压
- 离线校验：最终 APK 包含 `classes3.dex`、`assets/scan_ok.wav` 和 `assets/scan_ng.wav`
- 安装：已在 CT58C `00772513210059` 原位覆盖安装，原应用数据与地址设置保留
- 原生验证：应用启动，桥健康检查返回 `0.5.0/106`；配置兼容迁移为 `tone/short`；语音模式 OK、NG 两个反馈请求均成功播放且无桥错误日志
- 待用户验收：设备实际听感、设置切换、自动加入、手工确认、重复标签和查询失败场景

## 0.4.1 CT58C 测试包

- 日期：2026-10-12
- 包名：`com.tencent.weauth`
- versionCode：`105`
- 桥协议：`pda-bridge-v1`
- 文件：`releases/ct58c-test/label-system-0.4.1-ct58c.apk`
- 大小：36,697,265 字节
- SHA-256：`49ED791664668E1FE4E811972205976FCB763AF0A6F103324FA49BE2AA66D829`
- 签名校验：APK Signature Scheme v2、v3 通过
- 容器能力：H5 地址启动探测、连接失败本地兜底、重新加载、修改当前环境地址并立即重试、服务恢复自动重连
- 离线校验：最终 APK 包名、版本号、`classes3.dex` 以及“页面无法访问”“重新加载”“更换地址”文案存在
- 安装：当前未检测到 ADB 设备，尚未覆盖安装
- 待用户验收：服务关闭、错误地址、地址修正、服务恢复四种场景

## 0.4.0 CT58C 测试包

- 日期：2026-10-10
- 包名：`com.tencent.weauth`
- versionCode：`104`
- 桥协议：`pda-bridge-v1`
- 文件：`releases/ct58c-test/label-system-0.4.0-ct58c.apk`
- 大小：36,697,265 字节
- SHA-256：`8DA659539C03D993B4D6F9A701FA46C3BD9D59AC027BD34D9D9451FA70E059D2`
- 签名证书 SHA-256：`D6243317075DB9F1F78D9DD3F04E9229C861530C45B9D9C734F948FAB16215CE`
- 签名校验：APK Signature Scheme v2、v3 通过
- 设备：CT58C，序列号记录为 `00772513210059`
- 安装：从 0.3.0 原位覆盖升级成功
- 原生能力：扫码提示音播放与模式持久化
- 原生验证：应用启动、本机桥健康检查、提示音配置读取及静音测试接口通过
- 待用户验收：设置入口和子页面、扫码测试、三种有声提示音及关闭模式

## 0.3.0 CT58C 测试包

- 日期：2026-10-10
- 包名：`com.tencent.weauth`
- versionCode：`103`
- 桥协议：`pda-bridge-v1`
- 文件：`releases/ct58c-test/label-system-0.3.0-ct58c.apk`
- 大小：36,697,265 字节
- SHA-256：`836F1171A4E2A345BE68E39658A17D241B347DACB04E39D144709AAEF4DA7FCB`
- 签名证书 SHA-256：`D6243317075DB9F1F78D9DD3F04E9229C861530C45B9D9C734F948FAB16215CE`
- 签名校验：APK Signature Scheme v2、v3 通过
- 设备：CT58C，序列号记录为 `00772513210059`
- 安装：从 0.2.0 原位覆盖升级成功
- 容器：直接启动 H5、原生地址持久化、关闭 vConsole、隐藏小程序标题栏
- 原生验证：应用启动、本机桥健康检查、地址配置读取与同值保存通过
- 待用户验收：登录页地址设置、实际跨地址切换、页面外观和物理扫码

## 0.2.0 CT58C 测试包

- 日期：2026-10-10
- 包名：`com.tencent.weauth`
- versionCode：`102`
- 桥协议：`pda-bridge-v1`
- 文件：`releases/ct58c-test/label-system-0.2.0-ct58c.apk`
- 大小：36,693,169 字节
- SHA-256：`883DD9ED94FAEEFCA6F0D9C643C5BB5FA80139DB7D15EA44B394DEADCFDCD23B`
- 签名证书 SHA-256：`D6243317075DB9F1F78D9DD3F04E9229C861530C45B9D9C734F948FAB16215CE`
- 签名校验：APK Signature Scheme v2、v3 通过
- 设备：CT58C，序列号记录为 `00772513210059`
- 安装：从 0.1.0 原位覆盖升级成功
- 原生验证：应用启动、Urovo Intent 输出初始化、本机桥健康检查、ADB 模拟 `android.intent.ACTION_DECODE_DATA` 与 `barcode_string` 事件、800 ms 同码去重及 3.5 秒非活动门禁通过
- 待用户验收：物理扫码按键、业务页面显示、连续扫码及页面切换场景

本文件只记录可追溯信息；APK 和签名材料不提交 Git。
