# U8 字段采集工具

本目录只放目标 U8 环境的只读字段采集工具，不包含新增、修改、审核、弃审或删除调用。

`Export-UserpcoVoucherXml.ps1` 用于库存单据 `01/11/12/18/19/32`。它在 U8/IIS 服务器本机以 32 位 Windows PowerShell 运行，从已存在的服务器本机 DPAPI 配置取得 U8 登录信息，但不会打印或写出密码。配置兼容仅用于一次性采集，不是新网关的运行依赖。

任务文件放在 `runtime-data`，不要提交 Git：

```json
[
  {
    "documentCode": "material-outbound",
    "voucherType": "11",
    "voucherId": 1000000001,
    "voucherCode": "示例单号"
  }
]
```

原始 XML 也只能放在 `runtime-data`。仓库中最终只保留脱敏后的字段合同与证据结论。

采购到货、委外到货和销售发货分别由采购、销售组件读取，使用 `Export-SpecializedVoucherXml.ps1` 只读采集，不能用 `USERPCO` 代替。
