using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using U8Gateway.Api.Models;
using U8Gateway.Persistence.Configuration;
using U8Gateway.U8.TraceBack;

namespace U8Gateway.Api.Infrastructure
{
    internal static class DeploymentAdminService
    {
        private static string BusinessConfigPath =>
            ConfigurationManager.AppSettings["BusinessApiConfigPath"] ??
            @"D:\LabelSystem\Config\label-api.json";

        private static string BusinessApiBaseUrl =>
            (ConfigurationManager.AppSettings["BusinessApiBaseUrl"] ??
             "http://127.0.0.1:9890").TrimEnd('/');

        public static object Overview()
        {
            GatewaySettings gateway = AppRuntime.Settings.Current;
            object business = BusinessView(ReadBusinessConfig());
            object businessHealth;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    string json = client.GetStringAsync(
                        BusinessApiBaseUrl + "/api/v1/health").GetAwaiter().GetResult();
                    JObject data = JObject.Parse(json)["data"] as JObject;
                    businessHealth = new
                    {
                        reachable = true,
                        status = (string)data?["status"],
                        version = (string)data?["version"],
                        databaseReady = (bool?)data?["databaseReady"] ?? false,
                        u8WritesEnabled = (bool?)data?["u8WritesEnabled"] ?? false
                    };
                }
            }
            catch
            {
                businessHealth = new { reachable = false };
            }

            object migrations;
            try
            {
                JObject root = ReadBusinessConfig();
                string connectionString = (string)root["ConnectionStrings"]?["LabelSystem"];
                using (var connection = new SqlConnection(connectionString))
                using (var command = new SqlCommand(@"
SELECT COUNT_BIG(*),MAX(migration_id),MAX(applied_at)
FROM dbo.schema_migration;", connection))
                {
                    connection.Open();
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        reader.Read();
                        migrations = new
                        {
                            ready = true,
                            count = reader.GetInt64(0),
                            latest = reader.IsDBNull(1) ? null : reader.GetString(1),
                            appliedAt = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2)
                        };
                    }
                }
            }
            catch
            {
                migrations = new { ready = false };
            }

            return new
            {
                gateway = new
                {
                    version = AppRuntime.ServiceVersion,
                    configured = AppRuntime.Settings.IsConfigured,
                    environment = gateway.EnvironmentName,
                    database = gateway.SqlDatabase,
                    accountId = AccountCode(gateway.U8AccountId),
                    year = gateway.U8Year,
                    writesEnabled = gateway.EnableTestWrites,
                    allowedDatabase = gateway.WriteAllowedDatabase,
                    allowedAccountId = gateway.WriteAllowedAccountId,
                    revision = gateway.Revision
                },
                business,
                businessHealth,
                migrations,
                productionWritesSupported = false,
                adminAccess = "LOCAL_ONLY"
            };
        }

        public static object ReadBusinessView() => BusinessView(ReadBusinessConfig());

        public static object TestBusinessSql(BusinessSettingsInput input)
        {
            SqlConnectionStringBuilder builder = BusinessConnection(input, true);
            using (var connection = new SqlConnection(builder.ConnectionString))
            using (var command = new SqlCommand(@"
SELECT DB_NAME(),
       CASE WHEN OBJECT_ID(N'dbo.schema_migration',N'U') IS NULL THEN 0 ELSE 1 END;", connection))
            {
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    reader.Read();
                    return new
                    {
                        connected = true,
                        database = reader.GetString(0),
                        schemaReady = reader.GetInt32(1) == 1
                    };
                }
            }
        }

        public static object SaveBusiness(BusinessSettingsInput input)
        {
            ValidateBusiness(input);
            JObject root = ReadBusinessConfig();
            SqlConnectionStringBuilder connection = BusinessConnection(input, true);
            JObject label = root["LabelSystem"] as JObject ?? new JObject();
            JObject strings = root["ConnectionStrings"] as JObject ?? new JObject();
            JObject gateway = root["U8Gateway"] as JObject ?? new JObject();
            root["LabelSystem"] = label;
            root["ConnectionStrings"] = strings;
            root["U8Gateway"] = gateway;

            label["Environment"] = input.EnvironmentName.Trim();
            label["ErpAccountId"] = input.ErpAccountId.Trim();
            label["ErpYear"] = input.ErpYear;
            label["ErpDatabaseName"] = input.ErpDatabaseName.Trim();
            label["U8WritesEnabled"] = input.U8WritesEnabled;
            label["EnableDevelopmentLogin"] =
                string.Equals(input.EnvironmentName, "development", StringComparison.OrdinalIgnoreCase) &&
                input.EnableDevelopmentLogin;
            strings["LabelSystem"] = connection.ConnectionString;
            gateway["BaseUrl"] = input.GatewayBaseUrl.TrimEnd('/');

            string backup = BackupFile(BusinessConfigPath, @"D:\LabelSystem\Config\backups",
                "label-api.admin");
            WriteJsonAtomic(BusinessConfigPath, root);
            Audit("BUSINESS_CONFIG_SAVED", new JObject
            {
                ["environment"] = input.EnvironmentName,
                ["labelDatabase"] = input.LabelSqlDatabase,
                ["erpAccountId"] = input.ErpAccountId,
                ["erpYear"] = input.ErpYear,
                ["erpDatabase"] = input.ErpDatabaseName,
                ["u8WritesEnabled"] = input.U8WritesEnabled,
                ["backup"] = backup
            });
            return new { config = BusinessView(root), backup };
        }

        public static object TraceBackCheck(string code)
        {
            GatewaySettings settings = AppRuntime.Settings.Current;
            long orders;
            long goods;
            long flows;
            using (var connection = GatewayConnection(settings))
            using (var command = new SqlCommand(@"
SELECT
 (SELECT COUNT_BIG(*) FROM dbo.TraceBackOrder),
 (SELECT COUNT_BIG(*) FROM dbo.TraceBackGoods),
 (SELECT COUNT_BIG(*) FROM dbo.TraceBackOnlyCode);", connection))
            {
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    reader.Read();
                    orders = reader.GetInt64(0);
                    goods = reader.GetInt64(1);
                    flows = reader.GetInt64(2);
                }
            }
            object snapshot = null;
            if (!string.IsNullOrWhiteSpace(code))
            {
                snapshot = new TraceBackSnapshotReader(
                    settings.SqlServer, settings.SqlDatabase,
                    settings.SqlUser, settings.SqlPassword)
                    .GetSnapshots(new[] { code.Trim() })[0];
            }
            Audit("TRACEBACK_READ_CHECK", new JObject
            {
                ["database"] = settings.SqlDatabase,
                ["hasCode"] = !string.IsNullOrWhiteSpace(code)
            });
            return new { contractReady = true, orders, goods, flows, snapshot };
        }

        public static string BackupGatewaySettings()
        {
            string source = Path.Combine(AppRuntime.DataDirectory, "settings.json");
            return BackupFile(source, @"D:\U8GatewayData\config-backups", "gateway-settings");
        }

        public static void Audit(string action, JObject details)
        {
            string directory = @"D:\U8GatewayData\admin-audit";
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "admin-actions.jsonl");
            var entry = new JObject
            {
                ["time"] = DateTimeOffset.Now,
                ["action"] = action,
                ["details"] = details ?? new JObject()
            };
            File.AppendAllText(path, entry.ToString(Formatting.None) + Environment.NewLine,
                new UTF8Encoding(false));
        }

        private static object BusinessView(JObject root)
        {
            string connectionString = (string)root["ConnectionStrings"]?["LabelSystem"];
            var builder = new SqlConnectionStringBuilder(connectionString ?? string.Empty);
            JObject label = root["LabelSystem"] as JObject;
            JObject gateway = root["U8Gateway"] as JObject;
            return new
            {
                configured = !string.IsNullOrWhiteSpace(connectionString),
                configPath = BusinessConfigPath,
                environmentName = (string)label?["Environment"],
                version = (string)label?["Version"],
                labelSqlServer = builder.DataSource,
                labelSqlDatabase = builder.InitialCatalog,
                labelSqlUser = builder.UserID,
                hasLabelSqlPassword = !string.IsNullOrWhiteSpace(builder.Password),
                erpAccountId = (string)label?["ErpAccountId"],
                erpYear = (int?)label?["ErpYear"] ?? 0,
                erpDatabaseName = (string)label?["ErpDatabaseName"],
                gatewayBaseUrl = (string)gateway?["BaseUrl"],
                hasGatewayApiKey = !string.IsNullOrWhiteSpace((string)gateway?["ApiKey"]),
                enableDevelopmentLogin = (bool?)label?["EnableDevelopmentLogin"] ?? false,
                u8WritesEnabled = (bool?)label?["U8WritesEnabled"] ?? false
            };
        }

        private static JObject ReadBusinessConfig()
        {
            if (!File.Exists(BusinessConfigPath))
                throw new InvalidOperationException("Business API 外置配置不存在。");
            return JObject.Parse(File.ReadAllText(BusinessConfigPath, Encoding.UTF8));
        }

        private static SqlConnectionStringBuilder BusinessConnection(
            BusinessSettingsInput input, bool preservePassword)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            string password = input.LabelSqlPassword;
            if (preservePassword && string.IsNullOrWhiteSpace(password))
            {
                JObject root = ReadBusinessConfig();
                var current = new SqlConnectionStringBuilder(
                    (string)root["ConnectionStrings"]?["LabelSystem"] ?? string.Empty);
                password = current.Password;
            }
            return new SqlConnectionStringBuilder
            {
                DataSource = input.LabelSqlServer?.Trim(),
                InitialCatalog = input.LabelSqlDatabase?.Trim(),
                UserID = input.LabelSqlUser?.Trim(),
                Password = password,
                IntegratedSecurity = false,
                Encrypt = false,
                TrustServerCertificate = true,
                ConnectTimeout = 10,
                ApplicationName = "LabelSystem.DeploymentAdmin"
            };
        }

        private static void ValidateBusiness(BusinessSettingsInput input)
        {
            if (input == null ||
                !(string.Equals(input.EnvironmentName, "development", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(input.EnvironmentName, "test", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(input.EnvironmentName, "production", StringComparison.OrdinalIgnoreCase)) ||
                string.IsNullOrWhiteSpace(input.LabelSqlServer) ||
                string.IsNullOrWhiteSpace(input.LabelSqlDatabase) ||
                string.IsNullOrWhiteSpace(input.LabelSqlUser) ||
                string.IsNullOrWhiteSpace(input.ErpAccountId) ||
                input.ErpYear < 2000 || input.ErpYear > 2100 ||
                string.IsNullOrWhiteSpace(input.ErpDatabaseName) ||
                string.IsNullOrWhiteSpace(input.GatewayBaseUrl))
                throw new InvalidOperationException("Business API 配置不完整。");
            SqlConnectionStringBuilder connection = BusinessConnection(input, true);
            if (string.IsNullOrWhiteSpace(connection.Password))
                throw new InvalidOperationException("标签数据库密码未配置。");
            if (!input.U8WritesEnabled) return;
            if (string.Equals(input.EnvironmentName, "production", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("生产写入尚未开放。");
            string expected = "ENABLE " + input.ErpAccountId.Trim() + " " + input.ErpDatabaseName.Trim();
            if (!string.Equals(input.WriteConfirmation?.Trim(), expected, StringComparison.Ordinal))
                throw new InvalidOperationException("写入确认文字不匹配。");
        }

        private static SqlConnection GatewayConnection(GatewaySettings settings)
        {
            return new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = settings.SqlServer,
                InitialCatalog = settings.SqlDatabase,
                UserID = settings.SqlUser,
                Password = settings.SqlPassword,
                IntegratedSecurity = false,
                Encrypt = false,
                TrustServerCertificate = true,
                ConnectTimeout = 10,
                ApplicationName = "U8Gateway.DeploymentAdmin"
            }.ConnectionString);
        }

        private static string BackupFile(string source, string directory, string prefix)
        {
            if (!File.Exists(source)) return null;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory,
                prefix + "." + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + ".json");
            File.Copy(source, path, false);
            return path;
        }

        private static void WriteJsonAtomic(string path, JObject root)
        {
            string temp = path + ".admin.tmp";
            File.WriteAllText(temp, root.ToString(Formatting.Indented), new UTF8Encoding(false));
            try { File.Replace(temp, path, null, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static string AccountCode(string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            int separator = trimmed.LastIndexOf('@');
            return separator >= 0 ? trimmed.Substring(separator + 1) : trimmed;
        }
    }
}
