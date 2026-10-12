using System;
using System.Data.SqlClient;
using System.Net;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;
using U8Gateway.Contracts;
using U8Gateway.Contracts.Approvals;
using U8Gateway.Persistence.Configuration;
using U8Gateway.U8.Approvals;

namespace U8Gateway.Api.Controllers
{
    [RoutePrefix("api/v1/approval-documents")]
    public sealed class ApprovalDocumentsController : ApiController
    {
        [HttpGet, Route("")]
        public IHttpActionResult Search(string documentType, string status = "pending",
            string documentNo = null, DateTime? startDate = null, DateTime? endDate = null, int limit = 50)
        {
            if (!U8ApprovalDocumentReader.IsSupported(documentType))
                return Error(HttpStatusCode.BadRequest,"DOCUMENT_TYPE_INVALID","单据类型无效。");
            status = (status ?? "").Trim().ToLowerInvariant();
            if (status != ApprovalDocumentStatuses.Pending && status != ApprovalDocumentStatuses.Audited)
                return Error(HttpStatusCode.BadRequest,"STATUS_INVALID","审核状态无效。");
            if (startDate.HasValue && endDate.HasValue && startDate.Value.Date > endDate.Value.Date)
                return Error(HttpStatusCode.BadRequest,"DATE_RANGE_INVALID","开始日期不能晚于结束日期。");
            try { return Ok(ApiResponse<ApprovalDocumentSearchResult>.Ok(Reader().Search(documentType,status,documentNo,startDate,endDate,limit),GatewayRequestContext.Id)); }
            catch (SqlException) { return Error(HttpStatusCode.ServiceUnavailable,"U8_APPROVAL_QUERY_FAILED","U8 单据查询失败。"); }
            catch (Exception) { return Error(HttpStatusCode.InternalServerError,"U8_APPROVAL_READ_FAILED","U8 单据读取失败。"); }
        }

        [HttpGet, Route("{documentType}/{id:long}")]
        public IHttpActionResult Get(string documentType, long id)
        {
            if (!U8ApprovalDocumentReader.IsSupported(documentType) || id <= 0)
                return Error(HttpStatusCode.BadRequest,"DOCUMENT_INVALID","单据参数无效。");
            try
            {
                ApprovalDocumentDetail item = Reader().Get(documentType,id);
                return item == null ? Error(HttpStatusCode.NotFound,"DOCUMENT_NOT_FOUND","单据不存在。")
                    : Ok(ApiResponse<ApprovalDocumentDetail>.Ok(item,GatewayRequestContext.Id));
            }
            catch (SqlException) { return Error(HttpStatusCode.ServiceUnavailable,"U8_APPROVAL_QUERY_FAILED","U8 单据查询失败。"); }
            catch (Exception) { return Error(HttpStatusCode.InternalServerError,"U8_APPROVAL_READ_FAILED","U8 单据读取失败。"); }
        }

        private static U8ApprovalDocumentReader Reader()
        {
            GatewaySettings s=AppRuntime.CurrentSettings;
            if(s==null||string.IsNullOrWhiteSpace(s.SqlServer)||string.IsNullOrWhiteSpace(s.SqlDatabase)||string.IsNullOrWhiteSpace(s.SqlUser)) throw new InvalidOperationException("U8 Gateway 尚未配置。");
            return new U8ApprovalDocumentReader(s.SqlServer,s.SqlDatabase,s.SqlUser,s.SqlPassword);
        }

        private IHttpActionResult Error(HttpStatusCode status,string code,string message)
        {
            return Content(status,new ApiResponse<object>{Success=false,Code=code,Message=message,RequestId=GatewayRequestContext.Id,ServerTime=BusinessClock.Now});
        }
    }
}
