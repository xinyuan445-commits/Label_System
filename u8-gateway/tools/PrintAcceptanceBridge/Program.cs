using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Dispatcher;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using U8Gateway.Api.Controllers;
using U8Gateway.Persistence.Configuration;

// Explicit acceptance process. Only the TraceBack controller is registered; no U8 COM routes.
internal static class Program
{
    private sealed class OnlyTraceBack : IHttpControllerTypeResolver
    {
        public ICollection<Type> GetControllerTypes(IAssembliesResolver _) => new[] { typeof(TraceBackLabelsController) };
    }
    private static int Main() { try { Run().GetAwaiter().GetResult(); return 0; }
        catch(Exception ex) { Console.Error.WriteLine("Isolated gateway bridge failed: "+ex.GetType().Name+" "+ex.StackTrace); return 1; } }
    private static async Task Run()
    {
        Console.InputEncoding=new UTF8Encoding(false);
        Console.OutputEncoding=new UTF8Encoding(false);
        var raw = new System.Data.Common.DbConnectionStringBuilder {ConnectionString=Environment.GetEnvironmentVariable("PRINT_ACCEPTANCE_U8")};
        // Translate provider keywords explicitly; never forward modern-only keywords to net48.
        var connection = new SqlConnectionStringBuilder {DataSource=(string)raw["Data Source"],
            InitialCatalog=(string)raw["Initial Catalog"],UserID=(string)raw["User ID"],
            Password=(string)raw["Password"],Encrypt=true,TrustServerCertificate=true};
        if (!System.Text.RegularExpressions.Regex.IsMatch(connection.InitialCatalog, "^U8_PrintAcceptance_888_[a-f0-9]{16}$"))
            throw new InvalidOperationException("Isolated database required");
        var root = Environment.GetEnvironmentVariable("PRINT_ACCEPTANCE_WORK");
        var key = Environment.GetEnvironmentVariable("PRINT_ACCEPTANCE_KEY");
        var settings = new GatewaySettings {
            EnvironmentName="test", SqlServer=connection.DataSource,SqlDatabase=connection.InitialCatalog,
            SqlUser=connection.UserID,SqlPassword=connection.Password,U8Server="isolated-no-com",
            U8AccountId="888",U8Year="2021",U8User="acceptance",U8Password="not-used",
            EnableTestWrites=true,EnableProductionWrites=false,WriteAllowedDatabase=connection.InitialCatalog,
            WriteAllowedAccountId="888",InternalApiKey=key,OperationArchiveRoot=Path.Combine(root,"archive") };
        Directory.CreateDirectory(root);
        new ProtectedSettingsStore(Path.Combine(root,"settings.json")).Save(settings,Guid.NewGuid().ToString("N"));
        var assembly = typeof(TraceBackLabelsController).Assembly;
        assembly.GetType("U8Gateway.Api.Infrastructure.AppRuntime").GetMethod("Initialize").Invoke(null,new object[]{root});
        var config = new HttpConfiguration();
        config.Services.Replace(typeof(IHttpControllerTypeResolver),new OnlyTraceBack());
        assembly.GetType("U8Gateway.Api.WebApiConfig").GetMethod("Register").Invoke(null,new object[]{config});
        using(var server = new HttpServer(config))
        using(var client = new HttpClient(server))
        {
            Console.WriteLine("READY");
            string line;
            while((line=Console.ReadLine())!=null)
            {
                try {
                    var request=JObject.Parse(line);
                    using(var message=new HttpRequestMessage(new HttpMethod((string)request["method"]),"http://isolated.invalid"+(string)request["path"]))
                    {
                        message.Headers.Add("X-API-Key",key);
                        if(request["body"]?.Type==JTokenType.String)
                            message.Content=new StringContent((string)request["body"],Encoding.UTF8,"application/json");
                        using(var response=await client.SendAsync(message))
                            Console.WriteLine(JsonConvert.SerializeObject(new{status=(int)response.StatusCode,body=await response.Content.ReadAsStringAsync()}));
                    }
                } catch { Console.WriteLine("{\"status\":500,\"body\":\"{\\\"code\\\":\\\"ACCEPTANCE_BRIDGE_ERROR\\\"}\"}"); }
            }
        }
    }
}
