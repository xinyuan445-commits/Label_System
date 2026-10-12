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
