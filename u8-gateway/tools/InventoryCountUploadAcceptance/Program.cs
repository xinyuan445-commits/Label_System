using System;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using U8Gateway.Persistence.Configuration;
using U8Gateway.U8;
using U8Gateway.U8.Documents;
using U8Gateway.U8.Xml;
using U8Gateway.Metadata;
using U8Gateway.Core.Documents;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2 && args.Length != 3) return 2;
        Action<object> report = value => { if (args.Length == 3) File.WriteAllText(args[2], JsonConvert.SerializeObject(value)); };
        GatewaySettings s = null;
        try
        {
            s = new ProtectedSettingsStore(args[0]).Current;
            report(new { stage = "settings-loaded", s.EnvironmentName, s.EnableTestWrites, s.WriteAllowedDatabase, s.WriteAllowedAccountId });
            if (s.SqlDatabase != "UFDATA_888_2021" || U8Gateway.U8.Sources.U8InventoryCountReader.NormalizeAccount(s.U8AccountId) != "888")
                throw new InvalidOperationException("888 only");
            var options = new U8GatewayRuntimeOptions { SqlServer = s.SqlServer, SqlDatabase = s.SqlDatabase,
                SqlUser = s.SqlUser, SqlPassword = s.SqlPassword,
                U8 = new U8ConnectionOptions { Server = s.U8Server, AccountId = s.U8AccountId, Year = s.U8Year,
                    Subsystem = s.U8Subsystem, User = s.U8User, Password = s.U8Password, LoginDate = U8Gateway.Contracts.BusinessClock.Today } };
            using (var queue = new StaOperationQueue())
            {
                var definitions = new ContractDocumentDefinitionStore();
                var runtime = new LateBoundU8DocumentRuntime(queue, () => options, definitions, new AdoPersistedXmlCodec());
                report(new { stage = "native-query-started", s.EnvironmentName, s.EnableTestWrites, s.WriteAllowedDatabase, s.WriteAllowedAccountId });
                var result = new DocumentAdapterCatalog(new U8DocumentProfileCatalog(), runtime).Get("inventory-count")
                    .QueryAsync(args[1], default(System.Threading.CancellationToken)).GetAwaiter().GetResult();
                var payload = (DocumentPayload)result.Document;
                report(new { stage = "native-query-completed", result.Confirmed, lines = payload.Lines.Count,
                    header = payload.Header.Where(p => new[] { "ccvtype", "csource", "vt_id", "bposcheck" }.Contains(p.Key)),
                    s.EnvironmentName, s.EnableTestWrites, s.WriteAllowedDatabase, s.WriteAllowedAccountId });
                Console.WriteLine("Native read confirmed=" + result.Confirmed + "; lines=" + payload.Lines.Count);
                foreach (var pair in payload.Header.Where(p => new[] { "ccvtype", "csource", "vt_id", "bposcheck" }.Contains(p.Key)))
                    Console.WriteLine(pair.Key + "=" + pair.Value);
            }
            return 0;
        }
        catch (Exception error)
        {
            var root = error.GetBaseException(); var message = root.Message;
            if (s != null) foreach (var secret in new[] { s.SqlPassword, s.U8Password, s.InternalApiKey })
                if (!string.IsNullOrEmpty(secret)) message = message.Replace(secret, "[redacted]");
            Console.Error.WriteLine(root.GetType().Name + ": " + message + "; HResult=" + root.HResult);
            report(new { stage = "failed", type = root.GetType().Name, message, root.HResult });
            foreach (var frame in (root.StackTrace ?? "").Split('\n').Take(5)) Console.Error.WriteLine(frame.Trim());
            return 1;
        }
    }
}
