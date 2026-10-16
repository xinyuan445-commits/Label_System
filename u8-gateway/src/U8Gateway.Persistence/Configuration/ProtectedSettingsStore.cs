using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace U8Gateway.Persistence.Configuration
{
    public sealed class ProtectedSettingsStore
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("U8Gateway.Settings.v1");
        private readonly object _sync = new object();
        private readonly string _path;
        private GatewaySettings _current;

        public ProtectedSettingsStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("配置文件路径不能为空。", nameof(path));
            }

            _path = Path.GetFullPath(path);
            _current = Load();
        }

        public GatewaySettings Current
        {
            get
            {
                lock (_sync)
                {
                    return Clone(_current);
                }
            }
        }

        public bool IsConfigured
        {
            get
            {
                GatewaySettings value = Current;
                return value != null &&
                    !string.IsNullOrWhiteSpace(value.SqlServer) &&
                    !string.IsNullOrWhiteSpace(value.SqlDatabase) &&
                    !string.IsNullOrWhiteSpace(value.SqlUser) &&
                    !string.IsNullOrWhiteSpace(value.SqlPassword) &&
                    !string.IsNullOrWhiteSpace(value.U8Server) &&
                    !string.IsNullOrWhiteSpace(value.U8AccountId) &&
                    !string.IsNullOrWhiteSpace(value.U8Year) &&
                    !string.IsNullOrWhiteSpace(value.U8User) &&
                    !string.IsNullOrWhiteSpace(value.U8Password) &&
                    !string.IsNullOrWhiteSpace(value.InternalApiKey) &&
                    !string.IsNullOrWhiteSpace(value.AdminPasswordHash) &&
                    !string.IsNullOrWhiteSpace(value.OperationArchiveRoot);
            }
        }

        public GatewaySettingsView GetView()
        {
            GatewaySettings value = Current ?? new GatewaySettings();
            return new GatewaySettingsView
            {
                Configured = IsConfigured,
                EnvironmentName = value.EnvironmentName,
                BusinessTimeZone = value.BusinessTimeZone,
                SqlServer = value.SqlServer,
                SqlDatabase = value.SqlDatabase,
                SqlUser = value.SqlUser,
                HasSqlPassword = !string.IsNullOrWhiteSpace(value.SqlPassword),
                U8Server = value.U8Server,
                U8AccountId = value.U8AccountId,
                U8Year = value.U8Year,
                U8Subsystem = value.U8Subsystem,
                U8User = value.U8User,
                HasU8Password = !string.IsNullOrWhiteSpace(value.U8Password),
                DefaultMaker = value.DefaultMaker,
                OperationArchiveRoot = value.OperationArchiveRoot,
                EnableTestWrites = value.EnableTestWrites,
                EnableProductionWrites = value.EnableProductionWrites,
                WriteAllowedDatabase = value.WriteAllowedDatabase,
                WriteAllowedAccountId = value.WriteAllowedAccountId,
                HasInternalApiKey = !string.IsNullOrWhiteSpace(value.InternalApiKey),
                Revision = value.Revision
            };
        }

        public string Save(GatewaySettings settings, string newAdminPassword)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            lock (_sync)
            {
                GatewaySettings prior = _current ?? new GatewaySettings();
                bool generatedKey = false;
                if (string.IsNullOrWhiteSpace(settings.SqlPassword))
                {
                    settings.SqlPassword = prior.SqlPassword;
                }
                if (string.IsNullOrWhiteSpace(settings.U8Password))
                {
                    settings.U8Password = prior.U8Password;
                }
                if (string.IsNullOrWhiteSpace(settings.InternalApiKey))
                {
                    if (string.IsNullOrWhiteSpace(prior.InternalApiKey))
                    {
                        settings.InternalApiKey = GenerateKey();
                        generatedKey = true;
                    }
                    else
                    {
                        settings.InternalApiKey = prior.InternalApiKey;
                    }
                }
                settings.AdminPasswordHash = prior.AdminPasswordHash;
                settings.AdminPasswordSalt = prior.AdminPasswordSalt;
                if (string.IsNullOrWhiteSpace(settings.AdminPasswordHash))
                {
                    if (string.IsNullOrEmpty(newAdminPassword))
                    {
                        throw new InvalidOperationException("首次配置必须设置管理密码。");
                    }
                    SetAdminPassword(settings, newAdminPassword);
                }
                else if (!string.IsNullOrEmpty(newAdminPassword))
                {
                    SetAdminPassword(settings, newAdminPassword);
                }
                settings.Revision = prior.Revision + 1;
                Persist(settings);
                _current = Clone(settings);
                return generatedKey ? settings.InternalApiKey : null;
            }
        }

        public string RotateInternalApiKey()
        {
            lock (_sync)
            {
                GatewaySettings settings = Clone(_current ?? new GatewaySettings());
                settings.InternalApiKey = GenerateKey();
                settings.Revision++;
                Persist(settings);
                _current = settings;
                return settings.InternalApiKey;
            }
        }

        public bool VerifyAdminPassword(string password)
        {
            GatewaySettings settings = Current;
            if (settings == null || string.IsNullOrWhiteSpace(settings.AdminPasswordHash))
            {
                return false;
            }
            if (string.IsNullOrEmpty(password))
            {
                return false;
            }

            byte[] salt = Convert.FromBase64String(settings.AdminPasswordSalt);
            byte[] expected = Convert.FromBase64String(settings.AdminPasswordHash);
            using (var derive = new Rfc2898DeriveBytes(
                password,
                salt,
                100000,
                HashAlgorithmName.SHA256))
            {
                return FixedTimeEquals(expected, derive.GetBytes(expected.Length));
            }
        }

        private GatewaySettings Load()
        {
            if (!File.Exists(_path))
            {
                return new GatewaySettings();
            }

            var stored = JsonConvert.DeserializeObject<PersistedSettings>(
                File.ReadAllText(_path, Encoding.UTF8));
            return stored == null ? new GatewaySettings() : stored.ToRuntime();
        }

        private void Persist(GatewaySettings value)
        {
            string directory = Path.GetDirectoryName(_path);
            Directory.CreateDirectory(directory);
            string temp = _path + ".tmp";
            string backup = _path + ".bak";
            string json = JsonConvert.SerializeObject(
                PersistedSettings.FromRuntime(value),
                Formatting.Indented);
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, backup, true);
            }
            else
            {
                File.Move(temp, _path);
            }
        }

        private static void SetAdminPassword(GatewaySettings settings, string password)
        {
            byte[] salt = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(salt);
            }
            using (var derive = new Rfc2898DeriveBytes(
                password,
                salt,
                100000,
                HashAlgorithmName.SHA256))
            {
                settings.AdminPasswordSalt = Convert.ToBase64String(salt);
                settings.AdminPasswordHash = Convert.ToBase64String(derive.GetBytes(32));
            }
        }

        private static string GenerateKey()
        {
            byte[] value = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(value);
            }
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            byte[] bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                Entropy,
                DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(bytes);
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            byte[] bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(value),
                Entropy,
                DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }

        private static GatewaySettings Clone(GatewaySettings value)
        {
            return value == null
                ? null
                : JsonConvert.DeserializeObject<GatewaySettings>(
                    JsonConvert.SerializeObject(value));
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
        }

        private sealed class PersistedSettings
        {
            public GatewaySettings Public { get; set; }
            public string SqlPasswordProtected { get; set; }
            public string U8PasswordProtected { get; set; }
            public string InternalApiKeyProtected { get; set; }

            public static PersistedSettings FromRuntime(GatewaySettings value)
            {
                GatewaySettings publicValue = Clone(value);
                publicValue.SqlPassword = null;
                publicValue.U8Password = null;
                publicValue.InternalApiKey = null;
                return new PersistedSettings
                {
                    Public = publicValue,
                    SqlPasswordProtected = Protect(value.SqlPassword),
                    U8PasswordProtected = Protect(value.U8Password),
                    InternalApiKeyProtected = Protect(value.InternalApiKey)
                };
            }

            public GatewaySettings ToRuntime()
            {
                GatewaySettings value = Public ?? new GatewaySettings();
                value.SqlPassword = Unprotect(SqlPasswordProtected);
                value.U8Password = Unprotect(U8PasswordProtected);
                value.InternalApiKey = Unprotect(InternalApiKeyProtected);
                return value;
            }
        }
    }
}
