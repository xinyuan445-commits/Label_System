using System;
using System.Data.SqlClient;
using System.Net;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;
using U8Gateway.Contracts;
using U8Gateway.Persistence.Configuration;
using U8Gateway.U8.ReferenceData;

namespace U8Gateway.Api.Controllers
{
    [RoutePrefix("api/v1/reference-data")]
    public sealed class ReferenceDataController : ApiController
    {
        [HttpGet]
        [Route("rd-styles")]
        public IHttpActionResult GetRdStyles()
        {
            try
            {
                return Ok(ApiResponse<object>.Ok(Reader().GetRdStyles(), GatewayRequestContext.Id));
            }
            catch (SqlException)
            {
                return Error(HttpStatusCode.ServiceUnavailable,
                    "U8_REFERENCE_QUERY_FAILED", "U8 基础资料查询失败。");
            }
            catch (InvalidOperationException)
            {
                return Error(HttpStatusCode.ServiceUnavailable,
                    "U8_GATEWAY_NOT_CONFIGURED", "U8 Gateway 尚未完成配置。");
            }
        }

        [HttpGet]
        [Route("warehouse-positions")]
        public IHttpActionResult GetWarehousePositions()
        {
            try
            {
                return Ok(ApiResponse<object>.Ok(Reader().GetWarehousePositions(), GatewayRequestContext.Id));
            }
            catch (SqlException)
            {
                return Error(HttpStatusCode.ServiceUnavailable,
                    "U8_REFERENCE_QUERY_FAILED", "U8 基础资料查询失败。");
            }
            catch (InvalidOperationException)
            {
                return Error(HttpStatusCode.ServiceUnavailable,
                    "U8_GATEWAY_NOT_CONFIGURED", "U8 Gateway 尚未完成配置。");
            }
        }

        [HttpGet]
        [Route("departments")]
        public IHttpActionResult GetDepartments()
        {
            try
            {
                return Ok(ApiResponse<object>.Ok(Reader().GetDepartments(), GatewayRequestContext.Id));
            }
            catch (SqlException)
            {
                return Error(HttpStatusCode.ServiceUnavailable,
                    "U8_REFERENCE_QUERY_FAILED", "U8 基础资料查询失败。");
            }
            catch (InvalidOperationException)
            {
                return Error(HttpStatusCode.ServiceUnavailable,
                    "U8_GATEWAY_NOT_CONFIGURED", "U8 Gateway 尚未完成配置。");
            }
        }

        [HttpGet]
        [Route("document-master-data")]
        public IHttpActionResult GetDocumentMasterData()
        {
            try
            {
                return Ok(ApiResponse<object>.Ok(
                    Reader().GetDocumentMasterData(), GatewayRequestContext.Id));
            }
            catch (SqlException)
            {
                return Error(HttpStatusCode.ServiceUnavailable,
                    "U8_REFERENCE_QUERY_FAILED", "U8 基础资料查询失败。");
            }
            catch (InvalidOperationException)
            {
                return Error(HttpStatusCode.ServiceUnavailable,
                    "U8_GATEWAY_NOT_CONFIGURED", "U8 Gateway 尚未完成配置。");
            }
        }

        private static U8ReferenceDataReader Reader()
        {
            GatewaySettings settings = AppRuntime.CurrentSettings;
            if (settings == null ||
                string.IsNullOrWhiteSpace(settings.SqlServer) ||
                string.IsNullOrWhiteSpace(settings.SqlDatabase) ||
                string.IsNullOrWhiteSpace(settings.SqlUser))
                throw new InvalidOperationException();

            return new U8ReferenceDataReader(
                settings.SqlServer,
                settings.SqlDatabase,
                settings.SqlUser,
                settings.SqlPassword);
        }

        private IHttpActionResult Error(HttpStatusCode status, string code, string message)
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
