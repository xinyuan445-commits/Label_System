# 2026-11-05 上传基线回归通用记录

用户确认[功能开发基准](DEVELOPMENT_BASELINE.md)：先做基础、可落地的完整流程，不为后置能力禁止已支持的上传；优先沿用通用表/组件，额外设想只记后续。AGENTS及架构/业务文档引用该基准。

## 实现与数据

- 盘点累计基线移到business_operation_event，event_type为通用UPLOAD_BASELINE，event_data_json保存documentType、sourceDocumentId及previousLabels；第一次生成后固定，重试沿用。历史实盘仍读取通用business_operation_line，没有另建盘点单头/明细。
- 原单ID/单号由可信盘点上下文写回business_operation来源字段；并发判断复用通用操作状态和已有基线事件。同原单未完成上传仍先处理，避免漏累计；无原单仅停在准备阶段不再阻挡其他原单，实际正在写入/结果未知时保留必要的防重复保护。
- 仅LabelSystem_Dev安装[054 / 019](database/054_merge_inventory_count_baseline_into_events.sql)。原辅助表1条记录迁入事件，数组逐项保持原内容，补齐来源关联后删除inventory_count_upload_plan。018原SQL及校验值保留；019单独记录64位脚本校验值。
- 操作159的原单ID1000012700、空历史贡献[]已迁入；单据、草稿、标签余额、库存流水、旧三表和已完成同步任务载荷不改。本次没有新增ERP单据或数量流水。

## 发布与验证

- API9891升级程序集0.12.9 / 配置0.12.9-count-dev，目录`D:\LabelSystem\DevPrinting\releases\20261105_count_generic_0129\api`；H5 dev.93、Gateway0.11.6及APK不变。只更新开发API，生产008未变。
- 迁移和站点切换期间短暂停止开发API池，待原请求结束后执行；上传配置全程保持true，没有新增功能禁用条件。API已恢复ready，888能力uploadSaveEnabled=true，部署DLL与本地发布SHA256一致。
- API102通过/1跳过。新增测试覆盖迁入的Pascal字段数组与新事件格式产生完全一致重试载荷、空首轮基线、无效/错原单数据不重算。未重复前端/Gateway测试或声称新的实机上传已验收。
- 真实数据库验证：旧专表已不存在，1条基线事件、来源ID/原贡献保持；019校验值长度64；四码本地余额和旧表镜像余额仍5/50/6/14，SYNCED。[只读核对脚本](database/queries/盘点新表核对.sql)8组查询均执行通过，基线和过程记录共用同一事件表。

实际新上传仍由用户操作。本次不清理PDA草稿；API重启后需重新登录。

回退需同时恢复018结构/基线与旧API，不能只切回依赖已删除表的0.12.8。正常使用直接沿用新API及通用事件，不重传159。
