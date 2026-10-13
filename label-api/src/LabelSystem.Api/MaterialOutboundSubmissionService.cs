using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed class MaterialOutboundSubmissionService(
    MaterialOutboundService materialOutbound,
    DocumentPrepareService documents,
    MaterialOutboundExecutionService execution,
    MaterialOutboundRepository operations,
    IOptionsMonitor<LabelSystemOptions> options)
{
    public async Task<OperationGroupResult> SubmitAsync(
        MaterialOutboundPrepareRequest request,
        SessionUser user,
        string requestId,
        bool autoApprove,
        CancellationToken cancellationToken)
    {
        EnsureSubmissionAllowed(autoApprove);
        MaterialOutboundPrepareResult? prepared = null;
        try
        {
            prepared = await materialOutbound.PrepareAsync(request, requestId, cancellationToken);
            return await ExecutePreparedAsync(prepared, user, requestId);
        }
        catch
        {
            await CancelPendingAsync(prepared, user, requestId);
            throw;
        }
    }

    public async Task<OperationGroupResult> SubmitAsync(
        string documentType,
        DocumentPrepareRequest request,
        SessionUser user,
        string requestId,
        bool autoApprove,
        CancellationToken cancellationToken)
    {
        EnsureSubmissionAllowed(autoApprove);
        MaterialOutboundPrepareResult? prepared = null;
        try
        {
            prepared = await documents.PrepareAsync(documentType, request, requestId, cancellationToken);
            return await ExecutePreparedAsync(prepared, user, requestId);
        }
        catch
        {
            await CancelPendingAsync(prepared, user, requestId);
            throw;
        }
    }

    private void EnsureSubmissionAllowed(bool autoApprove)
    {
        if (!options.CurrentValue.U8WritesEnabled)
            throw new ApiException(StatusCodes.Status409Conflict,
                "U8_WRITE_DISABLED", "U8 写入未开放。");
        if (autoApprove)
            throw new ApiException(StatusCodes.Status409Conflict,
                "AUTO_APPROVE_NOT_READY", "请先关闭上传自动审核。");
    }

    private async Task<OperationGroupResult> ExecutePreparedAsync(
        MaterialOutboundPrepareResult prepared,
        SessionUser user,
        string requestId)
    {
        // 操作一旦创建就由服务端完成状态收口，不受浏览器断开影响。
        var result = await execution.ExecuteAsync(
            prepared.SubmissionGroupId, new ExecuteOperationGroupRequest(false),
            user, requestId, CancellationToken.None);
        if (!string.Equals(result.Status, "PENDING", StringComparison.OrdinalIgnoreCase)) return result;
        await CancelPendingAsync(prepared, user, requestId);
        throw new ApiException(StatusCodes.Status503ServiceUnavailable,
            "SUBMIT_INCOMPLETE", "上传未完成，请重试。");
    }

    private async Task CancelPendingAsync(
        MaterialOutboundPrepareResult? prepared,
        SessionUser user,
        string requestId)
    {
        if (prepared is null) return;
        try
        {
            var current = await operations.GetGroupAsync(
                prepared.SubmissionGroupId, user.Id, false, CancellationToken.None);
            if (string.Equals(current.Status, "PENDING", StringComparison.OrdinalIgnoreCase))
                await operations.CancelAsync(
                    prepared.SubmissionGroupId, requestId, user.Id, false, CancellationToken.None);
        }
        catch
        {
            // 保留原始异常；后台状态可由操作事件继续核对。
        }
    }
}
