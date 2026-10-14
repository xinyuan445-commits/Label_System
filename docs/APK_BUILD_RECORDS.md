# APK 构建记录

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
