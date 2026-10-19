using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabelSystem.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Explicit existing-development SQL runner. No migration, CREATE DATABASE, U8 COM or real driver.
return await DevelopmentAcceptance.Run();

static class DevelopmentAcceptance
{
    static readonly JsonSerializerOptions Json=PrintRules.Json;
    static readonly string Root=Path.GetFullPath(Environment.GetEnvironmentVariable("PRINT_DEV_REPO")??".");
    static readonly string RunId=Environment.GetEnvironmentVariable("PRINT_DEV_RUN")??"";
    static readonly string Batch="ACPT"+RunId;
    static readonly string Prefix="DEVPRINT-"+RunId+"-";
    static readonly string Namespace="dev-sql-acceptance-"+RunId;
    static readonly string Work=Path.Combine(Root,".tools","print-development-acceptance",RunId);
    static readonly string LabelConnection=Environment.GetEnvironmentVariable("PRINT_DEV_LABEL")??"";
    static readonly string U8Connection=Environment.GetEnvironmentVariable("PRINT_DEV_U8")??"";
    static readonly string Trigger="PrintAcceptance_"+RunId;
    static readonly Guid Device=Guid.NewGuid();
    static readonly HashSet<long> OwnedLabels=[];
    static readonly HashSet<string> OwnedOrders=[],OwnedGoods=[];
    static readonly Dictionary<string,(long Max,string Hash)> Baselines=[];
    static readonly string[] LabelTables=["product_label","inventory_flow","business_operation","business_operation_line","business_operation_event","traceback_sync_task","label_code_registry","label_print_template","label_print_template_version","label_print_asset","label_print_device"];
    static readonly string[] OldTables=["TraceBackOrder","TraceBackGoods","TraceBackOnlyCode"];
    static WebApplication? app;
    static Rpc? gateway;
    static Process? renderer;
    static GatewayTransport? transport;
    static SessionUser user=null!;
    static long ruleId,ruleVersion;
    static Guid? faultGoods;
    static bool finalized;
    static JsonObject? originalBinding;
    static string material="",materialName="",unit="",warehouse="",location="";
    static readonly CancellationToken Ct=CancellationToken.None;

    public static async Task<int> Run()
    {
        Console.OutputEncoding=new UTF8Encoding(false);Console.InputEncoding=new UTF8Encoding(false);
        try
        {
            if(!Regex.IsMatch(RunId,"^[a-f0-9]{12}$")||Environment.GetEnvironmentVariable("PRINT_DEV_EXISTING_888")!="EXPLICIT_MOCK_ONLY")throw new Exception("Explicit development opt-in required");
            Directory.CreateDirectory(Work);
            await Boundary(LabelConnection,"LabelSystem_Dev");await Boundary(U8Connection,"UFDATA_888_2021");
            foreach(var table in LabelTables)await Baseline(LabelConnection,table);
            foreach(var table in OldTables)await Baseline(U8Connection,table);
            originalBinding=await One(LabelConnection,"SELECT * FROM dbo.label_print_rule_binding WHERE singleton_id=1;");
            // Existing installation only; no DDL to install/upgrade application schema.
            if(Convert.ToInt32(await Scalar(LabelConnection,"SELECT COUNT(*) FROM dbo.schema_migration WHERE migration_id='017';"))!=1)throw new Exception("017 must already exist");
            await Start();
            Console.WriteLine("READY");
            string? line;
            while((line=await Console.In.ReadLineAsync()) is not null)
            {
                try
                {
                    var request=JsonNode.Parse(line)!.AsObject();
                    var result=await Control(request);
                    Console.WriteLine(JsonSerializer.Serialize(new{ok=true,data=result},Json));
                }
                catch(Exception ex){Console.WriteLine(JsonSerializer.Serialize(new{ok=false,error=ex.GetType().Name,sqlNumber=ex is SqlException se?se.Number:0,location=ex.StackTrace?.Split('\n').LastOrDefault(x=>x.Contains("DevelopmentAcceptance"))},Json));}
            }
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine("Development acceptance failed: "+ex.GetType().Name+" SQL="+(ex is SqlException se?se.Number:0)+" "+ex.StackTrace);return 1;}
        finally
        {
            try {if(!finalized && ruleVersion>0)await FinalizeRun();}catch(Exception ex){Console.Error.WriteLine("Cleanup requires inspection: "+ex.GetType().Name);}
            if(app is not null)await app.DisposeAsync();gateway?.Dispose();
            if(renderer is not null&&!renderer.HasExited){renderer.Kill(true);await renderer.WaitForExitAsync();}
        }
    }
    static async Task Boundary(string connection,string expected)
    {
        if(new SqlConnectionStringBuilder(connection).InitialCatalog!=expected)throw new Exception("Exact development database required");
        if((string?)await Scalar(connection,"SELECT DB_NAME();")!=expected)throw new Exception("Connected database mismatch");
    }
    static async Task<SqlConnection> Open(string connection)
    {
        var name=new SqlConnectionStringBuilder(connection).InitialCatalog;
        if(name is not ("LabelSystem_Dev" or "UFDATA_888_2021"))throw new Exception("Database outside allowed development scope");
        var c=new SqlConnection(connection);await c.OpenAsync();return c;
    }
    static SqlCommand Command(SqlConnection c,string sql,params(string,object?)[] args)
    {
        var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.CommandTimeout=90;
        foreach(var(k,v)in args)cmd.Parameters.AddWithValue(k,v??DBNull.Value);return cmd;
    }
    static async Task<object?> Scalar(string connection,string sql,params(string,object?)[] args)
    {await using var c=await Open(connection);await using var cmd=Command(c,sql,args);return await cmd.ExecuteScalarAsync();}
    static async Task Exec(string connection,string sql,params(string,object?)[] args)=>_=await Scalar(connection,sql,args);
    static async Task<JsonObject?> One(string connection,string sql,params(string,object?)[] args)
    {
        await using var c=await Open(connection);await using var cmd=Command(c,sql,args);await using var r=await cmd.ExecuteReaderAsync();
        if(!await r.ReadAsync())return null;var row=new JsonObject();
        for(var i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.GetValue(i) switch{DBNull=>null,byte[] b=>JsonValue.Create(Convert.ToHexString(b)),Guid g=>JsonValue.Create(g.ToString()),var v=>JsonSerializer.SerializeToNode(v,Json)};
        return row;
    }
    static async Task<string> Fingerprint(string connection,string table,long max)
    {
        // Old Order/Goods have no identity column. Exclude only this run's linked UUIDs.
        var ownedUuids=table=="TraceBackOrder"?OwnedOrders:OwnedGoods;
        var uuidParameters=ownedUuids.Select((v,i)=>("@uuid"+i,(object?)v)).ToArray();
        var uuidFilter=uuidParameters.Length==0?"1=1":(table=="TraceBackOrder"?"pdaOrderUuid":"pdaGoodsUuid")+" NOT IN("+string.Join(',',uuidParameters.Select(p=>p.Item1))+")";
        var filter=table switch{
            "label_print_device"=>"id<>@device",
            "TraceBackOrder" or "TraceBackGoods"=>uuidFilter,
            _=>"id<=@max"};
        string row;
        if(table is "TraceBackOrder" or "TraceBackGoods"){
            var columns=new List<string>();await using var c=await Open(connection);await using var cmd=Command(c,"SELECT name FROM sys.columns WHERE object_id=OBJECT_ID(@table) AND name<>'json' ORDER BY column_id;",("@table","dbo."+table));await using var reader=await cmd.ExecuteReaderAsync();
            while(await reader.ReadAsync())columns.Add("t.["+reader.GetString(0).Replace("]","]]")+"]");
            // Hash large legacy JSON raw, avoiding re-escaping the entire legacy table into one LOB.
            row="(CASE WHEN t.[json] IS NULL THEN N'N' ELSE N'V'+CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),CONVERT(nvarchar(max),t.[json]))),2) END)+(SELECT "+string.Join(',',columns)+" FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES)";
        }else row="(SELECT t.* FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES)";
        var value=await Scalar(connection,$"SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL((SELECT h FROM(SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),{row})),2) AS h FROM dbo.[{table}] t WHERE {filter}) q ORDER BY h FOR JSON PATH),N'[]'))),2);",new[]{("@max",(object?)max),("@device",Device)}.Concat(uuidParameters).ToArray());
        return (string)value!;
    }
    static async Task Baseline(string connection,string table)
    {
        await File.WriteAllTextAsync(Path.Combine(Work,"stage.txt"),"baseline:"+table);
        var max=table is "label_print_device" or "TraceBackOrder" or "TraceBackGoods"?0:Convert.ToInt64(await Scalar(connection,$"SELECT ISNULL(MAX(id),0) FROM dbo.[{table}];"));
        Baselines[table]=(max,await Fingerprint(connection,table,max));
        await File.WriteAllTextAsync(Path.Combine(Work,"baseline.json"),JsonSerializer.Serialize(Baselines.ToDictionary(k=>k.Key,v=>new{maxId=v.Value.Max,sha256=v.Value.Hash}),Json));
    }
    static ProcessStartInfo Child(string path,params string[] args)
    {
        var p=new ProcessStartInfo(path){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WorkingDirectory=Root};
        foreach(var a in args)p.ArgumentList.Add(a);
        p.Environment.Remove("PRINT_DEV_LABEL");p.Environment.Remove("PRINT_DEV_U8");return p;
    }
    static async Task Start()
    {
        var key=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pi=Child(Path.Combine(Root,"u8-gateway","tools","PrintDevelopmentAcceptanceBridge","bin","Debug","net48","PrintDevelopmentAcceptanceBridge.exe"));
        pi.Environment["PRINT_ACCEPTANCE_U8"]=U8Connection;pi.Environment["PRINT_ACCEPTANCE_KEY"]=key;
        pi.Environment["PRINT_ACCEPTANCE_WORK"]=Path.Combine(Work,"gateway");pi.Environment["PRINT_DEV_RUN"]=RunId;pi.Environment["PRINT_DEV_EXISTING_888"]="EXPLICIT_MOCK_ONLY";
        gateway=await Rpc.Start(pi);
        using var socket=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);socket.Start();var renderPort=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
        var renderKey=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var ri=Child(Path.Combine(Root,".tools","print-renderer","Scripts","python.exe"),"-m","label_renderer");ri.WorkingDirectory=Path.Combine(Root,"label-print","renderer");
        ri.Environment["LABEL_RENDERER_API_KEY"]=renderKey;ri.Environment["LABEL_RENDERER_PORT"]=renderPort.ToString();renderer=Process.Start(ri)!;
        _=renderer.StandardError.ReadToEndAsync();_=renderer.StandardOutput.ReadToEndAsync();
        using(var http=new HttpClient())
        for(var n=0;n<100;n++){
            try{using var req=new HttpRequestMessage(HttpMethod.Post,$"http://127.0.0.1:{renderPort}/fonts"){Content=new StringContent("{}",Encoding.UTF8,"application/json")};req.Headers.Add("X-Renderer-Key",renderKey);using var res=await http.SendAsync(req);if(res.IsSuccessStatusCode)break;}
            catch(HttpRequestException){}if(n==99)throw new Exception("Renderer unavailable");await Task.Delay(100);
        }
        using var serverKey=ECDsa.Create(ECCurve.NamedCurves.nistP256);var keyFile=Path.Combine(Work,"server.pem");await File.WriteAllTextAsync(keyFile,serverKey.ExportPkcs8PrivateKeyPem());
        using var deviceKey=ECDsa.Create(ECCurve.NamedCurves.nistP256);var deviceRoot=Path.Combine(Work,"agent");Directory.CreateDirectory(deviceRoot);await File.WriteAllBytesAsync(Path.Combine(deviceRoot,"device.der"),deviceKey.ExportPkcs8PrivateKey());
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{
            ["ConnectionStrings:LabelSystem"]=LabelConnection,["LabelSystem:Environment"]="development",["LabelSystem:ErpAccountId"]="888",["LabelSystem:ErpYear"]="2021",["LabelSystem:ErpDatabaseName"]="UFDATA_888_2021",["LabelSystem:U8WritesEnabled"]="true",
            ["Printing:Enabled"]="true",["Printing:AllowNewDispatch"]="true",["Printing:RecoveryEnabled"]="false",["Printing:Namespace"]=Namespace,["Printing:RootPath"]=Path.Combine(Work,"artifacts"),["Printing:SigningKeyPath"]=keyFile,["Printing:RendererUrl"]=$"http://127.0.0.1:{renderPort}",["Printing:RendererApiKey"]=renderKey,
            ["U8Gateway:BaseUrl"]="http://development-traceback.invalid",["U8Gateway:ApiKey"]=key});
        builder.Services.Configure<LabelSystemOptions>(builder.Configuration.GetSection("LabelSystem"));builder.Services.Configure<U8GatewayOptions>(builder.Configuration.GetSection("U8Gateway"));
        builder.Services.AddSingleton<SqlConnectionFactory>();builder.Services.AddScoped<LabelCodeRuleEngine>();builder.Services.AddScoped<UserRepository>();builder.Services.AddScoped<TraceBackMirrorRepository>();builder.Services.AddScoped<U8GatewayClient>();builder.Services.AddSingleton<DevelopmentSessionStore>();
        transport=new GatewayTransport(gateway);builder.Services.AddHttpClient(nameof(U8GatewayClient)).ConfigurePrimaryHttpMessageHandler(()=>transport);builder.Services.AddPrinting(builder.Configuration);
        app=builder.Build();app.UseMiddleware<RequestIdMiddleware>();app.UseMiddleware<ApiExceptionMiddleware>();app.UseMiddleware<SessionAuthenticationMiddleware>();app.MapPrinting();
        app.MapPost("/api/v1/auth/login",async(HttpContext h,LoginRequest r,DevelopmentSessionStore sessions,UserRepository users)=>{
            var u=r.Username=="admin"&&r.Password=="admin"?await users.FindSessionUserByLoginAsync(r.Username,false,Ct):await users.AuthenticateLocalUserAsync(r.Username,r.Password,Ct);
            return u is null?ApiResults.Error(h,401,"LOGIN_FAILED","账号或密码错误。"):ApiResults.Ok(h,sessions.Create(u));});
        app.MapGet("/api/v1/auth/me",(HttpContext h)=>ApiResults.Ok(h,RequestContext.User(h)));
        app.MapGet("/api/v1/functions",async(HttpContext h,UserRepository users)=>ApiResults.Ok(h,await users.ListFunctionsForUserAsync(RequestContext.User(h).Id,Ct)));
        await app.StartAsync();
        using var scope=app.Services.CreateScope();user=(await scope.ServiceProvider.GetRequiredService<UserRepository>().FindSessionUserByLoginAsync("admin",false,Ct))??throw new Exception("Existing development admin missing");
        var m=await One(LabelConnection,"""
          SELECT TOP(1) m.material_code,m.material_name,m.unit_code FROM dbo.u8_material_cache m
          JOIN UFDATA_888_2021.dbo.Inventory i ON i.cInvCode COLLATE DATABASE_DEFAULT=m.material_code AND i.cComunitCode COLLATE DATABASE_DEFAULT=m.unit_code
          WHERE m.source_account_id='888' AND m.source_database_name='UFDATA_888_2021'
          AND i.dEDate IS NULL AND LEN(m.material_code) BETWEEN 1 AND 25 AND LEN(m.unit_code)>0 AND LEN(m.material_name)>0 ORDER BY m.material_code;
          """)??throw new Exception("Matching 888 material cache required");
        material=m["material_code"]!.ToString();materialName=m["material_name"]!.ToString();unit=m["unit_code"]!.ToString();
        var w=await One(U8Connection,"""
          SELECT TOP(1) w.cWhCode,CASE WHEN w.bWhPos=1 THEN p.cPosCode ELSE '' END AS location
          FROM dbo.Warehouse w LEFT JOIN dbo.Position p ON p.cWhCode=w.cWhCode
          WHERE w.dWhEndDate IS NULL AND (w.bWhPos=0 OR p.cPosCode IS NOT NULL) ORDER BY w.bWhPos,w.cWhCode,p.cPosCode;
          """)??throw new Exception("Valid 888 warehouse required");
        warehouse=w["cWhCode"]!.ToString().Trim();location=w["location"]!.ToString().Trim();
        var spec=new LabelCodeRuleSpec{Symbologies=["QR"],Segments=[new(){Kind="FIELD",Field="ERP_ACCOUNT_ID",Length=3,ValidateCurrentAccount=true},new(){Kind="LITERAL",Value="|"},new(){Kind="FIELD",Field="MATERIAL_CODE",MaxLength=25},new(){Kind="LITERAL",Value="|"},new(){Kind="FIELD",Field="BATCH_NO",MaxLength=20},new(){Kind="LITERAL",Value="|"},new(){Kind="FIELD",Field="ISSUED_QUANTITY",MaxLength=20,DecimalScale=6},new(){Kind="LITERAL",Value="|"},new(){Kind="FIELD",Field="LABEL_UNIQUE_VALUE",Length=10}]};
        var valid=new LabelCodeRuleEngine().Validate(JsonSerializer.SerializeToElement(spec,Json));
        var ids=await One(LabelConnection,"""
          INSERT dbo.label_code_rule(rule_code,rule_name,object_type,created_by,updated_by) VALUES(@code,@name,'PRODUCT_LABEL',@user,@user);DECLARE @rule bigint=SCOPE_IDENTITY();
          INSERT dbo.label_code_rule_version(rule_id,version_no,version_status,spec_json,spec_sha256,created_by,updated_by,activated_by,activated_at)
          VALUES(@rule,1,'ACTIVE',@spec,@hash,@user,@user,@user,GETDATE());SELECT @rule AS ruleId,CONVERT(bigint,SCOPE_IDENTITY()) AS versionId;
          """,("@code","ACPT_"+RunId),("@name",Prefix+"模拟验收规则"),("@user",user.Id),("@spec",valid.Json),("@hash",valid.Sha256));
        ruleId=ids!["ruleId"]!.GetValue<long>();ruleVersion=ids["versionId"]!.GetValue<long>();
        var partition="PRINT:"+Hashing.Sha256(Namespace+"|development|888");
        await Exec(LabelConnection,"INSERT dbo.label_code_sequence(rule_version_id,partition_key,current_value) VALUES(@v,@p,@n);",("@v",ruleVersion),("@p",partition),("@n",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var publicState=new{runId=RunId,apiUrl=app.Urls.Single(),batch=Batch,prefix=Prefix,deviceId=Device,devicePublicKey=Convert.ToBase64String(deviceKey.ExportSubjectPublicKeyInfo()),deviceKeyHash=Convert.ToHexString(SHA256.HashData(deviceKey.ExportSubjectPublicKeyInfo())),serverPublicKey=Convert.ToBase64String(serverKey.ExportSubjectPublicKeyInfo()),ruleVersionId=ruleVersion,userId=user.Id,material,unit,warehouse,location};
        await File.WriteAllTextAsync(Path.Combine(Work,"public-state.json"),JsonSerializer.Serialize(publicState,Json));
    }
    static async Task<object?> Control(JsonObject q)
    {
        var method=q["method"]!.ToString();
        if(method=="state")return JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Work,"public-state.json")));
        if(method=="seed"){
            var scenario=q["scenario"]!.ToString();if(!Regex.IsMatch(scenario,"^[a-z-]{1,30}$"))throw new Exception("Scenario name rejected");
            var qty=q["quantity"]!.GetValue<decimal>();if(qty<=0||qty>100)throw new Exception("Synthetic quantity rejected");
            var id=Prefix+scenario;
            var row=await One(LabelConnection,"""
              INSERT dbo.business_operation(operation_no,operation_type,status,operator_id,operator_name_snapshot,client_request_id,erp_environment,erp_account_id,erp_year,erp_database_name_snapshot,erp_document_type,erp_document_id,erp_document_no)
              VALUES(@id,'MATERIAL_RETURN','SUCCESS',@user,@name,@id,'development','888',2021,'UFDATA_888_2021','material-outbound',@id,@id);DECLARE @op bigint=SCOPE_IDENTITY();
              INSERT dbo.product_label(current_qty,status) VALUES(@qty,'PENDING_PRINT');DECLARE @label bigint=SCOPE_IDENTITY();
              INSERT dbo.business_operation_line(operation_id,line_no,line_status,material_code,batch_no,unit_code,requested_qty,generated_label_id,erp_line_id)
              VALUES(@op,1,'SUCCESS',@mat,@batch,@unit,@qty,@label,'1');DECLARE @line bigint=SCOPE_IDENTITY();
              INSERT dbo.inventory_flow(operation_id,operation_line_id,object_type,label_id,object_sequence_no,operation_type,unit_code,qty_before,change_qty,qty_after,material_code,batch_no,serial_no,to_warehouse_code,to_location_code)
              VALUES(@op,@line,'PRODUCT_LABEL',@label,1,'MATERIAL_RETURN',@unit,0,@qty,@qty,@mat,@batch,'',@wh,@loc);DECLARE @flow bigint=SCOPE_IDENTITY();
              UPDATE dbo.product_label SET last_flow_id=@flow WHERE id=@label;SELECT @label AS labelId,@line AS originLineId,@flow AS sourceFlowId,row_version AS rowVersion FROM dbo.product_label WHERE id=@label;
              """,("@id",id),("@user",user.Id),("@name",Prefix+"模拟验收"),("@qty",qty),("@mat",material),("@batch",Batch),("@unit",unit),("@wh",warehouse),("@loc",location));
            OwnedLabels.Add(row!["labelId"]!.GetValue<long>());await File.WriteAllTextAsync(Path.Combine(Work,"owned-labels.json"),JsonSerializer.Serialize(OwnedLabels,Json));return row;
        }
        if(method=="audit")return await Audit(q["labelId"]!.GetValue<long>());
        if(method=="fault"){
            if(q["enabled"]!.GetValue<bool>()){
                using var scope=app!.Services.CreateScope();var repo=scope.ServiceProvider.GetRequiredService<PrintRepository>();
                var job=await repo.Get(Guid.Parse(q["jobId"]!.ToString()),user,Ct);if(job.Outputs.Count!=1||!OwnedLabels.Contains(job.Outputs[0].Snapshot.LabelId))throw new Exception("Fault target not owned");
                faultGoods=Guid.Parse(PrintRules.Registration(repo.Scope,job.Outputs[0],user.Name).GoodsUuid);
                await File.WriteAllTextAsync(Path.Combine(Work,"fault-ownership.json"),JsonSerializer.Serialize(new{database="UFDATA_888_2021",trigger=Trigger,goodsUuid=faultGoods},Json));
                await Exec(U8Connection,$"CREATE TRIGGER [dbo].[{Trigger}] ON [dbo].[TraceBackOnlyCode] AFTER INSERT AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM inserted WHERE pdaGoodsUuid='{faultGoods:D}') THROW 51017,N'Owned simulated acceptance failure',1; END;");
            }else await RemoveFault();return new{enabled=q["enabled"]!.GetValue<bool>(),scopedToOwnedEvent=true};
        }
        if(method=="drop-next-initial-response"){transport!.DropNextInitialResponse=true;return new{enabled=true};}
        if(method=="consume"){
            var label=q["labelId"]!.GetValue<long>();if(!OwnedLabels.Contains(label))throw new Exception("Only owned synthetic labels");
            var code=(string)(await Scalar(LabelConnection,"SELECT label_code FROM dbo.product_label WHERE id=@id;",("@id",label)))!;var qty=q["quantity"]!.GetValue<decimal>();
            if(qty!=65)throw new Exception("Only planned synthetic consumption allowed");
            var consumed=await One(U8Connection,"""
              DECLARE @order varchar(100)=CONVERT(varchar(36),NEWID()),@goods varchar(100)=CONVERT(varchar(36),NEWID());
              INSERT dbo.TraceBackOrder(json,pdaOrderUuid,orderType,time) VALUES('{}',@order,'Acceptance consumption',GETDATE());
              INSERT dbo.TraceBackGoods(json,pdaGoodsUuid,time,uploadOrderNumber,accountCode,ErpDetailId)
              SELECT TOP(1) json,@goods,GETDATE(),@order,'888','1' FROM dbo.TraceBackGoods g JOIN dbo.TraceBackOnlyCode f ON f.pdaGoodsUuid=g.pdaGoodsUuid WHERE f.traceBackCode=@code ORDER BY f.id;
              INSERT dbo.TraceBackOnlyCode(traceBackCode,traceBackType,pdaOrderUuid,pdaGoodsUuid,sn,sum,time,[User])
              SELECT TOP(1) traceBackCode,'Acceptance consumption',@order,@goods,sn,-@qty,GETDATE(),@user FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code ORDER BY id;
              SELECT @order AS orderUuid,@goods AS goodsUuid;
              """,("@code",code),("@qty",qty),("@user",Prefix+"mock"));
            OwnedOrders.Add(consumed!["orderUuid"]!.ToString());OwnedGoods.Add(consumed["goodsUuid"]!.ToString());return new{consumed=qty};
        }
        if(method=="finalize")return await FinalizeRun();
        throw new Exception("Control method rejected");
    }
    static async Task<object> Audit(long label)
    {
        if(!OwnedLabels.Contains(label))throw new Exception("Audit label not owned");
        var state=await One(LabelConnection,"""
          SELECT p.id AS labelId,p.label_code AS code,p.current_qty AS quantity,p.status,p.row_version AS rowVersion,p.locked_operation_id AS lockOwner,
          (SELECT COUNT(*) FROM dbo.inventory_flow WHERE label_id=p.id AND operation_type='MATERIAL_RETURN') AS sourceFlows,
          (SELECT COUNT(*) FROM dbo.inventory_flow WHERE label_id=p.id AND operation_type='PRINT') AS printFlows,
          (SELECT COUNT(*) FROM dbo.inventory_flow WHERE label_id=p.id AND operation_type='REPRINT') AS reprintFlows,
          (SELECT ISNULL(SUM(change_qty),0) FROM dbo.inventory_flow WHERE label_id=p.id AND operation_type IN('PRINT','REPRINT')) AS printDelta,
          (SELECT COUNT(*) FROM dbo.label_code_registry WHERE product_label_id=p.id) AS registeredCodes,
          (SELECT COUNT(*) FROM dbo.traceback_sync_task WHERE label_id=p.id AND event_phase='PRINT_TARGET') AS targetTasks,
          (SELECT MAX(status) FROM dbo.traceback_sync_task WHERE label_id=p.id AND event_phase='PRINT_TARGET') AS targetStatus,
          (SELECT MAX(event_key) FROM dbo.traceback_sync_task WHERE label_id=p.id AND event_phase='PRINT_TARGET') AS targetEvent,
          (SELECT MAX(current_value) FROM dbo.label_code_sequence WHERE rule_version_id=@rule) AS sequence
          FROM dbo.product_label p WHERE p.id=@id;
          """,("@id",label),("@rule",ruleVersion));
        var code=state!["code"]?.ToString()??"";
        state["traceback"]=await One(U8Connection,"""
          SELECT COUNT(*) AS flows,ISNULL(SUM(CONVERT(decimal(18,6),sum)),0) AS balance,
          ISNULL(SUM(CASE WHEN CONVERT(decimal(18,6),sum)>0 THEN 1 ELSE 0 END),0) AS positives,
          (SELECT COUNT(*) FROM dbo.TraceBackGoods WHERE pdaGoodsUuid IN(SELECT pdaGoodsUuid FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code)) AS goods,
          (SELECT COUNT(*) FROM dbo.TraceBackOrder WHERE pdaOrderUuid IN(SELECT pdaOrderUuid FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code)) AS orders
          FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code;
          """,("@code",code));
        // Detect rolled-back Order/Goods orphans even if there is no OnlyCode row.
        using var scope=app!.Services.CreateScope();var repo=scope.ServiceProvider.GetRequiredService<PrintRepository>();
        var jobKey=await Scalar(LabelConnection,"SELECT TOP(1) o.print_job_id FROM dbo.business_operation o JOIN dbo.business_operation_line l ON l.operation_id=o.id WHERE l.label_id=@id AND l.print_kind='FIRST' ORDER BY o.id;",("@id",label));
        if(jobKey is Guid jobId){
            var job=await repo.Get(jobId,user,Ct);var initial=PrintRules.Registration(repo.Scope,job.Outputs[0],user.Name);
            OwnedOrders.Add(initial.OrderUuid);OwnedGoods.Add(initial.GoodsUuid);
            state["initialRows"]=await One(U8Connection,"SELECT (SELECT COUNT(*) FROM dbo.TraceBackOrder WHERE pdaOrderUuid=@order) AS orders,(SELECT COUNT(*) FROM dbo.TraceBackGoods WHERE pdaGoodsUuid=@goods) AS goods,(SELECT COUNT(*) FROM dbo.TraceBackOnlyCode WHERE pdaGoodsUuid=@goods) AS flows;",("@order",initial.OrderUuid),("@goods",initial.GoodsUuid));
        }
        return state;
    }
    static async Task RemoveFault()
    {
        var definition=await Scalar(U8Connection,"SELECT OBJECT_DEFINITION(OBJECT_ID(@name));",("@name","dbo."+Trigger));
        if(definition is string text){if(faultGoods is null||!text.Contains(faultGoods.Value.ToString(),StringComparison.OrdinalIgnoreCase))throw new Exception("Fault trigger ownership mismatch");await Exec(U8Connection,$"DROP TRIGGER [dbo].[{Trigger}];");}
    }
    static async Task<object> FinalizeRun()
    {
        await RemoveFault();
        var current=await One(LabelConnection,"SELECT * FROM dbo.label_print_rule_binding WHERE singleton_id=1;");
        if(current is not null&&current["rule_version_id"]!.GetValue<long>()==ruleVersion){
            if(originalBinding is null)await Exec(LabelConnection,"DELETE dbo.label_print_rule_binding WHERE singleton_id=1 AND rule_version_id=@v;",("@v",ruleVersion));
            else await Exec(LabelConnection,"UPDATE dbo.label_print_rule_binding SET rule_version_id=@old,updated_by=@by,updated_at=@at WHERE singleton_id=1 AND rule_version_id=@v;",("@v",ruleVersion),("@old",originalBinding["rule_version_id"]!.GetValue<long>()),("@by",originalBinding["updated_by"]!.ToString()),("@at",DateTime.Parse(originalBinding["updated_at"]!.ToString(),CultureInfo.InvariantCulture)));
        }else if(current is not null && originalBinding is null)throw new Exception("Another administrator changed current rule; not overwriting");
        await Exec(LabelConnection,"UPDATE dbo.label_print_device SET is_enabled=0 WHERE id=@device;UPDATE dbo.label_code_rule SET is_enabled=0 WHERE id=@rule;",("@device",Device),("@rule",ruleId));
        var hashes=new JsonObject();var hashEvidence=new JsonObject();
        foreach(var(table,b)in Baselines){var connection=OldTables.Contains(table)?U8Connection:LabelConnection;var after=await Fingerprint(connection,table,b.Max);hashes[table]=after==b.Hash;hashEvidence[table]=new JsonObject{["beforeSha256"]=b.Hash,["afterSha256"]=after,["baselineMaxId"]=b.Max};}
        var binding=await One(LabelConnection,"SELECT * FROM dbo.label_print_rule_binding WHERE singleton_id=1;");
        var restore=(binding is null&&originalBinding is null)||(binding is not null&&originalBinding is not null&&binding["rule_version_id"]!.ToString()==originalBinding["rule_version_id"]!.ToString());
        var locks=OwnedLabels.Count==0?0:Convert.ToInt32(await Scalar(LabelConnection,$"SELECT COUNT(*) FROM dbo.product_label WHERE id IN({string.Join(',',OwnedLabels)}) AND locked_operation_id IS NOT NULL;"));
        var result=new{runId=RunId,originalRowsUnchanged=hashes,hashEvidence,ruleBindingRestored=restore,faultTriggerRemoved=await Scalar(U8Connection,"SELECT OBJECT_ID(@name);",("@name","dbo."+Trigger)) is DBNull or null,remainingOwnedLocks=locks,ownedLabels=OwnedLabels,retainedSyntheticRecords=true,deviceDisabled=true,ruleDisabled=true};
        await File.WriteAllTextAsync(Path.Combine(Work,"final-sql-evidence.json"),JsonSerializer.Serialize(result,Json));finalized=true;return result;
    }
    sealed class GatewayTransport(Rpc rpc):HttpMessageHandler
    {
        public bool DropNextInitialResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.PathAndQuery;if(!path.StartsWith("/api/v1/traceback/labels",StringComparison.Ordinal))throw new Exception("U8 COM/non-TraceBack route rejected");
            var result=await rpc.Send(new{method=request.Method.Method,path,body=request.Content is null?null:await request.Content.ReadAsStringAsync(ct)});
            if(path.EndsWith("/initial-registrations")&&DropNextInitialResponse&&result["status"]!.GetValue<int>()==200){DropNextInitialResponse=false;throw new HttpRequestException("Simulated lost response after actual 888 SQL commit");}
            return new HttpResponseMessage((HttpStatusCode)result["status"]!.GetValue<int>()){Content=new StringContent(result["body"]!.ToString(),Encoding.UTF8,"application/json")};
        }
    }
    sealed class Rpc:IDisposable
    {
        readonly Process process;readonly SemaphoreSlim gate=new(1);
        Rpc(Process p){process=p;_=process.StandardError.ReadToEndAsync();}
        public static async Task<Rpc> Start(ProcessStartInfo info){var rpc=new Rpc(Process.Start(info)!);if(await rpc.process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))!="READY")throw new Exception("Development gateway initialization failed");return rpc;}
        public async Task<JsonObject> Send(object request){await gate.WaitAsync();try{await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request,Json));await process.StandardInput.FlushAsync();return JsonNode.Parse(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(90))??throw new Exception("Gateway exited"))!.AsObject();}finally{gate.Release();}}
        public void Dispose(){if(!process.HasExited){process.StandardInput.Close();if(!process.WaitForExit(5000))process.Kill(true);}}
    }
}
