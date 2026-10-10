using System.Net;
using System.Linq;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;
using U8Gateway.Contracts;
using U8Gateway.Contracts.Metadata;

namespace U8Gateway.Api.Controllers
{
    [RoutePrefix("api/v1/document-types")]
    public sealed class DocumentTypesController : ApiController
    {
        [HttpGet]
        [Route("")]
        public IHttpActionResult GetAll()
        {
            DocumentTypeSummary[] summaries = AppRuntime.Definitions.GetAll()
                .Select(x => new DocumentTypeSummary
                {
                    ContractVersion = x.ContractVersion,
                    DocumentType = x.DocumentType,
                    Title = x.Title,
                    Status = x.Status,
                    WritePolicyStatus = x.WritePolicyStatus,
                    Capabilities = x.Capabilities
                })
                .ToArray();
            return Ok(ApiResponse<object>.Ok(
                summaries,
                GatewayRequestContext.Id));
        }

        [HttpGet]
        [Route("{documentType}/schema")]
        public IHttpActionResult Get(string documentType)
        {
            DocumentDefinition definition =
                AppRuntime.Definitions.Get(documentType);
            if (definition == null)
            {
                return Content(HttpStatusCode.NotFound, new ApiResponse<object>
                {
                    Success = false,
                    Code = "DOCUMENT_TYPE_NOT_FOUND",
                    Message = "单据类型不存在。",
                    RequestId = GatewayRequestContext.Id,
                    ServerTime = BusinessClock.Now
                });
            }
            return Ok(ApiResponse<DocumentDefinition>.Ok(
                definition,
                GatewayRequestContext.Id));
        }
    }
}
