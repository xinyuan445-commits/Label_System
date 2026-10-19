using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed class PrintService(PrintRepository repository,PrintArtifacts artifacts,U8GatewayClient gateway,
    IOptionsMonitor<PrintingOptions> printOptions,IOptionsMonitor<LabelSystemOptions> labelOptions,
    TraceBackMirrorRepository mirrors)
{
    public async Task<object> Resolve(string code,SessionUser user,CancellationToken ct)
    {
        RequireEnabled();
        if(string.IsNullOrWhiteSpace(code)||code.Length>100)
            throw new ApiException(400,"PRINT_CODE_INVALID","请输入完整标签码。");
        var snapshot=(await gateway.GetTraceBackSnapshotsAsync([code],ct)).Single();
        var line=await mirrors.UpsertAsync(snapshot,user,false,ct,preserveTerminalState:true);
        return new { label=await repository.FindLabel(code,ct), inventory=line };
    }
    public async Task<JsonObject> LegacyBaseline(LegacyPrintBaselineRequest request,SessionUser user,CancellationToken ct)
    {
        RequireEnabled();
        var evidence=await gateway.GetLegacyPrintEvidenceAsync(request.Code,ct);
        return await repository.RecordLegacyBaseline(request,evidence,user,ct);
    }
    public void RequireEnabled(bool writes=false)
    {
        if(!printOptions.CurrentValue.Enabled)throw new ApiException(503,"PRINT_DISABLED","打印功能尚未启用。");
        _=repository.Scope;
        if(writes&&!labelOptions.CurrentValue.U8WritesEnabled)
            throw new ApiException(409,"PRINT_WRITES_BLOCKED","当前环境未允许追溯写入。");
    }
    public void RequireDispatch()
    {
        RequireEnabled();
        if(!printOptions.CurrentValue.AllowNewDispatch)
            throw PrintRules.Conflict("PRINT_DISPATCH_DISABLED","已暂停新的打印发送，可继续恢复原任务。");
    }
    public async Task<PrintJob> First(FirstPrintRequest request,SessionUser user,CancellationToken ct)
    {
        RequireEnabled(true);
        RequireDispatch();
        if(string.IsNullOrWhiteSpace(request.Printer)||(request.Printer.Contains("PDF",StringComparison.OrdinalIgnoreCase)&&request.Printer!="Microsoft Print to PDF")||request.Printer=="浏览器打印")
            throw new ApiException(400,"PRINT_EXPORT_ONLY","请选择标签打印机。");
        var job=await repository.PrepareFirst(request,user,ct);
        return await Render(job,user,ct);
    }
    public async Task<PrintJob> Reprint(ReprintRequest request,SessionUser user,CancellationToken ct)
    {
        RequireEnabled();
        RequireDispatch();
        var job=await repository.PrepareReprint(request,user,ct);
        return await Render(job,user,ct);
    }
    public async Task<PrintJob> Render(PrintJob job,SessionUser user,CancellationToken ct)
    {
        if(job.Phase!="PREPARING")return job;
        foreach(var output in job.Outputs.Where(o=>o.Kind=="FIRST"))
            await gateway.ValidateInitialRegistrationAsync(PrintRules.Registration(repository.Scope,output,user.Name),ct);
        var template=await repository.TemplateVersion(job.TemplateVersionId,ct);
        var assets=await repository.AssetsForVersion(job.TemplateVersionId,ct);
        var manifest=await artifacts.Build(job,template,assets,repository.Scope,ct);
        return await repository.SaveManifest(job.JobId,manifest,user,ct);
    }
    public async Task<PrintJob> Receive(Guid jobId,SignedPrintMessage receipt,SessionUser user,CancellationToken ct)
    {
        RequireEnabled();
        var before=await repository.Get(jobId,user,ct);
        if(before.Outputs.Any(o=>o.Kind=="FIRST"))RequireEnabled(true);
        var job=await repository.Receipt(jobId,receipt,user,ct);
        if(job.Outputs.Any(o=>o.Kind=="FIRST"&&o.FlowId.HasValue))
            return await RetrySync(jobId,user,ct);
        return job;
    }
    public async Task<PrintJob> RetrySync(Guid jobId,SessionUser user,CancellationToken ct)
    {
        RequireEnabled(true);
        foreach(var task in await repository.SyncTasks(jobId,user,ct))
        {
            var payload=JsonSerializer.Deserialize<InitialLabelRegistration>(task.S("payload_json"),PrintRules.Json)!;
            try
            {
                if(printOptions.CurrentValue.SimulationOnly)
                {
                    var state=printOptions.CurrentValue.SimulationStatePath;
                    var fault=Path.Combine(state,jobId.ToString("D")+".fail-sync-once");
                    if(File.Exists(fault))
                    {
                        File.Move(fault,fault+".consumed",true);
                        throw PrintRules.Conflict("PRINT_SIMULATED_SYNC_FAILURE","模拟追溯登记失败，请恢复原任务。");
                    }
                }
                var result=await gateway.RegisterInitialAsync(payload,ct);
                await repository.CompleteSync(jobId,task.L("id"),result,user,ct);
            }
            catch(Exception ex) when(ex is ApiException or HttpRequestException or TaskCanceledException)
            {
                // SQL outbox remains authoritative. Never invoke printer or re-create the output here.
                if(!ct.IsCancellationRequested)await repository.FailSync(jobId,task.L("id"),user,ct);
            }
        }
        return await repository.Get(jobId,user,ct);
    }
    public async Task<JsonObject> Publish(long id,PublishPrintTemplateRequest request,SessionUser user,CancellationToken ct)
    {
        RequireEnabled();
        var fonts=await artifacts.Renderer("/fonts",new{},ct);
        return await repository.Publish(id,request.ExpectedRowVersion,user,fonts,ct);
    }
    public async Task<JsonObject> SaveAsset(PrintAssetRequest request,SessionUser user,CancellationToken ct)
    {
        byte[] bytes;
        try{bytes=Convert.FromBase64String(request.DataBase64);}
        catch(FormatException){throw new ApiException(400,"PRINT_ASSET_INVALID","图片格式无效。");}
        if(bytes.Length is <1 or >5242880 || request.WidthPx<=0||request.HeightPx<=0 ||
            !((request.MediaType=="image/png"&&bytes.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10}))||
              (request.MediaType=="image/jpeg"&&bytes.Length>3&&bytes[0]==255&&bytes[1]==216&&bytes[2]==255)))
            throw new ApiException(400,"PRINT_ASSET_INVALID","仅支持有效的 PNG/JPEG 图片。");
        var verified=await artifacts.Renderer("/image-info",new{dataBase64=request.DataBase64},ct);
        if(verified.L("width")!=request.WidthPx||verified.L("height")!=request.HeightPx||
            verified.S("mediaType")!=request.MediaType)
            throw new ApiException(400,"PRINT_ASSET_INVALID","图片尺寸或格式不匹配。");
        var path=await artifacts.Store("assets",bytes,request.MediaType=="image/png"?"png":"jpg",ct);
        return await repository.SaveAsset(request,path,bytes,user,ct);
    }
    public async Task<byte[]> Preview(PrintPreviewRequest request,CancellationToken ct)
    {
        var template=await repository.TemplateVersion(request.TemplateVersionId,ct);
        var assets=await repository.AssetsForVersion(request.TemplateVersionId,ct);
        var resources=new List<object>();
        foreach(var asset in assets)resources.Add(new{id=asset.L("id"),sha256=asset.S("sha256"),
            dataBase64=Convert.ToBase64String(await artifacts.Read(asset.S("storage_relative_path"),asset.S("sha256"),ct))});
        var variables=(JsonObject)request.Variables.DeepClone();
        variables["条码"]="PREVIEW";variables["Code"]="PREVIEW";
        var result=await artifacts.Renderer("/render",new{
            preview=true,canvasData=JsonNode.Parse(template.S("canvas_json")),
            widthMm=template.D("width_mm"),heightMm=template.D("height_mm"),
            fontManifest=JsonNode.Parse(template.S("font_manifest_json")),assets=resources,
            outputs=new[]{new{outputId=Guid.NewGuid(),code="PREVIEW",variables,
                frozenAt=BusinessClock.Now.ToString("O")}}},ct);
        return Convert.FromBase64String(result["pages"]![0]!["pdf"]!.ToString());
    }
}
