using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using U8Gateway.Contracts;
using U8Gateway.Core.Documents;
using U8Gateway.Core.Operations;

namespace U8Gateway.Persistence.Archive
{
    public sealed class FileOperationRegistry : IOperationRegistry
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private static readonly JsonSerializerSettings SerializerSettings =
            new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DateFormatString = "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };

        private readonly string _indexPath;

        public FileOperationRegistry(OperationArchiveOptions options)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.RootPath))
            {
                throw new ArgumentException("操作凭证根目录不能为空。", nameof(options));
            }

            string root = Path.GetFullPath(options.RootPath);
            Directory.CreateDirectory(root);
            _indexPath = Path.Combine(root, ".index");
            Directory.CreateDirectory(_indexPath);
        }

        public OperationClaimResult Claim(OperationRecord record)
        {
            ValidateRecord(record);
            return WithLock(record.ClientRequestId, () =>
            {
                OperationRecord existing = Read(record.ClientRequestId);
                if (existing == null)
                {
                    DateTimeOffset now = record.CreatedAt == default(DateTimeOffset)
                        ? BusinessClock.Now
                        : record.CreatedAt;
                    record.CreatedAt = now;
                    record.UpdatedAt = now;
                    Persist(record);
                    return new OperationClaimResult
                    {
                        Outcome = OperationClaimOutcomes.Claimed,
                        Record = Clone(record)
                    };
                }

                if (!string.Equals(
                    existing.PayloadHash,
                    record.PayloadHash,
                    StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        existing.DocumentType,
                        record.DocumentType,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        existing.OperationType,
                        record.OperationType,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new OperationClaimResult
                    {
                        Outcome = OperationClaimOutcomes.Conflict,
                        Record = existing
                    };
                }

                return new OperationClaimResult
                {
                    Outcome = string.Equals(
                        existing.Status,
                        OperationStages.Success,
                        StringComparison.OrdinalIgnoreCase)
                        ? OperationClaimOutcomes.Replay
                        : OperationClaimOutcomes.Pending,
                    Record = existing
                };
            });
        }

        public OperationRecord Get(string clientRequestId)
        {
            RequireClientRequestId(clientRequestId);
            return WithLock(clientRequestId, () => Read(clientRequestId));
        }

        public OperationRecord Update(
            string clientRequestId,
            string status,
            string code,
            string message,
            DocumentOperationResult result,
            DateTimeOffset occurredAt)
        {
            RequireClientRequestId(clientRequestId);
            if (string.IsNullOrWhiteSpace(status))
            {
                throw new ArgumentException("操作状态不能为空。", nameof(status));
            }

            return WithLock(clientRequestId, () =>
            {
                OperationRecord record = Read(clientRequestId);
                if (record == null)
                {
                    throw new InvalidOperationException("操作幂等记录不存在。");
                }

                record.Status = status;
                record.Code = code;
                record.Message = message;
                record.Result = CopyResult(result);
                record.UpdatedAt = occurredAt == default(DateTimeOffset)
                    ? BusinessClock.Now
                    : occurredAt;
                if (!string.IsNullOrWhiteSpace(result?.VoucherCode))
                {
                    record.TargetVoucherCode = result.VoucherCode;
                }
                Persist(record);
                return Clone(record);
            });
        }

        private T WithLock<T>(string clientRequestId, Func<T> action)
        {
            string lockPath = PathFor(clientRequestId) + ".lock";
            for (int attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    using (new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.WriteThrough))
                    {
                        return action();
                    }
                }
                catch (IOException) when (attempt < 49)
                {
                    Thread.Sleep(20);
                }
            }

            throw new IOException("操作幂等记录正被占用。");
        }

        private OperationRecord Read(string clientRequestId)
        {
            string path = PathFor(clientRequestId);
            if (!File.Exists(path))
            {
                return null;
            }

            OperationRecord record = JsonConvert.DeserializeObject<OperationRecord>(
                File.ReadAllText(path, Encoding.UTF8));
            if (record == null || !string.Equals(
                record.ClientRequestId,
                clientRequestId,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException("操作幂等记录无效。");
            }
            return record;
        }

        private void Persist(OperationRecord record)
        {
            string path = PathFor(record.ClientRequestId);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            JToken token = JToken.FromObject(
                record,
                JsonSerializer.Create(SerializerSettings));
            string json = SensitiveDataRedactor.Redact(token)
                .ToString(Formatting.Indented);
            byte[] bytes = Utf8WithoutBom.GetBytes(json);
            using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, null, true);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        private string PathFor(string clientRequestId)
        {
            return Path.Combine(_indexPath, Hash(clientRequestId) + ".json");
        }

        private static string Hash(string value)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
                var result = new StringBuilder(bytes.Length * 2);
                foreach (byte item in bytes)
                {
                    result.Append(item.ToString("x2"));
                }
                return result.ToString();
            }
        }

        private static void ValidateRecord(OperationRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            RequireClientRequestId(record.ClientRequestId);
            if (string.IsNullOrWhiteSpace(record.PayloadHash))
                throw new ArgumentException("请求摘要不能为空。", nameof(record));
            if (string.IsNullOrWhiteSpace(record.DocumentType))
                throw new ArgumentException("单据类型不能为空。", nameof(record));
            if (string.IsNullOrWhiteSpace(record.OperationType))
                throw new ArgumentException("操作类型不能为空。", nameof(record));
            if (string.IsNullOrWhiteSpace(record.Status))
                throw new ArgumentException("操作状态不能为空。", nameof(record));
        }

        private static void RequireClientRequestId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            {
                throw new ArgumentException(
                    "clientRequestId 必须填写且不能超过 64 个字符。",
                    nameof(value));
            }
        }

        private static DocumentOperationResult CopyResult(DocumentOperationResult result)
        {
            if (result == null)
            {
                return null;
            }
            return new DocumentOperationResult
            {
                Confirmed = result.Confirmed,
                Code = result.Code,
                Message = result.Message,
                VoucherId = result.VoucherId,
                VoucherCode = result.VoucherCode,
                Version = result.Version
            };
        }

        private static OperationRecord Clone(OperationRecord record)
        {
            return record == null
                ? null
                : JsonConvert.DeserializeObject<OperationRecord>(
                    JsonConvert.SerializeObject(record));
        }
    }
}
