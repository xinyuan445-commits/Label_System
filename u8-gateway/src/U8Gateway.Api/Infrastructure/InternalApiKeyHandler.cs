using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using U8Gateway.Contracts;

namespace U8Gateway.Api.Infrastructure
{
    internal sealed class InternalApiKeyHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri.AbsolutePath;
            if (path.Equals("/api/v1/health", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
            {
                return base.SendAsync(request, cancellationToken);
            }

            string expected = AppRuntime.Settings?.Current?.InternalApiKey;
            if (string.IsNullOrWhiteSpace(expected))
            {
                return Task.FromResult(Error(
                    request,
                    HttpStatusCode.ServiceUnavailable,
                    "NOT_CONFIGURED",
                    "服务尚未完成配置。"));
            }

            string supplied = request.Headers.TryGetValues("X-API-Key", out var values)
                ? values.FirstOrDefault()
                : null;
            if (!FixedTimeEquals(expected, supplied))
            {
                return Task.FromResult(Error(
                    request,
                    HttpStatusCode.Unauthorized,
                    "INVALID_API_KEY",
                    "X-API-Key 无效。"));
            }

            return base.SendAsync(request, cancellationToken);
        }

        private static HttpResponseMessage Error(
            HttpRequestMessage request,
            HttpStatusCode status,
            string code,
            string message)
        {
            return request.CreateResponse(status, new ApiResponse<object>
            {
                Success = false,
                Code = code,
                Message = message,
                RequestId = GatewayRequestContext.Id,
                ServerTime = BusinessClock.Now
            });
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null)
            {
                return false;
            }
            byte[] a = Encoding.UTF8.GetBytes(left);
            byte[] b = Encoding.UTF8.GetBytes(right);
            if (a.Length != b.Length)
            {
                return false;
            }
            int difference = 0;
            for (int index = 0; index < a.Length; index++)
            {
                difference |= a[index] ^ b[index];
            }
            return difference == 0;
        }
    }
}
