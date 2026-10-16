using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;
using U8Gateway.Contracts;
using U8Gateway.Contracts.TraceBack;
using U8Gateway.Persistence.Configuration;
using U8Gateway.U8.TraceBack;

namespace U8Gateway.Api.Controllers
{
    [RoutePrefix("api/v1/traceback/labels")]
    public sealed class TraceBackLabelsController : ApiController
    {
        [HttpGet]
        [Route("{code}")]
        public IHttpActionResult Get(string code)
        {
            IList<TraceBackLabelSnapshot> result = Read(new[] { code });
            TraceBackLabelSnapshot snapshot = result.First();
            if (snapshot.Status == "NOT_FOUND")
                return Error(HttpStatusCode.NotFound, "TRACEBACK_LABEL_NOT_FOUND", "未找到标签。");
            return Ok(ApiResponse<TraceBackLabelSnapshot>.Ok(snapshot, GatewayRequestContext.Id));
        }

        [HttpPost]
        [Route("snapshots")]
        public IHttpActionResult Post(TraceBackSnapshotRequest request)
        {
            if (request?.Codes == null || request.Codes.Count == 0 || request.Codes.Count > 100)
                return Error(HttpStatusCode.BadRequest, "TRACEBACK_CODES_INVALID", "条码数量无效。");
            return Ok(ApiResponse<object>.Ok(Read(request.Codes), GatewayRequestContext.Id));
        }

        [HttpPost]
        [Route("sync-events")]
        public IHttpActionResult Sync(TraceBackSyncEventRequest request)
        {
            try
            {
                try { AppRuntime.EnsureU8WriteAllowed(); }
                catch (InvalidOperationException exception)
                {
                    throw new TraceBackSyncException("TRACEBACK_WRITE_DISABLED", exception.Message);
                }
                GatewaySettings settings = AppRuntime.CurrentSettings;
                var writer = new TraceBackSyncWriter(
                    settings.SqlServer, settings.SqlDatabase, settings.SqlUser, settings.SqlPassword);
                TraceBackSyncEventResult result = writer.Apply(request);
                return Ok(ApiResponse<TraceBackSyncEventResult>.Ok(result, GatewayRequestContext.Id));
            }
            catch (TraceBackSyncException exception)
            {
                return Content(HttpStatusCode.Conflict, new ApiResponse<object>
                {
                    Success = false,
                    Code = exception.Code,
                    Message = exception.Message,
                    RequestId = GatewayRequestContext.Id,
                    ServerTime = BusinessClock.Now
                });
            }
            catch (Exception)
            {
                return Content(HttpStatusCode.InternalServerError, new ApiResponse<object>
                {
                    Success = false,
                    Code = "TRACEBACK_SYNC_FAILED",
                    Message = "追溯同步失败。",
                    RequestId = GatewayRequestContext.Id,
                    ServerTime = BusinessClock.Now
                });
            }
        }

        private IList<TraceBackLabelSnapshot> Read(IList<string> codes)
        {
            try { return Reader().GetSnapshots(codes); }
            catch (ArgumentException)
            {
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.BadRequest,
                    new ApiResponse<object>
                    {
                        Success = false, Code = "TRACEBACK_CODES_INVALID", Message = "条码数量无效。",
                        RequestId = GatewayRequestContext.Id, ServerTime = BusinessClock.Now
                    }));
            }
            catch (TraceBackContractException)
            {
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.ServiceUnavailable,
                    new ApiResponse<object>
                    {
                        Success = false, Code = "TRACEBACK_CONTRACT_INVALID", Message = "追溯数据结构不兼容。",
                        RequestId = GatewayRequestContext.Id, ServerTime = BusinessClock.Now
                    }));
            }
            catch (SqlException)
            {
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.ServiceUnavailable,
                    new ApiResponse<object>
                    {
                        Success = false, Code = "TRACEBACK_QUERY_FAILED", Message = "追溯标签查询失败。",
                        RequestId = GatewayRequestContext.Id, ServerTime = BusinessClock.Now
                    }));
            }
        }

        private static TraceBackSnapshotReader Reader()
        {
            GatewaySettings settings = AppRuntime.CurrentSettings;
            if (settings == null || string.IsNullOrWhiteSpace(settings.SqlServer) ||
                string.IsNullOrWhiteSpace(settings.SqlDatabase) || string.IsNullOrWhiteSpace(settings.SqlUser))
                throw new TraceBackContractException("Gateway 未配置。");
            return new TraceBackSnapshotReader(
                settings.SqlServer, settings.SqlDatabase, settings.SqlUser, settings.SqlPassword);
        }

        private IHttpActionResult Error(HttpStatusCode status, string code, string message) =>
            Content(status, new ApiResponse<object>
            {
                Success = false, Code = code, Message = message,
                RequestId = GatewayRequestContext.Id, ServerTime = BusinessClock.Now
            });
    }
}
