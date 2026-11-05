# 2026-11-05 888盘点上传保存

## 范围与业务规则

用户要求开放盘点上传，并明确“只处理已扫标签的盘点差异，未扫标签保持不变”。用户随后要求第一张实际上传由自己操作，因此本次没有代替用户提交U8 Create/Update，也没有进行实际盘点审核、库存落账或TraceBack写入。

- 有原单：回写同一张未审核盘点单，按料号/批号/仓库及按需货位汇总`已扫标签实盘－上传前标签余额`，目标行实盘为`原单账面＋该差异`。只更新命中的原生autoID、允许的盈亏类别和备注；其余行保留。例：原单账面8，所扫标签余额5、盘成4，则回写7，不能把该行直接覆盖成4。
- 无原单：按同一维度汇总差异，读取U8 CurrentStock或InvPositionSum的账面基准，构造新增未审核盘点单。存在自由项或辅计量歧义时拒绝；这一新增路径的真实保存仍待用户验收。
- 本轮只保存未审核，不自动审核，不在保存时修改标签余额或生成盘点差异流水/旧三表事件。888的盘点审核/弃审入口继续拦截。生产008上传仍关闭。
- 源单指纹、真实内容、审核状态和标签最新流水/余额/位置均复核；Gateway在STA队列中、调用U8前再次读取旧三表。Code打印量不参与余额或差异计算，单位仍跟随料号。
- 已有非零差异实盘的目标行不盲目再次覆盖，返回COUNT_ROW_ALREADY_COUNTED。未扫行存在实盘值不影响其他行上传。复杂的同一行多轮盘点累计/修订后置，需保留逐标签历史后再实施。
- 复用现有business_operation、明细、锁、归档、幂等和恢复，不新增盘点平台或数据库迁移。旧请求缺inventoryCount上下文时在镜像刷新/建操作之前拒绝，大小写变体同样拦截。

## 已部署

| 组件 | 当前 | 状态 |
| --- | --- | --- |
| H5 | 0.2.0-dev.92，172.16.10.45:5175 | 单据管理按服务端uploadSaveEnabled显示888上传；成功显示“已保存，未审核” |
| 开发Business API | 9891，程序集0.12.6、配置0.12.6-count-dev | LabelSystem_Dev / 888 / UFDATA_888_2021；开发池Started |
| 开发Gateway | 159本机9881，0.11.4.0 | 实际环境名test、账套888；开发池Started，外置来源及凭证未修改 |
| 生产后台 | 9890 API0.12.3、9880 Gateway0.11.2.0 | 健康接口只读核对，未发布程序、未改账套或写入开关 |

开发API最终目录：`D:\LabelSystem\DevPrinting\releases\20261105_count_upload_0126\api-final`。Gateway目录仍为`D:\LabelSystem\DevPrinting\releases\20261104_count_collection\gateway`，更新7个U8Gateway DLL；App_Data、Web.config及受保护来源配置保留。APK与PDA本机数据未改。

新增开关位于服务器外置开发配置`D:\LabelSystem\DevPrinting\Config\label-api.dev.json`的`LabelSystem.InventoryCountUploadsEnabled=true`，同时要求现有U8WritesEnabled=true且Business API环境development/888/UFDATA_888_2021精确匹配。源码默认false，不能靠该开关向008开放。H5读取capabilities.uploadSaveEnabled，不按客户端development档位推断账套；只读核验的canSubmit=false不作为上传保存开关。

## 验证证据

- H5 38项流程/存储/壳/上传序列化测试通过，类型检查和生产构建通过。上传保留实盘0及多标签，不按原单账面拆分标签，强制autoApprove=false；008和旧草稿在修改状态前拒绝。
- API全套93项通过、1项跳过，其中盘点37项；Release发布通过。覆盖原单账面加已扫差异、未扫行不参与补丁、负结果拒绝、多个标签对应一个ERP行、原单/无原单及门禁、旧请求和大小写路由的建档前拦截。
- Gateway合同及Release构建通过；ADO原XML补丁测试验证未扫行、账面和未支持属性保留，只允许实盘数量补丁，禁止新增/删除源单行和改料号。
- 实际只读回查888的PDD26101311成功，原生USERPCO返回1000行；目标标签验证MATCHED，补丁字段validate通过。目标行部署时账面8、实盘0，标签余额5。capabilities实际返回accountId=888、uploadSaveEnabled=true；两站健康版本及H5 dev.92模块核对通过。
- 当前库business_operation_line的请求数量约束实际允许0；现有结构可复用。本次没有执行迁移或业务数据SQL写入。
- 真实Create/Update保存、重复提交后的真实恢复和实机点击尚未验收，留给用户首次上传，不能用上述只读/自动测试声称已通过。

排查原生连接时先发现IIS进程激活U8旧CLR组件失败，Web.config的startup设置不能配置w3wp.exe。改为只在Gateway进程首次U8登录前绑定现有CLR4的旧组件激活策略；无系统.NET安装或全局IIS改动。原生只读工具在没有startup标志的独立进程中已成功读取1000行，随后在9881站点亦验证成功。依据为[微软BindAsLegacyV2Runtime说明](https://learn.microsoft.com/dotnet/framework/unmanaged-api/hosting/iclrruntimeinfo-bindaslegacyv2runtime-method)。U8许可饱和由用户释放；工具不退出他人的U8会话。

## 使用与回退

重新登录后进入盘点单据管理，自行点击上传ERP。有原单回写原单，成功显示ERP单号及“未审核”；记录与数量保留，不需要重置或清理存储。只读快照过期时重选同一原单，仍保留已扫数量。

旧API目录为`D:\LabelSystem\DevPrinting\releases\20261105_count_default_0125\api`；配置副本仅在服务器`D:\LabelSystem\DevPrinting\Config\label-api.pre-count-upload-0126.json`，Gateway旧DLL在本次release的gateway-backup。停对应池并等待Stopped后恢复路径、配置及DLL，再启动核对健康。仅需关闭新上传时先关闭InventoryCountUploadsEnabled；已在U8执行的结果继续按操作记录核对，不靠回退程序撤销业务数据。未推送GitHub。
