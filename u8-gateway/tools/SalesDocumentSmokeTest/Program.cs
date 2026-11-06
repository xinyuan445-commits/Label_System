using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Xml.Linq;
using Newtonsoft.Json;
using U8Gateway.Core.Documents;
using U8Gateway.Metadata;
using U8Gateway.Persistence.Configuration;
using U8Gateway.U8;
using U8Gateway.U8.Documents;
using U8Gateway.U8.Sources;
using U8Gateway.U8.Xml;

internal static class Program
{
    private static int Main(string[] args)
    {
        GatewaySettings settings = null;
        try
        {
            LegacyClrActivation.Ensure();
            if (!U8SourceDocumentReader.IsSupportedSourceType("sales-order-return") ||
                !U8SourceDocumentReader.IsSupportedSourceType("sales-delivery-return")) throw new Exception("Missing return source types.");
            var definition = new ContractDocumentDefinitionStore().Get("sales-delivery");
            var prepared = new DocumentWritePreparer().PrepareCreate(definition, new DocumentOperationRequest {
                OperatorName = "test", Header = new Dictionary<string, object> {
                    ["ddate"]="2026-11-06", ["cstcode"]="01", ["cdepcode"]="D1", ["ccuscode"]="C1",
                    ["cexch_name"]="人民币", ["iexchrate"]=1m, ["breturnflag"]=1 },
                Lines = new List<IDictionary<string, object>> { new Dictionary<string, object> {
                    ["cwhcode"]="W1", ["cinvcode"]="M1", ["iquantity"]=-2m, ["icorid"]=1000031711,
                    ["ccorcode"]="DL1", ["isosid"]=1000016857, ["itaxunitprice"]=200m } }
            }, DateTimeOffset.Now);
            if (!prepared.IsValid || !prepared.Payload.Lines[0].ContainsKey("icorid") ||
                !prepared.Payload.Lines[0].ContainsKey("ccorcode")) throw new Exception("Return native linkage contract failed: " + JsonConvert.SerializeObject(prepared.Issues));
            var packet = new AdoPersistedXmlCodec().Encode(definition, prepared.Payload);
            var decoded = new AdoPersistedXmlCodec().Decode(definition, packet);
            if (Convert.ToDecimal(decoded.Lines[0]["iquantity"]) != -2m || Convert.ToInt64(decoded.Lines[0]["icorid"]) != 1000031711)
                throw new Exception("Return quantity/link XML round trip failed.");
            foreach (var sign in new[] { 1m, -1m }) foreach (var rate in new[] { 1m, 7.125m })
            {
                var row = new XElement("row", new XAttribute("imoney", sign * 176.99m), new XAttribute("isum", sign * 200m));
                typeof(LateBoundU8DocumentRuntime).GetMethod("CompleteSalesRow", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { row, "G1", "U1", 0, rate });
                var amount = decimal.Round(sign * 176.99m * rate, 2, MidpointRounding.AwayFromZero);
                var total = decimal.Round(sign * 200m * rate, 2, MidpointRounding.AwayFromZero);
                if ((string)row.Attribute("editprop") != "A" || (string)row.Attribute("ccomunitcode") != "U1" ||
                    (decimal)row.Attribute("inatmoney") != amount || (decimal)row.Attribute("inatsum") != total ||
                    (decimal)row.Attribute("inattax") != total - amount)
                    throw new Exception("Native sales units/base-currency amounts failed.");
            }
            foreach (var available in new[] { 1m, 3m })
            {
                var row = new XElement("row", new XAttribute("icorid", 41), new XAttribute("iquantity", -2));
                typeof(LateBoundU8DocumentRuntime).GetMethod("CompleteSalesReturnReference", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { row, available });
                if (available == 3m ? row.Attribute("icorid") != null || (long)row.Attribute("icoridlsid") != 41 :
                    row.Attribute("icoridlsid") != null || (long)row.Attribute("icorid") != 41)
                    throw new Exception("Invoiced/uninvoiced original dispatch linkage failed.");
            }
            if (args.Length == 0) { Console.WriteLine("sales source type contracts passed (no SQL/COM)"); return 0; }
            if (args.Length != 2) throw new Exception("Expected settings path and existing voucher code.");
            settings = new ProtectedSettingsStore(args[0]).Current;
            if (settings.SqlDatabase != "UFDATA_888_2021") throw new Exception("888 only.");
            if (args[1].StartsWith("source:", StringComparison.Ordinal))
            {
                var parts = args[1].Split(':');
                var reader = new U8SourceDocumentReader(settings.SqlServer, settings.SqlDatabase, settings.SqlUser, settings.SqlPassword);
                Console.WriteLine(JsonConvert.SerializeObject(reader.GetLines(parts[1], new[] { int.Parse(parts[2]) })));
                return 0;
            }
            var options = new U8GatewayRuntimeOptions { SqlServer = settings.SqlServer, SqlDatabase = settings.SqlDatabase,
                SqlUser = settings.SqlUser, SqlPassword = settings.SqlPassword,
                U8 = new U8ConnectionOptions { Server = settings.U8Server, AccountId = settings.U8AccountId, Year = settings.U8Year,
                    Subsystem = settings.U8Subsystem, User = settings.U8User, Password = settings.U8Password,
                    LoginDate = U8Gateway.Contracts.BusinessClock.Today } };
            using (var queue = new StaOperationQueue())
            {
                var store = new ContractDocumentDefinitionStore();
                var runtime = new LateBoundU8DocumentRuntime(queue, () => options, store, new AdoPersistedXmlCodec());
                var result = new DocumentAdapterCatalog(new U8DocumentProfileCatalog(), runtime).Get("sales-delivery")
                    .QueryAsync(args[1], default(System.Threading.CancellationToken)).GetAwaiter().GetResult();
                var data = (DocumentPayload)result.Document;
                Console.WriteLine(JsonConvert.SerializeObject(new { result.Confirmed, result.VoucherCode, result.Version,
                    header = data.Header.Where(p => new[] { "breturnflag", "cvouchtype", "csocode" }.Contains(p.Key)),
                    lines = data.Lines.Select(l => l.Where(p => new[] { "autoid", "idlsid", "irowno", "isosid", "icorid", "iquantity", "ccorcode" }.Contains(p.Key))) }));
            }
            return 0;
        }
        catch (Exception error)
        {
            string message = error.ToString();
            if (settings != null) foreach (var secret in new[] { settings.SqlPassword, settings.U8Password, settings.InternalApiKey })
                if (!string.IsNullOrEmpty(secret)) message = message.Replace(secret, "[redacted]");
            Console.Error.WriteLine(message);
            return 1;
        }
    }
}
