using System;
using System.IO;
using U8Gateway.Contracts;
using U8Gateway.Core.Metadata;
using U8Gateway.Core.Documents;
using U8Gateway.Core.Operations;
using U8Gateway.Metadata;
using U8Gateway.Persistence.Archive;
using U8Gateway.Persistence.Configuration;
using U8Gateway.U8;
using U8Gateway.U8.Documents;
using U8Gateway.U8.Xml;

namespace U8Gateway.Api.Infrastructure
{
    internal static class AppRuntime
    {
        private static readonly object Sync = new object();

        public static string DataDirectory { get; private set; }
        public static ProtectedSettingsStore Settings { get; private set; }
        public static IDocumentDefinitionStore Definitions { get; private set; }
        public static StaOperationQueue U8Queue { get; private set; }
        public static IDocumentAdapterCatalog DocumentAdapters { get; private set; }

        public static GatewaySettings CurrentSettings => Settings.Current;

        public static string ServiceVersion =>
            typeof(AppRuntime).Assembly.GetName().Version.ToString();

        public static void Initialize(string dataDirectory)
        {
            lock (Sync)
            {
                if (Settings != null)
                {
                    return;
                }

                DataDirectory = Path.GetFullPath(dataDirectory);
                Directory.CreateDirectory(DataDirectory);
                Settings = new ProtectedSettingsStore(
                    Path.Combine(DataDirectory, "settings.json"));
                Definitions = new ContractDocumentDefinitionStore();
                U8Queue = new StaOperationQueue();
                DocumentAdapters = new DocumentAdapterCatalog(
                    new U8DocumentProfileCatalog(),
                    new LateBoundU8DocumentRuntime(
                        U8Queue,
                        RuntimeOptions,
                        Definitions,
                        new AdoPersistedXmlCodec()));
            }
        }

        private static U8GatewayRuntimeOptions RuntimeOptions()
        {
            GatewaySettings settings = Settings.Current;
            return new U8GatewayRuntimeOptions
            {
                EnvironmentName = settings.EnvironmentName,
                EnableWrites = settings.EnableTestWrites,
                WriteAllowedDatabase = settings.WriteAllowedDatabase,
                WriteAllowedAccountId = settings.WriteAllowedAccountId,
                SqlServer = settings.SqlServer,
                SqlDatabase = settings.SqlDatabase,
                SqlUser = settings.SqlUser,
                SqlPassword = settings.SqlPassword,
                U8 = new U8ConnectionOptions
                {
                    Server = settings.U8Server,
                    AccountId = settings.U8AccountId,
                    Year = settings.U8Year,
                    Subsystem = settings.U8Subsystem,
                    User = settings.U8User,
                    Password = settings.U8Password,
                    LoginDate = BusinessClock.Today
                }
            };
        }

        public static void EnsureU8WriteAllowed()
        {
            LateBoundU8DocumentRuntime.ValidateWriteOptions(RuntimeOptions());
        }

        public static IOperationJournal CreateOperationJournal()
        {
            return new FileOperationJournal(new OperationArchiveOptions
            {
                RootPath = ResolveOperationArchiveRoot()
            });
        }

        public static IOperationRegistry CreateOperationRegistry()
        {
            return new FileOperationRegistry(new OperationArchiveOptions
            {
                RootPath = ResolveOperationArchiveRoot()
            });
        }

        public static DateTimeOffset BusinessNow()
        {
            return BusinessClock.Now;
        }

        private static string ResolveOperationArchiveRoot()
        {
            GatewaySettings settings = CurrentSettings;
            if (string.IsNullOrWhiteSpace(settings.OperationArchiveRoot))
            {
                throw new InvalidOperationException("操作凭证目录尚未配置。");
            }

            return Path.IsPathRooted(settings.OperationArchiveRoot)
                ? settings.OperationArchiveRoot
                : Path.Combine(DataDirectory, settings.OperationArchiveRoot);
        }

        public static void Dispose()
        {
            lock (Sync)
            {
                U8Queue?.Dispose();
                U8Queue = null;
                DocumentAdapters = null;
                Definitions = null;
                Settings = null;
            }
        }
    }
}
