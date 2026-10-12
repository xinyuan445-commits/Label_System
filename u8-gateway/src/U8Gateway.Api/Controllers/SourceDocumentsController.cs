using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;
using U8Gateway.Contracts;
using U8Gateway.Contracts.Sources;
using U8Gateway.Persistence.Configuration;
using U8Gateway.U8.Sources;

namespace U8Gateway.Api.Controllers
{
    [RoutePrefix("api/v1/source-documents")]
    public sealed class SourceDocumentsController : ApiController
    {
        [HttpGet]
        [Route("")]
        public IHttpActionResult Search(
            string sourceType,
            string keyword = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            int limit = 50)
        {
            if (string.IsNullOrWhiteSpace(sourceType))
            {
                return Error(HttpStatusCode.BadRequest, "SOURCE_TYPE_REQUIRED", "请选择源单类型。");
            }
            if (startDate.HasValue && endDate.HasValue && startDate.Value.Date > endDate.Value.Date)
            {
                return Error(HttpStatusCode.BadRequest, "DATE_RANGE_INVALID", "开始日期不能晚于结束日期。");
            }
            if (!U8SourceDocumentReader.IsSupportedSourceType(sourceType))
            {
                return Error(HttpStatusCode.Conflict, "SOURCE_TYPE_NOT_READY", "该源单类型尚未开放。");
            }

            try
            {
                SourceDocumentSearchResult result = Reader().Search(
                    sourceType,
                    keyword,
                    startDate,
                    endDate,
                    limit);
                return Ok(ApiResponse<SourceDocumentSearchResult>.Ok(
                    result,
                    GatewayRequestContext.Id));
            }
            catch (SqlException)
            {
                return Error(HttpStatusCode.ServiceUnavailable, "U8_SOURCE_QUERY_FAILED", "U8 源单查询失败。");
            }
            catch (InvalidOperationException)
            {
                return Error(HttpStatusCode.Conflict, "SOURCE_TYPE_NOT_READY", "该源单类型尚未开放。");
            }
        }

        [HttpPost]
        [Route("lines")]
        public IHttpActionResult Lines(SourceDocumentLinesRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SourceType))
            {
                return Error(HttpStatusCode.BadRequest, "SOURCE_TYPE_REQUIRED", "请选择源单类型。");
            }
            if (!U8SourceDocumentReader.IsSupportedSourceType(request.SourceType))
            {
                return Error(HttpStatusCode.Conflict, "SOURCE_TYPE_NOT_READY", "该源单类型尚未开放。");
            }
            int[] ids = (request.SourceDocumentIds ?? new List<int>())
                .Where(x => x > 0)
                .Distinct()
                .ToArray();
            if (ids.Length == 0 || ids.Length > 20)
            {
                return Error(HttpStatusCode.BadRequest, "SOURCE_DOCUMENTS_INVALID", "请选择有效的源单。");
            }

            try
            {
                IList<SourceDocumentLine> result = Reader().GetLines(request.SourceType, ids);
                if (result.Count == 0)
                {
                    return Error(HttpStatusCode.NotFound, "SOURCE_LINES_NOT_FOUND", "未找到可用的源单明细。");
                }
                return Ok(ApiResponse<IList<SourceDocumentLine>>.Ok(
                    result,
                    GatewayRequestContext.Id));
            }
            catch (SqlException)
            {
                return Error(HttpStatusCode.ServiceUnavailable, "U8_SOURCE_QUERY_FAILED", "U8 源单查询失败。");
            }
            catch (InvalidOperationException)
            {
                return Error(HttpStatusCode.Conflict, "SOURCE_TYPE_NOT_READY", "该源单类型尚未开放。");
            }
        }

        [HttpPost]
        [Route("material-requirements")]
        public IHttpActionResult Requirements(MaterialRequirementRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SourceType))
            {
                return Error(HttpStatusCode.BadRequest, "SOURCE_TYPE_REQUIRED", "请选择源单类型。");
            }
            if (!string.Equals(request.SourceType, SourceDocumentTypeCodes.ProductionOrder,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Error(HttpStatusCode.Conflict, "SOURCE_TYPE_NOT_READY", "该源单类型尚未开放。");
            }
            int[] ids = (request.SourceLineIds ?? new List<int>())
                .Where(x => x > 0)
                .Distinct()
                .ToArray();
            if (ids.Length == 0 || ids.Length > 1000)
            {
                return Error(HttpStatusCode.BadRequest, "SOURCE_LINES_INVALID", "请选择有效的源单明细。");
            }

            try
            {
                IList<MaterialRequirementLine> result =
                    Reader().GetProductionRequirements(ids);
                if (result.Count == 0)
                {
                    return Error(HttpStatusCode.NotFound, "MATERIAL_REQUIREMENTS_NOT_FOUND", "未找到可领用材料。");
                }
                return Ok(ApiResponse<IList<MaterialRequirementLine>>.Ok(
                    result,
                    GatewayRequestContext.Id));
            }
            catch (SqlException)
            {
                return Error(HttpStatusCode.ServiceUnavailable, "U8_SOURCE_QUERY_FAILED", "U8 源单查询失败。");
            }
        }

        private static U8SourceDocumentReader Reader()
        {
            GatewaySettings settings = AppRuntime.CurrentSettings;
            if (settings == null ||
                string.IsNullOrWhiteSpace(settings.SqlServer) ||
                string.IsNullOrWhiteSpace(settings.SqlDatabase) ||
                string.IsNullOrWhiteSpace(settings.SqlUser))
            {
                throw new InvalidOperationException("U8 Gateway 尚未完成数据库配置。");
            }
            return new U8SourceDocumentReader(
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
