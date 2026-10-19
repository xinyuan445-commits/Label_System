using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed record PrintPermission(string Function);
public static class PrintEndpoints
{
    public static void AddPrinting(this IServiceCollection services,IConfiguration config)
    {
        services.Configure<PrintingOptions>(config.GetSection("Printing"));
        services.AddScoped<PrintRepository>();
        services.AddScoped<PrintService>();
        services.AddScoped<PrintArtifacts>();
        services.AddHttpClient("PrintRenderer",client=>client.Timeout=TimeSpan.FromMinutes(3));
        services.AddHostedService<PrintRecoveryWorker>();
    }
    public static void MapPrinting(this WebApplication app)
    {
        app.MapGet("/api/v1/print/capabilities",(HttpContext context,IOptionsMonitor<PrintingOptions> p,
            PrintArtifacts artifacts)=>ApiResults.Ok(context,new
        {
            enabled=p.CurrentValue.Enabled,allowNewDispatch=p.CurrentValue.AllowNewDispatch,
            simulationOnly=p.CurrentValue.SimulationOnly,
            sourceEnabled=p.CurrentValue.SourceEnabled,
            simulationDeviceId=p.CurrentValue.SimulationOnly?p.CurrentValue.SimulationDeviceId:Guid.Empty,
            simulationDeviceKeyHash=p.CurrentValue.SimulationOnly?p.CurrentValue.SimulationDeviceKeyHash:"",
            protocol="driver-labels-v2",requiredMigration="017",
            serverPublicKey=p.CurrentValue.Enabled?artifacts.PublicKey():"",
            maxOutputs=100,previewConsumesCode=false,successBasis="DRIVER_ACCEPTED"
        }));
        var group=app.MapGroup("/api/v1/print");
        group.AddEndpointFilter(async (context,next)=>
        {
            var http=context.HttpContext;
            var services=http.RequestServices;
            services.GetRequiredService<PrintService>().RequireEnabled();
            var function=http.GetEndpoint()?.Metadata.GetMetadata<PrintPermission>()?.Function??"print.view";
            if(!await services.GetRequiredService<UserRepository>().HasFunctionAccessAsync(
                RequestContext.User(http).Id,function,http.RequestAborted))
                throw new ApiException(403,"PRINT_FORBIDDEN","没有此打印功能权限。");
            http.Response.Headers.CacheControl="private, no-store";
            return await next(context);
        });
        group.MapGet("/pending-labels",async(HttpContext h,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.Pending(RequestContext.User(h),ct)));
        group.MapPost("/source-targets",async(HttpContext h,SignedPrintMessage request,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.ReserveSource(request,RequestContext.User(h),ct)))
            .WithMetadata(new PrintPermission("print.first"));
        group.MapPost("/simulation/labels",async(HttpContext h,PrintSimulationSeedRequest request,PrintRepository r,CancellationToken ct)=>
        {
            RequireAdmin(h);
            return ApiResults.Ok(h,await r.SeedSimulation(request,RequestContext.User(h),ct));
        }).WithMetadata(new PrintPermission("print.first"));
        group.MapGet("/simulation/labels/{id:long}",async(HttpContext h,long id,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.AuditSimulation(id,RequestContext.User(h),ct)));
        group.MapGet("/source-summary",async(HttpContext h,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.SourceSummary(RequestContext.User(h),ct)));
        group.MapPost("/labels/resolve",async(HttpContext h,ResolvePrintLabelRequest request,PrintService s,CancellationToken ct)=>
            ApiResults.Ok(h,await s.Resolve(request.Code,RequestContext.User(h),ct)));
        group.MapGet("/labels/{id:long}/original",async(HttpContext h,long id,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.OriginalLabel(id,RequestContext.User(h),ct)));
        group.MapPost("/labels/legacy-baseline",async(HttpContext h,LegacyPrintBaselineRequest request,PrintService s,CancellationToken ct)=>
            ApiResults.Ok(h,await s.LegacyBaseline(request,RequestContext.User(h),ct)))
            .WithMetadata(new PrintPermission("print.reconcile"));
        group.MapGet("/jobs",async(HttpContext h,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.List(RequestContext.User(h),ct)));
        group.MapGet("/jobs/{id:guid}",async(HttpContext h,Guid id,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.Get(id,RequestContext.User(h),ct)));
        group.MapPost("/jobs/first",async(HttpContext h,FirstPrintRequest request,PrintService s,CancellationToken ct)=>
            ApiResults.Ok(h,await s.First(request,RequestContext.User(h),ct))).WithMetadata(new PrintPermission("print.first"));
        group.MapPost("/jobs/reprint",async(HttpContext h,ReprintRequest request,PrintService s,CancellationToken ct)=>
            ApiResults.Ok(h,await s.Reprint(request,RequestContext.User(h),ct))).WithMetadata(new PrintPermission("print.reprint"));
        group.MapPost("/jobs/{id:guid}/render",async(HttpContext h,Guid id,PrintRepository r,PrintService s,CancellationToken ct)=>
        {
            var job=await r.Get(id,RequestContext.User(h),ct);
            s.RequireEnabled(job.Outputs.Any(o=>o.Kind=="FIRST"));
            return ApiResults.Ok(h,await s.Render(job,RequestContext.User(h),ct));
        }).WithMetadata(new PrintPermission("print.confirm"));
        group.MapPost("/jobs/{id:guid}/receipts",async(HttpContext h,Guid id,SignedPrintMessage request,PrintService s,CancellationToken ct)=>
            ApiResults.Ok(h,await s.Receive(id,request,RequestContext.User(h),ct))).WithMetadata(new PrintPermission("print.confirm"));
        group.MapPost("/jobs/{id:guid}/confirmations",async(HttpContext h,Guid id,SignedPrintMessage request,PrintService s,CancellationToken ct)=>
            ApiResults.Ok(h,await s.Receive(id,request,RequestContext.User(h),ct))).WithMetadata(new PrintPermission("print.confirm"));
        group.MapPost("/jobs/{id:guid}/retry-sync",async(HttpContext h,Guid id,PrintService s,CancellationToken ct)=>
            ApiResults.Ok(h,await s.RetrySync(id,RequestContext.User(h),ct))).WithMetadata(new PrintPermission("print.confirm"));
        group.MapPost("/jobs/{id:guid}/reconciliations",async(HttpContext h,Guid id,PrintReconcileRequest request,
            PrintRepository r,PrintService s,CancellationToken ct)=>{
            RequireAdmin(h);
            var job=await r.RecordReconciliation(id,request,RequestContext.User(h),ct);
            // Only the original signed device receipt can move the job out of UNKNOWN.
            return ApiResults.Ok(h,request.Receipt is null?job:
                await s.Receive(id,request.Receipt,RequestContext.User(h),ct));
        }).WithMetadata(new PrintPermission("print.reconcile"));
        group.MapPost("/jobs/{id:guid}/dispatch-authorizations",async(HttpContext h,Guid id,
            PrintDispatchRequest request,PrintRepository r,PrintArtifacts a,PrintService s,CancellationToken ct)=>
        {
            var job=await r.Get(id,RequestContext.User(h),ct);
            s.RequireEnabled(job.Outputs.Any(o=>o.Kind=="FIRST"));
            s.RequireDispatch();
            return ApiResults.Ok(h,await r.Authorize(id,request,RequestContext.User(h),a.Sign,ct));
        }).WithMetadata(new PrintPermission("print.confirm"));
        group.MapGet("/jobs/{id:guid}/bundle",async(HttpContext h,Guid id,PrintRepository r,PrintArtifacts a,CancellationToken ct)=>
        {
            var job=await r.Get(id,RequestContext.User(h),ct);
            if(job.Manifest is null)throw PrintRules.Conflict("PRINT_NOT_READY","打印文件尚未准备好。");
            return Results.File(await a.Read(job.Manifest.S("bundlePath"),job.Manifest.S("bundleSha256"),ct),"application/zip");
        });
        group.MapGet("/jobs/{id:guid}/pages/{output:guid}",async(HttpContext h,Guid id,Guid output,
            PrintRepository r,PrintArtifacts a,CancellationToken ct)=>
        {
            var job=await r.Get(id,RequestContext.User(h),ct);
            var page=job.Manifest?["pages"]?.AsArray().OfType<JsonObject>()
                .SingleOrDefault(x=>x.S("outputId")==output.ToString())
                ??throw new ApiException(404,"PRINT_PAGE_NOT_FOUND","标签页不存在。");
            return Results.File(await a.Read(page.S("pdfPath"),page.S("pdfSha256"),ct),"application/pdf");
        });
        group.MapPost("/jobs/{id:guid}/cancel",async(HttpContext h,Guid id,PrintActionRequest request,
            PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.ChangeState(id,request,false,RequestContext.User(h),ct)))
            .WithMetadata(new PrintPermission("print.confirm"));
        group.MapPost("/jobs/{id:guid}/resume",async(HttpContext h,Guid id,PrintActionRequest request,
            PrintRepository r,PrintService s,CancellationToken ct)=>
        {
            var existing=await r.Get(id,RequestContext.User(h),ct);
            s.RequireEnabled(existing.Outputs.Any(x=>x.Kind=="FIRST"));
            var job=await r.ChangeState(id,request,true,RequestContext.User(h),ct);
            return ApiResults.Ok(h,await s.Render(job,RequestContext.User(h),ct));
        }).WithMetadata(new PrintPermission("print.confirm"));
        group.MapGet("/code-rule",async(HttpContext h,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.CurrentRule(ct)));
        group.MapPut("/code-rule",async(HttpContext h,PrintRuleRequest request,PrintRepository r,CancellationToken ct)=>
        {
            RequireAdmin(h);
            return ApiResults.Ok(h,await r.SetRule(request,RequestContext.User(h),ct));
        }).WithMetadata(new PrintPermission("print.template.manage"));
        group.MapGet("/devices",async(HttpContext h,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.Devices(RequestContext.User(h),ct)));
        group.MapPost("/devices",async(HttpContext h,PrintDeviceRequest request,PrintRepository r,CancellationToken ct)=>
        {
            RequireAdmin(h);
            return ApiResults.Ok(h,await r.PairDevice(request,RequestContext.User(h),ct));
        }).WithMetadata(new PrintPermission("print.device.manage"));
        group.MapPut("/devices/{id:guid}",async(HttpContext h,Guid id,PrintDeviceStateRequest request,PrintRepository r,CancellationToken ct)=>{
            RequireAdmin(h);return ApiResults.Ok(h,await r.SetDeviceState(id,request,ct));
        }).WithMetadata(new PrintPermission("print.device.manage"));
        group.MapPost("/previews",async(PrintPreviewRequest request,PrintService s,CancellationToken ct)=>
            Results.File(await s.Preview(request,ct),"application/pdf"));
        group.MapGet("/templates",async(HttpContext h,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.Templates(ct)));
        group.MapGet("/templates/{id:long}",async(HttpContext h,long id,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.Template(id,ct)));
        group.MapPost("/templates",async(HttpContext h,PrintTemplateRequest request,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.SaveTemplate(null,request,RequestContext.User(h),ct)))
            .WithMetadata(new PrintPermission("print.template.manage"));
        group.MapPut("/templates/{id:long}",async(HttpContext h,long id,PrintTemplateRequest request,PrintRepository r,CancellationToken ct)=>
            ApiResults.Ok(h,await r.SaveTemplate(id,request,RequestContext.User(h),ct)))
            .WithMetadata(new PrintPermission("print.template.manage"));
        group.MapPost("/templates/{id:long}/publish",async(HttpContext h,long id,PublishPrintTemplateRequest request,PrintService s,CancellationToken ct)=>
            ApiResults.Ok(h,await s.Publish(id,request,RequestContext.User(h),ct)))
            .WithMetadata(new PrintPermission("print.template.manage"));
        group.MapPost("/assets",async(HttpContext h,PrintAssetRequest request,PrintService s,CancellationToken ct)=>
            ApiResults.Ok(h,await s.SaveAsset(request,RequestContext.User(h),ct)))
            .WithMetadata(new PrintPermission("print.template.manage"));
        group.MapGet("/assets/{id:long}",async(long id,PrintRepository r,PrintArtifacts a,CancellationToken ct)=>
        {
            var asset=await r.Asset(id,ct);
            return Results.File(await a.Read(asset.S("storage_relative_path"),asset.S("sha256"),ct),asset.S("media_type"));
        });
        group.MapGet("/assets/{id:long}/metadata",async(HttpContext h,long id,PrintRepository r,CancellationToken ct)=>
        {
            var asset=await r.Asset(id,ct);
            return ApiResults.Ok(h,new{id,sha256=asset.S("sha256"),mediaType=asset.S("media_type"),
                widthPx=asset.L("width_px"),heightPx=asset.L("height_px"),sizeBytes=asset.L("size_bytes")});
        });
        group.MapPost("/barcode-preview",async(HttpContext h,JsonObject request,PrintArtifacts a,CancellationToken ct)=>
        {
            var result=await a.Renderer("/barcode",request,ct);
            return Results.File(Convert.FromBase64String(result.S("dataBase64")),"image/png");
        });
        group.MapGet("/barcode-preview",async(string type,string data,PrintArtifacts a,CancellationToken ct)=>{
            if(data.Length>2048)throw new ApiException(400,"PRINT_BARCODE_INVALID","条码内容过长。");
            var result=await a.Renderer("/barcode",new{type,data},ct);
            return Results.File(Convert.FromBase64String(result.S("dataBase64")),"image/png");
        });
    }
    private static void RequireAdmin(HttpContext h)
    {
        if(!RequestContext.IsAdmin(h))throw new ApiException(403,"PRINT_ADMIN_REQUIRED","仅管理员可操作。");
    }
}
