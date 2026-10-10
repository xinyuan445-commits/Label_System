# 单据字段合同

每种单据使用一个 JSON 文件保存经过核对的字段合同。合同同时是 API 文档、服务端校验、H5 表单和测试用例的共同来源。

状态：

- `draft`：正在采集，不允许开放写接口；
- `reviewed`：字段已经人工复核，但尚未完成真实测试账套验收；
- `verified`：已完成目标 U8 版本和账套验收，可以启用对应能力。

正式文件使用单据编码命名，例如：

```text
material-outbound.json
inventory-count.json
```

不能根据旧项目、数据库列名或其他 U8 版本直接把状态改为 `verified`。

已取得原生 XML 结构证据的单据，会先生成 `draft` 合同并登记全部已发现字段。此阶段字段的 `requiredMode` 和 `valueSource` 使用 `unverified`，读写能力全部关闭；只有在测试账套逐项验证后，才能改成正式分类并开放相应接口。

重新生成草稿合同：

```powershell
.\tools\U8FieldDiscovery\Build-DraftContracts.ps1
```
