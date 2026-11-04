using System;
using System.Data.SqlClient;
using System.Net;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;
using U8Gateway.Contracts;
using U8Gateway.Contracts.Sources;
using U8Gateway.Persistence.Configuration;
using U8Gateway.U8.Sources;

namespace U8Gateway.Api.Controllers
{
    [RoutePrefix("api/v1/inventory-counts")]
    public sealed class InventoryCountsController : ApiController
    {
        [HttpGet, Route("")]
        public IHttpActionResult Search(string documentNo = null, DateTime? startDate = null,
            DateTime? endDate = null, int limit = 100)
        {
            if (startDate.HasValue && endDate.HasValue && startDate.Value.Date > endDate.Value.Date)
                return Error(HttpStatusCode.BadRequest, "DATE_RANGE_INVALID", "开始日期不能晚于结束日期。");
            try { return Ok(ApiResponse<object>.Ok(Reader().Search(documentNo, startDate, endDate, limit), GatewayRequestContext.Id)); }
            catch (SqlException) { return Error(HttpStatusCode.ServiceUnavailable, "COUNT_SOURCE_QUERY_FAILED", "盘点单查询失败。"); }
            catch (InvalidOperationException) { return Error(HttpStatusCode.Conflict, "COUNT_SOURCE_UNAVAILABLE", "盘点单读取失败，请稍后重试。"); }
        }

        [HttpGet, Route("{id:long}")]
        public IHttpActionResult Get(long id)
        {
            if (id <= 0) return Error(HttpStatusCode.BadRequest, "COUNT_SOURCE_INVALID", "请选择有效盘点单。");
            try
            {
                InventoryCountSource source = Reader().Get(id);
                return source == null ? Error(HttpStatusCode.NotFound, "COUNT_SOURCE_NOT_FOUND", "盘点单不存在。")
                    : Ok(ApiResponse<InventoryCountSource>.Ok(source, GatewayRequestContext.Id));
            }
            catch (SqlException) { return Error(HttpStatusCode.ServiceUnavailable, "COUNT_SOURCE_QUERY_FAILED", "盘点单查询失败。"); }
            catch (InvalidOperationException) { return Error(HttpStatusCode.Conflict, "COUNT_SOURCE_CHANGED", "盘点单不可用或已变化，请重新选择。"); }
        }

        [HttpGet, Route("warehouses/{code}")]
        public IHttpActionResult Warehouse(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 20)
                return Error(HttpStatusCode.BadRequest, "COUNT_WAREHOUSE_INVALID", "请选择仓库。");
            try
            {
                var result = Reader().GetWarehouse(code);
                return result == null ? Error(HttpStatusCode.NotFound, "COUNT_WAREHOUSE_NOT_FOUND", "仓库不存在或已停用。")
                    : Ok(ApiResponse<InventoryCountWarehouse>.Ok(result, GatewayRequestContext.Id));
            }
            catch (SqlException) { return Error(HttpStatusCode.ServiceUnavailable, "COUNT_WAREHOUSE_QUERY_FAILED", "仓库查询失败。"); }
            catch (InvalidOperationException) { return Error(HttpStatusCode.Conflict, "COUNT_WAREHOUSE_INVALID", "仓库货位状态需核对。"); }
        }

        private static U8InventoryCountReader Reader()
        {
            GatewaySettings s = AppRuntime.CurrentSettings;
            if (s == null || string.IsNullOrWhiteSpace(s.SqlServer) || string.IsNullOrWhiteSpace(s.SqlDatabase) ||
                string.IsNullOrWhiteSpace(s.SqlUser) || string.IsNullOrWhiteSpace(s.U8AccountId))
                throw new InvalidOperationException("U8尚未配置。");
            return new U8InventoryCountReader(s.SqlServer, s.SqlDatabase, s.SqlUser, s.SqlPassword, s.U8AccountId);
        }

        private IHttpActionResult Error(HttpStatusCode status, string code, string message) =>
            Content(status, new ApiResponse<object> { Success = false, Code = code, Message = message,
                RequestId = GatewayRequestContext.Id, ServerTime = BusinessClock.Now });
    }
}
