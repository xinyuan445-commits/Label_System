# U8 单据字段采集规范

## 1. 目的

为每种目标单据建立经过目标 U8 版本和目标账套验证的完整字段合同，明确表头、表体、辅助明细、必填条件、读写权限、数据类型和 U8 映射。

## 2. 证据顺序

字段结论按以下证据建立：

1. 目标 U8 页面及账套业务设置；
2. 同版本 U8 COM `VoucherCO.Load` 返回的原始 XML；
3. U8 COM 新增、修改和审核的实际校验结果；
4. U8 数据库表结构和只读查询；
5. 现有源码和历史文档，仅作为参考。

数据库列允许为空，不代表 API 一定非必填；单张样例 XML 没有某字段，也不代表该字段不存在。

## 3. 样例矩阵

每种单据至少准备并只读导出以下适用场景：

- 普通存货；
- 批次管理存货；
- 货位管理仓库；
- 辅计量单位及固定或浮动换算；
- 自由项管理；
- 保质期和批次属性；
- 有来源单据和无来源单据；
- 多行、不同单位、不同批号和不同货位；
- 未审核和已审核状态；
- 有下游关联的状态。

一个样例无法覆盖全部字段，必须合并多份 XML 并保存覆盖矩阵。

## 4. 采集步骤

1. 确认单据 U8 类型、编号类型、COM 对象和目标账套。
2. 在 U8 中由业务人员准备字段完整且内容可识别的样例。
3. 使用 `VoucherCO.Load` 导出表头、表体和货位或辅助明细 XML。
4. 删除或隔离真实业务值，只保留字段结构用于合同整理。
5. 对照 U8 页面确定中文名称、业务含义和条件必填规则。
6. 对照数据库元数据核对类型、长度、精度、主外键和时间戳。
7. 通过测试账套分别验证新增、修改、审核、弃审和允许时的删除。
8. 回读单据，确认 U8 最终保存值。
9. 更新 `document-contracts/{type}.json`，完成评审后才把状态改为 `verified`。

## 5. 字段分类

每个字段至少记录：

```text
name
title
scope
dataType
maxLength
precision
scale
requiredMode
requiredWhen
createWritable
updateWritable
clearable
valueSource
u8Field
validation
returnable
evidence
```

`scope` 使用 `header`、`line`、`position` 或 `system`。`requiredMode` 正式结论使用 `always`、`conditional` 或 `optional`；尚未验证的草稿字段使用 `unverified`。系统 ID、审核人、审核日期、时间戳、记账状态、工作流状态和打印次数等字段只读。

## 6. 原始资料保护

原始 U8 XML 可能包含单号、料号、批号、仓库、货位和人员信息。原始文件只能放在受控的本地采集目录，不提交 Git；仓库只保存脱敏后的字段合同、覆盖清单和结论。

## 7. 目标环境采集进度（2026-10-10）

已确认目标 U8 安装目录为 `D:\U8SOFT`，组件版本为 U8 16.0。生产账套只读采集结果：

| 单据 | 组件 | 类型 | 表头 | 表体 | 原生 XML |
| --- | --- | --- | --- | --- | --- |
| 采购到货 | `VoucherCO_PU` | 枚举 2 | `PU_ArrivalVouch` | `PU_ArrivalVouchs` | 已采集 |
| 委外到货 | `VoucherCO_PU` | 枚举 2，业务类型区分 | `PU_ArrivalVouch` | `PU_ArrivalVouchs` | 已采集 |
| 采购入库 | `USERPCO` | `01` | `RdRecord01` | `RdRecords01` | 已采集 |
| 发货单 | `VoucherCO_SA` / `USSAServer` | `DispatchBlue` / 数据库 `05` | `DispatchList` | `DispatchLists` | 已采集 |
| 销售出库 | `USERPCO` | `32` | `RdRecord32` | `RdRecords32` | 已采集 |
| 材料出库 | `USERPCO` | `11` | `RdRecord11` | `RdRecords11` | 已采集 |
| 调拨单 | `USERPCO` | `12` | `TransVouch` | `TransVouchs` | 已采集 |
| 货位调整 | `USERPCO` | `19` | `AdjustPVouch` | `AdjustPVouchs` | 已采集 |
| 盘点单 | `USERPCO` | `18` | `CheckVouch` | `CheckVouchs` | 已采集 |

六类库存单据的脱敏结构证据在 `document-contracts/evidence/userpco`。采购到货和委外到货的证据在 `document-contracts/evidence/voucherco-pu`，发货单证据在 `document-contracts/evidence/voucherco-sa`。9 类合同合计 2,584 个字段。当前字段数量是 U8 返回结构的数量，不等于 API 必须全部由调用方传入；系统字段、展示字段和计算字段后续会分别标记为只读或由 U8 生成。

销售只读采集通过 `USSAServer.clsSystem.Init` 建立销售模块上下文，再调用 `clsVouchLoad.GetVoucherDataWEB`；`VoucherCO_SA` 的新增、修改和状态操作将在测试账套单独验证。

本次只读采集没有调用 `Insert`、`Update`、`Verify`、`UnVerify` 或 `Delete`。测试账套写入验证尚未开始。

测试账套 888 已只读回读除委外到货外的 8 类现有单据。8 类 XML 字段数量分别与当前合同完全一致，没有发现测试与生产结构差异。委外到货因测试账套没有现成样例且产品决定暂缓，当前不构造测试单。
