using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed record ArchiveResult(string RelativePath, string Sha256);

public sealed class OperationArchiveService(IOptionsMonitor<ArchiveOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public async Task<ArchiveResult> CreateAsync(
        string documentCode,
        string documentName,
        string action,
        string requestId,
        object payload,
        CancellationToken cancellationToken)
    {
        var root = options.CurrentValue.RootPath;
        if (string.IsNullOrWhiteSpace(root))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "ARCHIVE_NOT_CONFIGURED", "操作凭证目录尚未配置。");
        root = Path.IsPathRooted(root) ? root : Path.Combine(AppContext.BaseDirectory, root);

        var now = BusinessClock.Now;
        var dayDirectory = Path.Combine(root, now.ToString("yyyy-MM-dd"));
        var documentDirectory = Path.Combine(dayDirectory, Sanitize(documentName) + "_" + Sanitize(documentCode));
        Directory.CreateDirectory(documentDirectory);

        var content = JsonSerializer.Serialize(new
        {
            stage = "REQUEST_RECEIVED",
            requestId,
            action,
            serverTime = now,
            payload
        }, JsonOptions);

        string filePath;
        for (var sequence = 0; ; sequence++)
        {
            var suffix = sequence == 0 ? string.Empty : $"_{sequence}";
            filePath = Path.Combine(documentDirectory, $"{now:HH_mm_ss_fff}_{action}{suffix}.txt");
            try
            {
                await using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
                break;
            }
            catch (IOException) when (sequence < 100)
            {
                // 同一毫秒重名时追加序号，禁止覆盖。
            }
        }

        var relativePath = Path.GetRelativePath(root, filePath).Replace(Path.DirectorySeparatorChar, '/');
        return new ArchiveResult(relativePath, Hashing.Sha256(content));
    }

    private static string Sanitize(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value;
    }
}
