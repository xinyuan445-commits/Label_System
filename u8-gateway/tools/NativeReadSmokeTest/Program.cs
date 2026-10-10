using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using U8Gateway.Core.Documents;
using U8Gateway.Metadata;
using U8Gateway.U8;
using U8Gateway.U8.Documents;
using U8Gateway.U8.Xml;

namespace U8Gateway.NativeReadSmokeTest
{
    internal static class Program
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("MaterialOutboundApi.Settings.v1");

        private static int Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine(
                    "Usage: NativeReadSmokeTest <settings.json> <database> <samples.json>");
                return 2;
            }

            try
            {
                JObject stored = JObject.Parse(File.ReadAllText(args[0], Encoding.UTF8));
                JObject publicSettings = (JObject)stored["Public"];
                string database = args[1];
                string accountCode = database.Substring("UFDATA_".Length, 3);
                string configuredAccount = (string)publicSettings["U8AccId"];
                int separator = configuredAccount.LastIndexOf('@');
                string account = separator >= 0
                    ? configuredAccount.Substring(0, separator) + "@" + accountCode
                    : "(default)@" + accountCode;

                var options = new U8GatewayRuntimeOptions
                {
                    SqlServer = (string)publicSettings["SqlServer"],
                    SqlDatabase = database,
                    SqlUser = (string)publicSettings["SqlUser"],
                    SqlPassword = Unprotect((string)stored["SqlPasswordProtected"]),
                    U8 = new U8ConnectionOptions
                    {
                        Server = (string)publicSettings["U8Server"],
                        AccountId = account,
                        Year = (string)publicSettings["U8Year"],
                        Subsystem = (string)publicSettings["U8SubId"],
                        User = (string)publicSettings["U8UserId"],
                        Password = Unprotect((string)stored["U8PasswordProtected"]),
                        Serial = (string)publicSettings["U8Serial"],
                        LoginDate = DateTime.Today
                    }
                };

                JObject samplesFile = JObject.Parse(File.ReadAllText(args[2], Encoding.UTF8));
                List<ReadJob> jobs = samplesFile["samples"]
                    .Select(x => new ReadJob
                    {
                        DocumentType = (string)x["documentType"],
                        VoucherCode = (string)x["voucherCode"]
                    })
                    .GroupBy(x => x.DocumentType, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First())
                    .Where(x => !string.Equals(
                        x.DocumentType,
                        "subcontract-arrival",
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var definitions = new ContractDocumentDefinitionStore();
                using (var queue = new StaOperationQueue())
                {
                    var runtime = new LateBoundU8DocumentRuntime(
                        queue,
                        () => options,
                        definitions,
                        new AdoPersistedXmlCodec());
                    var adapters = new DocumentAdapterCatalog(
                        new U8DocumentProfileCatalog(),
                        runtime);
                    var results = new List<object>();
                    foreach (ReadJob job in jobs)
                    {
                        IDocumentAdapter adapter = adapters.Get(job.DocumentType);
                        DocumentOperationResult result = adapter
                            .QueryAsync(job.VoucherCode, default(System.Threading.CancellationToken))
                            .GetAwaiter()
                            .GetResult();
                        var payload = result.Document as DocumentPayload;
                        results.Add(new
                        {
                            documentType = job.DocumentType,
                            confirmed = result.Confirmed,
                            code = result.Code,
                            headerFields = payload?.Header?.Count ?? 0,
                            lines = payload?.Lines?.Count ?? 0,
                            positions = payload?.Positions?.Count ?? 0
                        });
                        if (!result.Confirmed)
                        {
                            throw new InvalidOperationException(
                                job.DocumentType + ": " + result.Code + " " + result.Message);
                        }
                    }
                    Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(
                        results,
                        Newtonsoft.Json.Formatting.Indented));
                }
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static string Unprotect(string value)
        {
            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(value),
                Entropy,
                DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }

        private sealed class ReadJob
        {
            public string DocumentType { get; set; }
            public string VoucherCode { get; set; }
        }
    }
}
