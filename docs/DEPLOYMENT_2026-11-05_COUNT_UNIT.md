# 2026-11-05 盘点单位规则修正

用户要求单位跟随料号，不核对标签单位与原单单位。旧标签保存“张”，原单物料单位为0101（名称同为张）；旧字符串比较导致误报。前后端取消该比较，有原单的表单单位沿用原单物料。料号、批号、仓库、按需货位、异常余额、重复候选及自由项/辅计量限制继续校验。

## 发布与验证

- H5 0.2.0-dev.90，现有5175服务返回新运行配置与扫码模块；当前仍连接9891/888。
- Business API源码/开发程序集0.12.4，配置0.12.4-count-dev。仅发布LabelSystemDevPrint / LabelSystemDevPrintPool至`D:\LabelSystem\DevPrinting\releases\20261105_count_unit_0124\api`，外置配置仅更新版本。
- 生产9890/008保留0.12.3；Gateway0.11.2和APK0.5.5/111未更新。无数据库迁移或U8/TraceBack业务写入，未清除本机草稿。
- H5盘点34项、API盘点30项及前后端Release构建通过。新增单位文字/编码不同或标签单位为空仍可按原单物料盘点的回归用例，同时验证错误料号仍拒绝。
- 实际登录开发API读取888原单PDD26101311，并对`008|11103-OSB-ENF-4922-CKB2300|20260617GXS|50.00|2606180042`提交只读validate，返回MATCHED、余额5、货位487、latestFlowId53177、canSubmit=false；随后退出测试会话。
- 未查看截图或做实机扫码验收；ERP新增/回填和差异落账仍未开放，上传继续禁用。

## 回退

开发API原路径为`D:\LabelSystem\DevPrinting\releases\20261104_204508_count_location\api`。停开发池并等待Stopped后恢复站点路径及服务器配置副本`D:\LabelSystem\DevPrinting\Config\label-api.pre-count-unit-0124.json`，启动并验证健康。服务器配置副本不进入仓库；程序回退不操作业务数据。API池重启会使内存会话失效，PDA重新登录并重新打开页面即可，不需清理数据。
