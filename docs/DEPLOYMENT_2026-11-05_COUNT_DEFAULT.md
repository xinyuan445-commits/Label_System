# 2026-11-05 盘点显示与数量默认值

按用户要求，盘点扫码列表与明细确认页隐藏标签数量/可用量；在线新扫标签以旧三表当前余额默认填入盘点数量，允许修改后确认。合法零余额默认0；断网无实时标签资料时数量仍为空，不读Code打印数量或原单账面作为默认。保存和联网复核保留用户修改的数量，旧草稿不批量重填。

单位显示原单物料名称。Gateway原单行新增unitName，从Inventory主单位关联ComputationUnit读取；Business API完整保留并纳入源指纹。H5优先原单名称，兼容旧标签单位名称或文字；不恢复标签/原单单位比较，不增加换算。

## 验证与发布

- H5 dev.91，5175及9891/888接入不变；自动流程/SSR等36项和类型/生产构建通过，覆盖默认实时余额、0、离线空值、改数后复核保留、旧行从原单取单位名称、隐藏数量及其他业务隔离。
- Business API0.12.5，开发配置0.12.5-count-dev；31项盘点测试及Release发布通过，含单位名称JSON传递与指纹变化。
- Gateway0.11.3，合同测试和Release构建通过；只更新开发9881的Api/Contracts/U8三个DLL，未覆盖App_Data、Web.config及外部配置。
- 开发API新目录为`D:\LabelSystem\DevPrinting\releases\20261105_count_default_0125\api`；Gateway目录仍为`D:\LabelSystem\DevPrinting\releases\20261104_count_collection\gateway`。两个应用池已启动，健康版本分别0.12.5-count-dev与0.11.3.0；源账套888 / UFDATA_888_2021。
- 真实只读验证PDD26101311：目标料号11103-OSB-ENF-4922-CKB2300、批号20260617GXS、货位487，返回unitCode=0101、unitName=张；指定Code校验MATCHED、余额5、canSubmit=false。H5运行配置dev.91及默认余额模块HTTP验证通过。
- 生产9890/008的API0.12.3与9880 Gateway0.11.2未发布；APK不变。无迁移、无U8/TraceBack业务写入、无草稿清理，ERP盘点上传仍禁用。未做截图或实机扫码验收，未推送GitHub。

## 使用与回退

重新打开页面并登录。旧原单快照未带单位名称，重选同一原单可更新指纹和名称，已有盘点数量保留；不要清除PDA存储。默认数量只作用于新扫码，已保存数量不自动覆盖。

开发API上版为`D:\LabelSystem\DevPrinting\releases\20261105_count_unit_0124\api`。配置副本仅在服务器`D:\LabelSystem\DevPrinting\Config\label-api.pre-count-default-0125.json`，Gateway旧DLL在本次release的gateway-backup。停对应池并等待Stopped后恢复API路径、配置及三个Gateway DLL，再启动核对健康；不操作业务数据库。
