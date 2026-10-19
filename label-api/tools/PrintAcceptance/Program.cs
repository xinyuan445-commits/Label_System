using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabelSystem.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Opt-in integration runner. Every writable connection is a freshly created, named shadow DB.
// The existing 888 database supplies empty table structure only. No real printer is reachable.
return await Acceptance.Run();

static class Acceptance
{
    static readonly JsonSerializerOptions Json=PrintRules.Json;
    static readonly string Root=Path.GetFullPath(Environment.GetEnvironmentVariable("PRINT_ACCEPTANCE_REPO")??".");
    static readonly string RunId=Guid.NewGuid().ToString("N")[..16];
    static readonly string LabelDb="LabelSystem_PrintAcceptance_"+RunId;
    static readonly string U8Db="U8_PrintAcceptance_888_"+RunId;
    static readonly string Work=Path.Combine(Root,".tools","print-acceptance",RunId);
    static string labelConnection="",u8Connection="";
    static readonly List<object> Evidence=[];
    static readonly List<Rpc> Children=[];
    static HttpClient api=null!;
    static WebApplication app=null!;
    static Rpc gateway=null!,agent=null!;
    static GatewayTransport transport=null!;
    static Process? renderer;
    static readonly Guid Device=Guid.NewGuid();
    static string serverPublic="";
    static long template;
    static readonly CancellationToken Ct=CancellationToken.None;

    public static async Task<int> Run()
    {
        Directory.CreateDirectory(Work);
        var successful=false;
        try
        {
            await Provision();
            await Start();
            await Setup();
            await Scenarios();
            successful=true;
            Console.WriteLine("PASS: all isolated acceptance assertions");
            return 0;
        }
        catch(Exception ex)
        {
            // Never print SQL connections, process environments, signed payloads or secret keys.
            Console.WriteLine("FAIL: "+ex.GetType().Name+" "+Safe(ex.Message));
            Evidence.Add(new{check="runner",passed=false,error=Safe(ex.Message)});
            return 1;
        }
        finally
        {
            if(app is not null)await app.DisposeAsync();
            foreach(var child in Children)child.Dispose();
            if(renderer is not null&&!renderer.HasExited){renderer.Kill(true);await renderer.WaitForExitAsync();}
            var result=new{runId=RunId,passed=successful,labelDatabase=LabelDb,traceBackDatabase=U8Db,
                account="888",environment="test",production008Accessed=false,realPrinterStarted=false,
                startedAt=DateTimeOffset.Now.ToString("O"),evidence=Evidence};
            await File.WriteAllTextAsync(Path.Combine(Work,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions(Json){WriteIndented=true}));
            Console.WriteLine("EVIDENCE: "+Path.Combine(Work,"result.json"));
            await Cleanup();
        }
    }
    static string Safe(string message)
    {
        if(message.Contains("password",StringComparison.OrdinalIgnoreCase)||message.Contains("server=",StringComparison.OrdinalIgnoreCase)||message.Contains("data source",StringComparison.OrdinalIgnoreCase))return "sensitive diagnostic suppressed";
        return message.Length>1800?message[..1800]:message;
    }
    static async Task Cleanup()
    {
        var removed=new List<string>();
        try
        {
            SqlConnection.ClearAllPools();
            var master=Environment.GetEnvironmentVariable("PRINT_ACCEPTANCE_MASTER")!;
            if(new SqlConnectionStringBuilder(master).InitialCatalog!="master")throw new Exception("Cleanup master boundary rejected");
            foreach(var name in new[]{LabelDb,U8Db})
            {
                if(!Regex.IsMatch(name,"^(LabelSystem_PrintAcceptance_|U8_PrintAcceptance_888_)[a-f0-9]{16}$")||!name.EndsWith(RunId))throw new Exception("Cleanup ownership rejected");
                await Exec(master,$"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END;");
                Check(await Scalar(master,"SELECT DB_ID(@name);",("@name",name)) is DBNull,"temporary-database-removed",new{database=name});removed.Add(name);
            }
            foreach(var file in new[]{Path.Combine(Work,"server.pem"),Path.Combine(Work,"agent","device.der"),Path.Combine(Work,"gateway","settings.json")})if(File.Exists(file))File.Delete(file);
            await File.WriteAllTextAsync(Path.Combine(Work,"cleanup.json"),JsonSerializer.Serialize(new{passed=true,removed,at=DateTimeOffset.Now.ToString("O")},Json));
        }
        catch(Exception ex){Console.WriteLine("CLEANUP_FAILED: "+Safe(ex.Message));await File.WriteAllTextAsync(Path.Combine(Work,"cleanup.json"),JsonSerializer.Serialize(new{passed=false,removed,error=Safe(ex.Message)},Json));}
    }
    static string Conn(string master,string database)
    {
        if(!Regex.IsMatch(database,"^(LabelSystem_PrintAcceptance_|U8_PrintAcceptance_888_)[a-f0-9]{16}$"))throw new Exception("Writable database name rejected");
        var b=new SqlConnectionStringBuilder(master){InitialCatalog=database,TrustServerCertificate=true,ApplicationName="Label Print Isolated Acceptance"};return b.ConnectionString;
    }
    static async Task<SqlConnection> Open(string connection)
    {
        var b=new SqlConnectionStringBuilder(connection);
        if(b.InitialCatalog!="master"&&!Regex.IsMatch(b.InitialCatalog,"^(LabelSystem_PrintAcceptance_|U8_PrintAcceptance_888_)[a-f0-9]{16}$"))throw new Exception("Connection boundary rejected");
        var c=new SqlConnection(connection);await c.OpenAsync();return c;
    }
    static async Task<object?> Scalar(string connection,string sql,params (string,object?)[] args)
    {
        await using var c=await Open(connection);await using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.CommandTimeout=120;
        foreach(var(k,v)in args)cmd.Parameters.AddWithValue(k,v??DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }
    static async Task Exec(string connection,string sql,params (string,object?)[] args)=>_ = await Scalar(connection,sql,args);
    static async Task Provision()
    {
        var master=Environment.GetEnvironmentVariable("PRINT_ACCEPTANCE_MASTER")??throw new Exception("Explicit master connection required");
        var b=new SqlConnectionStringBuilder(master);
        if(b.InitialCatalog!="master")throw new Exception("Master-only bootstrap required");
        if((string?)await Scalar(master,"SELECT DB_NAME();")!="master")throw new Exception("Master boundary failed");
        labelConnection=Conn(master,LabelDb);u8Connection=Conn(master,U8Db);
        await Exec(master,$"CREATE DATABASE [{LabelDb}];");
        await Exec(master,$"CREATE DATABASE [{U8Db}];");
        Console.WriteLine("CREATED: "+LabelDb+" / "+U8Db);
        string[] migrations=["001_label_business_schema_draft.sql","002_seed_settings_and_source_reservation_index.sql",
            "003_cache_u8_rd_style.sql","004_fix_reference_data_sync_time_constraint.sql","005_cache_u8_warehouse_position_department.sql",
            "006_create_users_permissions_preferences.sql","007_add_local_user_credentials.sql","008_create_user_admin_audit.sql",
            "009_create_synced_document_drafts.sql","031_create_label_code_rules.sql","034_allow_shared_inventory_label_rule.sql",
            "037_cache_u8_document_master_data.sql","040_add_generated_label_to_operation_line.sql","043_defer_product_label_code_until_print.sql",
            "046_add_traceback_mirror_and_sync_tasks.sql","049_cache_u8_materials.sql","052_label_print_phase1.sql"];
        foreach(var file in migrations)
        {
            var source=await File.ReadAllTextAsync(Path.Combine(Root,"docs","database",file));
            if(Regex.IsMatch(source,@"(?im)^\s*USE\s+"))throw new Exception("Migration changes database context");
            foreach(var batch in Regex.Split(source,@"(?im)^\s*GO\s*(?:--[^\r\n]*)?$"))if(!string.IsNullOrWhiteSpace(batch))await Exec(labelConnection,batch);
            Console.WriteLine("MIGRATED: "+file);
        }
        // Copies no rows or production data. The legacy table definitions come from account 888.
        foreach(var table in new[]{"TraceBackOrder","TraceBackGoods","TraceBackOnlyCode"})
            await Exec(u8Connection,$"SELECT TOP(0) * INTO dbo.[{table}] FROM [UFDATA_888_2021].dbo.[{table}];");
        await Exec(u8Connection,"""
            SELECT TOP(0) cInvCode,cInvName,cComunitCode,bInvBatch,dEDate INTO dbo.Inventory FROM UFDATA_888_2021.dbo.Inventory;
            SELECT TOP(0) cWhCode,cWhName,bWhPos,dWhEndDate INTO dbo.Warehouse FROM UFDATA_888_2021.dbo.Warehouse;
            SELECT TOP(0) cWhCode,cPosCode,cPosName INTO dbo.Position FROM UFDATA_888_2021.dbo.Position;
            INSERT dbo.Inventory(cInvCode,cInvName,cComunitCode,bInvBatch) VALUES('MAT-ACCEPT','Synthetic acceptance','EA',1);
            INSERT dbo.Warehouse(cWhCode,cWhName,bWhPos) VALUES('ACCEPT','Synthetic warehouse',1);
            INSERT dbo.Position(cWhCode,cPosCode,cPosName) VALUES('ACCEPT','P1','Synthetic position');
            """);
        Check(Convert.ToInt32(await Scalar(labelConnection,"SELECT COUNT(*) FROM dbo.schema_migration;"))==17,"migration-001-through-017",new{migrationCount=17});
    }
    static ProcessStartInfo Child(string file,params string[] args)
    {
        var info=new ProcessStartInfo(file){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WorkingDirectory=Root};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        info.Environment.Remove("PRINT_ACCEPTANCE_MASTER");return info;
    }
    static async Task Start()
    {
        var internalKey=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pi=Child(Path.Combine(Root,"u8-gateway","tools","PrintAcceptanceBridge","bin","Debug","net48","PrintAcceptanceBridge.exe"));
        pi.Environment["PRINT_ACCEPTANCE_U8"]=u8Connection;pi.Environment["PRINT_ACCEPTANCE_KEY"]=internalKey;
        pi.Environment["PRINT_ACCEPTANCE_WORK"]=Path.Combine(Work,"gateway");gateway=await Rpc.Start(pi);Children.Add(gateway);
        using var socket=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
        var renderKey=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var ri=Child(Path.Combine(Root,".tools","print-renderer","Scripts","python.exe"),"-m","label_renderer");ri.WorkingDirectory=Path.Combine(Root,"label-print","renderer");
        ri.Environment["LABEL_RENDERER_API_KEY"]=renderKey;ri.Environment["LABEL_RENDERER_PORT"]=port.ToString();
        renderer=Process.Start(ri)!;_ = renderer.StandardError.ReadToEndAsync();
        using(var http=new HttpClient())for(var n=0;n<100;n++)
        {
            try{using var request=new HttpRequestMessage(HttpMethod.Post,$"http://127.0.0.1:{port}/fonts"){Content=new StringContent("{}",Encoding.UTF8,"application/json")};request.Headers.Add("X-Renderer-Key",renderKey);using var res=await http.SendAsync(request);if(res.IsSuccessStatusCode)break;}
            catch(HttpRequestException){}if(n==99)throw new Exception("Isolated renderer did not become ready");await Task.Delay(100);
        }
        using var signing=ECDsa.Create(ECCurve.NamedCurves.nistP256);var keyPath=Path.Combine(Work,"server.pem");await File.WriteAllTextAsync(keyPath,signing.ExportPkcs8PrivateKeyPem());
        serverPublic=Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo());
        using var device=ECDsa.Create(ECCurve.NamedCurves.nistP256);Directory.CreateDirectory(Path.Combine(Work,"agent"));await File.WriteAllBytesAsync(Path.Combine(Work,"agent","device.der"),device.ExportPkcs8PrivateKey());
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseTestServer();builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{
            ["ConnectionStrings:LabelSystem"]=labelConnection,["LabelSystem:Environment"]="test",["LabelSystem:ErpAccountId"]="888",
            ["LabelSystem:ErpYear"]="2021",["LabelSystem:ErpDatabaseName"]=U8Db,["LabelSystem:U8WritesEnabled"]="true",
            ["Printing:Enabled"]="true",["Printing:AllowNewDispatch"]="true",["Printing:RecoveryEnabled"]="false",
            ["Printing:Namespace"]="acceptance-"+RunId,["Printing:RootPath"]=Path.Combine(Work,"artifacts"),["Printing:SigningKeyPath"]=keyPath,
            ["Printing:RendererUrl"]=$"http://127.0.0.1:{port}",["Printing:RendererApiKey"]=renderKey,
            ["U8Gateway:BaseUrl"]="http://isolated.invalid",["U8Gateway:ApiKey"]=internalKey});
        builder.Services.Configure<LabelSystemOptions>(builder.Configuration.GetSection("LabelSystem"));builder.Services.Configure<U8GatewayOptions>(builder.Configuration.GetSection("U8Gateway"));
        builder.Services.AddSingleton<SqlConnectionFactory>();builder.Services.AddScoped<LabelCodeRuleEngine>();builder.Services.AddScoped<UserRepository>();
        builder.Services.AddScoped<TraceBackMirrorRepository>();builder.Services.AddScoped<U8GatewayClient>();builder.Services.AddSingleton<DevelopmentSessionStore>();
        transport=new GatewayTransport(gateway);builder.Services.AddHttpClient(nameof(U8GatewayClient)).ConfigurePrimaryHttpMessageHandler(()=>transport);
        builder.Services.AddPrinting(builder.Configuration);
        app=builder.Build();app.UseMiddleware<RequestIdMiddleware>();app.UseMiddleware<ApiExceptionMiddleware>();app.UseMiddleware<SessionAuthenticationMiddleware>();app.MapPrinting();
        await app.StartAsync();api=app.GetTestClient();
        var token=app.Services.GetRequiredService<DevelopmentSessionStore>().Create(new SessionUser("acceptance","Acceptance",["admin"])).Token;
        api.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        await Exec(labelConnection,"""
            INSERT dbo.application_user(user_id,login_name,display_name,auth_source,is_admin,created_by,updated_by)
            VALUES('acceptance','acceptance','Acceptance','DEVELOPMENT',1,'acceptance','acceptance');
            UPDATE dbo.system_function SET is_enabled=1 WHERE function_code LIKE 'print.%';
            """);
        await Call("POST","devices",new PrintDeviceRequest(Device,"Isolated mock",Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()),"acceptance"));
        await StartAgent();
    }
    static async Task StartAgent()
    {
        var info=Child(Path.Combine(Root,".tools","print-acceptance","agent-test.exe"),"-test.run","^TestPrintAcceptanceBridge$");
        info.Environment["PRINT_ACCEPTANCE_AGENT_ROOT"]=Path.Combine(Work,"agent");info.Environment["PRINT_ACCEPTANCE_DEVICE"]=Device.ToString();
        info.Environment["PRINT_ACCEPTANCE_SERVER_PUBLIC"]=serverPublic;agent=await Rpc.Start(info);Children.Add(agent);
    }
    static async Task<JsonObject> Call(string method,string path,object? body=null,int expected=200)
    {
        using var req=new HttpRequestMessage(new HttpMethod(method),"/api/v1/print/"+path);
        if(body is not null)req.Content=JsonContent.Create(body,options:Json);
        using var res=await api.SendAsync(req);var text=await res.Content.ReadAsStringAsync();
        if((int)res.StatusCode!=expected)throw new Exception($"API {method} {path}: {(int)res.StatusCode} {text}");
        if(expected!=200)return new JsonObject();var node=JsonNode.Parse(text)!;return node["data"]!.AsObject();
    }
    static T Read<T>(JsonObject value)=>value.Deserialize<T>(Json)!;
    static string Id()=>Guid.NewGuid().ToString();
    static async Task Setup()
    {
        var spec=new LabelCodeRuleSpec{Symbologies=["QR"],Segments=[
            new(){Kind="FIELD",Field="ERP_ACCOUNT_ID",Length=3,ValidateCurrentAccount=true},new(){Kind="LITERAL",Value="|"},
            new(){Kind="FIELD",Field="MATERIAL_CODE",MaxLength=30},new(){Kind="LITERAL",Value="|"},new(){Kind="FIELD",Field="BATCH_NO",MaxLength=20},new(){Kind="LITERAL",Value="|"},
            new(){Kind="FIELD",Field="ISSUED_QUANTITY",MaxLength=20,DecimalScale=6},new(){Kind="LITERAL",Value="|"},new(){Kind="FIELD",Field="LABEL_UNIQUE_VALUE",Length=8}]};
        var validated=new LabelCodeRuleEngine().Validate(JsonSerializer.SerializeToElement(spec,Json));
        var version=Convert.ToInt64(await Scalar(labelConnection,"""
            INSERT dbo.label_code_rule(rule_code,rule_name,object_type,created_by,updated_by) VALUES('ACCEPT','Acceptance rule','PRODUCT_LABEL','acceptance','acceptance');
            DECLARE @rule bigint=SCOPE_IDENTITY();
            INSERT dbo.label_code_rule_version(rule_id,version_no,version_status,spec_json,spec_sha256,created_by,updated_by,activated_by,activated_at)
            OUTPUT inserted.id VALUES(@rule,1,'ACTIVE',@spec,@hash,'acceptance','acceptance','acceptance',GETDATE());
            """,("@spec",validated.Json),("@hash",validated.Sha256)));
        await Call("PUT","code-rule",new PrintRuleRequest(version,null));
        var canvas=JsonSerializer.SerializeToElement(new{version=1,stage=new{width_mm=80,height_mm=50},objects=new object[]{
            new{id="code",type="barcode",barcodeType="qr",data="{{Code}}",x=2,y=2,width=28,height=28},
            new{id="qty",type="text",text="Q={{quantity}}",x=33,y=3,width=43,height=10,fontSize=3}}});
        var saved=await Call("POST","templates",new PrintTemplateRequest("Acceptance 80x50",canvas,80,50,null));
        var published=await Call("POST",$"templates/{saved["id"]}/publish",new PublishPrintTemplateRequest(saved["row_version"]!.ToString()));template=published["id"]!.GetValue<long>();
        await Exec(labelConnection,"""
            INSERT dbo.u8_material_cache(source_account_id,source_database_name,material_code,material_name,specification,unit_code,requires_batch,last_seen_at)
            VALUES('888',@db,'MAT-ACCEPT','Synthetic acceptance','','EA',1,GETDATE());
            """,("@db",U8Db));
    }
    static async Task<(long Label,long Line)> Seed(decimal qty)
    {
        // Synthetic business submission already booked +qty; printing must never book it again.
        var id=Id();var label=Convert.ToInt64(await Scalar(labelConnection,"""
            INSERT dbo.business_operation(operation_no,operation_type,status,operator_id,operator_name_snapshot,client_request_id,
              erp_environment,erp_account_id,erp_year,erp_database_name_snapshot,erp_document_type,erp_document_id,erp_document_no)
            VALUES(@id,'MATERIAL_RETURN','SUCCESS','acceptance','Acceptance',@id,'test','888',2021,@db,'material-outbound',@id,@id);
            DECLARE @op bigint=SCOPE_IDENTITY();
            INSERT dbo.product_label(current_qty,status) VALUES(@qty,'PENDING_PRINT');DECLARE @label bigint=SCOPE_IDENTITY();
            INSERT dbo.business_operation_line(operation_id,line_no,line_status,material_code,batch_no,unit_code,requested_qty,generated_label_id,erp_line_id)
            VALUES(@op,1,'SUCCESS','MAT-ACCEPT','B1','EA',@qty,@label,'1');DECLARE @line bigint=SCOPE_IDENTITY();
            INSERT dbo.inventory_flow(operation_id,operation_line_id,object_type,label_id,object_sequence_no,operation_type,unit_code,qty_before,change_qty,qty_after,
              material_code,batch_no,serial_no,to_warehouse_code,to_location_code)
            VALUES(@op,@line,'PRODUCT_LABEL',@label,1,'MATERIAL_RETURN','EA',0,@qty,@qty,'MAT-ACCEPT','B1','','ACCEPT','P1');
            UPDATE dbo.product_label SET last_flow_id=SCOPE_IDENTITY() WHERE id=@label;
            SELECT @label;
            """,("@id",id),("@qty",qty),("@db",U8Db)));
        return(label,Convert.ToInt64(await Scalar(labelConnection,"SELECT id FROM dbo.business_operation_line WHERE generated_label_id=@label;",("@label",label))));
    }
    static async Task<string> Version(long label)=>Convert.ToHexString((byte[])(await Scalar(labelConnection,"SELECT row_version FROM dbo.product_label WHERE id=@id;",("@id",label)))!);
    static async Task<PrintJob> First((long Label,long Line) target,string? client=null)=>Read<PrintJob>(await Call("POST","jobs/first",new FirstPrintRequest(client??Id(),template,Device,"Mock label printer",[new(target.Label,target.Line,await Version(target.Label))])));
    static async Task<PrintJob> Get(Guid id)=>Read<PrintJob>(await Call("GET",$"jobs/{id}"));
    static async Task<SignedPrintMessage> Submit(PrintJob job,string mode="sent",bool duplicate=false,Guid[]? outputIds=null)
    {
        var ticket=Read<SignedPrintMessage>(await Call("POST",$"jobs/{job.JobId}/dispatch-authorizations",new PrintDispatchRequest(Id(),job.RowVersion,outputIds??job.Outputs.Where(o=>o.FlowId is null).Select(o=>o.OutputId).ToArray())));
        var bundle=await api.GetByteArrayAsync($"/api/v1/print/jobs/{job.JobId}/bundle");
        var command=new{method="POST",path="/v2/print-labels",mode,body=new{authorization=ticket,bundle=Convert.ToBase64String(bundle)}};
        var result=await agent.Send(command);
        Check(result["status"]!.GetValue<int>()==200,"mock-driver-accepted",new{jobId=job.JobId,mode});
        var receipt=JsonSerializer.Deserialize<SignedPrintMessage>(result["body"]!.ToString(),Json)!;
        if(duplicate){var again=await agent.Send(command);Check(again["body"]!.ToString()==result["body"]!.ToString()&&again["count"]!.ToString()==result["count"]!.ToString(),"assistant-repeat-no-resend",new{driverCalls=again["count"]!.GetValue<int>()});}
        return receipt;
    }
    static async Task<PrintJob> Receive(Guid job,SignedPrintMessage receipt)=>Read<PrintJob>(await Call("POST",$"jobs/{job}/receipts",receipt));
    static async Task Scenarios()
    {
        var t=await Seed(100);var request=Id();var originalVersion=await Version(t.Label);
        var job=await First(t,request);
        var code=job.Outputs.Single().Snapshot.Code;
        Check(job.Phase=="READY"&&job.Outputs.Single().Snapshot.Quantity=="100","first-prepared-frozen-quantity",new{code,quantity="100"});
        // Same request returns the same frozen job even though its original rowversion is stale.
        var replay=Read<PrintJob>(await Call("POST","jobs/first",new FirstPrintRequest(request,template,Device,"Mock label printer",[new(t.Label,t.Line,originalVersion)])));
        Check(replay.JobId==job.JobId&&replay.Outputs[0].Snapshot.Code==code,"first-request-replay-same-code",new{jobId=job.JobId});
        await Call("POST","jobs/first",new FirstPrintRequest(request,template,Device,"Mock label printer",[new(t.Label,t.Line,"0000000000000000")]),409);
        var receipt=await Submit(job,duplicate:true);job=await Receive(job.JobId,receipt);
        Check(job.Phase=="COMPLETED","first-completed",new{jobId=job.JobId,phase=job.Phase});
        await Task.WhenAll(Enumerable.Range(0,10).Select(_=>Receive(job.JobId,receipt)));await Audit("first-and-ten-concurrent-receipts",t.Label,code,100,1,0,1,100);

        // Existing legacy consumption changes the current balance, never the original paper quantity.
        await ConsumeLegacy(code,65);
        await Call("POST","labels/resolve",new ResolvePrintLabelRequest(code));
        var sequence=await Scalar(labelConnection,"SELECT MAX(current_value) FROM dbo.label_code_sequence;");
        var reprint=Read<PrintJob>(await Call("POST","jobs/reprint",new ReprintRequest(Id(),t.Label,await Version(t.Label),2,"Acceptance reprint")));
        Check(reprint.Outputs.Count==2&&reprint.Outputs.All(x=>x.Snapshot.Quantity=="100"&&x.Snapshot.Code==code),"reprint-original-paper",new{balance=35,paperQuantity=100,copies=2});
        var rr=await Submit(reprint,duplicate:true);await Receive(reprint.JobId,rr);await Receive(reprint.JobId,rr);
        await Audit("reprint-no-quantity-change",t.Label,code,35,1,2,2,35);
        Check(Equals(sequence,await Scalar(labelConnection,"SELECT MAX(current_value) FROM dbo.label_code_sequence;")),"reprint-no-new-code",new{sequence});

        var lost=await Seed(12.5m);var lostJob=await First(lost);var lostReceipt=await Submit(lostJob);
        var count=(await agent.Send(new{method="COUNT"}))["count"]!.GetValue<int>();
        agent.Dispose();await StartAgent();var authorized=await Get(lostJob.JobId);
        var status=await agent.Send(new{method="GET",path=$"/v2/label-jobs/{lostJob.JobId}/{authorized.AttemptId}?scope="+Uri.EscapeDataString(Encoding.UTF8.GetString(Convert.FromBase64String(authorized.Authorization!.Payload)).ParseObject()["scope"]!.ToString())});
        var recovered=JsonSerializer.Deserialize<SignedPrintMessage>(status["body"]!.ToString(),Json)!;
        Check(status["status"]!.GetValue<int>()==200&&recovered==lostReceipt&&status["count"]!.GetValue<int>()==count,"lost-receipt-restart-recovery-no-resend",new{driverCalls=count});
        await Audit("lost-before-receipt",lost.Label,lostJob.Outputs[0].Snapshot.Code,12.5m,0,0,0,0,true);
        await Receive(lostJob.JobId,recovered);await Receive(lostJob.JobId,recovered);
        await Audit("lost-receipt-recovered-once",lost.Label,lostJob.Outputs[0].Snapshot.Code,12.5m,1,0,1,12.5m);

        var failure=await Seed(7);var failureJob=await First(failure);var fr=await Submit(failureJob);
        await Exec(u8Connection,"ALTER TABLE dbo.TraceBackOnlyCode WITH NOCHECK ADD CONSTRAINT AcceptanceFailPositive CHECK([sum]<0);");
        var pending=await Receive(failureJob.JobId,fr);
        Check(pending.Phase=="TRACEBACK_PENDING","three-table-failure-retains-outbox",new{phase=pending.Phase});
        await Audit("three-table-transaction-rolled-back",failure.Label,failureJob.Outputs[0].Snapshot.Code,7,1,0,0,0,true);
        await Exec(u8Connection,"ALTER TABLE dbo.TraceBackOnlyCode DROP CONSTRAINT AcceptanceFailPositive;");
        await Call("POST",$"jobs/{failureJob.JobId}/retry-sync",new{});await Call("POST",$"jobs/{failureJob.JobId}/retry-sync",new{});
        await Receive(failureJob.JobId,fr);
        await Audit("three-table-recovery-once",failure.Label,failureJob.Outputs[0].Snapshot.Code,7,1,0,1,7);

        var wire=await Seed(9);var wireJob=await First(wire);var wr=await Submit(wireJob);transport.DropNextInitialResponse=true;
        var wirePending=await Receive(wireJob.JobId,wr);Check(wirePending.Phase=="TRACEBACK_PENDING","legacy-commit-response-lost",new{phase=wirePending.Phase});
        await Audit("legacy-committed-response-lost",wire.Label,wireJob.Outputs[0].Snapshot.Code,9,1,0,1,9,true);
        await Call("POST",$"jobs/{wireJob.JobId}/retry-sync",new{});await Receive(wireJob.JobId,wr);
        await Audit("legacy-commit-retry-already-applied",wire.Label,wireJob.Outputs[0].Snapshot.Code,9,1,0,1,9);

        var unknown=await Seed(4);var unknownJob=await First(unknown);var ur=await Submit(unknownJob,"unknown",true);
        var uncertain=await Receive(unknownJob.JobId,ur);Check(uncertain.Phase=="RECONCILIATION_REQUIRED","unknown-keeps-lock",new{phase=uncertain.Phase});
        await Call("POST",$"jobs/{unknownJob.JobId}/cancel",new PrintActionRequest(Id(),uncertain.RowVersion),409);
        await Audit("unknown-no-ledger-entry",unknown.Label,unknownJob.Outputs[0].Snapshot.Code,4,0,0,0,0,true);

        var a=await Seed(3);var b=await Seed(6);
        var batch=Read<PrintJob>(await Call("POST","jobs/first",new FirstPrintRequest(Id(),template,Device,"Mock label printer",[
            new(a.Label,a.Line,await Version(a.Label)),new(b.Label,b.Line,await Version(b.Label))])));
        var ar=await Submit(batch,outputIds:[batch.Outputs.Single(o=>o.Snapshot.LabelId==a.Label).OutputId]);
        var afterA=await Receive(batch.JobId,ar);
        Check(afterA.Phase=="READY"&&afterA.Outputs.Single(o=>o.Snapshot.LabelId==b.Label).Phase=="READY","batch-a-completed-b-remains-ready",new{phase=afterA.Phase});
        await Audit("batch-a-unlocked",a.Label,batch.Outputs.Single(o=>o.Snapshot.LabelId==a.Label).Snapshot.Code,3,1,0,1,3);
        await Audit("batch-b-retains-own-lock",b.Label,batch.Outputs.Single(o=>o.Snapshot.LabelId==b.Label).Snapshot.Code,6,0,0,0,0,true);
        var br=await Submit(afterA);await Receive(batch.JobId,br);await Receive(batch.JobId,ar);
        await Audit("batch-b-completed-once",b.Label,batch.Outputs.Single(o=>o.Snapshot.LabelId==b.Label).Snapshot.Code,6,1,0,1,6);

        await Legacy();
        var summary=await api.GetFromJsonAsync<JsonObject>("/api/v1/print/source-summary",Json);
        var rows=summary!["data"]!.AsArray();
        Check(rows.OfType<JsonObject>().Where(x=>x["requiredQty"]!.GetValue<decimal>()==100).All(x=>x["firstRegisteredQty"]!.GetValue<decimal>()==100&&x["remainingQty"]!.GetValue<decimal>()==0),
            "source-summary-reprint-not-first",new{required=100,firstRegistered=100,remaining=0});
    }
    static async Task Legacy()
    {
        const string legacyCode="888|MAT-ACCEPT|B1|50|87654321";
        var snapshot=new PrintSnapshot(999999,legacyCode,"87654321","50","EA","MAT-ACCEPT","Synthetic acceptance","B1","ACCEPT","P1",
            null,null,null,1,null,null,"legacy-source","LEGACY","LEGACY-001","1",null,DateTimeOffset.Now.ToString("O"),new JsonObject());
        var registration=PrintRules.Registration("synthetic-legacy-"+RunId,new PrintOutput(Guid.NewGuid(),1,"FIRST","READY",null,snapshot),"Acceptance");
        var registered=await gateway.Send(new{method="POST",path="/api/v1/traceback/labels/initial-registrations",body=JsonSerializer.Serialize(registration,Json)});
        Check(registered["status"]!.GetValue<int>()==200,"synthetic-history-legacy-seeded",new{initial=50});
        await ConsumeLegacy(legacyCode,32);
        var resolved=await Call("POST","labels/resolve",new ResolvePrintLabelRequest(legacyCode));var label=resolved["label"]!["labelId"]!.GetValue<long>();
        var request=new LegacyPrintBaselineRequest(Id(),legacyCode,template,Device,"Mock label printer",await Version(label));
        await Call("POST","labels/legacy-baseline",request);await Call("POST","labels/legacy-baseline",request);
        var job=Read<PrintJob>(await Call("POST","jobs/reprint",new ReprintRequest(Id(),label,await Version(label),1,"Synthetic history reprint")));
        Check(job.Outputs.Single().Snapshot.Quantity=="50","history-original-quantity-not-current-balance",new{originalQuantity=50,balance=18});
        var receipt=await Submit(job,duplicate:true);await Receive(job.JobId,receipt);await Receive(job.JobId,receipt);
        await Audit("history-reprint-no-fake-first",label,legacyCode,18,0,1,2,18,newTarget:false);
        Check(Convert.ToInt32(await Scalar(labelConnection,"SELECT COUNT(*) FROM dbo.business_operation_event WHERE event_type='LEGACY_FIRST_PRINT_BASELINE';"))==1,
            "history-baseline-recorded-once",new{baselines=1});
    }
    static JsonObject ParseObject(this string value)=>JsonNode.Parse(value)!.AsObject();
    static async Task ConsumeLegacy(string code,decimal qty)
    {
        // Synthetic legacy write inside the shadow only, to represent a completed earlier issue.
        await Exec(u8Connection,"""
            DECLARE @order varchar(100)=CONVERT(varchar(36),NEWID()),@goods varchar(100)=CONVERT(varchar(36),NEWID());
            INSERT dbo.TraceBackOrder(json,pdaOrderUuid,orderType,time) VALUES('{}',@order,'Acceptance consumption',GETDATE());
            INSERT dbo.TraceBackGoods(json,pdaGoodsUuid,time,uploadOrderNumber,accountCode,ErpDetailId)
            SELECT TOP(1) json,@goods,GETDATE(),@order,'888','1' FROM dbo.TraceBackGoods g
              JOIN dbo.TraceBackOnlyCode f ON f.pdaGoodsUuid=g.pdaGoodsUuid WHERE f.traceBackCode=@code ORDER BY f.id;
            INSERT dbo.TraceBackOnlyCode(traceBackCode,traceBackType,pdaOrderUuid,pdaGoodsUuid,sn,sum,time,[User])
            SELECT TOP(1) traceBackCode,'Acceptance consumption',@order,@goods,sn,-@qty,GETDATE(),'acceptance'
            FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code ORDER BY id;
            """,("@code",code),("@qty",qty));
    }
    static async Task Audit(string name,long label,string code,decimal expectedQty,int prints,int reprints,int legacyRows,decimal legacyBalance,bool locked=false,bool newTarget=true)
    {
        var actualQty=Convert.ToDecimal(await Scalar(labelConnection,"SELECT current_qty FROM dbo.product_label WHERE id=@id;",("@id",label)));
        var p=Convert.ToInt32(await Scalar(labelConnection,"SELECT COUNT(*) FROM dbo.inventory_flow WHERE label_id=@id AND operation_type='PRINT';",("@id",label)));
        var r=Convert.ToInt32(await Scalar(labelConnection,"SELECT COUNT(*) FROM dbo.inventory_flow WHERE label_id=@id AND operation_type='REPRINT';",("@id",label)));
        var delta=Convert.ToDecimal(await Scalar(labelConnection,"SELECT ISNULL(SUM(change_qty),0) FROM dbo.inventory_flow WHERE label_id=@id AND operation_type IN('PRINT','REPRINT');",("@id",label)));
        var old=Convert.ToInt32(await Scalar(u8Connection,"SELECT COUNT(*) FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code;",("@code",code)));
        var oldQty=Convert.ToDecimal(await Scalar(u8Connection,"SELECT ISNULL(SUM(CONVERT(decimal(18,6),sum)),0) FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code;",("@code",code)));
        var isLocked=Convert.ToInt32(await Scalar(labelConnection,"SELECT CASE WHEN locked_operation_id IS NULL THEN 0 ELSE 1 END FROM dbo.product_label WHERE id=@id;",("@id",label)))==1;
        var goods=Convert.ToInt32(await Scalar(u8Connection,"SELECT COUNT(*) FROM dbo.TraceBackGoods WHERE pdaGoodsUuid IN(SELECT pdaGoodsUuid FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code);",("@code",code)));
        var orders=Convert.ToInt32(await Scalar(u8Connection,"SELECT COUNT(*) FROM dbo.TraceBackOrder WHERE pdaOrderUuid IN(SELECT pdaOrderUuid FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code);",("@code",code)));
        var tasks=Convert.ToInt32(await Scalar(labelConnection,"SELECT COUNT(*) FROM dbo.traceback_sync_task WHERE label_id=@id AND event_phase='PRINT_TARGET';",("@id",label)));
        var orphanFree=true;
        if(newTarget)
        {
            var eventKey=PrintRules.Key("print-initial",$"acceptance-{RunId}|test|888|2021|{U8Db}",label.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var initialOrders=Convert.ToInt32(await Scalar(u8Connection,"SELECT COUNT(*) FROM dbo.TraceBackOrder WHERE pdaOrderUuid=@uuid;",("@uuid",PrintRules.StableGuid(eventKey+":order").ToString())));
            var initialGoods=Convert.ToInt32(await Scalar(u8Connection,"SELECT COUNT(*) FROM dbo.TraceBackGoods WHERE pdaGoodsUuid=@uuid;",("@uuid",PrintRules.StableGuid(eventKey+":goods").ToString())));
            orphanFree=initialOrders==(legacyRows>0?1:0)&&initialGoods==(legacyRows>0?1:0);
            var sourceFlows=Convert.ToInt32(await Scalar(labelConnection,"SELECT COUNT(*) FROM dbo.inventory_flow WHERE label_id=@id AND operation_type='MATERIAL_RETURN';",("@id",label)));
            orphanFree&=sourceFlows==1;
        }
        Check(actualQty==expectedQty&&p==prints&&r==reprints&&delta==0&&old==legacyRows&&goods==legacyRows&&orders==legacyRows&&oldQty==legacyBalance&&isLocked==locked,
            name,new{label,code,currentQuantity=actualQty,printFlows=p,reprintFlows=r,printQuantityChange=delta,traceBackOrder=orders,traceBackGoods=goods,traceBackOnlyCode=old,traceBackBalance=oldQty,isLocked});
        Check(orphanFree&&tasks==(prints>0?1:0),name+"-source-outbox-and-no-orphans",new{sourceUnchanged=orphanFree,printTargetTasks=tasks});
    }
    static void Check(bool condition,string name,object details)
    {
        Evidence.Add(new{check=name,passed=condition,details});Console.WriteLine((condition?"PASS: ":"FAIL: ")+name+" "+JsonSerializer.Serialize(details,Json));
        if(!condition)throw new Exception("Assertion failed: "+name);
    }
    sealed class GatewayTransport(Rpc rpc):HttpMessageHandler
    {
        public bool DropNextInitialResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.PathAndQuery;
            if(!path.StartsWith("/api/v1/traceback/labels",StringComparison.Ordinal))throw new Exception("Non-TraceBack route blocked");
            var result=await rpc.Send(new{method=request.Method.Method,path,body=request.Content is null?null:await request.Content.ReadAsStringAsync(ct)});
            if(result["status"]!.GetValue<int>()>=400)Console.WriteLine("ISOLATED_GATEWAY_RESULT: "+result["status"]+" "+Safe(result["body"]!.ToString()));
            if(path.EndsWith("/initial-registrations")&&DropNextInitialResponse){DropNextInitialResponse=false;throw new HttpRequestException("Injected lost gateway response after SQL commit");}
            return new HttpResponseMessage((HttpStatusCode)result["status"]!.GetValue<int>()){Content=new StringContent(result["body"]!.ToString(),Encoding.UTF8,"application/json")};
        }
    }
    sealed class Rpc:IDisposable
    {
        readonly Process process;readonly SemaphoreSlim gate=new(1);readonly Task<string> errors;
        Rpc(Process p){process=p;errors=p.StandardError.ReadToEndAsync();}
        public static async Task<Rpc> Start(ProcessStartInfo info)
        {
            var rpc=new Rpc(Process.Start(info)!);var ready=await rpc.process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if(ready!="READY")throw new Exception("Isolated child failed to initialize: "+(ready??"EOF")+" "+(rpc.process.HasExited?await rpc.errors:""));return rpc;
        }
        public async Task<JsonObject> Send(object request)
        {
            await gate.WaitAsync();try{await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request,Json));await process.StandardInput.FlushAsync();
                var line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(90));return JsonNode.Parse(line??throw new Exception("Isolated child exited"))!.AsObject();}finally{gate.Release();}
        }
        public void Dispose(){if(!process.HasExited){process.Kill(true);process.WaitForExit();}}
    }
}
