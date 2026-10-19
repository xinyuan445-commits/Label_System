using System.Security.Cryptography;
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
