using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using U8Gateway.Api.Infrastructure;
using U8Gateway.Contracts;
using U8Gateway.Contracts.Documents;
using U8Gateway.Contracts.Metadata;
using U8Gateway.Contracts.Operations;
using U8Gateway.Core.Documents;
using U8Gateway.Core.Operations;
using U8Gateway.Persistence.Archive;
using U8Gateway.Persistence.Configuration;

namespace U8Gateway.Api.Controllers
{
    [RoutePrefix("api/v1/documents")]
    public sealed class DocumentsController : ApiController
    {
        [HttpGet]
        [Route("{documentType}/{voucherCode}")]
        public Task<IHttpActionResult> Get(
            string documentType,
            string voucherCode,
            CancellationToken cancellationToken)
        {
            return ExecuteRead(
                documentType,
                capabilities => capabilities.Query,
                adapter => adapter.QueryAsync(voucherCode, cancellationToken));
        }

        [HttpPost]
        [Route("{documentType}/validate")]
        public IHttpActionResult Validate(
            string documentType,
            DocumentOperationRequest request,
            [FromUri] string operation = "add")
        {
            DocumentDefinition definition = AppRuntime.Definitions.Get(documentType);
            if (definition == null)
                return Error(HttpStatusCode.NotFound, "DOCUMENT_TYPE_NOT_FOUND", "单据类型不存在。");

            var preparer = new DocumentWritePreparer();
            DocumentPreparationResult result;
            if (string.Equals(operation, "edit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(operation, "update", StringComparison.OrdinalIgnoreCase))
                result = preparer.PreparePatch(definition, request, AppRuntime.BusinessNow());
            else if (string.Equals(operation, "add", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(operation, "create", StringComparison.OrdinalIgnoreCase))
                result = preparer.PrepareCreate(definition, request, AppRuntime.BusinessNow());
            else
                return Error(HttpStatusCode.BadRequest, "VALIDATION_OPERATION_INVALID",
                    "operation 只允许 add 或 edit。");

            if (!result.IsValid) return ValidationError(result);
            return Ok(ApiResponse<DocumentPreparationResult>.Ok(
                result,
                GatewayRequestContext.Id,
                "字段校验通过"));
        }

        [HttpPost]
        [Route("{documentType}")]
        public Task<IHttpActionResult> Create(
            string documentType,
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return ExecuteWrite(
                documentType,
                DocumentOperationCodes.Add,
                null,
                request,
                capabilities => capabilities.Create,
                adapter => adapter.CreateAsync(request, cancellationToken));
        }

        [AcceptVerbs("PATCH")]
        [Route("{documentType}/{voucherCode}")]
        public Task<IHttpActionResult> Update(
            string documentType,
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return ExecuteWrite(
                documentType,
                DocumentOperationCodes.Edit,
                voucherCode,
                request,
                capabilities => capabilities.Update,
                adapter => adapter.UpdateAsync(voucherCode, request, cancellationToken));
        }

        [HttpPost]
        [Route("{documentType}/{voucherCode}/verify")]
        public Task<IHttpActionResult> Verify(
            string documentType,
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return ExecuteWrite(
                documentType,
                DocumentOperationCodes.Verify,
                voucherCode,
                request,
                capabilities => capabilities.Verify,
                adapter => adapter.VerifyAsync(voucherCode, request, cancellationToken));
        }

        [HttpPost]
        [Route("{documentType}/{voucherCode}/unverify")]
        public Task<IHttpActionResult> Unverify(
            string documentType,
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return ExecuteWrite(
                documentType,
                DocumentOperationCodes.Unverify,
                voucherCode,
                request,
                capabilities => capabilities.Unverify,
                adapter => adapter.UnverifyAsync(voucherCode, request, cancellationToken));
        }

        [HttpPost]
        [Route("{documentType}/{voucherCode}/delete")]
        public Task<IHttpActionResult> Delete(
            string documentType,
            string voucherCode,
            DocumentOperationRequest request,
            CancellationToken cancellationToken)
        {
            return ExecuteWrite(
                documentType,
                DocumentOperationCodes.Delete,
                voucherCode,
                request,
                capabilities => capabilities.Delete,
                adapter => adapter.DeleteAsync(voucherCode, request, cancellationToken));
        }

        private async Task<IHttpActionResult> ExecuteRead(
            string documentType,
            Func<DocumentCapabilities, bool> enabled,
            Func<IDocumentAdapter, Task<DocumentOperationResult>> action)
        {
            DocumentDefinition definition = AppRuntime.Definitions.Get(documentType);
            IDocumentAdapter adapter = AppRuntime.DocumentAdapters.Get(documentType);
            if (definition == null || adapter == null)
            {
                return Error(HttpStatusCode.NotFound, "DOCUMENT_TYPE_NOT_FOUND", "单据类型不存在。");
            }
            if (definition.Capabilities == null || !enabled(definition.Capabilities))
            {
                return Error(
                    HttpStatusCode.Conflict,
                    "DOCUMENT_OPERATION_DISABLED",
                    "该操作尚未通过测试账套验证。");
            }

            DocumentOperationResult result = await action(adapter);
            if (result == null || !result.Confirmed)
            {
                return Error(
                    HttpStatusCode.Conflict,
                    result?.Code ?? "U8_RESULT_UNKNOWN",
                    result?.Message ?? "U8 返回结果不明确。");
            }
            return Ok(ApiResponse<DocumentOperationResult>.Ok(
                result,
                GatewayRequestContext.Id));
        }

        private async Task<IHttpActionResult> ExecuteWrite(
            string documentType,
            string operationType,
            string voucherCode,
            DocumentOperationRequest request,
            Func<DocumentCapabilities, bool> enabled,
            Func<IDocumentAdapter, Task<DocumentOperationResult>> action)
        {
            DocumentDefinition definition = AppRuntime.Definitions.Get(documentType);
            IDocumentAdapter adapter = AppRuntime.DocumentAdapters.Get(documentType);
            if (definition == null || adapter == null)
            {
                return Error(HttpStatusCode.NotFound, "DOCUMENT_TYPE_NOT_FOUND", "单据类型不存在。");
            }
            if (definition.Capabilities == null || !enabled(definition.Capabilities))
            {
                return Error(
                    HttpStatusCode.Conflict,
                    "DOCUMENT_OPERATION_DISABLED",
                    "该操作尚未通过测试账套验证。");
            }

            string requestError = ValidateWriteRequest(request);
            if (requestError != null)
            {
                return Error(HttpStatusCode.BadRequest, "INVALID_OPERATION_REQUEST", requestError);
            }

            if (string.Equals(operationType, DocumentOperationCodes.Add,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(operationType, DocumentOperationCodes.Edit,
                    StringComparison.OrdinalIgnoreCase))
            {
                var preparer = new DocumentWritePreparer();
                DocumentPreparationResult prepared = string.Equals(
                    operationType, DocumentOperationCodes.Add,
                    StringComparison.OrdinalIgnoreCase)
                    ? preparer.PrepareCreate(definition, request, AppRuntime.BusinessNow())
                    : preparer.PreparePatch(definition, request, AppRuntime.BusinessNow());
                if (!prepared.IsValid) return ValidationError(prepared);
                request.Header = prepared.Payload.Header;
                request.Lines = prepared.Payload.Lines;
                request.Positions = prepared.Payload.Positions;
                request.Auxiliary = prepared.Payload.Auxiliary;
            }

            try
            {
                AppRuntime.EnsureU8WriteAllowed();
            }
            catch (InvalidOperationException exception)
            {
                return Error(
                    HttpStatusCode.Conflict,
                    "U8_WRITE_GUARD_BLOCKED",
                    exception.Message);
            }

            object payload = new
            {
                documentType,
                operationType,
                voucherCode,
                request
            };
            string payloadHash = OperationPayloadHasher.Compute(payload);
            IOperationJournal journal = null;
            IOperationRegistry registry = null;
            OperationJournalHandle handle = null;
            try
            {
                GatewaySettings settings = AppRuntime.CurrentSettings;
                DateTimeOffset occurredAt = AppRuntime.BusinessNow();
                journal = AppRuntime.CreateOperationJournal();
                handle = journal.Begin(new OperationJournalStart
                {
                    ClientRequestId = request.ClientRequestId,
                    RequestId = GatewayRequestContext.Id,
                    Environment = settings.EnvironmentName,
                    U8Database = settings.SqlDatabase,
                    DocumentType = documentType,
                    DocumentTitle = definition.Title,
                    OperationType = operationType,
                    OperatorId = request.OperatorId,
                    OperatorName = request.OperatorName,
                    DeviceId = request.DeviceId,
                    DeviceModel = request.DeviceModel,
                    ApkVersion = request.ApkVersion,
                    H5Version = request.H5Version,
                    BridgeVersion = request.BridgeVersion,
                    VoucherCode = voucherCode,
                    ExpectedVersion = request.ExpectedVersion,
                    Reason = request.Reason,
                    ServiceVersion = AppRuntime.ServiceVersion,
                    PayloadHash = payloadHash,
                    Payload = payload,
                    OccurredAt = occurredAt
                });

                registry = AppRuntime.CreateOperationRegistry();
                OperationClaimResult claim = registry.Claim(new OperationRecord
                {
                    ClientRequestId = request.ClientRequestId,
                    RequestId = GatewayRequestContext.Id,
                    PayloadHash = payloadHash,
                    Environment = settings.EnvironmentName,
                    DocumentType = documentType,
                    DocumentTitle = definition.Title,
                    OperationType = operationType,
                    TargetVoucherCode = voucherCode,
                    Status = OperationStages.Received,
                    Code = "RECEIVED",
                    Message = "请求已接收",
                    CreatedAt = occurredAt,
                    UpdatedAt = occurredAt
                });
                if (claim.Outcome == OperationClaimOutcomes.Conflict)
                {
                    TryAppend(
                        journal,
                        handle,
                        OperationStages.DuplicateBlocked,
                        "IDEMPOTENCY_CONFLICT",
                        "同一 clientRequestId 对应的请求内容不同");
                    return Error(
                        HttpStatusCode.Conflict,
                        "IDEMPOTENCY_CONFLICT",
                        "clientRequestId 已被其他请求使用。");
                }
                if (claim.Outcome == OperationClaimOutcomes.Replay)
                {
                    TryAppend(
                        journal,
                        handle,
                        OperationStages.IdempotentReplay,
                        claim.Record.Code,
                        "返回已完成操作的原结果");
                    if (claim.Record.Result == null || !claim.Record.Result.Confirmed)
                    {
                        return Error(
                            HttpStatusCode.Conflict,
                            "RECONCILIATION_REQUIRED",
                            "历史操作缺少完整结果，请先核对。");
                    }
                    return Ok(ApiResponse<DocumentOperationResult>.Ok(
                        claim.Record.Result,
                        GatewayRequestContext.Id,
                        "重复请求，已返回原结果"));
                }
                if (claim.Outcome == OperationClaimOutcomes.Pending)
                {
                    TryAppend(
                        journal,
                        handle,
                        OperationStages.DuplicateBlocked,
                        "OPERATION_ALREADY_EXISTS",
                        "相同请求正在处理或等待核对",
                        new Dictionary<string, object>
                        {
                            ["status"] = claim.Record.Status
                        });
                    return Error(
                        HttpStatusCode.Conflict,
                        IsReconciliationStatus(claim.Record.Status)
                            ? "RECONCILIATION_REQUIRED"
                            : "OPERATION_IN_PROGRESS",
                        IsReconciliationStatus(claim.Record.Status)
                            ? "该请求需要先核对，禁止直接重试。"
                            : "该请求正在处理，请查询操作状态。");
                }

                Append(journal, handle, OperationStages.Validated, "OK", "请求校验通过");
                registry.Update(
                    request.ClientRequestId,
                    OperationStages.Validated,
                    "OK",
                    "请求校验通过",
                    null,
                    AppRuntime.BusinessNow());
                Append(
                    journal,
                    handle,
                    OperationStages.U8Writing,
                    null,
                    "开始调用 U8",
                    new Dictionary<string, object>
                    {
                        ["voucherCode"] = voucherCode,
                        ["expectedVersion"] = request.ExpectedVersion
                    });
                registry.Update(
                    request.ClientRequestId,
                    OperationStages.U8Writing,
                    null,
                    "开始调用 U8",
                    null,
                    AppRuntime.BusinessNow());
            }
            catch (Exception)
            {
                TryAppend(
                    journal,
                    handle,
                    OperationStages.Failed,
                    "PRE_U8_GUARD_FAILED",
                    "写入安全门禁失败，未调用 U8");
                TryUpdate(
                    registry,
                    request.ClientRequestId,
                    OperationStages.Failed,
                    "PRE_U8_GUARD_FAILED",
                    "写入安全门禁失败，未调用 U8",
                    null);
                return Error(
                    HttpStatusCode.ServiceUnavailable,
                    "OPERATION_GUARD_UNAVAILABLE",
                    "操作凭证或幂等记录无法写入，已停止调用 U8。");
            }

            DocumentOperationResult result;
            try
            {
                result = await action(adapter);
            }
            catch (OperationCanceledException)
            {
                TryAppend(
                    journal,
                    handle,
                    OperationStages.ResultUnknown,
                    "REQUEST_CANCELLED",
                    "请求已取消，U8 结果需要核对");
                TryAppend(
                    journal,
                    handle,
                    OperationStages.ReconciliationRequired,
                    "REQUEST_CANCELLED",
                    "禁止直接重试");
                TryUpdate(
                    registry,
                    request.ClientRequestId,
                    OperationStages.ReconciliationRequired,
                    "REQUEST_CANCELLED",
                    "请求已取消，禁止直接重试",
                    null);
                throw;
            }
            catch (Exception exception)
            {
                TryAppend(
                    journal,
                    handle,
                    OperationStages.ResultUnknown,
                    "U8_EXECUTION_EXCEPTION",
                    exception.Message,
                    new Dictionary<string, object>
                    {
                        ["exceptionType"] = exception.GetType().FullName
                    });
                TryAppend(
                    journal,
                    handle,
                    OperationStages.ReconciliationRequired,
                    "U8_EXECUTION_EXCEPTION",
                    "禁止直接重试");
                TryUpdate(
                    registry,
                    request.ClientRequestId,
                    OperationStages.ReconciliationRequired,
                    "U8_EXECUTION_EXCEPTION",
                    exception.Message,
                    null);
                return Error(
                    HttpStatusCode.InternalServerError,
                    "U8_RESULT_UNKNOWN",
                    "U8 执行结果需要核对，禁止直接重试。");
            }

            try
            {
                Append(
                    journal,
                    handle,
                    OperationStages.U8Returned,
                    result?.Code,
                    result?.Message,
                    ResultData(result));
                registry.Update(
                    request.ClientRequestId,
                    OperationStages.U8Returned,
                    result?.Code,
                    result?.Message,
                    result,
                    AppRuntime.BusinessNow());
                if (result == null || (!result.Confirmed && !result.OutcomeKnown))
                {
                    Append(
                        journal,
                        handle,
                        OperationStages.ResultUnknown,
                        result?.Code ?? "U8_RESULT_UNKNOWN",
                        result?.Message ?? "U8 返回结果不明确");
                    Append(
                        journal,
                        handle,
                        OperationStages.ReconciliationRequired,
                        result?.Code ?? "U8_RESULT_UNKNOWN",
                        "禁止直接重试");
                    registry.Update(
                        request.ClientRequestId,
                        OperationStages.ReconciliationRequired,
                        result?.Code ?? "U8_RESULT_UNKNOWN",
                        result?.Message ?? "U8 返回结果不明确",
                        result,
                        AppRuntime.BusinessNow());
                }
                else if (result.Confirmed)
                {
                    Append(
                        journal,
                        handle,
                        OperationStages.U8Confirmed,
                        result.Code,
                        "U8 回查确认完成",
                        ResultData(result));
                    registry.Update(
                        request.ClientRequestId,
                        OperationStages.U8Confirmed,
                        result.Code,
                        "U8 回查确认完成",
                        result,
                        AppRuntime.BusinessNow());
                    Append(
                        journal,
                        handle,
                        OperationStages.Success,
                        result.Code,
                        "操作完成");
                    registry.Update(
                        request.ClientRequestId,
                        OperationStages.Success,
                        result.Code,
                        "操作完成",
                        result,
                        AppRuntime.BusinessNow());
                }
                else
                {
                    Append(
                        journal,
                        handle,
                        OperationStages.Failed,
                        result.Code,
                        result.Message,
                        ResultData(result));
                    registry.Update(
                        request.ClientRequestId,
                        OperationStages.Failed,
                        result.Code,
                        result.Message,
                        result,
                        AppRuntime.BusinessNow());
                }
            }
            catch (Exception)
            {
                TryUpdate(
                    registry,
                    request.ClientRequestId,
                    OperationStages.ReconciliationRequired,
                    "OPERATION_RECORD_WRITE_FAILED",
                    "U8 已被调用，但操作记录未完整写入",
                    result);
                return Error(
                    HttpStatusCode.InternalServerError,
                    "RECONCILIATION_REQUIRED",
                    "U8 已被调用，但操作凭证写入失败，请先核对结果，禁止直接重试。");
            }

            if (result == null || !result.Confirmed)
            {
                return Error(
                    HttpStatusCode.Conflict,
                    result?.Code ?? "U8_RESULT_UNKNOWN",
                    result?.Message ?? "U8 返回结果不明确。");
            }
            return Ok(ApiResponse<DocumentOperationResult>.Ok(
                result,
                GatewayRequestContext.Id));
        }

        private static bool IsReconciliationStatus(string status)
        {
            return string.Equals(
                    status,
                    OperationStages.ResultUnknown,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    status,
                    OperationStages.ReconciliationRequired,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string ValidateWriteRequest(DocumentOperationRequest request)
        {
            if (request == null)
            {
                return "请求内容不能为空。";
            }
            if (string.IsNullOrWhiteSpace(request.ClientRequestId) ||
                request.ClientRequestId.Length > 64)
            {
                return "clientRequestId 必须填写且不能超过 64 个字符。";
            }
            if (string.IsNullOrWhiteSpace(request.OperatorId) ||
                request.OperatorId.Length > 64)
            {
                return "operatorId 必须填写且不能超过 64 个字符。";
            }
            if (string.IsNullOrWhiteSpace(request.DeviceId) ||
                request.DeviceId.Length > 128)
            {
                return "deviceId 必须填写且不能超过 128 个字符。";
            }
            return null;
        }

        private static IDictionary<string, object> ResultData(DocumentOperationResult result)
        {
            if (result == null)
            {
                return null;
            }
            return new Dictionary<string, object>
            {
                ["confirmed"] = result.Confirmed,
                ["voucherId"] = result.VoucherId,
                ["voucherCode"] = result.VoucherCode,
                ["version"] = result.Version
            };
        }

        private static void Append(
            IOperationJournal journal,
            OperationJournalHandle handle,
            string stage,
            string code,
            string message,
            IDictionary<string, object> data = null)
        {
            journal.Append(handle, new OperationJournalEvent
            {
                Stage = stage,
                Code = code,
                Message = message,
                Data = data,
                OccurredAt = AppRuntime.BusinessNow()
            });
        }

        private static void TryAppend(
            IOperationJournal journal,
            OperationJournalHandle handle,
            string stage,
            string code,
            string message,
            IDictionary<string, object> data = null)
        {
            try
            {
                Append(journal, handle, stage, code, message, data);
            }
            catch
            {
            }
        }

        private static void TryUpdate(
            IOperationRegistry registry,
            string clientRequestId,
            string status,
            string code,
            string message,
            DocumentOperationResult result)
        {
            if (registry == null)
            {
                return;
            }
            try
            {
                registry.Update(
                    clientRequestId,
                    status,
                    code,
                    message,
                    result,
                    AppRuntime.BusinessNow());
            }
            catch
            {
            }
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

        private IHttpActionResult ValidationError(DocumentPreparationResult result)
        {
            return Content(HttpStatusCode.BadRequest,
                new ApiResponse<DocumentPreparationResult>
                {
                    Success = false,
                    Code = "DOCUMENT_VALIDATION_FAILED",
                    Message = "字段校验未通过。",
                    RequestId = GatewayRequestContext.Id,
                    ServerTime = BusinessClock.Now,
                    Data = result
                });
        }
    }
}
