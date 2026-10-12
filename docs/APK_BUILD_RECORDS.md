# APK 构建记录

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
