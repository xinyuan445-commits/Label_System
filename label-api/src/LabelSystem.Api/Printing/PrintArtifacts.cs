using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed class PrintArtifacts(IOptionsMonitor<PrintingOptions> options,IHttpClientFactory clients)
{
    private string Root => Path.GetFullPath(options.CurrentValue.RootPath);
    public string Resolve(string relative)
    {
        if(Path.IsPathRooted(relative))throw new InvalidOperationException("Invalid artifact path.");
        var path=Path.GetFullPath(Path.Combine(Root,relative));
        if(!path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid artifact path.");
        return path;
    }
    public async Task<string> Store(string folder,byte[] bytes,string suffix,CancellationToken ct)
    {
        var hash=Convert.ToHexString(SHA256.HashData(bytes));
        var relative=$"{folder}/{hash}.{suffix}";
        var path=Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                await stream.WriteAsync(bytes,ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            File.Move(temporary,path,false);
        }
        catch(IOException) when(File.Exists(path))
        {
            var existing=await File.ReadAllBytesAsync(path,ct);
            if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing),SHA256.HashData(bytes)))
                throw new InvalidOperationException("Immutable artifact changed.");
        }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
        return relative;
    }
    public async Task<byte[]> Read(string relative,string expectedHash,CancellationToken ct)
    {
        var bytes=await File.ReadAllBytesAsync(Resolve(relative),ct);
        if(Convert.ToHexString(SHA256.HashData(bytes))!=expectedHash)
            throw new ApiException(503,"PRINT_ARTIFACT_UNAVAILABLE","打印文件校验失败。");
        return bytes;
    }
    public SignedPrintMessage Sign(JsonObject payload)
    {
        using var key=ECDsa.Create();
        var path=options.CurrentValue.SigningKeyPath;
        if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))
            throw new ApiException(503,"PRINT_SIGNING_NOT_CONFIGURED","打印签名尚未配置。");
        key.ImportFromPem(File.ReadAllText(path));
        if(key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7")
            throw new ApiException(503,"PRINT_SIGNING_NOT_CONFIGURED","打印签名须使用 P-256 密钥。");
        var bytes=Encoding.UTF8.GetBytes(PrintRules.Canonical(payload));
        return new SignedPrintMessage(Convert.ToBase64String(bytes),
            Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence)));
    }
    public string PublicKey()
    {
        using var key=ECDsa.Create();
        var path=options.CurrentValue.SigningKeyPath;
        if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))return "";
        key.ImportFromPem(File.ReadAllText(path));
        if(key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7")
            throw new ApiException(503,"PRINT_SIGNING_NOT_CONFIGURED","打印签名须使用 P-256 密钥。");
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }
    public async Task<JsonObject> Renderer(string path,object request,CancellationToken ct)
    {
        var p=options.CurrentValue;
        if(!Uri.TryCreate(p.RendererUrl,UriKind.Absolute,out var uri)||string.IsNullOrWhiteSpace(p.RendererApiKey)||
            (uri.Scheme!="https"&&!uri.IsLoopback))
            throw new ApiException(503,"PRINT_RENDERER_NOT_CONFIGURED","标签渲染尚未配置。");
        using var message=new HttpRequestMessage(HttpMethod.Post,new Uri(uri,path))
            {Content=new StringContent(JsonSerializer.Serialize(request,PrintRules.Json),Encoding.UTF8,"application/json")};
        message.Headers.Add("X-Renderer-Key",p.RendererApiKey);
        using var response=await clients.CreateClient("PrintRenderer").SendAsync(message,ct);
        if(!response.IsSuccessStatusCode)
            throw new ApiException(422,"PRINT_RENDER_INVALID","标签渲染校验失败，请检查模板与变量。");
        return await response.Content.ReadFromJsonAsync<JsonObject>(PrintRules.Json,ct)
            ??throw new ApiException(422,"PRINT_RENDER_INVALID","标签渲染结果无效。");
    }
    public async Task<JsonObject> Build(PrintJob job,JsonObject template,IReadOnlyList<JsonObject> assets,
        string scope,CancellationToken ct)
    {
        var resourceData=new List<object>();
        foreach(var asset in assets)
            resourceData.Add(new{id=asset.L("id"),sha256=asset.S("sha256"),
                dataBase64=Convert.ToBase64String(await Read(asset.S("storage_relative_path"),asset.S("sha256"),ct))});
        var rendered=await Renderer("/render",new
        {
            canvasData=JsonNode.Parse(template.S("canvas_json")),widthMm=template.D("width_mm"),heightMm=template.D("height_mm"),
            fontManifest=JsonNode.Parse(template.S("font_manifest_json")),
            assets=resourceData,outputs=job.Outputs.Select(o=>new{outputId=o.OutputId,code=o.Snapshot.Code,
                variables=o.Snapshot.Variables,frozenAt=o.Snapshot.FrozenAt})
        },ct);
        if(rendered.S("version")!=options.CurrentValue.RendererVersion)
            throw PrintRules.Conflict("PRINT_RENDERER_VERSION_CHANGED","渲染版本不匹配。");
        var pages=rendered["pages"]!.AsArray();
        if(pages.Count!=job.Outputs.Count||pages.Select(x=>x!["outputId"]!.ToString()).Distinct().Count()!=pages.Count)
            throw PrintRules.Conflict("PRINT_RENDER_INVALID","标签页数不匹配。");
        var content=new JsonObject
        {
            ["version"]=2,["scope"]=scope,["jobId"]=job.JobId.ToString(),["deviceId"]=job.DeviceId.ToString(),
            ["printer"]=job.Printer,["widthMm"]=template.D("width_mm"),["heightMm"]=template.D("height_mm"),
            ["reprint"]=job.Outputs.All(o=>o.Kind=="REPRINT"),
            ["templateVersionId"]=job.TemplateVersionId,["templateHash"]=template.S("content_sha256"),
            ["fontManifest"]=rendered["fontManifest"]!.DeepClone(),["pages"]=new JsonArray()
        };
        using var buffer=new MemoryStream();
        var filenames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true))
        {
            foreach(var page in pages.OfType<JsonObject>())
            {
                var id=Guid.Parse(page.S("outputId"));
                if(!job.Outputs.Any(o=>o.OutputId==id))throw PrintRules.Conflict("PRINT_RENDER_INVALID","打印副本不匹配。");
                var png=Convert.FromBase64String(page.S("png"));
                var pdf=Convert.FromBase64String(page.S("pdf"));
                var pdfPath=await Store(job.JobId.ToString("N"),pdf,"pdf",ct);
                var payload=job.Printer=="Microsoft Print to PDF"?pdf:png;
                var extension=job.Printer=="Microsoft Print to PDF"?".pdf":".png";
                var file=id.ToString("N")+extension;
                var output=job.Outputs.Single(o=>o.OutputId==id);
                var filename=extension==".pdf"?BusinessFilename(output,filenames):file;
                var hash=Convert.ToHexString(SHA256.HashData(payload));
                content["pages"]!.AsArray().Add(new JsonObject
                    {["outputId"]=id.ToString(),["file"]=file,["filename"]=filename,["sha256"]=hash,
                     ["pdfPath"]=pdfPath,["pdfSha256"]=Convert.ToHexString(SHA256.HashData(pdf))});
                var entry=zip.CreateEntry(file,CompressionLevel.Fastest);
                entry.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
                await using var stream=entry.Open();
                await stream.WriteAsync(payload,ct);
            }
            var metadata=zip.CreateEntry("manifest.json");
            metadata.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
            await using var manifestStream=metadata.Open();
            await manifestStream.WriteAsync(Encoding.UTF8.GetBytes(PrintRules.Canonical(content)),ct);
        }
        var bundle=buffer.ToArray();
        if(bundle.Length>64*1024*1024)throw new ApiException(422,"PRINT_BUNDLE_TOO_LARGE","打印文件过大，请减少标签。");
        var bundlePath=await Store(job.JobId.ToString("N"),bundle,"zip",ct);
        content["bundlePath"]=bundlePath;
        content["bundleSha256"]=Convert.ToHexString(SHA256.HashData(bundle));
        // Hash excludes its own storage path; the final immutable DB manifest includes that path.
        var canonical=Encoding.UTF8.GetBytes(PrintRules.Canonical(content));
        content["manifestPath"]=await Store(job.JobId.ToString("N"),canonical,"json",ct);
        return content;
    }

    public static string BusinessFilename(PrintOutput output,ISet<string>? used=null)
    {
        static string Safe(string value)
        {
            var invalid=Path.GetInvalidFileNameChars().ToHashSet();
            var cleaned=new string(value.Trim().Select(c=>invalid.Contains(c)||char.IsControl(c)?'_':c).ToArray()).Trim(' ','.');
            return string.IsNullOrWhiteSpace(cleaned)?"0":cleaned;
        }
        var snapshot=output.Snapshot;
        var document=Safe(snapshot.SourceDocumentNo);
        var rawLine=snapshot.Variables.S("行号");
        var line=Safe(string.IsNullOrWhiteSpace(rawLine)?snapshot.ErpDetailId??"0":rawLine);
        var rawSerial=snapshot.Code.Split('|').LastOrDefault();
        var serial=Safe(string.IsNullOrWhiteSpace(rawSerial)?snapshot.SerialNo:rawSerial);
        var stem=$"{document}_{line}_{serial}";
        if(stem.Length>230)stem=stem[..230];
        var filename=stem+".pdf";
        if(used is null)return filename;
        var suffix=1;
        while(!used.Add(filename))
        {
            suffix++;
            var marker=$"_{suffix}";
            var baseStem=stem.Length+marker.Length>230?stem[..(230-marker.Length)]:stem;
            filename=baseStem+marker+".pdf";
        }
        return filename;
    }
}
