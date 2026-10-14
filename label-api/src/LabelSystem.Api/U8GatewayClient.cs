using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed record ProxyResponse(int StatusCode, string Content, string ContentType);

public sealed class U8GatewayClient(
    IHttpClientFactory clientFactory,
    IOptionsMonitor<U8GatewayOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<MaterialRequirementLine>> GetMaterialRequirementsAsync(
        IReadOnlyCollection<int> sourceLineIds,
        CancellationToken cancellationToken)
    {
        var request = new MaterialRequirementRequest("production-order", sourceLineIds.ToArray());
        using var message = CreateRequest(HttpMethod.Post, "/api/v1/source-documents/material-requirements");
        message.Content = JsonContent.Create(request, options: JsonOptions);
        using var response = await Client().SendAsync(message, cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<List<MaterialRequirementLine>>>(
            JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Success != true || envelope.Data is null)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                envelope?.Code ?? "U8_SOURCE_QUERY_FAILED",
                envelope?.Message ?? "U8 源单查询失败。");
        }
        return envelope.Data;
    }

    public async Task<IReadOnlyList<SourceDocumentLine>> GetSourceDocumentLinesAsync(
        string sourceType,
        IReadOnlyCollection<int> sourceDocumentIds,
        CancellationToken cancellationToken)
    {
        var request = new SourceDocumentLinesRequest(sourceType, sourceDocumentIds.ToArray());
        using var message = CreateRequest(HttpMethod.Post, "/api/v1/source-documents/lines");
        message.Content = JsonContent.Create(request, options: JsonOptions);
        using var response = await Client().SendAsync(message, cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<List<SourceDocumentLine>>>(
            JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Success != true || envelope.Data is null)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                envelope?.Code ?? "U8_SOURCE_QUERY_FAILED",
                envelope?.Message ?? "U8 源单查询失败。");
        }
        return envelope.Data;
    }

    public async Task<IReadOnlyList<RdStyleSourceItem>> GetRdStylesAsync(
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Get, "/api/v1/reference-data/rd-styles");
        using var response = await Client().SendAsync(message, cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<List<RdStyleSourceItem>>>(
            JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Success != true || envelope.Data is null)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                envelope?.Code ?? "U8_REFERENCE_QUERY_FAILED",
                envelope?.Message ?? "U8 基础资料查询失败。");
        }
        return envelope.Data;
    }

    public async Task<IReadOnlyList<WarehousePositionSourceItem>> GetWarehousePositionsAsync(
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Get, "/api/v1/reference-data/warehouse-positions");
        using var response = await Client().SendAsync(message, cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<List<WarehousePositionSourceItem>>>(
            JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Success != true || envelope.Data is null)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                envelope?.Code ?? "U8_REFERENCE_QUERY_FAILED",
                envelope?.Message ?? "U8 基础资料查询失败。");
        }
        return envelope.Data;
    }

    public async Task<IReadOnlyList<DepartmentSourceItem>> GetDepartmentsAsync(
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Get, "/api/v1/reference-data/departments");
        using var response = await Client().SendAsync(message, cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<List<DepartmentSourceItem>>>(
            JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Success != true || envelope.Data is null)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                envelope?.Code ?? "U8_REFERENCE_QUERY_FAILED",
                envelope?.Message ?? "U8 基础资料查询失败。");
        }
        return envelope.Data;
    }

    public async Task<IReadOnlyList<MaterialSourceItem>> SearchMaterialsAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken)
    {
        var path = "/api/v1/reference-data/materials?keyword=" +
                   Uri.EscapeDataString(keyword?.Trim() ?? string.Empty) +
                   "&limit=" + Math.Clamp(limit, 1, 100);
        using var message = CreateRequest(HttpMethod.Get, path);
        using var response = await Client().SendAsync(message, cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<List<MaterialSourceItem>>>(
            JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Success != true || envelope.Data is null)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                envelope?.Code ?? "U8_REFERENCE_QUERY_FAILED",
                envelope?.Message ?? "U8 物料查询失败。");
        }
        return envelope.Data;
    }

    public async Task<DocumentMasterDataSource> GetDocumentMasterDataAsync(
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Get, "/api/v1/reference-data/document-master-data");
        using var response = await Client().SendAsync(message, cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<DocumentMasterDataSource>>(
            JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Success != true || envelope.Data is null)
        {
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                envelope?.Code ?? "U8_REFERENCE_QUERY_FAILED",
                envelope?.Message ?? "U8 基础资料查询失败。");
        }
        return envelope.Data;
    }

    public async Task ValidateDocumentAsync(
        string documentType,
        GatewayDocumentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = CreateRequest(
                HttpMethod.Post,
                $"/api/v1/documents/{Uri.EscapeDataString(documentType)}/validate?operation=add");
            message.Content = JsonContent.Create(request, options: JsonOptions);
            using var response = await Client().SendAsync(message, cancellationToken);
            var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<JsonElement>>(
                JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode && envelope?.Success == true) return;
            throw new GatewayWriteException(
                (int)response.StatusCode,
                envelope?.Code ?? "U8_DOCUMENT_VALIDATION_FAILED",
                ValidationMessage(envelope?.Data,
                    envelope?.Message ?? "U8 单据校验失败。"));
        }
        catch (GatewayWriteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayWriteException(
                StatusCodes.Status504GatewayTimeout,
                "U8_VALIDATION_TIMEOUT",
                "U8 校验超时，请稍后重试。");
        }
        catch (HttpRequestException)
        {
            throw new GatewayWriteException(
                StatusCodes.Status503ServiceUnavailable,
                "U8_GATEWAY_UNAVAILABLE",
                "U8 服务暂时不可用。");
        }
        catch (JsonException)
        {
            throw new GatewayWriteException(
                StatusCodes.Status502BadGateway,
                "U8_GATEWAY_RESPONSE_INVALID",
                "U8 服务返回异常。");
        }
    }

    public async Task<GatewayDocumentResult> CreateDocumentAsync(
        string documentType,
        GatewayDocumentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = CreateRequest(
                HttpMethod.Post,
                $"/api/v1/documents/{Uri.EscapeDataString(documentType)}");
            message.Content = JsonContent.Create(request, options: JsonOptions);
            using var response = await Client().SendAsync(message, cancellationToken);
            var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<GatewayDocumentResult>>(
                JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode && envelope?.Success == true &&
                envelope.Data is { Confirmed: true } result)
                return result;

            var code = envelope?.Code ?? "U8_DOCUMENT_CREATE_FAILED";
            throw new GatewayWriteException(
                (int)response.StatusCode,
                code,
                envelope?.Message ?? "U8 单据新增失败。",
                IsUnknownResult(code, (int)response.StatusCode));
        }
        catch (GatewayWriteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayWriteException(
                StatusCodes.Status504GatewayTimeout,
                "U8_RESULT_UNKNOWN",
                "U8 请求超时，需要核对。",
                true);
        }
        catch (HttpRequestException)
        {
            throw new GatewayWriteException(
                StatusCodes.Status503ServiceUnavailable,
                "U8_RESULT_UNKNOWN",
                "U8 连接中断，需要核对。",
                true);
        }
    }

    public async Task<GatewayDocumentResult> GetDocumentAsync(
        string documentType,
        string voucherCode,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = CreateRequest(
                HttpMethod.Get,
                $"/api/v1/documents/{Uri.EscapeDataString(documentType)}/{Uri.EscapeDataString(voucherCode)}");
            using var response = await Client().SendAsync(message, cancellationToken);
            var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<GatewayDocumentResult>>(
                JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode && envelope?.Success == true &&
                envelope.Data is { Confirmed: true } result)
                return result;
            throw new GatewayWriteException(
                (int)response.StatusCode,
                envelope?.Code ?? "U8_DOCUMENT_QUERY_FAILED",
                envelope?.Message ?? "U8 单据回查失败。",
                true);
        }
        catch (GatewayWriteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayWriteException(
                StatusCodes.Status504GatewayTimeout,
                "U8_DOCUMENT_QUERY_TIMEOUT",
                "U8 单据回查超时，需要核对。",
                true);
        }
        catch (HttpRequestException)
        {
            throw new GatewayWriteException(
                StatusCodes.Status503ServiceUnavailable,
                "U8_GATEWAY_UNAVAILABLE",
                "U8 服务暂时不可用。",
                true);
        }
        catch (JsonException)
        {
            throw new GatewayWriteException(
                StatusCodes.Status502BadGateway,
                "U8_GATEWAY_RESPONSE_INVALID",
                "U8 服务返回异常。",
                true);
        }
    }

    public async Task<GatewayDocumentResult> VerifyDocumentAsync(
        string documentType,
        string voucherCode,
        GatewayDocumentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = CreateRequest(
                HttpMethod.Post,
                $"/api/v1/documents/{Uri.EscapeDataString(documentType)}/{Uri.EscapeDataString(voucherCode)}/verify");
            message.Content = JsonContent.Create(request, options: JsonOptions);
            using var response = await Client().SendAsync(message, cancellationToken);
            var envelope = await response.Content.ReadFromJsonAsync<GatewayEnvelope<GatewayDocumentResult>>(
                JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode && envelope?.Success == true &&
                envelope.Data is { Confirmed: true } result)
                return result;

            var code = envelope?.Code ?? "U8_DOCUMENT_APPROVAL_FAILED";
            throw new GatewayWriteException(
                (int)response.StatusCode,
                code,
                envelope?.Message ?? "U8 单据审核失败。",
                envelope?.Data?.OutcomeKnown == false || IsUnknownResult(code, (int)response.StatusCode));
        }
        catch (GatewayWriteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayWriteException(
                StatusCodes.Status504GatewayTimeout,
                "U8_APPROVAL_RESULT_UNKNOWN",
                "U8 审核请求超时，需要核对。",
                true);
        }
        catch (HttpRequestException)
        {
            throw new GatewayWriteException(
                StatusCodes.Status503ServiceUnavailable,
                "U8_APPROVAL_RESULT_UNKNOWN",
                "U8 审核连接中断，需要核对。",
                true);
        }
    }

    public async Task<ProxyResponse> ForwardGetAsync(
        string relativePathAndQuery,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, relativePathAndQuery);
        using var response = await Client().SendAsync(request, cancellationToken);
        return await ToProxyResponseAsync(response, cancellationToken);
    }

    public async Task<ProxyResponse> ForwardPostAsync(
        string relativePath,
        string body,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, relativePath);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Client().SendAsync(request, cancellationToken);
        return await ToProxyResponseAsync(response, cancellationToken);
    }

    private HttpClient Client()
    {
        var current = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(current.BaseUrl) || string.IsNullOrWhiteSpace(current.ApiKey))
            throw new ApiException(StatusCodes.Status503ServiceUnavailable,
                "U8_GATEWAY_NOT_CONFIGURED", "U8 服务尚未配置。");
        var client = clientFactory.CreateClient(nameof(U8GatewayClient));
        client.BaseAddress = new Uri(current.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        return client;
    }

    private static bool IsUnknownResult(string code, int statusCode) =>
        statusCode >= 500 ||
        code.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("RECONCILIATION", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("NOT_CONFIRMED", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("IN_PROGRESS", StringComparison.OrdinalIgnoreCase);

    private static string ValidationMessage(JsonElement? data, string fallback)
    {
        if (data is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("issues", out var issues) ||
            issues.ValueKind != JsonValueKind.Array) return fallback;
        var issue = issues.EnumerateArray().FirstOrDefault();
        if (issue.ValueKind != JsonValueKind.Object) return fallback;
        var path = issue.TryGetProperty("path", out var pathValue) ? pathValue.ToString() : string.Empty;
        var message = issue.TryGetProperty("message", out var messageValue)
            ? messageValue.ToString() : string.Empty;
        return string.IsNullOrWhiteSpace(message) ? fallback :
            string.IsNullOrWhiteSpace(path) ? message : $"{path}：{message}";
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var current = options.CurrentValue;
        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        request.Headers.TryAddWithoutValidation("X-API-Key", current.ApiKey);
        return request;
    }

    private static async Task<ProxyResponse> ToProxyResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";
        return new ProxyResponse((int)response.StatusCode, content, contentType);
    }
}
