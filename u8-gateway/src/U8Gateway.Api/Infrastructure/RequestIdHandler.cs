using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace U8Gateway.Api.Infrastructure
{
    internal sealed class RequestIdHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string value = request.Headers.TryGetValues("X-Request-Id", out var values)
                ? values.FirstOrDefault()
                : null;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            {
                value = Guid.NewGuid().ToString("N");
            }

            GatewayRequestContext.Id = value;
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            response.Headers.TryAddWithoutValidation("X-Request-Id", value);
            return response;
        }
    }
}
