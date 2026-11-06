# TraceBack盘点JSON合同与旧样本核查

核查及开发888发布：2026-11-05；本轮只调整盘点。参考888旧系统记录，排除新同步器生成的简化Order，不将存在JSON误写的历史行为直接当合同。

## 旧系统共同结构及业务差异

| 类型 | 旧PDA样本 | 特有内容 |
| --- | --- | --- |
| 材料出库 | PCLCK-2026-09-27-718 / 717 | 出库类别、业务类型；有工单时保存生产订单来源与SourceVoucherID/Code |
| 调拨 | PDBD-2026-09-27-512 / 511 | 调出/调入部门、出入类别；Goods.U8保存transferIn/OutWarehouse及Location |
| 货位调整 | PHWTZD-2026-09-24-17 / 16 | 仓库；Goods.U8保存beforeAdjustmentStorageLocation、adjustedStorageLocation |
| 盘点 | PPDD-2026-09-23-132 / 128 | 仓库、盈亏类别、U8.cWhCode、U8.bPosCheck及可选盘点原单来源 |

Order共同顶层：pdaID、documentNumber、orderType、CurrencyOrderData、documentStatus、isSelect、showList、documentDate、documentDateTs；有来源时还可含SourceOrderList。CurrencyOrderData通常含U8、phoneMac、pdaUser、department、uniquekeyGuid、AcceptDate及业务字段。showList实际是title/message数组，SourceOrderList在核查盘点样本中是逗号结尾的单号字符串，不能猜成JSON数组。是否选择为客户端瞬时状态，不等同ERP审核。

Goods共同顶层：pdaID、pdaOrderNumber、id/name/code、CurrencyGoodsData、inventory、Attribute、quantity、quantity2，以及可选Price、SourceOrder、soPdaId、mergeDataJson、打印方案等。位置编码及名称放在inventory，批号在Attribute.batchNumber。CurrencyGoodsData.U8除业务字段外还含自由项、单位组等物料属性。

CurrencyOrderData.uniquekeyGuid在抽样中等于pdaOrderUuid；CurrencyGoodsData.uniquekeyGuid与pdaGoodsUuid并不相等，不能把两者强行绑定。后者保留原有物料身份，不作为流水幂等标识。

## 本次盘点写入合同

- API从可信归档、业务操作及已确认ERP结果生成CountContext，冻结在同步任务载荷中。PDA业务单号用真实operation_no，不用clientRequestId；JSON pdaID分别使用本地操作/明细ID，不借用上一笔PDA行号。
- Goods.quantity、quantity2、Attribute.quantity2表示该码实盘；Attribute.quantityD11表示提交前实时余额。OnlyCode.sum仍是两者之差。旧样本52867为实盘50、盘前30、差异+20；52781为实盘5、盘前6、差异-1。
- 保留物料、批号、单位、自由项和打印快照，位置按本次确认目标更新（0.11.7）；清除旧调拨四位置、旧货位调整位置、SourceOrder/soPdaId分配、mergeDataJson及inventory中历史库存缓存。不把仓库库存缓存改成单张标签余额。
- Order补齐日期、上海零点毫秒时间戳、仓库、盘货位状态、盈亏类别、实际操作人、设备信息、已确认ERP号和真实来源ID/单号。showList为本次业务摘要数组；有原单SourceOrderList写原单号加逗号，无原单不伪造来源。documentStatus=2表示本次已上传，U8仍未审核。
- 新盘点表头未采集的经手人/部门不沿用上一张单；未选择币种也不伪造旧PDA币种ID。旧系统可选字段不必照抄，已有明确值的业务字段不能丢失。
- 仍限test/888/UFDATA_888_2021；材料、调拨及货位调整写入逻辑本轮不改，后续逐类适配，不能据盘点修复宣称全部旧JSON已兼容。

## 已写记录修复与边界

操作159 / PDD26101311原写入JSON有数量字段不一致、残留调拨内容及Order缺字段。本次使用[显式修复工具](../u8-gateway/tools/CountTraceBackJsonRepair/README.md)先预览4份Goods/1份Order，再按原UUID比较并更新JSON；Serializable事务验证任务均完成、归档校验一致、流水仍是最新及余额符合原事件。

修复后复查0份待修改。OnlyCode原ID54042～54045及sum=0/+1/0/0不变；新旧标签余额为5/50/6/14，均SYNCED且无锁；U8账面仍8/62/77、实盘5/50/20。本轮不写U8单据、不新增数量流水、不改变标签余额、不改已完成任务载荷/哈希，新增一条JSON修复审计。后续真实上传仍由用户操作。

## 2026-11-05目标位置补充（0.11.7）

盘点不以标签原仓库/货位限制扫码。有原单以原单仓库为准，无原单以制单表头为准；目标仓库启用货位管理时在数量确认中选择有效“盘点货位”，原单盘货位时限匹配料号/批号的原单货位。跨仓不猜货位，未启用货位管理时清空目标货位。上传ERP确认后，新旧标签余额按实盘同步、当前位置按本次目标更新；扫码和草稿不改变标签。原位置留作实时复核和审计，未扫标签不变。

Goods同时更新根warehouseCode/name、locationCode、storageLocationCode/name及inventory仓库/货位字段；存在的wareHouseCode/cWhCode/positionCode/cPosCode等别名同步，避免旧别名优先读取产生错误位置。目标不管理货位时清空相关位置值。Order仓库取本次盘点表头，不取标签原仓库。盘前位置仍在本地明细/库存流水中，数量字段及幂等合同不变。历史159不追溯改位置，本次没有新增旧表流水。见[发布记录](DEPLOYMENT_2026-11-05_COUNT_POSITION.md)。

2026-11-06真实888：11个新盘点事件的Goods三处实盘/盘前数量及OnlyCode差异、Order业务/ERP号/备注和showList均核对通过，5码最新位置一致；原历史JSON未改。备注codex test，详情见[实测报告](test-builds/2026-11-06-inventory-count-real-888.md)。
