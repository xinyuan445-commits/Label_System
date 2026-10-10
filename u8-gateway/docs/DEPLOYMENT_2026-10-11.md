# 2026-10-11 IIS 部署记录

## 部署结果

新 U8 Gateway 已作为独立 IIS 站点部署到 `172.16.1.159`：

```text
Site: U8Gateway
Application pool: U8GatewayPool
URL: http://172.16.1.159:9880
Physical path: D:\U8Gateway\site
Operation archive: D:\U8GatewayData\operation-archive
Environment: test
Database: UFDATA_888_2021
U8 account: (default)@888
Test writes: disabled
Service version: 0.2.0.0
```

原有 `Default Web Site`、`PDA` 和 `MaterialOutboundApi` 站点名称、目录和绑定未修改。

## 验收结果

- IIS 站点和应用池状态：`Started`；
- 外部健康检查：`running`、`configured=true`；
- SQL 连接测试：通过；
- U8 登录、初始化和注销测试：通过；
- 单据类型：9 类；
- 材料出库 schema 写入策略：`candidate-reference-derived`；
- 新增字段无写入校验：通过，服务端日期和制单人默认值正确；
- 服务器外访问管理页：403，符合仅本机管理要求；
- 真实新增接口：409 `DOCUMENT_OPERATION_DISABLED`，未调用 U8 写入。

首次启动发现 Web API 依赖的 Newtonsoft.Json 版本需要绑定重定向，已在 `Web.config` 固定为随发布包提供的 13.0.0.0，重新部署后启动正常。

## 凭据

新站点使用独立生成的管理密码和内部 API Key。开发机交接文件位于：

```text
u8-gateway/runtime-data/deployment/U8Gateway-credentials.txt
```

该目录已由 `.gitignore` 排除，不能上传 Git、粘贴到文档或写入 H5。后续标签系统业务后端通过安全配置读取 API Key。

## 尚未开放

新增、修改、审核、弃审和删除仍全部关闭。当前部署可用于健康检查、schema、无写入字段校验和已经验收的只读查询。下一阶段在 888 对每种单据逐项完成 CO 保存、修改、回读和失败场景测试，再按单据、按操作开放能力。008 保持只读。
