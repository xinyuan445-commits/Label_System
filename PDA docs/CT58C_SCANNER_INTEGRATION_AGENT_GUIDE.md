# CT58C PDA 扫码接入指南（Agent Handoff）

本文用于把当前项目中已经过 CT58C 真机验证的物理扫码能力迁移到其他 Android、混合应用或小程序容器项目。目标读者是负责新项目实现的开发 Agent。

## 1. 任务目标

在 CT58C 上按下物理扫码键后，应用无需聚焦输入框即可收到条码，并把条码交给当前可接收扫码的业务页面。

必须保持的设备协议：

```text
输出方式：Intent / 广播
Action：android.intent.ACTION_DECODE_DATA
字符串字段：barcode_string
Urovo 输出模式：0（Intent 模式）
```

兼容接收的第二个 Action：

```text
android.intent.action.SCANRESULT
```

Action 和字段名区分大小写。不要把 `android.intent.ACTION_DECODE_DATA` 改成自定义值，除非同时修改 PDA 扫描服务配置、Manifest、动态 Receiver 和测试。

## 2. 已验证基线与来源

当前仓库的权威实现：

- `android-bridge/src/com/mupi/scanner/UrovoScanReceiver.java`
- `android-bridge/src/com/mupi/scanner/UrovoScanInitializer.java`
- `mainapp/miniprogram/services/intent-scanner.ts`
- `mainapp/scripts/build_apk.ps1`
- `docs/ct58c-scanner-baseline.md`

历史真机基线为 `dist/mupi-scan-v0.2.3-ct58c.apk`。它证明上述 Action、字段和 Urovo 初始化方式在 CT58C 上可用，不代表应把旧 APK 当作新项目安装包。

如果能同时复制源码，优先复制两份 Java 文件，再按新项目包名和运行时通信方式做最小适配。不要凭本文重新发明一套不同的扫码协议。

## 3. Android Manifest 必需声明

将包名替换为新项目实际类名。Receiver 必须允许接收设备扫描服务发出的外部广播；初始化 Provider 不对外导出。

```xml
<application ...>
    <receiver
        android:name="com.example.scanner.UrovoScanReceiver"
        android:enabled="true"
        android:exported="true">
        <intent-filter>
            <action android:name="android.intent.ACTION_DECODE_DATA" />
            <action android:name="android.intent.action.SCANRESULT" />
        </intent-filter>
    </receiver>

    <provider
        android:name="com.example.scanner.UrovoScanInitializer"
        android:authorities="${applicationId}.urovo-scanner-init"
        android:enabled="true"
        android:exported="false"
        android:initOrder="100" />
</application>
```

如果需要使用当前实现的原生错误提示音，还要声明：

```xml
<uses-permission android:name="android.permission.MODIFY_AUDIO_SETTINGS" />
```

注意：

- Android 12 及以上必须显式填写 `android:exported`。
- `exported="false"` 会导致 CT58C 的外部扫描服务无法投递广播。
- Manifest 静态 Receiver 与运行时动态 Receiver 可以同时保留；当前实现会对短时间重复投递去重。
- 如果新项目只使用动态 Receiver，必须确保应用进程已启动，并在 Android 13 及以上使用 `Context.RECEIVER_EXPORTED`。

## 4. 应用启动时初始化 Urovo 扫描器

当前实现使用 `ContentProvider.onCreate()` 尽早执行初始化，不需要 Activity 已打开。Urovo SDK 类通过反射调用，因此非 Urovo 设备找不到类时会静默跳过，不会导致应用启动失败。

初始化顺序必须是：

1. 注册运行时 Receiver，监听两个 Action。
2. 反射创建 `android.device.ScanManager`。
3. 调用 `openScanner()`。
4. 使用 `android.device.scanner.configuration.PropertyID` 设置广播 Action 和数据字段。
5. 调用 `switchOutputMode(0)` 切换到 Intent 输出。

核心逻辑：

```java
private static final String ACTION = "android.intent.ACTION_DECODE_DATA";
private static final String DATA_TAG = "barcode_string";

Class<?> managerClass = Class.forName("android.device.ScanManager");
Object manager = managerClass.getConstructor().newInstance();
managerClass.getMethod("openScanner").invoke(manager);

Class<?> propertyClass = Class.forName(
    "android.device.scanner.configuration.PropertyID"
);
int actionKey = propertyClass
    .getField("WEDGE_INTENT_ACTION_NAME")
    .getInt(null);
int dataKey = propertyClass
    .getField("WEDGE_INTENT_DATA_STRING_TAG")
    .getInt(null);

managerClass.getMethod(
    "setParameterString",
    int[].class,
    String[].class
).invoke(
    manager,
    new Object[] {
        new int[] { actionKey, dataKey },
        new String[] { ACTION, DATA_TAG }
    }
);

managerClass.getMethod("switchOutputMode", int.class).invoke(manager, 0);
```

异常策略：

- `ClassNotFoundException`：视为非 Urovo 设备，记录普通信息并继续启动。
- 其他异常：记录警告，但不要让扫码初始化使整个应用崩溃。
- 不要仅因一次扫码失败就自动切换到键盘楔入模式；先检查广播和字段。

## 5. Receiver 读取条码

主要字段是 `barcode_string`。为兼容固件和其他 PDA，当前实现按以下候选字段读取 `String`、`CharSequence` 或 UTF-8 `byte[]`：

```text
barcode_string
value
com.symbol.datawedge.data_string
data
decode_data
barcode
```

基础读取示例：

```java
public void onReceive(Context context, Intent intent) {
    if (intent == null) return;

    Object raw = intent.getExtras() == null
        ? null
        : intent.getExtras().get("barcode_string");

    String barcode = null;
    if (raw instanceof String) {
        barcode = (String) raw;
    } else if (raw instanceof CharSequence) {
        barcode = raw.toString();
    } else if (raw instanceof byte[]) {
        barcode = new String((byte[]) raw, StandardCharsets.UTF_8);
    }

    if (barcode == null || barcode.trim().isEmpty()) return;
    deliverBarcode(barcode, intent.getAction(), "barcode_string");
}
```

生产实现还应满足：

- 不要先截断、转大写或删除控制字符；原始值交给业务解析层清洗。
- 同一条码可能经两个 Action 连续投递。以条码内容为签名，在 800 ms 内去重，不要把 Action 加入签名。
- 某些固件会让 `barcode_string` 保留上一条值，而把新值写入 `value` 或 `decode_data`。如果读取多个候选，应优先选择看起来像当前业务条码且不等于上一条的值。
- 外部广播是不可信输入。业务层仍须校验长度、格式、权限和当前页面状态。
- 日志可以记录 Action、字段名和长度，不要默认记录完整业务条码。

## 6. 把原生扫码交给业务层

根据新项目架构二选一，不要同时造两套主通道。

### 6.1 普通原生或已有 JS Bridge 项目

Receiver 收到条码后，通过项目现有的进程内事件总线、ViewModel、Repository 或可信 JS Bridge 投递。业务页面进入前台时订阅，离开时取消订阅。

必须保留的行为：

- 只有当前可接收扫码的页面处理数据。
- 忙碌、弹窗确认、上传或页面离开时可暂停接收。
- 页面切换时清理或标记旧事件，防止上一页条码被下一页消费。
- 每条事件至少保留 `barcode`、`action`、`field`、`receivedAt`。

推荐事件结构：

```json
{
  "id": "timestamp-random",
  "barcode": "原始扫码内容",
  "action": "android.intent.ACTION_DECODE_DATA",
  "field": "barcode_string",
  "receivedAt": 1760000000000
}
```

### 6.2 微信多端小程序 APK 或无法直接回调 JS 的容器

当前项目使用原子文件收件箱。仅当新项目存在相同限制时使用此方案。

目录协议：

```text
<USER_DATA_PATH>/mupi-scan/intent-inbox/
├─ bridge-ready.marker
├─ bridge-active.marker
├─ sound-mode.txt
├─ alert-tone.request
└─ <event-id>.json
```

握手和时序：

- JS 创建目录并写 `bridge-ready.marker`，内容可为 `urovo-intent-v1`。
- 页面开始监听时清理旧 `.json` 和 `.tmp`，立即写 `bridge-active.marker`。
- 页面活动期间每 1 秒刷新 active marker。
- Receiver 只向同时存在 ready marker、且 active marker 最后修改时间不超过 3.5 秒的 `intent-inbox` 写数据。
- Receiver 先写 `<id>.tmp`，完成后原子重命名为 `<id>.json`。
- JS 每约 160 ms 按文件名排序取最早的 `.json`，解析、回调并删除。
- 页面隐藏或卸载时停止轮询并删除 active marker。

这套门禁用于防止后台扫码、错误页面消费和半写 JSON。不要改成直接写最终 `.json` 文件。

TypeScript 调用形态：

```ts
const stopScanner = startIntentScanner(
  (barcode, event) => {
    processBarcode(barcode)
  },
  () => pageIsActive && !busy && !confirmationOpen,
)

// 页面隐藏或卸载
stopScanner()
```

## 7. CT58C 设备侧配置

在 CT58C 扫描服务或系统扫码设置中确认：

```text
扫描器：启用
输出模式：Intent / 广播
广播 Action：android.intent.ACTION_DECODE_DATA
数据字段 / String extra：barcode_string
```

不要启用仅键盘输入的模式。键盘楔入依赖输入框焦点，会弹出软键盘，也无法可靠区分扫码和人工输入。

如果初始化代码成功执行，应用会尝试自动打开扫描器并恢复上述 Action、字段和 Intent 输出模式；设备侧仍应核对一次实际配置。

## 8. iData 兼容（可选）

当前初始化器仅在 `Build.MANUFACTURER` 或 `Build.BRAND` 包含 `idata` 时发送：

```java
Intent names = new Intent("android.intent.action.CUSTOMBROADCAST");
names.putExtra("broadcast", "android.intent.ACTION_DECODE_DATA");
names.putExtra("value", "barcode_string");
context.sendBroadcast(names);

Intent mode = new Intent("android.intent.action.BARCODEOUTPUT");
mode.putExtra("android.intent.action.BARCODEOUTPUT", 1);
context.sendBroadcast(mode);
```

这不是 CT58C 的必需步骤。保留时必须继续限定 iData 品牌，不能在所有 Android 设备上无条件发送厂商配置广播。

## 9. 构建接入方式

### 9.1 正常 Android 工程

把两个 Java 类作为普通源码加入工程，通过 Gradle 编译，并直接维护 Manifest。无需生成独立 `classes3.dex`。

### 9.2 已生成 APK 后注入

当前项目因为原始 APK 来自微信开发者工具，采用后处理：

1. Apktool 解包基础 APK。
2. 修改解包后的 `AndroidManifest.xml`，加入 Receiver、Provider 和权限。
3. 用 Android SDK `android.jar` 编译两个 Java 文件，源码/目标级别为 Java 8。
4. 使用 D8，`--min-api 23`，把 class 转为 dex。
5. 把生成的 `classes.dex` 作为 `classes3.dex` 注入重建后的 APK。
6. `zipalign` 对齐。
7. 使用项目正式 keystore 签名。
8. 用 `apksigner verify --verbose` 校验签名。
9. 检查最终 APK 确实包含 `classes3.dex`。

只有同类封闭容器才需要使用 APK 注入。迁移到标准 Android 工程时不要沿用该复杂流程。

升级安装包必须使用与已安装版本一致的签名；否则 Android 不允许覆盖安装。不要把 keystore 或密码写入本文、源码或日志。

## 10. 调用和生命周期要求

每个使用物理扫码的页面都应遵循：

```text
进入/显示页面
  -> 注册或启动扫码监听
  -> 清除上一页面遗留事件
  -> 声明 canReceive 条件

收到条码
  -> 先检查页面仍活动
  -> 检查是否忙碌/弹窗/提交中
  -> 执行业务格式校验
  -> 持久化或处理
  -> 必要时暂停下一次扫码

隐藏/卸载页面
  -> 停止监听
  -> 删除活动标记或取消订阅
```

不要在多个可见页面同时无条件消费同一个扫码事件。

## 11. 验证步骤

### 11.1 不连接真机的广播验证

安装测试 APK 后可用 ADB 显式发送广播。替换包名和 Receiver 类名：

```powershell
adb shell am broadcast `
  -n com.example.app/com.example.scanner.UrovoScanReceiver `
  -a android.intent.ACTION_DECODE_DATA `
  --es barcode_string "TEST-CT58C-001"
```

预期：

- Receiver 日志显示选中了 `barcode_string`。
- 当前活动扫码页收到一次 `TEST-CT58C-001`。
- 非活动页面不消费。
- 连续发送同一码两次且间隔小于 800 ms，只处理一次。

### 11.2 CT58C 真机验收

1. 安装最终签名 APK，不要安装构建过程中的 base APK。
2. 启动应用并进入扫码测试页。
3. 不点击任何输入框，直接按物理扫描键。
4. 确认页面显示 Action、原始值、清理后值、长度、结束符和接收时间。
5. 扫描有效业务条码，确认只处理一次。
6. 快速连续扫描两个不同条码，确认顺序正确且第二条不丢失。
7. 离开扫码页后再次扫描，确认业务页不误收。
8. 杀掉并重启应用，重新进入扫码页验证初始化仍有效。
9. 验证离线场景不会因为网络失败丢失已经接收的本地扫码结果。

### 11.3 H5 服务不可达兜底验收

APK `0.4.1` 起，本机 `127.0.0.1:18765` 包装页会探测当前环境的 H5 地址。探测失败时由包装页直接显示本地兜底，不依赖 H5 服务：

1. 关闭当前 H5 服务后启动 APK，确认出现“页面无法访问”。
2. 点击“重新加载”，确认仍不可达时保留兜底页，不进入 WebView 系统错误页。
3. 点击“更换地址”，输入新的 HTTP 或 HTTPS 地址并保存，确认只修改当前环境地址并立即重试。
4. 输入无效地址，确认提示“地址格式不正确”且不覆盖原配置。
5. 恢复原 H5 服务，点击重新加载或等待自动探测，确认进入 H5。
6. 确认扫码桥、提示音和其他环境地址没有被修改。

### 11.3 发布前检查

- [ ] Manifest 中存在两个扫码 Action。
- [ ] Receiver 为 enabled、exported。
- [ ] Provider 为 enabled、非 exported，authority 唯一。
- [ ] Urovo 初始化调用 `openScanner()`、字段配置和 `switchOutputMode(0)`。
- [ ] 主字段仍为 `barcode_string`。
- [ ] 同码 800 ms 去重已验证。
- [ ] 页面生命周期停止监听已验证。
- [ ] 最终 APK 签名校验通过。
- [ ] 最终 APK 或 AAB 包含扫码桥类。
- [ ] H5 不可达时显示本地兜底，并可重新加载或更换当前环境地址。
- [ ] CT58C 无输入框焦点时真机扫码通过。
- [ ] 已记录最终安装包版本、大小、SHA-256 和真机结论。

## 12. 常见故障定位

### 物理扫码键无反应

依次检查：

1. 安装的是最终包，不是未注入桥的 base APK。
2. PDA 扫描服务已启用。
3. 输出模式是 Intent/广播，不是键盘输入。
4. Action 精确为 `android.intent.ACTION_DECODE_DATA`。
5. 字符串字段精确为 `barcode_string`。
6. Manifest Receiver 存在且 `exported="true"`。
7. Logcat 中是否出现 `MupiUrovoInit` 或 `MupiUrovoReceiver` 警告。
8. 如果使用文件收件箱，ready 和 active marker 是否存在且新鲜。

### Logcat 有广播，但页面无数据

- 检查业务页是否真正启动监听。
- 检查 `canReceive()` 是否因 busy、弹窗或路由状态返回 false。
- 检查文件收件箱路径是否与容器的 `USER_DATA_PATH` 对应。
- 检查 Receiver 是否找到含两个 marker 的 `intent-inbox`。
- 检查 JSON 是否使用临时文件加原子重命名发布。

### 一次扫码处理两次

- 设备可能同时发送两个 Action。
- 去重签名只使用条码值，窗口保持 800 ms。
- 不要用 Action + 条码作为签名，否则跨 Action 的重复无法被拦截。

### 总是收到上一条码

- 同时检查 `value`、`decode_data` 等备用字段。
- 如果 `barcode_string` 等于上一条，而其他候选是新值，选择新候选。
- 不要在 Receiver 中缓存并重复发送最近条码。

### 应用启动崩溃

- Urovo SDK 必须通过反射调用，或确保 SDK 只打进对应产品变体。
- `ClassNotFoundException` 必须作为非 Urovo 设备正常分支处理。
- 初始化失败不得向外抛出导致 Provider 创建失败。

### H5 服务关闭后只能看到系统错误页

- 确认安装版本不低于 `0.4.1`。
- 容器入口必须仍为本机包装页，不能把远程 H5 地址直接作为 APK 起始页。
- 检查 `127.0.0.1:18765/v1/health` 是否可访问。
- 检查当前环境地址是否为 HTTP 或 HTTPS 完整地址。

## 13. Agent 实施边界

后续 Agent 在其他项目使用本文时应先确认目标是：

- 标准 Android 原生工程；
- WebView/混合应用；或
- 微信多端小程序 APK。

然后选择最短的原生到业务层通信方式。不得为了复用当前实现而强行引入文件轮询；文件收件箱是容器限制下的适配层，不是 CT58C 协议本身。

以下部分属于稳定兼容合同，除非有新的真机证据，不应改变：

- `android.intent.ACTION_DECODE_DATA`
- `barcode_string`
- `openScanner()`
- `switchOutputMode(0)`
- 外部广播 Receiver
- 同码短窗去重
- 页面活动状态门禁

任何协议调整都应先保留当前可用安装包，再生成新包，并完成 CT58C 真机回归后才能替换基线。
