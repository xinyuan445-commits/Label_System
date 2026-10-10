using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using U8Gateway.Contracts;
using U8Gateway.Contracts.Operations;
using U8Gateway.Core.Operations;

namespace U8Gateway.Persistence.Archive
{
    public sealed class FileOperationJournal : IOperationJournal
    {
        private static readonly UTF8Encoding Utf8WithoutBom =
            new UTF8Encoding(false);
        private readonly string _rootPath;
        private readonly ConcurrentDictionary<string, object> _fileLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerSettings SerializerSettings =
            new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DateFormatString = "yyyy-MM-dd'T'HH:mm:ss.fffzzz",
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };

        public FileOperationJournal(OperationArchiveOptions options)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.RootPath))
            {
                throw new ArgumentException("操作凭证根目录不能为空。", nameof(options));
            }

            _rootPath = Path.GetFullPath(options.RootPath);
            Directory.CreateDirectory(_rootPath);
        }

        public OperationJournalHandle Begin(OperationJournalStart start)
        {
            if (start == null)
            {
                throw new ArgumentNullException(nameof(start));
            }
            Require(start.ClientRequestId, nameof(start.ClientRequestId));
            Require(start.DocumentType, nameof(start.DocumentType));
            Require(start.OperationType, nameof(start.OperationType));

            DateTimeOffset occurredAt = start.OccurredAt == default(DateTimeOffset)
                ? BusinessClock.Now
                : start.OccurredAt;
            string folderName = SafeSegment(
                (string.IsNullOrWhiteSpace(start.DocumentTitle)
                    ? start.DocumentType
                    : start.DocumentTitle) + "_" + start.DocumentType);
            string directory = Path.Combine(
                _rootPath,
                occurredAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                folderName);
            Directory.CreateDirectory(directory);

            string baseName = occurredAt.ToString(
                "HH_mm_ss_fff",
                CultureInfo.InvariantCulture) + "_" + SafeSegment(start.OperationType);
            string path = CreateUniqueFile(directory, baseName);
            var handle = new OperationJournalHandle
            {
                ClientRequestId = start.ClientRequestId,
                FilePath = path
            };

            var data = new Dictionary<string, object>
            {
                ["clientRequestId"] = start.ClientRequestId,
                ["requestId"] = start.RequestId,
                ["environment"] = start.Environment,
                ["u8Database"] = start.U8Database,
                ["documentType"] = start.DocumentType,
                ["documentTitle"] = start.DocumentTitle,
                ["operationType"] = start.OperationType,
                ["operatorId"] = start.OperatorId,
                ["operatorName"] = start.OperatorName,
                ["deviceId"] = start.DeviceId,
                ["deviceModel"] = start.DeviceModel,
                ["apkVersion"] = start.ApkVersion,
                ["h5Version"] = start.H5Version,
                ["bridgeVersion"] = start.BridgeVersion,
                ["voucherCode"] = start.VoucherCode,
                ["expectedVersion"] = start.ExpectedVersion,
                ["reason"] = start.Reason,
                ["serviceVersion"] = start.ServiceVersion,
                ["payloadHash"] = start.PayloadHash,
                ["payload"] = start.Payload
            };
            Append(handle, new OperationJournalEvent
            {
                Stage = OperationStages.Received,
                OccurredAt = occurredAt,
                Data = data
            });
            return handle;
        }

        public void Append(OperationJournalHandle handle, OperationJournalEvent value)
        {
            if (handle == null || string.IsNullOrWhiteSpace(handle.FilePath))
            {
                throw new ArgumentException("操作凭证句柄无效。", nameof(handle));
            }
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            Require(value.Stage, nameof(value.Stage));

            string path = Path.GetFullPath(handle.FilePath);
            EnsureUnderRoot(path);
            if (value.OccurredAt == default(DateTimeOffset))
            {
                value.OccurredAt = BusinessClock.Now;
            }

            JToken token = JToken.FromObject(
                value,
                JsonSerializer.Create(SerializerSettings));
            string line = SensitiveDataRedactor.Redact(token)
                .ToString(Formatting.None) + Environment.NewLine;
            byte[] bytes = Utf8WithoutBom.GetBytes(line);
            object sync = _fileLocks.GetOrAdd(path, _ => new object());
            lock (sync)
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }
        }

        private static string CreateUniqueFile(string directory, string baseName)
        {
            for (int attempt = 1; attempt <= 999; attempt++)
            {
                string suffix = attempt == 1
                    ? string.Empty
                    : "_" + attempt.ToString("00", CultureInfo.InvariantCulture);
                string path = Path.Combine(directory, baseName + suffix + ".txt");
                try
                {
                    using (new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read))
                    {
                    }
                    return path;
                }
                catch (IOException)
                {
                }
            }

            throw new IOException("无法创建唯一的操作凭证文件。目录：" + directory);
        }

        private void EnsureUnderRoot(string path)
        {
            string prefix = _rootPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("操作凭证路径超出配置的根目录。");
            }
        }

        private static string SafeSegment(string value)
        {
            string source = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            var result = new StringBuilder(source.Length);
            foreach (char character in source)
            {
                result.Append(invalid.Contains(character) ? '_' : character);
            }
            return result.ToString();
        }

        private static void Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(name + " 不能为空。", name);
            }
        }
    }
}
