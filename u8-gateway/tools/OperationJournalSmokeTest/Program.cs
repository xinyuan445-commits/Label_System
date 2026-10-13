using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using U8Gateway.Contracts;
using U8Gateway.Contracts.Operations;
using U8Gateway.Core.Operations;
using U8Gateway.Persistence.Archive;

namespace U8Gateway.OperationJournalSmokeTest
{
    internal static class Program
    {
        private static int Main()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "u8gateway-operation-journal-" + Guid.NewGuid().ToString("N"));
            try
            {
                TestHashing();
                TestArchive(root);
                TestRegistry(root);
                Console.WriteLine("operation-journal: ok; registry: ok; idempotency: ok; hashing: ok; redaction: ok; append: ok");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                if (Directory.Exists(root) &&
                    Path.GetFileName(root).StartsWith(
                        "u8gateway-operation-journal-",
                        StringComparison.Ordinal))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestHashing()
        {
            var left = new Dictionary<string, object>
            {
                ["header"] = new Dictionary<string, object>
                {
                    ["warehouse"] = "A",
                    ["quantity"] = 12.5m
                },
                ["operation"] = "add"
            };
            var right = new Dictionary<string, object>
            {
                ["operation"] = "add",
                ["header"] = new Dictionary<string, object>
                {
                    ["quantity"] = 12.5m,
                    ["warehouse"] = "A"
                }
            };

            string first = OperationPayloadHasher.Compute(left);
            string second = OperationPayloadHasher.Compute(right);
            Require(first == second, "属性顺序不应改变请求摘要。");
            Require(first.Length == 64, "请求摘要必须是 SHA-256。" );
            right["operation"] = "edit";
            Require(
                first != OperationPayloadHasher.Compute(right),
                "业务内容变化后请求摘要必须变化。");
        }

        private static void TestArchive(string root)
        {
            var journal = new FileOperationJournal(new OperationArchiveOptions
            {
                RootPath = root
            });
            var payload = new Dictionary<string, object>
            {
                ["header"] = new Dictionary<string, object>
                {
                    ["warehouse"] = "A01",
                    ["password"] = "raw-secret-one"
                },
                ["diagnostic"] = "Server=db;Password=raw-secret-two;Token=raw-secret-three"
            };
            OperationJournalHandle handle = journal.Begin(new OperationJournalStart
            {
                ClientRequestId = "client-test-001",
                RequestId = "request-test-001",
                Environment = "test",
                U8Database = "test-database",
                DocumentType = "purchase-receipt",
                DocumentTitle = "采购入库",
                OperationType = "add",
                OperatorId = "operator-1",
                OperatorName = "测试员",
                DeviceId = "device-1",
                DeviceModel = "CT58C",
                VoucherCode = null,
                ServiceVersion = "test",
                PayloadHash = OperationPayloadHasher.Compute(payload),
                Payload = payload,
                OccurredAt = new DateTimeOffset(2026, 10, 10, 8, 35, 12, 126, TimeSpan.FromHours(8))
            });

            journal.Append(handle, new OperationJournalEvent
            {
                Stage = OperationStages.Validated,
                Code = "OK",
                Message = "Bearer raw-secret-four"
            });
            Parallel.For(0, 12, index => journal.Append(handle, new OperationJournalEvent
            {
                Stage = "TEST_" + index,
                Data = new Dictionary<string, object> { ["index"] = index }
            }));
            journal.Append(handle, new OperationJournalEvent
            {
                Stage = OperationStages.Failed,
                Code = "TEST_FAILURE",
                Message = "Pwd=raw-secret-five"
            });

            Require(File.Exists(handle.FilePath), "操作凭证文件没有创建。");
            Require(
                handle.FilePath.Contains(Path.Combine("2026-10-10", "采购入库_purchase-receipt")),
                "操作凭证目录不正确。");
            string[] lines = File.ReadAllLines(handle.FilePath);
            Require(lines.Length == 15, "追加事件数量不正确。");
            foreach (string line in lines)
            {
                JObject.Parse(line);
            }

            string text = File.ReadAllText(handle.FilePath);
            Require(!text.Contains("raw-secret-"), "操作凭证中存在未脱敏信息。");
            Require(text.Contains("***REDACTED***"), "操作凭证中没有脱敏标记。");
            Require(
                JObject.Parse(lines[0])["stage"]?.ToString() == OperationStages.Received,
                "操作凭证首行必须是 RECEIVED。");
        }

        private static void TestRegistry(string root)
        {
            var registry = new FileOperationRegistry(new OperationArchiveOptions
            {
                RootPath = root
            });
            const string clientRequestId = "parallel-client-test-001";
            const string payloadHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var outcomes = new ConcurrentBag<string>();
            Parallel.For(0, 24, index =>
            {
                OperationClaimResult claim = registry.Claim(NewRecord(
                    clientRequestId,
                    payloadHash,
                    "request-" + index));
                outcomes.Add(claim.Outcome);
            });

            Require(
                outcomes.Count(x => x == OperationClaimOutcomes.Claimed) == 1,
                "并发请求只能有一个取得执行权。");
            Require(
                outcomes.Count(x => x == OperationClaimOutcomes.Pending) == 23,
                "重复并发请求必须保持等待状态。");

            OperationClaimResult conflict = registry.Claim(NewRecord(
                clientRequestId,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "request-conflict"));
            Require(
                conflict.Outcome == OperationClaimOutcomes.Conflict,
                "同一 clientRequestId 的不同内容必须冲突。");

            registry.Update(
                clientRequestId,
                OperationStages.Success,
                "OK",
                "Password=raw-registry-secret",
                new U8Gateway.Core.Documents.DocumentOperationResult
                {
                    Confirmed = true,
                    OutcomeKnown = true,
                    Code = "OK",
                    Message = "成功",
                    VoucherId = "1",
                    VoucherCode = "TEST-001",
                    Version = "v1"
                },
                BusinessClock.Now);
            OperationClaimResult replay = registry.Claim(NewRecord(
                clientRequestId,
                payloadHash,
                "request-replay"));
            Require(
                replay.Outcome == OperationClaimOutcomes.Replay,
                "完成后的重复请求必须返回原结果。");
            Require(
                replay.Record.Result?.VoucherCode == "TEST-001",
                "幂等重放没有保留原结果。");

            const string failedRequestId = "known-failure-retry-001";
            Require(
                registry.Claim(NewRecord(failedRequestId, payloadHash, "failed-1")).Outcome ==
                OperationClaimOutcomes.Claimed,
                "明确失败测试必须先取得执行权。");
            registry.Update(
                failedRequestId,
                OperationStages.Failed,
                "U8_CALL_BINDING_FAILED",
                "写入前失败",
                new U8Gateway.Core.Documents.DocumentOperationResult
                {
                    Confirmed = false,
                    OutcomeKnown = true,
                    Code = "U8_CALL_BINDING_FAILED",
                    Message = "写入前失败"
                },
                BusinessClock.Now);
            Require(
                registry.Claim(NewRecord(failedRequestId, payloadHash, "failed-2")).Outcome ==
                OperationClaimOutcomes.Claimed,
                "确认未写入的失败必须允许同请求安全重试。");

            OperationRecord stored = registry.Get(clientRequestId);
            Require(
                stored.Message.Contains("***REDACTED***"),
                "幂等记录错误信息没有脱敏。");
            string registryText = string.Join(
                Environment.NewLine,
                Directory.GetFiles(Path.Combine(root, ".index"), "*.json")
                    .Select(File.ReadAllText));
            Require(
                !registryText.Contains("raw-registry-secret"),
                "幂等记录中存在未脱敏信息。");
        }

        private static OperationRecord NewRecord(
            string clientRequestId,
            string payloadHash,
            string requestId)
        {
            return new OperationRecord
            {
                ClientRequestId = clientRequestId,
                RequestId = requestId,
                PayloadHash = payloadHash,
                Environment = "test",
                DocumentType = "purchase-receipt",
                DocumentTitle = "采购入库",
                OperationType = "add",
                Status = OperationStages.Received,
                CreatedAt = BusinessClock.Now,
                UpdatedAt = BusinessClock.Now
            };
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
