using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LabelSystem.Api;

public sealed class PrintingOptions
{
    public bool Enabled { get; set; }
    public bool AllowNewDispatch { get; set; }
    public bool RecoveryEnabled { get; set; }
    public bool SourceEnabled { get; set; }
    public bool SimulationOnly { get; set; }
    public Guid SimulationDeviceId { get; set; }
    public string SimulationDeviceKeyHash { get; set; } = "";
    public string SimulationStatePath { get; set; } = "";
    public string SourceVerificationKey { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string RootPath { get; set; } = "runtime-data/printing";
    public string SigningKeyPath { get; set; } = "";
    public string RendererUrl { get; set; } = "";
    public string RendererApiKey { get; set; } = "";
    public string RendererVersion { get; set; } = "label-renderer/1";
}

public sealed record PrintTargetRequest(long LabelId, long OriginLineId, string ExpectedRowVersion);
public sealed record PrintSimulationSeedRequest(string ClientRequestId, decimal Quantity = 100);
public sealed record PrintSourceRow(string SourceKey,string SourceKind,string DocumentId,string DocumentNo,string DetailId,
    string MaterialCode,string UnitCode,string BatchNo,string WarehouseCode,string LocationCode,
    decimal EligibleQuantity,decimal LegacyQuantity,IReadOnlyList<string> LegacyCodes,IReadOnlyList<decimal> Amounts);
public sealed record VerifiedPrintSource(string ClientRequestId,string PlanHash,string UserId,long ExpiresAt,
    IReadOnlyList<PrintSourceRow> Rows);
public sealed record FirstPrintRequest(string ClientRequestId, long TemplateVersionId, Guid DeviceId,
    string Printer, IReadOnlyList<PrintTargetRequest> Targets);
public sealed record ReprintRequest(string ClientRequestId, long LabelId, string ExpectedRowVersion,
    int Copies, string? Reason, bool ConfirmInactive = false, Guid? DeviceId = null, string? Printer = null);
public sealed record PrintActionRequest(string ClientRequestId, string ExpectedRowVersion);
public sealed record PrintDispatchRequest(string ClientRequestId, string ExpectedRowVersion,
    IReadOnlyList<Guid> OutputIds);
public sealed record SignedPrintMessage(string Payload, string Signature);
public sealed record PrintDeviceRequest(Guid Id, string Name, string PublicKey, string? OwnerUserId = null);
public sealed record PrintDeviceStateRequest(string ExpectedRowVersion, bool Enabled);
public sealed record PrintPreviewRequest(long TemplateVersionId, JsonObject Variables);
public sealed record PrintReconcileRequest(string ClientRequestId,string ExpectedRowVersion,string Reason,
    SignedPrintMessage? Receipt = null);
public sealed record PrintRuleRequest(long RuleVersionId, string? ExpectedRowVersion);
public sealed record PrintTemplateRequest(string Name, JsonElement CanvasData, decimal WidthMm,
    decimal HeightMm, string? ExpectedRowVersion);
public sealed record PublishPrintTemplateRequest(string ExpectedRowVersion);
public sealed record PrintTemplateStateRequest(string ExpectedRowVersion, bool Enabled);
public sealed record PrintAssetRequest(string MediaType, string DataBase64, int WidthPx, int HeightPx);
public sealed record ResolvePrintLabelRequest(string Code);
public sealed record LegacyPrintEvidence(string Code,long InitialFlowId,decimal InitialQuantity,
    string PrintedAt,string EvidenceHash,TraceBackLabelSnapshot Original);
public sealed record LegacyPrintBaselineRequest(string ClientRequestId,string Code,long TemplateVersionId,
    Guid DeviceId,string Printer,string ExpectedRowVersion);

public sealed record PrintSnapshot(
    long LabelId, string Code, string SerialNo, string Quantity, string UnitCode,
    string MaterialCode, string MaterialName, string BatchNo,
    string WarehouseCode, string LocationCode, string? ManufactureDate, string? ExpiryDate,
    string? AttributesJson, int AttributeSchemaVersion, string? IdentityHash,
    long? OriginLineId, string SourceDocumentType, string SourceDocumentId, string SourceDocumentNo,
    string? ErpDetailId, long? RuleVersionId, string FrozenAt, JsonObject Variables);

public sealed record PrintOutput(Guid OutputId, long LineId, string Kind, string Phase,
    long? FlowId, PrintSnapshot Snapshot);
public sealed record ArchivedPrintOutput(Guid JobId, Guid OutputId, string Printer,
    long TemplateVersionId, PrintSnapshot Snapshot, JsonObject Manifest);
public sealed record PrintJob(Guid JobId, long OperationId, string OperatorId, string Phase,
    string Status, Guid DeviceId, string Printer, long TemplateVersionId, string RowVersion,
    string? ManifestHash, JsonObject? Manifest, IReadOnlyList<PrintOutput> Outputs,
    Guid? AttemptId = null, SignedPrintMessage? Authorization = null, string DeviceKeyHash = "")
{
    public bool CanRedispatch => Phase == "READY";
    public bool LocalRecorded => Outputs.Count > 0 && Outputs.All(x => x.FlowId.HasValue);
}

public sealed record InitialLabelRegistration(
    int ContractVersion, string EventKey, string PayloadHash, string Code, string SerialNo,
    decimal Quantity, string OrderUuid, string GoodsUuid, string OperatorName,
    string MaterialCode, string MaterialName, string UnitCode, string BatchNo,
    string TargetWarehouseCode, string TargetLocationCode, string? ManufactureDate,
    string? ExpiryDate, string? AttributesJson, string SourceDocumentType,
    string SourceDocumentId, string SourceDocumentNo, string? ErpDetailId,
    bool ExpectedAbsent = true, long ExpectedLatestFlowId = 0, decimal ExpectedBalance = 0);
public sealed record InitialLabelRegistrationResult(string EventKey, string PayloadHash,
    bool AlreadyApplied, long FlowId, TraceBackLabelSnapshot Snapshot);

public static class PrintRules
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = true };

    public static ApiException Conflict(string code, string message) => new(409, code, message);
    public static string Quantity(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    public static decimal ParseQuantity(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result)
            || result <= 0 || result > 999999999999.999999m || decimal.Round(result, 6) != result)
            throw new ApiException(400, "PRINT_QUANTITY_INVALID", "标签数量无效。");
        return result;
    }
    public static string Canonical<T>(T value) => CanonicalNode(JsonSerializer.SerializeToNode(value, Json));
    private static string CanonicalNode(JsonNode? value)
    {
        if (value is JsonObject obj)
            return "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Key) + ":" + CanonicalNode(p.Value))) + "}";
        if (value is JsonArray array) return "[" + string.Join(",", array.Select(CanonicalNode)) + "]";
        if (value is JsonValue scalar && scalar.TryGetValue<JsonElement>(out var element)
            && element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number))
            return number.ToString("G29", CultureInfo.InvariantCulture);
        return value?.ToJsonString(Json) ?? "null";
    }
    public static string Hash<T>(T value) => Hashing.Sha256(Canonical(value));
    public static string Key(string kind, string scope, string identity) =>
        kind + ":" + Hashing.Sha256(scope + "\n" + identity);
    public static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        return new Guid(bytes);
    }
    public static string RequestId(string value)
    {
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            throw new ApiException(400, "PRINT_REQUEST_INVALID", "请求编号无效。");
        return "print-" + id.ToString("N");
    }
    public static void RequireKnownUnsent(string phase)
    {
        if (phase is not ("PREPARING" or "READY" or "FAILED_NO_OUTPUT" or "CANCELLED"))
            throw Conflict("PRINT_RESULT_UNKNOWN", "请先恢复原打印任务。");
    }
    public static bool HasRemainingDependencies(IEnumerable<PrintOutput> outputs, long labelId,
        bool initialRegistrationPending) =>
        initialRegistrationPending || outputs.Any(x => x.Snapshot.LabelId == labelId && !x.FlowId.HasValue);
    public static string AggregatePhase(IEnumerable<string> pendingPhases,bool registrationPending)
    {
        var phases=pendingPhases.ToArray();
        if(phases.Length==0)return registrationPending?"TRACEBACK_PENDING":"COMPLETED";
        if(phases.Any(p=>p is not ("READY" or "FAILED_NO_OUTPUT")))return "RECONCILIATION_REQUIRED";
        return phases.Contains("FAILED_NO_OUTPUT")?"FAILED_NO_OUTPUT":"READY";
    }

    public static InitialLabelRegistration Registration(string scope, PrintOutput output, string operatorName)
    {
        var s = output.Snapshot;
        var key = Key("print-initial", scope, s.LabelId.ToString(CultureInfo.InvariantCulture));
        var request = new InitialLabelRegistration(1, key, "", s.Code, s.SerialNo,
            ParseQuantity(s.Quantity), StableGuid(key + ":order").ToString(),
            StableGuid(key + ":goods").ToString(), operatorName, s.MaterialCode, s.MaterialName,
            s.UnitCode, s.BatchNo, s.WarehouseCode, s.LocationCode, s.ManufactureDate, s.ExpiryDate,
            s.AttributesJson, s.SourceDocumentType, s.SourceDocumentId, s.SourceDocumentNo, s.ErpDetailId);
        return request with { PayloadHash = RegistrationHash(request) };
    }

    // Cross-runtime contract shared with Gateway: ordered strings, UTF-8 byte length prefixes.
    public static string RegistrationHash(InitialLabelRegistration r)
    {
        var fields = new[] { "1", r.EventKey, r.Code, r.SerialNo, Quantity(r.Quantity),
            r.OrderUuid.ToLowerInvariant(), r.GoodsUuid.ToLowerInvariant(), r.OperatorName,
            r.MaterialCode, r.MaterialName, r.UnitCode, r.BatchNo, r.TargetWarehouseCode,
            r.TargetLocationCode, r.ManufactureDate ?? "", r.ExpiryDate ?? "", r.AttributesJson ?? "",
            r.SourceDocumentType, r.SourceDocumentId, r.SourceDocumentNo, r.ErpDetailId ?? "" };
        return Hashing.Sha256(string.Concat(fields.Select(s =>
            Encoding.UTF8.GetByteCount(s).ToString(CultureInfo.InvariantCulture) + ":" + s)));
    }
    public static string PublicKey(string base64)
    {
        try
        {
            using var key = ECDsa.Create();
            var bytes = Convert.FromBase64String(base64);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value!="1.2.840.10045.3.1.7")
                throw new CryptographicException();
            return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new ApiException(400, "PRINT_DEVICE_KEY_INVALID", "设备公钥无效。"); }
    }
    public static JsonObject Verify(SignedPrintMessage signed, string publicKey)
    {
        try
        {
            var payload = Convert.FromBase64String(signed.Payload);
            if (payload.Length > 1024 * 1024) throw new FormatException();
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (!key.VerifyData(payload, Convert.FromBase64String(signed.Signature),
                    HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new CryptographicException();
            return JsonNode.Parse(payload)?.AsObject() ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException
                                     or InvalidOperationException)
        { throw Conflict("PRINT_RECEIPT_INVALID", "打印回执校验失败。"); }
    }
}
