using System;
using System.Net;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;
using U8Gateway.Contracts;
using U8Gateway.Core.Operations;

namespace U8Gateway.Api.Controllers
{
    [RoutePrefix("api/v1/operations")]
    public sealed class OperationsController : ApiController
    {
        [HttpGet]
        [Route("{clientRequestId}")]
        public IHttpActionResult Get(string clientRequestId)
        {
            if (string.IsNullOrWhiteSpace(clientRequestId) || clientRequestId.Length > 64)
            {
                return Error(
                    HttpStatusCode.BadRequest,
                    "INVALID_CLIENT_REQUEST_ID",
                    "clientRequestId 无效。");
            }

            OperationRecord record;
            try
            {
                record = AppRuntime.CreateOperationRegistry().Get(clientRequestId);
            }
            catch (Exception)
            {
                return Error(
                    HttpStatusCode.ServiceUnavailable,
                    "OPERATION_REGISTRY_UNAVAILABLE",
                    "操作记录暂时无法读取。");
            }

            if (record == null)
            {
                return Error(
                    HttpStatusCode.NotFound,
                    "OPERATION_NOT_FOUND",
                    "操作记录不存在。");
            }

            return Ok(ApiResponse<object>.Ok(new
            {
                record.ClientRequestId,
                record.RequestId,
                record.Environment,
                record.DocumentType,
                record.DocumentTitle,
                record.OperationType,
                record.TargetVoucherCode,
                record.Status,
                record.Code,
                record.Message,
                record.Result,
                record.CreatedAt,
                record.UpdatedAt
            }, GatewayRequestContext.Id));
        }

        private IHttpActionResult Error(
            HttpStatusCode status,
            string code,
            string message)
        {
            return Content(status, new ApiResponse<object>
            {
                Success = false,
                Code = code,
                Message = message,
                RequestId = GatewayRequestContext.Id,
                ServerTime = BusinessClock.Now
            });
        }
    }
}
