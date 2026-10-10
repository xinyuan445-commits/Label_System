# APK 构建记录

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
