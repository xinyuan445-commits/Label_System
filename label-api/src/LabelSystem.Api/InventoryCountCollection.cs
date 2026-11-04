using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed record InventoryCountHeader(long Id, string DocumentNo, DateTime? DocumentDate,
    string WarehouseCode, string Status, string? AuditedBy, DateTime? AuditedAt, string Version, int LineCount,
    string? InCategoryCode = null, string? InCategoryName = null,
    string? OutCategoryCode = null, string? OutCategoryName = null);
public sealed record InventoryCountSourceLine(long Id, int LineNo, string MaterialCode, string MaterialName,
    string BatchNo, string WarehouseCode, string LocationCode, string UnitCode, decimal BookQuantity,
    decimal? ActualQuantity, IReadOnlyList<string> FreeItems, bool RequiresAuxiliaryUnit);
public sealed record InventoryCountSource(string AccountId, string DatabaseName, InventoryCountHeader Document,
    IReadOnlyList<InventoryCountSourceLine> Lines, string Fingerprint = "");
public sealed record InventoryCountObservation(string Code, string? Quantity);
public sealed record InventoryCountValidateRequest(string AccountId, string DatabaseName,
    string SourceFingerprint, IReadOnlyList<InventoryCountObservation> Observations);
public sealed record InventoryCountObservationResult(string Code, string Status, string Message,
    long? SourceLineId = null, string? MaterialName = null, decimal? Balance = null, long? LatestFlowId = null);
public sealed record InventoryCountValidationResult(string SourceFingerprint,
    IReadOnlyList<InventoryCountObservationResult> Items, bool CanSubmit = false);
public sealed record InventoryCountDirectValidateRequest(string AccountId, string DatabaseName, string WarehouseCode,
    IReadOnlyList<InventoryCountObservation> Observations);
public sealed record InventoryCountDirectCheck(string Code, string Status, string Message,
    InventoryObjectLine? Label = null, long? LatestFlowId = null);
public sealed record InventoryCountDirectResult(IReadOnlyList<InventoryCountDirectCheck> Items, bool CanSubmit = false);

public static partial class InventoryCountRules
{
    public const int MaximumObservations = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    [GeneratedRegex(@"^(?:0|[1-9][0-9]{0,11})(?:\.[0-9]{1,6})?$", RegexOptions.CultureInvariant)]
    private static partial Regex QuantityPattern();

    public static bool TryQuantity(string? value, out decimal quantity)
    {
        quantity = 0;
        return value is not null && QuantityPattern().IsMatch(value.Trim()) &&
            decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quantity);
    }

    public static string Fingerprint(InventoryCountSource source) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(source with { Fingerprint = "" }, JsonOptions)));

    public static void RequireSource(InventoryCountSource source, string account, string database)
    {
        if (!Same(source.AccountId, account) || !Same(source.DatabaseName, database))
            throw new ApiException(409, "COUNT_ACCOUNT_MISMATCH", "盘点账套不一致，请重新选择。");
        if (source.Document is null || source.Document.Id <= 0 || source.Document.Status != "pending" ||
            !string.IsNullOrWhiteSpace(source.Document.AuditedBy) || source.Document.AuditedAt.HasValue ||
            string.IsNullOrWhiteSpace(source.Document.Version))
            throw new ApiException(409, "COUNT_SOURCE_UNAVAILABLE", "盘点单不可用，请重新选择。");
        if (source.Lines is null || source.Lines.Count == 0 || source.Lines.Count > 5000 ||
            source.Document.LineCount != source.Lines.Count || source.Lines.Any(l => l is null || l.Id <= 0 ||
                string.IsNullOrWhiteSpace(l.MaterialCode) || string.IsNullOrWhiteSpace(l.WarehouseCode)) ||
            source.Lines.Select(l => l.Id).Distinct().Count() != source.Lines.Count)
            throw new ApiException(409, "COUNT_SOURCE_INCOMPLETE", "盘点明细不完整，请重新查询。");
    }

    public static void RequireObservationList(IReadOnlyList<InventoryCountObservation>? observations)
    {
        if (observations is null || observations.Count == 0 || observations.Count > MaximumObservations ||
            observations.Any(o => o is null || string.IsNullOrWhiteSpace(o.Code) || o.Code.Trim().Length > 100) ||
            observations.Select(o => o.Code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != observations.Count)
            throw new ApiException(400, "COUNT_OBSERVATIONS_INVALID", "盘点标签为空、重复或数量过多。");
    }

    public static InventoryCountObservationResult Match(InventoryCountSource source,
        InventoryCountObservation observation, TraceBackLabelSnapshot snapshot)
    {
        var code = observation.Code.Trim();
        if (!Same(code, snapshot.Code) || snapshot.Status is not ("AVAILABLE" or "USED_UP") ||
            snapshot.Balance is null or < 0 || snapshot.LatestFlowId is null or <= 0 ||
            snapshot.Anomalies is null || snapshot.Anomalies.Count > 0)
            return new(code, "LABEL_INVALID", "标签待核对。");
        var candidates = source.Lines.Where(line => Same(line.MaterialCode, snapshot.MaterialCode) &&
            Same(line.BatchNo, snapshot.BatchNo) && Same(line.WarehouseCode, snapshot.WarehouseCode) &&
            Same(line.LocationCode, snapshot.LocationCode)).ToArray();
        if (candidates.Length == 0) return new(code, "OUTSIDE_SOURCE", "标签不属于此盘点单。");
        if (candidates.Length > 1) return new(code, "AMBIGUOUS_SOURCE", "原单有重复明细，请核对。");
        var match = candidates[0];
        if (match.RequiresAuxiliaryUnit || match.FreeItems is null || match.FreeItems.Any(v => !string.IsNullOrWhiteSpace(v)))
            return new(code, "IDENTITY_NOT_SUPPORTED", "此明细的单位或自由项需另行核对。");
        if (string.IsNullOrWhiteSpace(match.UnitCode) || !Same(match.UnitCode, snapshot.UnitCode))
            return new(code, "UNIT_MISMATCH", "计量单位不一致。");
        if (!TryQuantity(observation.Quantity, out _))
            return new(code, "QUANTITY_REQUIRED", "请输入有效实盘数量。", match.Id, match.MaterialName);
        return new(code, "MATCHED", "", match.Id, match.MaterialName, snapshot.Balance, snapshot.LatestFlowId);
    }

    public static InventoryCountDirectCheck MatchDirect(string warehouse, InventoryCountObservation observation,
        TraceBackLabelSnapshot snapshot)
    {
        var code = observation.Code.Trim();
        if (!Same(code, snapshot.Code) || snapshot.Status is not ("AVAILABLE" or "USED_UP") ||
            snapshot.Balance is null or < 0 || snapshot.LatestFlowId is null or <= 0 ||
            snapshot.Anomalies is null || snapshot.Anomalies.Count > 0 ||
            string.IsNullOrWhiteSpace(snapshot.MaterialCode) || string.IsNullOrWhiteSpace(snapshot.UnitCode))
            return new(code, "LABEL_INVALID", "标签待核对。");
        if (!Same(warehouse, snapshot.WarehouseCode)) return new(code, "OUTSIDE_WAREHOUSE", "标签不属于所选仓库。");
        if (!TryQuantity(observation.Quantity, out _)) return new(code, "QUANTITY_REQUIRED", "请输入有效实盘数量。");
        return new(code, "MATCHED", "", new(snapshot.Code, "PRODUCT_LABEL", snapshot.MaterialCode,
            snapshot.MaterialName ?? snapshot.MaterialCode, snapshot.BatchNo ?? "", snapshot.UnitCode,
            snapshot.UnitName ?? "", snapshot.WarehouseCode!, snapshot.WarehouseName ?? "",
            snapshot.LocationCode ?? "", snapshot.LocationName ?? "", snapshot.Balance.Value, null,
            snapshot.Status == "USED_UP" ? "USED" : "ACTIVE", ""), snapshot.LatestFlowId);
    }

    // The old generic create path can otherwise fail only AFTER creating an ERP voucher.
    public static void RejectLegacyWrite(string documentType)
    {
        if (Same(documentType, "inventory-count"))
            throw new ApiException(409, "COUNT_WRITE_NOT_READY", "盘点上传ERP暂未开放。");
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
}

public sealed partial class U8GatewayClient
{
    public async Task<InventoryCountSource> GetInventoryCountSourceAsync(long id, CancellationToken ct)
    {
        using var message = CreateRequest(HttpMethod.Get, $"/api/v1/inventory-counts/{id}");
        using var response = await Client().SendAsync(message, ct);
        var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<InventoryCountSource>>(JsonOptions, ct);
        if (!response.IsSuccessStatusCode || envelope?.Success != true || envelope.Data is null)
            throw new ApiException(response.IsSuccessStatusCode ? 502 : (int)response.StatusCode,
                envelope?.Code ?? "COUNT_SOURCE_QUERY_FAILED", envelope?.Message ?? "盘点单读取失败。");
        return envelope.Data;
    }
}

public sealed class InventoryCountCollectionService(U8GatewayClient gateway, IOptionsMonitor<LabelSystemOptions> settings)
{
    public async Task<InventoryCountDirectResult> ValidateDirectAsync(InventoryCountDirectValidateRequest request, CancellationToken ct)
    {
        InventoryCountRules.RequireObservationList(request.Observations);
        if (string.IsNullOrWhiteSpace(request.WarehouseCode) || request.WarehouseCode.Length > 20)
            throw new ApiException(400, "COUNT_WAREHOUSE_REQUIRED", "请选择仓库。");
        void RequireAccount()
        {
            var current = settings.CurrentValue;
            if (!string.Equals(request.AccountId, current.ErpAccountId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.DatabaseName, current.ErpDatabaseName, StringComparison.OrdinalIgnoreCase))
                throw new ApiException(409, "COUNT_ACCOUNT_MISMATCH", "盘点账套不一致，请重新打开单据。");
        }
        RequireAccount();
        var snapshots = new Dictionary<string, TraceBackLabelSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in request.Observations.Chunk(100))
            foreach (var snapshot in await gateway.GetTraceBackSnapshotsAsync(batch.Select(row => row.Code.Trim()).ToArray(), ct))
                if (!snapshots.TryAdd(snapshot.Code, snapshot))
                    throw new ApiException(502, "COUNT_SNAPSHOT_INVALID", "标签查询返回重复，请重试。");
        RequireAccount();
        return new(request.Observations.Select(row => snapshots.TryGetValue(row.Code.Trim(), out var snapshot)
            ? InventoryCountRules.MatchDirect(request.WarehouseCode.Trim(), row, snapshot)
            : new InventoryCountDirectCheck(row.Code.Trim(), "NOT_FOUND", "未找到标签。")).ToArray());
    }

    public async Task<InventoryCountSource> GetSourceAsync(long id, CancellationToken ct)
    {
        if (id <= 0) throw new ApiException(400, "COUNT_SOURCE_INVALID", "请选择盘点单。");
        var source = await gateway.GetInventoryCountSourceAsync(id, ct);
        var options = settings.CurrentValue;
        InventoryCountRules.RequireSource(source, options.ErpAccountId, options.ErpDatabaseName);
        if (source.Document.Id != id) throw new ApiException(502, "COUNT_SOURCE_INVALID", "盘点单返回不一致。");
        return source with { Fingerprint = InventoryCountRules.Fingerprint(source) };
    }

    public async Task<InventoryCountValidationResult> ValidateAsync(long id, InventoryCountValidateRequest request,
        CancellationToken ct)
    {
        InventoryCountRules.RequireObservationList(request.Observations);
        var source = await GetSourceAsync(id, ct);
        if (!string.Equals(request.AccountId, source.AccountId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.DatabaseName, source.DatabaseName, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(409, "COUNT_ACCOUNT_MISMATCH", "盘点账套不一致，请重新选择。");
        if (request.SourceFingerprint != source.Fingerprint)
            throw new ApiException(409, "COUNT_SOURCE_CHANGED", "原盘点单已变化，采集记录已保留。");
        var snapshots = new Dictionary<string, TraceBackLabelSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in request.Observations.Chunk(100))
        {
            var response = await gateway.GetTraceBackSnapshotsAsync(batch.Select(o => o.Code.Trim()).ToArray(), ct);
            foreach (var snapshot in response)
                if (!snapshots.TryAdd(snapshot.Code, snapshot))
                    throw new ApiException(502, "COUNT_SNAPSHOT_INVALID", "标签查询返回重复，请重试。");
        }
        var after = await GetSourceAsync(id, ct);
        if (after.Fingerprint != source.Fingerprint)
            throw new ApiException(409, "COUNT_SOURCE_CHANGED", "原盘点单已变化，采集记录已保留。");
        return new(source.Fingerprint, request.Observations.Select(o => snapshots.TryGetValue(o.Code.Trim(), out var snapshot)
            ? InventoryCountRules.Match(source, o, snapshot)
            : new InventoryCountObservationResult(o.Code.Trim(), "NOT_FOUND", "未找到标签。")).ToArray());
    }
}

public static class InventoryCountCollectionEndpoints
{
    public static void MapInventoryCountCollection(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory-counts");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var h = invocation.HttpContext;
            if (!await h.RequestServices.GetRequiredService<UserRepository>().HasFunctionAccessAsync(
                    RequestContext.User(h).Id, "inventory-count", h.RequestAborted))
                throw new ApiException(403, "FUNCTION_FORBIDDEN", "无权操作。");
            h.Response.Headers.CacheControl = "private, no-store";
            return await next(invocation);
        });
        group.MapGet("/capabilities", (HttpContext h, IOptionsMonitor<LabelSystemOptions> options) =>
            ApiResults.Ok(h, new { collectionEnabled = true, directDraftEnabled = true, canSubmit = false,
                accountId = options.CurrentValue.ErpAccountId, databaseName = options.CurrentValue.ErpDatabaseName,
                maximumObservations = InventoryCountRules.MaximumObservations, offlineColdStart = false }));
        group.MapGet("", async (HttpContext h, U8GatewayClient gateway, string? documentNo,
            DateOnly? startDate, DateOnly? endDate, CancellationToken ct) =>
        {
            if (startDate > endDate) throw new ApiException(400, "DATE_RANGE_INVALID", "开始日期不能晚于结束日期。");
            var query = "?limit=100&documentNo=" + Uri.EscapeDataString(documentNo?.Trim() ?? "");
            if (startDate.HasValue) query += "&startDate=" + startDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (endDate.HasValue) query += "&endDate=" + endDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var response = await gateway.ForwardGetAsync("/api/v1/inventory-counts" + query, ct);
            return Results.Content(response.Content, response.ContentType, statusCode: response.StatusCode);
        });
        group.MapGet("/{id:long}", async (HttpContext h, long id, InventoryCountCollectionService service, CancellationToken ct) =>
            ApiResults.Ok(h, await service.GetSourceAsync(id, ct)));
        group.MapPost("/{id:long}/validate", async (HttpContext h, long id, InventoryCountValidateRequest request,
            InventoryCountCollectionService service, CancellationToken ct) => ApiResults.Ok(h, await service.ValidateAsync(id, request, ct)));
        group.MapPost("/validate-direct", async (HttpContext h, InventoryCountDirectValidateRequest request,
            InventoryCountCollectionService service, CancellationToken ct) => ApiResults.Ok(h, await service.ValidateDirectAsync(request, ct)));
    }
}
