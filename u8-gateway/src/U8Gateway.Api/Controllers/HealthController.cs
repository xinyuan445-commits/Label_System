using System.Reflection;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;
using U8Gateway.Contracts;

namespace U8Gateway.Api.Controllers
{
    [RoutePrefix("api/v1/health")]
    public sealed class HealthController : ApiController
    {
        [HttpGet]
        [Route("")]
        public IHttpActionResult Get()
        {
            return Ok(ApiResponse<object>.Ok(new
            {
                status = "running",
                configured = AppRuntime.Settings.IsConfigured,
                version = Assembly.GetExecutingAssembly().GetName().Version.ToString(),
                u8Connected = false
            }, GatewayRequestContext.Id));
        }
    }
}
