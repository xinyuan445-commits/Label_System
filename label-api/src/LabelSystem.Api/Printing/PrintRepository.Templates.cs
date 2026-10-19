using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LabelSystem.Api;

public sealed partial class PrintRepository
{
    public Task<IReadOnlyList<JsonObject>> Templates(CancellationToken ct) =>
        Transaction<IReadOnlyList<JsonObject>>(async db=>await db.Query("""
            SELECT id,name,width_mm AS widthMm,height_mm AS heightMm,published_version_id AS publishedVersionId,
              row_version AS rowVersion FROM dbo.label_print_template WHERE is_enabled=1 ORDER BY id DESC;
            """),ct);
    public Task<JsonObject> Template(long id,CancellationToken ct) => Transaction(async db=>
        await db.One("SELECT * FROM dbo.label_print_template WHERE id=@id;",("@id",id))
        ??throw new ApiException(404,"PRINT_TEMPLATE_NOT_FOUND","模板不存在。"),ct);
    public Task<JsonObject> TemplateVersion(long id,CancellationToken ct) => Transaction(async db=>
        await db.One("SELECT * FROM dbo.label_print_template_version WHERE id=@id;",("@id",id))
        ??throw new ApiException(404,"PRINT_TEMPLATE_NOT_FOUND","模板版本不存在。"),ct);
    public Task<JsonObject> SaveTemplate(long? id,PrintTemplateRequest request,SessionUser user,CancellationToken ct) =>
        Transaction(async db=>
        {
            ValidateCanvas(request);
            if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>200)
                throw new ApiException(400,"PRINT_TEMPLATE_INVALID","请输入模板名称。");
            long target;
            if(id.HasValue)
            {
                var row=await db.One("SELECT row_version FROM dbo.label_print_template WITH(UPDLOCK,HOLDLOCK) WHERE id=@id;",("@id",id.Value))
                    ??throw new ApiException(404,"PRINT_TEMPLATE_NOT_FOUND","模板不存在。");
                Version(row,request.ExpectedRowVersion);
                await db.Execute("""
                    UPDATE dbo.label_print_template SET name=@name,draft_canvas_json=@canvas,width_mm=@w,
                      height_mm=@h,updated_by=@user,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@id;
                    """,("@name",request.Name.Trim()),("@canvas",request.CanvasData.GetRawText()),
                    ("@w",request.WidthMm),("@h",request.HeightMm),("@user",user.Id),("@id",id.Value));
                target=id.Value;
            }
            else target=Convert.ToInt64(await db.Scalar("""
                INSERT dbo.label_print_template(name,draft_canvas_json,width_mm,height_mm,created_by,updated_by)
                OUTPUT inserted.id VALUES(@name,@canvas,@w,@h,@user,@user);
                """,("@name",request.Name.Trim()),("@canvas",request.CanvasData.GetRawText()),
                ("@w",request.WidthMm),("@h",request.HeightMm),("@user",user.Id)),CultureInfo.InvariantCulture);
            return (await db.One("SELECT * FROM dbo.label_print_template WHERE id=@id;",("@id",target)))!;
        },ct);
    public static void ValidateCanvas(PrintTemplateRequest request)
    {
        try { ValidateCanvasShape(request); }
        catch(Exception ex) when(ex is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new ApiException(400,"PRINT_TEMPLATE_INVALID","模板尺寸或内容无效。"); }
    }
    private static void ValidateCanvasShape(PrintTemplateRequest request)
    {
        if(request.WidthMm is <=0 or >1000 || request.HeightMm is <=0 or >1000 ||
            decimal.Round(request.WidthMm,3)!=request.WidthMm || decimal.Round(request.HeightMm,3)!=request.HeightMm ||
            request.CanvasData.ValueKind!=JsonValueKind.Object || request.CanvasData.GetRawText().Length>1024*1024 ||
            !request.CanvasData.TryGetProperty("version",out var version)||version.GetInt32()!=1 ||
            !request.CanvasData.TryGetProperty("stage",out var stage)||
            stage.GetProperty("width_mm").GetDecimal()!=request.WidthMm ||
            stage.GetProperty("height_mm").GetDecimal()!=request.HeightMm ||
            !request.CanvasData.TryGetProperty("objects",out var objects)||objects.ValueKind!=JsonValueKind.Array||
            objects.GetArrayLength() is <1 or >50)
            throw new ApiException(400,"PRINT_TEMPLATE_INVALID","模板尺寸或内容无效。");
        foreach(var obj in objects.EnumerateArray())
            if(!obj.TryGetProperty("type",out var type) ||
                type.GetString() is not ("text" or "rect" or "line" or "image" or "barcode" or "table"))
                throw new ApiException(400,"PRINT_TEMPLATE_INVALID","模板包含不支持的对象。");
        if(objects.EnumerateArray().Any(o=>!o.TryGetProperty("id",out var id)||id.ValueKind!=JsonValueKind.String||
              string.IsNullOrWhiteSpace(id.GetString())) ||
            objects.EnumerateArray().Select(o=>o.GetProperty("id").GetString()).Distinct().Count()!=objects.GetArrayLength())
            throw new ApiException(400,"PRINT_TEMPLATE_INVALID","模板对象标识无效。");
    }
    public Task<JsonObject> Publish(long id,string expected,SessionUser user,JsonObject fonts,CancellationToken ct) =>
        Transaction(async db=>
        {
            var row=await db.One("SELECT * FROM dbo.label_print_template WITH(UPDLOCK,HOLDLOCK) WHERE id=@id;",("@id",id))
                ??throw new ApiException(404,"PRINT_TEMPLATE_NOT_FOUND","模板不存在。");
            Version(row,expected);
            var canvas=JsonNode.Parse(row.S("draft_canvas_json"))!.AsObject();
            var assets=canvas["objects"]!.AsArray().OfType<JsonObject>()
                .Where(x=>x.S("type")=="image"&&x["assetId"] is not null).Select(x=>x.L("assetId")).Distinct().ToArray();
            if(assets.Length>20)throw new ApiException(400,"PRINT_TEMPLATE_INVALID","图片数量过多。");
            foreach(var asset in assets)
                if(await db.Scalar("SELECT id FROM dbo.label_print_asset WHERE id=@id;",("@id",asset)) is null)
                    throw PrintRules.Conflict("PRINT_ASSET_MISSING","模板图片缺失。");
            var variables=Regex.Matches(row.S("draft_canvas_json"),@"\{\{\s*([^}]+?)\s*\}\}")
                .Select(m=>m.Groups[1].Value.Trim()).Distinct().Order(StringComparer.Ordinal).ToArray();
            var version=Convert.ToInt64(await db.Scalar("""
                INSERT dbo.label_print_template_version(template_id,version_no,canvas_json,width_mm,height_mm,
                  variable_contract_json,content_sha256,renderer_version,font_manifest_json,created_by)
                OUTPUT inserted.id
                SELECT @id,ISNULL(MAX(version_no),0)+1,@canvas,@w,@h,@variables,@hash,@renderer,@fonts,@user
                FROM dbo.label_print_template_version WITH(UPDLOCK,HOLDLOCK) WHERE template_id=@id;
                """,("@id",id),("@canvas",PrintRules.Canonical(canvas)),("@w",row.D("width_mm")),("@h",row.D("height_mm")),
                ("@variables",PrintRules.Canonical(variables)),("@hash",Convert.FromHexString(PrintRules.Hash(new{canvas,fonts}))),
                ("@renderer",printOptions.CurrentValue.RendererVersion),("@fonts",PrintRules.Canonical(fonts)),("@user",user.Id)),CultureInfo.InvariantCulture);
            foreach(var asset in assets) await db.Execute(
                "INSERT dbo.label_print_template_version_asset(version_id,asset_id) VALUES(@version,@asset);",
                ("@version",version),("@asset",asset));
            await db.Execute("UPDATE dbo.label_print_template SET published_version_id=@version,updated_by=@user,updated_at=DATEADD(HOUR,8,SYSUTCDATETIME()) WHERE id=@id;",
                ("@version",version),("@user",user.Id),("@id",id));
            return (await db.One("SELECT * FROM dbo.label_print_template_version WHERE id=@id;",("@id",version)))!;
        },ct);

    public Task<IReadOnlyList<JsonObject>> AssetsForVersion(long version,CancellationToken ct) =>
        Transaction<IReadOnlyList<JsonObject>>(async db=>await db.Query("""
            SELECT a.* FROM dbo.label_print_asset a JOIN dbo.label_print_template_version_asset v ON v.asset_id=a.id
            WHERE v.version_id=@version ORDER BY a.id;
            """,("@version",version)),ct);
    public Task<JsonObject> Asset(long id,CancellationToken ct)=>Transaction(async db=>
        await db.One("SELECT * FROM dbo.label_print_asset WHERE id=@id;",("@id",id))
        ??throw new ApiException(404,"PRINT_ASSET_NOT_FOUND","图片不存在。"),ct);
    public Task<JsonObject> SaveAsset(PrintAssetRequest request,string path,byte[] bytes,SessionUser user,CancellationToken ct)=>
        Transaction(async db=>
        {
            var hash=SHA256.HashData(bytes);
            var old=await db.One("SELECT * FROM dbo.label_print_asset WHERE sha256=@hash;",("@hash",hash));
            if(old is not null)return old;
            return (await db.One("""
                INSERT dbo.label_print_asset(sha256,media_type,storage_relative_path,size_bytes,width_px,height_px,created_by)
                OUTPUT inserted.* VALUES(@hash,@type,@path,@size,@w,@h,@user);
                """,("@hash",hash),("@type",request.MediaType),("@path",path),("@size",bytes.LongLength),
                ("@w",request.WidthPx),("@h",request.HeightPx),("@user",user.Id)))!;
        },ct);
}
