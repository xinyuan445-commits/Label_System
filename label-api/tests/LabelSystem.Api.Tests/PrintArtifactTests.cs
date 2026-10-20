using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json.Nodes;
using LabelSystem.Api;
using Microsoft.Extensions.Options;
using Xunit;

namespace LabelSystem.Api.Tests;

public sealed class PrintArtifactTests
{
    [Fact]
    public async Task ConcurrentIdenticalArtifactsAreAtomicAndTamperingBlocksReads()
    {
        var folder=Path.Combine(Path.GetTempPath(),"label-print-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var artifacts=new PrintArtifacts(new FixedOptions(new PrintingOptions{RootPath=folder}),new NoNetwork());
            byte[] bytes=new byte[100000];Random.Shared.NextBytes(bytes);
            var paths=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>artifacts.Store("pages",bytes,"pdf",default)));
            Assert.Single(paths.Distinct());
            Assert.Equal(bytes,await artifacts.Read(paths[0],Convert.ToHexString(SHA256.HashData(bytes)),default));
            Assert.Empty(Directory.GetFiles(folder,"*.tmp-*",SearchOption.AllDirectories));
            await File.WriteAllTextAsync(artifacts.Resolve(paths[0]),"changed");
            await Assert.ThrowsAsync<ApiException>(()=>artifacts.Read(paths[0],Convert.ToHexString(SHA256.HashData(bytes)),default));
            Assert.Throws<InvalidOperationException>(()=>artifacts.Resolve("../outside"));
        }
        finally
        {
            // This exact GUID directory is created and owned by this test.
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),Path.GetFullPath(folder),StringComparison.OrdinalIgnoreCase);
            Directory.Delete(folder,true);
        }
    }

    [Fact]
    public void PdfFilenameUsesDocumentLineAndBarcodeFinalSegment()
    {
        var snapshot=new PrintSnapshot(1,"888|MAT|B01|2.00|2610200042","different", "2","EA",
            "MAT","物料","B01","WH","P1",null,null,null,1,null,7,"U8_BILL:CLCK","1","DBD/26103204","9",
            3,"2026-10-20T08:00:00+08:00",new JsonObject{{"行号","9"}});
        var output=new PrintOutput(Guid.NewGuid(),11,"FIRST","READY",null,snapshot);
        Assert.Equal("DBD_26103204_9_2610200042.pdf",PrintArtifacts.BusinessFilename(output));
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("DBD_26103204_9_2610200042.pdf",PrintArtifacts.BusinessFilename(output,used));
        Assert.Equal("DBD_26103204_9_2610200042_2.pdf",PrintArtifacts.BusinessFilename(output,used));
    }

    [Fact]
    public async Task PdfReprintReusesVerifiedFirstPrintWithoutRenderer()
    {
        var folder=Path.Combine(Path.GetTempPath(),"label-print-reuse-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var artifacts=new PrintArtifacts(new FixedOptions(new PrintingOptions{RootPath=folder}),new NoNetwork());
            var sourceJobId=Guid.NewGuid();
            var sourceOutputId=Guid.NewGuid();
            var pdf="%PDF-1.4\narchived-label"u8.ToArray();
            var pdfHash=Convert.ToHexString(SHA256.HashData(pdf));
            var pdfPath=await artifacts.Store(sourceJobId.ToString("N"),pdf,"pdf",default);
            var snapshot=Snapshot();
            var sourceManifest=new JsonObject
            {
                ["widthMm"]=80,["heightMm"]=50,["templateVersionId"]=3,["templateHash"]="TEMPLATE",
                ["fontManifest"]=new JsonObject{{"version","fonts/1"}},
                ["pages"]=new JsonArray(new JsonObject
                {
                    ["outputId"]=sourceOutputId.ToString(),["file"]=sourceOutputId.ToString("N")+".pdf",
                    ["filename"]="original.pdf",["sha256"]=pdfHash,["pdfPath"]=pdfPath,["pdfSha256"]=pdfHash
                })
            };
            var source=new ArchivedPrintOutput(sourceJobId,sourceOutputId,"Microsoft Print to PDF",3,snapshot,sourceManifest);
            var outputs=Enumerable.Range(0,2).Select(i=>
                new PrintOutput(Guid.NewGuid(),i+1,"REPRINT","PREPARING",null,snapshot)).ToArray();
            var job=new PrintJob(Guid.NewGuid(),7,"user","PREPARING","PENDING",Guid.NewGuid(),
                "Microsoft Print to PDF",3,"01",null,null,outputs);

            var manifest=await artifacts.Reuse(job,source,"dev|888",default);

            Assert.NotNull(manifest);
            Assert.Equal(sourceJobId.ToString(),manifest!["reusedFromJobId"]?.ToString());
            Assert.Equal(2,manifest["pages"]!.AsArray().Count);
            var bundle=await artifacts.Read(manifest["bundlePath"]!.ToString(),manifest["bundleSha256"]!.ToString(),default);
            using var archive=new ZipArchive(new MemoryStream(bundle),ZipArchiveMode.Read);
            Assert.Equal(3,archive.Entries.Count);
            Assert.Equal(2,archive.Entries.Count(entry=>entry.Name.EndsWith(".pdf",StringComparison.Ordinal)));
            Assert.All(archive.Entries.Where(entry=>entry.Name.EndsWith(".pdf",StringComparison.Ordinal)),entry=>
            {
                using var stream=entry.Open();
                using var copy=new MemoryStream();
                stream.CopyTo(copy);
                Assert.Equal(pdf,copy.ToArray());
            });
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),Path.GetFullPath(folder),StringComparison.OrdinalIgnoreCase);
            Directory.Delete(folder,true);
        }
    }

    [Fact]
    public async Task PdfOnlyArchiveFallsBackToRendererForPhysicalPrinter()
    {
        var folder=Path.Combine(Path.GetTempPath(),"label-print-reuse-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var artifacts=new PrintArtifacts(new FixedOptions(new PrintingOptions{RootPath=folder}),new NoNetwork());
            var sourceJobId=Guid.NewGuid();
            var sourceOutputId=Guid.NewGuid();
            var pdf="%PDF-1.4\narchived-label"u8.ToArray();
            var hash=Convert.ToHexString(SHA256.HashData(pdf));
            var path=await artifacts.Store(sourceJobId.ToString("N"),pdf,"pdf",default);
            var snapshot=Snapshot();
            var source=new ArchivedPrintOutput(sourceJobId,sourceOutputId,"Microsoft Print to PDF",3,snapshot,new JsonObject
            {
                ["widthMm"]=80,["heightMm"]=50,["templateVersionId"]=3,["templateHash"]="TEMPLATE",
                ["fontManifest"]=new JsonObject{{"version","fonts/1"}},
                ["pages"]=new JsonArray(new JsonObject{{"outputId",sourceOutputId.ToString()},{"pdfPath",path},{"pdfSha256",hash}})
            });
            var output=new PrintOutput(Guid.NewGuid(),1,"REPRINT","PREPARING",null,snapshot);
            var job=new PrintJob(Guid.NewGuid(),7,"user","PREPARING","PENDING",Guid.NewGuid(),
                "Zebra",3,"01",null,null,[output]);

            Assert.Null(await artifacts.Reuse(job,source,"dev|888",default));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),Path.GetFullPath(folder),StringComparison.OrdinalIgnoreCase);
            Directory.Delete(folder,true);
        }
    }

    private static PrintSnapshot Snapshot() => new(1,"888|MAT|B01|2.00|2610200042","2610200042","2","EA",
        "MAT","物料","B01","WH","P1",null,null,null,1,null,7,"U8_BILL:CCPRK","1","CCPRKD20264985","1",
        3,"2026-10-20T08:00:00+08:00",new JsonObject{{"行号","1"},{"单位","张"}});
    private sealed class FixedOptions(PrintingOptions value):IOptionsMonitor<PrintingOptions>
    {
        public PrintingOptions CurrentValue=>value;
        public PrintingOptions Get(string? name)=>value;
        public IDisposable? OnChange(Action<PrintingOptions,string?> listener)=>null;
    }
    private sealed class NoNetwork:IHttpClientFactory
    {
        public HttpClient CreateClient(string name)=>throw new InvalidOperationException("Network is forbidden in artifact tests.");
    }
}
