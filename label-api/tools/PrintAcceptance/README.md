# 打印第一阶段隔离验收工具

当前现有开发库/888四组真实SQL验收使用另一个[显式开发工具](../PrintDevelopmentAcceptance/README.md)，已通过并单独归档。本工具仍会建/删影子库，本轮未运行，不能与开发工具混用。

显式创建一对临时SQL库，执行001–017，运行实际API/Gateway/渲染器和模拟打印助手，归档脱敏断言后删除本轮两库。不会修改现有开发/测试/生产库，不运行U8 COM或真实驱动；普通测试不运行此工具。

前提：Windows、.NET8 SDK、net48运行时、Go满足connector/go.mod；本机 `.tools/print-renderer/Scripts/python.exe` 已安装renderer/requirements-dev.txt；已有授权的WinRM管理连接。脚本在内存读取远程现有连接凭据并先切master，不能输出连接内容。

从项目根目录显式执行：

```powershell
dotnet build u8-gateway/tools/PrintAcceptanceBridge
dotnet build label-api/tools/PrintAcceptance
Push-Location label-print/connector
go test -c -o ../../.tools/print-acceptance/agent-test.exe
Pop-Location
./label-api/tools/PrintAcceptance/Invoke-IsolatedAcceptance.ps1
```

输出位于 `.tools/print-acceptance/<16位GUID>/result.json` 和 `cleanup.json`。工具仅允许命名为 `LabelSystem_PrintAcceptance_<GUID>`、`U8_PrintAcceptance_888_<GUID>` 的本轮数据库；只从 `UFDATA_888_2021` 读取空表结构。完成或异常均尝试清理，删除前检查归属，检查cleanup.json确认结果。进程被强制杀死或机器断电仍可能留下临时库，须依据结果中的本轮GUID人工核验清理，不能用通配符删除。

临时Gateway配置含DPAPI保护的SQL凭据，测试密钥只供本轮使用，正常结束会删除；不能将整个工作目录或进程环境上传。可归档脱敏结果、cleanup记录、合成PDF/PNG和源码摘要。验收报告见 `docs/test-builds/2026-10-19-print-phase1-isolated.md`。

业务夹具直接表示已成功且数量已落账的来源，不执行ERP制单。旧表索引/触发器、现有开发库升级、浏览器网络和真实Windows打印不在本工具通过范围。后续常规联调优先复用既有开发库；失败注入需要独立范围。
