using System.Net.Http.Json;

namespace LabelSystem.Api;

public sealed partial class U8GatewayClient
{
    public async Task<LegacyPrintEvidence> GetLegacyPrintEvidenceAsync(string code,CancellationToken ct)
    {
        using var message=CreateRequest(HttpMethod.Get,"/api/v1/traceback/labels/"+Uri.EscapeDataString(code)+"/initial-print-evidence");
        using var response=await Client().SendAsync(message,ct);
        var envelope=await response.Content.ReadFromJsonAsync<GatewayEnvelope<LegacyPrintEvidence>>(JsonOptions,ct);
        if(!response.IsSuccessStatusCode||envelope?.Success!=true||envelope.Data is null)
            throw new ApiException(409,"PRINT_LEGACY_EVIDENCE_REQUIRED","历史打印依据需核对。");
        return envelope.Data;
    }
    public async Task ValidateInitialRegistrationAsync(InitialLabelRegistration request,CancellationToken ct)
    {
        using var message=CreateRequest(HttpMethod.Post,"/api/v1/traceback/labels/initial-registrations/validate");
        message.Content=JsonContent.Create(request,options:JsonOptions);
        using var response=await Client().SendAsync(message,ct);
        var envelope=await response.Content.ReadFromJsonAsync<GatewayEnvelope<object>>(JsonOptions,ct);
        if(!response.IsSuccessStatusCode || envelope?.Success!=true)
            throw new ApiException(409,envelope?.Code??"PRINT_VALIDATION_FAILED",envelope?.Message??"打印来源核验失败。");
    }
    public async Task<InitialLabelRegistrationResult> RegisterInitialAsync(InitialLabelRegistration request,CancellationToken ct)
    {
        using var message=CreateRequest(HttpMethod.Post,"/api/v1/traceback/labels/initial-registrations");
        message.Content=JsonContent.Create(request,options:JsonOptions);
        using var response=await Client().SendAsync(message,ct);
        var envelope=await response.Content.ReadFromJsonAsync<GatewayEnvelope<InitialLabelRegistrationResult>>(JsonOptions,ct);
        if(!response.IsSuccessStatusCode || envelope?.Success!=true || envelope.Data is null)
            throw new ApiException(409,envelope?.Code??"PRINT_SYNC_UNCONFIRMED",envelope?.Message??"登记未完成，请恢复原任务。");
        return envelope.Data;
    }
}
