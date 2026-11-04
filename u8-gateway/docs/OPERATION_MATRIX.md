# U8 单据操作矩阵

当前核对：2026-11-04。下表是组件方法映射，不是接口放行清单。0.11.0源码新增盘点只读列表/明细，盘点create=false、update/delete=false，未部署。其他类型按自身能力与账套门禁执行；生产008支持受控写入，不能用早期“生产只读”描述现行部署。详见[当前状态](../../docs/PROJECT_STATUS.md)和[API合同](API_DESIGN.md)。

## 1. 共同规则

- 对外操作统一为查询、新增、修改、审核、弃审和删除，对内按 CO 组件分别实现。
- 方法签名已按目标服务器 U8 16.0 互操作程序集核对；业务可用性仍必须在测试账套 888 验证。
- 任何写操作都必须先写操作凭证，成功后回读 U8；不能仅凭 COM 返回值判定成功。
- 删除调用 U8 正式能力，不执行数据库 `DELETE`。
- 生产写入必须命中独立生产开关、精确账套/数据库白名单及Business API开关；新能力先888验收，不因组件存在而直接放行。

## 2. 调用矩阵

| 单据 | 组件 | 查询 | 新增 | 修改 | 审核 | 弃审 | 删除 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 采购到货 | `VoucherCO_PU` | `GetVoucherDataById` | `GetVoucherNO` + `VoucherSave` | `Modify` | `ConfirmArr` | `CancelconfirmArr` | `Delete` |
| 委外到货 | `VoucherCO_PU` | `GetVoucherDataById` | `GetVoucherNO` + `VoucherSave` | `Modify` | `ConfirmArr` | `CancelconfirmArr` | `Delete` |
| 采购入库 | `USERPCO` | `Load` | `GetNewVouchCode` + `Insert` | `Load` + `Update` | `Verify` | `UnVerify` | `Load` 时间戳 + `Delete` |
| 发货单 | `VoucherCO_SA` / `USSAServer` | `clsVouchLoad.GetVoucherDataWEB` | `GetVoucherNO` + `Save` | `Modify` | `VerifyVouch(true)` | `VerifyVouch(false)` | `Delete` |
| 销售出库 | `USERPCO` | `Load` | `GetNewVouchCode` + `Insert` | `Load` + `Update` | `Verify` | `UnVerify` | `Load` 时间戳 + `Delete` |
| 材料出库 | `USERPCO` | `Load` | `GetNewVouchCode` + `Insert` | `Load` + `Update` | `Verify` | `UnVerify` | `Load` 时间戳 + `Delete` |
| 调拨单 | `USERPCO` | `Load` | `GetNewVouchCode` + `Insert` | `Load` + `Update` | `Verify` | `UnVerify` | `Load` 时间戳 + `Delete` |
| 货位调整 | `USERPCO` | `Load` | `GetNewVouchCode` + `Insert` | `Load` + `Update` | `Verify` | `UnVerify` | `Load` 时间戳 + `Delete` |
| 盘点单 | `USERPCO` | `Load` | `GetNewVouchCode` + `Insert` | `Load` + `Update` | `Verify` | `UnVerify` | `Load` 时间戳 + `Delete` |

## 3. 早期适配状态（历史基线）

以下保留初次组件接入时的未验收描述；当前放行与现场证据以上方链接为准，不能据此认定所有写入口仍关闭。

9 类单据均已取得只读 XML 字段结构。发货单读取使用注册的 `USSAServer.clsSystem` 和 `USSAServer.clsVouchLoad`，不再把 `SystemInfo.cSysInfo` 误作销售系统上下文；`VoucherCO_SA` 仍负责后续新增、修改和状态操作。

测试账套 888 已由新网关编译运行时直接回读采购到货、采购入库、发货、销售出库、材料出库、调拨、货位调整和盘点 8 类历史单据，字段结构与生产只读证据一致。这 8 类已开放查询能力。采购、销售、库存三组审核、弃审和删除适配代码已实现：调用 CO 后按单据表中的审核人字段或记录是否存在进行 SQL 回查，库存删除还携带加载所得 `ufts` 时间戳。以上状态操作尚未在 888 实际写入验收，能力开关继续关闭。

新增、修改的第一版候选必填、默认值和可写范围已经形成，并实现了无写入校验与保留原单字段的 PATCH XML 合并。来源单据约束、批次/货位等条件必填和各 CO 保存参数仍需在 888 逐项验证，不能因候选策略存在就开放写接口。全部写操作还受测试环境、限定数据库和限定账套门禁控制。

委外到货保留字段合同、操作映射和接口位置，但按 2026-10-10 的产品决定暂缓写入验收与功能开放。
