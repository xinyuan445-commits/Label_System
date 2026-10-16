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

        private static string DeploymentTemplatePath =>
            @"D:\U8GatewayData\deployment-templates.json";

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
                    productionWritesEnabled = gateway.EnableProductionWrites,
                    allowedDatabase = gateway.WriteAllowedDatabase,
                    allowedAccountId = gateway.WriteAllowedAccountId,
                    revision = gateway.Revision
                },
                business,
                businessHealth,
                migrations,
                productionWritesSupported = true,
                adminAccess = "LOCAL_ONLY"
            };
        }

        public static object ReadBusinessView() => BusinessView(ReadBusinessConfig());

        public static object ReadTemplates()
        {
            return TemplateView(ReadOrCreateTemplateDocument());
        }

        public static object SaveTemplate(DeploymentTemplateInput input)
        {
            string key = TemplateKey(input?.Key);
            JObject template = TemplateJson(input?.Gateway, input?.Business, key);
            JObject document = ReadOrCreateTemplateDocument();
            string backup = BackupFile(DeploymentTemplatePath,
                @"D:\U8GatewayData\config-backups", "deployment-templates");
            document[key] = template;
            document["updatedAt"] = DateTimeOffset.Now;
            WriteJsonAtomic(DeploymentTemplatePath, document);
            Audit("DEPLOYMENT_TEMPLATE_SAVED", new JObject
            {
                ["template"] = key,
                ["gatewayDatabase"] = (string)template["gateway"]?["sqlDatabase"],
                ["gatewayAccount"] = AccountCode((string)template["gateway"]?["u8AccountId"]),
                ["labelDatabase"] = (string)template["business"]?["labelSqlDatabase"],
                ["backup"] = backup
            });
            return new { templates = TemplateView(document), backup };
        }

        public static object ApplyTemplate(ApplyDeploymentTemplateInput input)
        {
            string key = TemplateKey(input?.Key);
            JObject document = ReadOrCreateTemplateDocument();
            JObject template = document[key] as JObject ??
                throw new InvalidOperationException("配置模板不存在。");
            AdminSettingsInput gatewayInput =
                template["gateway"]?.ToObject<AdminSettingsInput>();
            BusinessSettingsInput businessInput =
                template["business"]?.ToObject<BusinessSettingsInput>();
            TemplateJson(gatewayInput, businessInput, key);
            string expected = "APPLY " + key.ToUpperInvariant() + " " +
                AccountCode(gatewayInput.U8AccountId) + " " + gatewayInput.SqlDatabase;
            if (!string.Equals(input?.Confirmation?.Trim(), expected, StringComparison.Ordinal))
                throw new InvalidOperationException("应用模板确认文字不匹配。");

            GatewaySettings currentGateway = AppRuntime.Settings.Current;
            JObject currentBusiness = ReadBusinessConfig();
            GatewaySettings nextGateway = GatewaySettingsFromTemplate(gatewayInput);
            JObject nextBusiness = BuildBusinessConfig(businessInput);
            TestGatewaySql(gatewayInput, currentGateway.SqlPassword);
            TestBusinessSql(businessInput);
            string gatewayBackup = BackupGatewaySettings();
            string businessBackup = BackupFile(BusinessConfigPath,
                @"D:\LabelSystem\Config\backups", "label-api.template-apply");
            bool gatewaySaved = false;
            try
            {
                AppRuntime.Settings.Save(nextGateway, null);
                gatewaySaved = true;
                WriteJsonAtomic(BusinessConfigPath, nextBusiness);
            }
            catch
            {
                if (gatewaySaved) AppRuntime.Settings.Save(currentGateway, null);
                WriteJsonAtomic(BusinessConfigPath, currentBusiness);
                throw;
            }
            Audit("DEPLOYMENT_TEMPLATE_APPLIED", new JObject
            {
                ["template"] = key,
                ["gatewayDatabase"] = nextGateway.SqlDatabase,
                ["gatewayAccount"] = AccountCode(nextGateway.U8AccountId),
                ["labelDatabase"] = businessInput.LabelSqlDatabase,
                ["gatewayBackup"] = gatewayBackup,
                ["businessBackup"] = businessBackup
            });
            return new
            {
                template = key,
                gateway = AppRuntime.Settings.GetView(),
                business = BusinessView(nextBusiness),
                gatewayBackup,
                businessBackup,
                businessRestartRequired = true
            };
        }

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
            JObject root = BuildBusinessConfig(input);

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

        private static JObject BuildBusinessConfig(BusinessSettingsInput input)
        {
            JObject root = ReadBusinessConfig();
            SqlConnectionStringBuilder connection = BusinessConnection(input, true);
            JObject label = root["LabelSystem"] as JObject;
            if (label == null)
            {
                label = new JObject();
                root["LabelSystem"] = label;
            }
            JObject strings = root["ConnectionStrings"] as JObject;
            if (strings == null)
            {
                strings = new JObject();
                root["ConnectionStrings"] = strings;
            }
            JObject gateway = root["U8Gateway"] as JObject;
            if (gateway == null)
            {
                gateway = new JObject();
                root["U8Gateway"] = gateway;
            }
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
            return root;
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
            bool production = string.Equals(
                input.EnvironmentName, "production", StringComparison.OrdinalIgnoreCase);
            string expected = production
                ? "ENABLE PRODUCTION " + input.ErpAccountId.Trim() + " " + input.ErpDatabaseName.Trim()
                : "ENABLE " + input.ErpAccountId.Trim() + " " + input.ErpDatabaseName.Trim();
            if (!string.Equals(input.WriteConfirmation?.Trim(), expected, StringComparison.Ordinal))
                throw new InvalidOperationException("写入确认文字不匹配。");
        }

        private static JObject ReadOrCreateTemplateDocument()
        {
            if (File.Exists(DeploymentTemplatePath))
                return JObject.Parse(File.ReadAllText(DeploymentTemplatePath, Encoding.UTF8));
            JObject businessRoot = ReadBusinessConfig();
            GatewaySettings gateway = AppRuntime.Settings.Current;
            AdminSettingsInput developmentGateway = TemplateGatewayInput(gateway);
            BusinessSettingsInput developmentBusiness = TemplateBusinessInput(businessRoot);
            AdminSettingsInput productionGateway = TemplateGatewayInput(gateway);
            productionGateway.EnvironmentName = "production";
            productionGateway.SqlDatabase = Replace888(productionGateway.SqlDatabase);
            productionGateway.U8AccountId = ReplaceAccount(productionGateway.U8AccountId, "008");
            productionGateway.WriteAllowedDatabase = Replace888(productionGateway.WriteAllowedDatabase);
            productionGateway.WriteAllowedAccountId = ReplaceAccount(
                productionGateway.WriteAllowedAccountId, "008");
            productionGateway.EnableTestWrites = false;
            productionGateway.EnableProductionWrites = false;
            BusinessSettingsInput productionBusiness = TemplateBusinessInput(businessRoot);
            productionBusiness.EnvironmentName = "production";
            productionBusiness.LabelSqlDatabase = "LabelSystem_Prod";
            productionBusiness.ErpAccountId = "008";
            productionBusiness.ErpDatabaseName = Replace888(productionBusiness.ErpDatabaseName);
            productionBusiness.EnableDevelopmentLogin = false;
            productionBusiness.U8WritesEnabled = false;
            var document = new JObject
            {
                ["version"] = 1,
                ["updatedAt"] = DateTimeOffset.Now,
                ["development"] = TemplateJson(
                    developmentGateway, developmentBusiness, "development"),
                ["production"] = TemplateJson(
                    productionGateway, productionBusiness, "production")
            };
            WriteJsonAtomic(DeploymentTemplatePath, document);
            Audit("DEPLOYMENT_TEMPLATES_CREATED", new JObject
            {
                ["path"] = DeploymentTemplatePath
            });
            return document;
        }

        private static object TemplateView(JObject document)
        {
            return new
            {
                version = (int?)document["version"] ?? 1,
                updatedAt = (DateTimeOffset?)document["updatedAt"],
                development = document["development"],
                production = document["production"],
                passwordsStored = false
            };
        }

        private static JObject TemplateJson(
            AdminSettingsInput gateway, BusinessSettingsInput business, string key)
        {
            if (gateway == null || business == null)
                throw new InvalidOperationException("配置模板不完整。");
            bool production = string.Equals(key, "production", StringComparison.Ordinal);
            string gatewayEnvironment = production ? "production" : gateway.EnvironmentName?.Trim();
            string businessEnvironment = production ? "production" : business.EnvironmentName?.Trim();
            bool gatewayWrites = !production && gateway.EnableTestWrites;
            bool businessWrites = !production && business.U8WritesEnabled;
            ValidateGatewayTemplate(gateway, gatewayEnvironment, gatewayWrites);
            var businessForValidation = new BusinessSettingsInput
            {
                EnvironmentName = businessEnvironment,
                LabelSqlServer = business.LabelSqlServer,
                LabelSqlDatabase = business.LabelSqlDatabase,
                LabelSqlUser = business.LabelSqlUser,
                ErpAccountId = business.ErpAccountId,
                ErpYear = business.ErpYear,
                ErpDatabaseName = business.ErpDatabaseName,
                GatewayBaseUrl = business.GatewayBaseUrl,
                EnableDevelopmentLogin = !production && business.EnableDevelopmentLogin,
                U8WritesEnabled = businessWrites,
                WriteConfirmation = businessWrites
                    ? "ENABLE " + business.ErpAccountId?.Trim() + " " + business.ErpDatabaseName?.Trim()
                    : null
            };
            ValidateBusiness(businessForValidation);
            return new JObject
            {
                ["gateway"] = JObject.FromObject(new
                {
                    environmentName = gatewayEnvironment,
                    sqlServer = gateway.SqlServer?.Trim(),
                    sqlDatabase = gateway.SqlDatabase?.Trim(),
                    sqlUser = gateway.SqlUser?.Trim(),
                    u8Server = gateway.U8Server?.Trim(),
                    u8AccountId = gateway.U8AccountId?.Trim(),
                    u8Year = gateway.U8Year?.Trim(),
                    u8Subsystem = gateway.U8Subsystem?.Trim(),
                    u8User = gateway.U8User?.Trim(),
                    defaultMaker = gateway.DefaultMaker?.Trim(),
                    operationArchiveRoot = gateway.OperationArchiveRoot?.Trim(),
                    enableTestWrites = gatewayWrites,
                    enableProductionWrites = false,
                    writeAllowedDatabase = gateway.WriteAllowedDatabase?.Trim(),
                    writeAllowedAccountId = gateway.WriteAllowedAccountId?.Trim()
                }),
                ["business"] = JObject.FromObject(new
                {
                    environmentName = businessEnvironment,
                    labelSqlServer = business.LabelSqlServer?.Trim(),
                    labelSqlDatabase = business.LabelSqlDatabase?.Trim(),
                    labelSqlUser = business.LabelSqlUser?.Trim(),
                    erpAccountId = business.ErpAccountId?.Trim(),
                    erpYear = business.ErpYear,
                    erpDatabaseName = business.ErpDatabaseName?.Trim(),
                    gatewayBaseUrl = business.GatewayBaseUrl?.TrimEnd('/'),
                    enableDevelopmentLogin = !production && business.EnableDevelopmentLogin,
                    u8WritesEnabled = businessWrites
                })
            };
        }

        private static void ValidateGatewayTemplate(
            AdminSettingsInput input, string environment, bool writesEnabled)
        {
            if (string.IsNullOrWhiteSpace(environment) ||
                string.IsNullOrWhiteSpace(input.SqlServer) ||
                string.IsNullOrWhiteSpace(input.SqlDatabase) ||
                string.IsNullOrWhiteSpace(input.SqlUser) ||
                string.IsNullOrWhiteSpace(input.U8Server) ||
                string.IsNullOrWhiteSpace(input.U8AccountId) ||
                string.IsNullOrWhiteSpace(input.U8Year) ||
                string.IsNullOrWhiteSpace(input.U8Subsystem) ||
                string.IsNullOrWhiteSpace(input.U8User) ||
                string.IsNullOrWhiteSpace(input.OperationArchiveRoot))
                throw new InvalidOperationException("Gateway 模板不完整。");
            if (!writesEnabled) return;
            if (!string.Equals(environment, "test", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(input.SqlDatabase, input.WriteAllowedDatabase,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(AccountCode(input.U8AccountId),
                    AccountCode(input.WriteAllowedAccountId),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Gateway 写入模板只能绑定指定测试账套。");
        }

        private static GatewaySettings GatewaySettingsFromTemplate(AdminSettingsInput input)
        {
            string environment = input.EnvironmentName?.Trim();
            bool writes = input.EnableTestWrites &&
                !string.Equals(environment, "production", StringComparison.OrdinalIgnoreCase);
            ValidateGatewayTemplate(input, environment, writes);
            return new GatewaySettings
            {
                EnvironmentName = environment,
                BusinessTimeZone = "China Standard Time",
                SqlServer = input.SqlServer?.Trim(),
                SqlDatabase = input.SqlDatabase?.Trim(),
                SqlUser = input.SqlUser?.Trim(),
                U8Server = input.U8Server?.Trim(),
                U8AccountId = input.U8AccountId?.Trim(),
                U8Year = input.U8Year?.Trim(),
                U8Subsystem = input.U8Subsystem?.Trim(),
                U8User = input.U8User?.Trim(),
                DefaultMaker = input.DefaultMaker?.Trim(),
                OperationArchiveRoot = input.OperationArchiveRoot?.Trim(),
                EnableTestWrites = writes,
                EnableProductionWrites = false,
                WriteAllowedDatabase = input.WriteAllowedDatabase?.Trim(),
                WriteAllowedAccountId = input.WriteAllowedAccountId?.Trim()
            };
        }

        private static AdminSettingsInput TemplateGatewayInput(GatewaySettings value)
        {
            return new AdminSettingsInput
            {
                EnvironmentName = value.EnvironmentName,
                SqlServer = value.SqlServer,
                SqlDatabase = value.SqlDatabase,
                SqlUser = value.SqlUser,
                U8Server = value.U8Server,
                U8AccountId = value.U8AccountId,
                U8Year = value.U8Year,
                U8Subsystem = value.U8Subsystem,
                U8User = value.U8User,
                DefaultMaker = value.DefaultMaker,
                OperationArchiveRoot = value.OperationArchiveRoot,
                EnableTestWrites = value.EnableTestWrites,
                EnableProductionWrites = value.EnableProductionWrites,
                WriteAllowedDatabase = value.WriteAllowedDatabase,
                WriteAllowedAccountId = value.WriteAllowedAccountId
            };
        }

        private static BusinessSettingsInput TemplateBusinessInput(JObject root)
        {
            string connectionString = (string)root["ConnectionStrings"]?["LabelSystem"];
            var connection = new SqlConnectionStringBuilder(connectionString ?? string.Empty);
            JObject label = root["LabelSystem"] as JObject;
            JObject gateway = root["U8Gateway"] as JObject;
            return new BusinessSettingsInput
            {
                EnvironmentName = (string)label?["Environment"],
                LabelSqlServer = connection.DataSource,
                LabelSqlDatabase = connection.InitialCatalog,
                LabelSqlUser = connection.UserID,
                ErpAccountId = (string)label?["ErpAccountId"],
                ErpYear = (int?)label?["ErpYear"] ?? 0,
                ErpDatabaseName = (string)label?["ErpDatabaseName"],
                GatewayBaseUrl = (string)gateway?["BaseUrl"],
                EnableDevelopmentLogin = (bool?)label?["EnableDevelopmentLogin"] ?? false,
                U8WritesEnabled = (bool?)label?["U8WritesEnabled"] ?? false
            };
        }

        private static void TestGatewaySql(AdminSettingsInput input, string password)
        {
            using (var connection = new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = input.SqlServer,
                InitialCatalog = input.SqlDatabase,
                UserID = input.SqlUser,
                Password = password,
                IntegratedSecurity = false,
                Encrypt = false,
                TrustServerCertificate = true,
                ConnectTimeout = 10
            }.ConnectionString))
            using (var command = new SqlCommand("SELECT 1", connection))
            {
                connection.Open();
                command.ExecuteScalar();
            }
        }

        private static string TemplateKey(string value)
        {
            string key = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (key != "development" && key != "production")
                throw new InvalidOperationException("配置模板名称无效。");
            return key;
        }

        private static string Replace888(string value)
        {
            return (value ?? string.Empty).Replace("888", "008");
        }

        private static string ReplaceAccount(string value, string account)
        {
            string current = (value ?? string.Empty).Trim();
            int separator = current.LastIndexOf('@');
            return separator >= 0 ? current.Substring(0, separator + 1) + account : account;
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
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".admin.tmp";
            File.WriteAllText(temp, root.ToString(Formatting.Indented), new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null, true);
                else File.Move(temp, path);
            }
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
