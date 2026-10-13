using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LabelSystem.Api;

public sealed class MaterialOutboundExecutionService(
    MaterialOutboundExecutionRepository executionRepository,
    MaterialOutboundRepository operations,
    OperationArchiveService archive,
    U8GatewayClient gateway,
    UserRepository users,
    IOptionsMonitor<LabelSystemOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<OperationGroupValidationResult> ValidateAsync(
        string groupId,
        SessionUser user,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareGatewayRequestsAsync(groupId, user, cancellationToken);
        foreach (var item in prepared)
            await ValidateGatewayAsync(item.Request, cancellationToken);
        return new OperationGroupValidationResult(
            groupId, prepared.Count, prepared.Sum(item => item.Operation.Lines.Count), true);
    }

    public async Task<OperationGroupResult> ExecuteAsync(
        string groupId,
        ExecuteOperationGroupRequest request,
        SessionUser user,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.U8WritesEnabled)
            throw new ApiException(StatusCodes.Status409Conflict,
                "U8_WRITE_DISABLED", "U8 写入未开放。");
        if (request.AutoApprove)
            throw new ApiException(StatusCodes.Status409Conflict,
                "AUTO_APPROVE_NOT_READY", "请先关闭上传自动审核。");

        var prepared = await PrepareGatewayRequestsAsync(groupId, user, cancellationToken);
        foreach (var item in prepared)
            await ValidateGatewayAsync(item.Request, cancellationToken);

        foreach (var item in prepared)
        {
            if (item.Operation.Status == "SUCCESS") continue;
            if (item.Operation.Status != "PENDING" || item.Operation.ErpStatus != "PENDING")
                throw new ApiException(StatusCodes.Status409Conflict,
                    "OPERATION_NOT_PENDING", "业务操作不能上传，需要先核对状态。");
            await executionRepository.BeginAsync(
                item.Operation, options.CurrentValue.LockMinutes, requestId, cancellationToken);
            try
            {
                var result = await gateway.CreateDocumentAsync(
                    "material-outbound", item.Request, cancellationToken);
                var rows = ReadErpRows(result.Document);
                EnsureErpRows(item.Operation, rows);
                await executionRepository.CompleteAsync(
                    item.Operation, result, item.RedVoucher, rows, requestId, cancellationToken);
            }
            catch (GatewayWriteException error)
            {
                await executionRepository.MarkFailureAsync(
                    item.Operation, error.Code, error.Message, error.ResultUnknown,
                    requestId, cancellationToken);
                if (error.ResultUnknown) break;
            }
            catch (Exception error)
            {
                await executionRepository.MarkFailureAsync(
                    item.Operation, "LOCAL_COMMIT_UNKNOWN", error.Message, true,
                    requestId, cancellationToken);
                break;
            }
        }
        return await operations.GetGroupAsync(groupId, user.Id, false, cancellationToken);
    }

    private async Task<IReadOnlyList<PreparedGatewayOperation>> PrepareGatewayRequestsAsync(
        string groupId,
        SessionUser user,
        CancellationToken cancellationToken)
    {
        var plan = await executionRepository.GetPlanAsync(groupId, user.Id, cancellationToken);
        var erp = await users.GetErpOperatorAsync(user.Id, cancellationToken);
        var payloadCache = new Dictionary<string, NormalizedRequest>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PreparedGatewayOperation>(plan.Count);
        foreach (var operation in plan)
        {
            if (operation.Lines.Any(line => line.ObjectType == "PALLET" || line.SourcePalletId.HasValue))
                throw new ApiException(StatusCodes.Status409Conflict,
                    "PALLET_EXECUTION_NOT_READY", "托盘出库执行尚未开放，请使用产品标签。");
            if (string.IsNullOrWhiteSpace(operation.ArchiveRelativePath) ||
                string.IsNullOrWhiteSpace(operation.ArchiveSha256))
                throw new ApiException(StatusCodes.Status409Conflict,
                    "ARCHIVE_MISSING", "操作凭证不完整，需要核对。");
            if (!payloadCache.TryGetValue(operation.ArchiveRelativePath, out var normalized))
            {
                var payload = await archive.ReadPayloadAsync(
                    operation.ArchiveRelativePath, operation.ArchiveSha256, cancellationToken);
                normalized = Normalize(payload);
                payloadCache.Add(operation.ArchiveRelativePath, normalized);
            }
            var requirements = normalized.HasProductionSource
                ? await gateway.GetMaterialRequirementsAsync(
                    operation.Lines.Where(line => line.SourceLineId.HasValue)
                        .Select(line => line.SourceLineId!.Value).Distinct().ToArray(),
                    cancellationToken)
                : [];
            result.Add(new PreparedGatewayOperation(
                operation,
                BuildGatewayRequest(operation, normalized, requirements, erp),
                normalized.Direction == "RED"));
        }
        return result;
    }

    private static GatewayDocumentRequest BuildGatewayRequest(
        MaterialOutboundExecutionOperation operation,
        NormalizedRequest request,
        IReadOnlyList<MaterialRequirementLine> requirements,
        ErpOperator erp)
    {
        var header = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ddate"] = request.DocumentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["cwhcode"] = operation.WarehouseCode,
            ["crdcode"] = request.CategoryCode,
            ["cdepcode"] = request.DepartmentCode,
            ["cprobatch"] = Empty(request.ProductionBatch),
            ["cmemo"] = Empty(request.Reason),
            ["csource"] = request.HasProductionSource ? "生产订单" : "库存",
            ["cbustype"] = "领料",
            ["vt_id"] = 65
        };
        if (request.HasProductionSource && int.TryParse(
                operation.SourceDocumentId, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var sourceDocumentId))
            header["iproorderid"] = sourceDocumentId;
        ApplyAdditional(header, request.HeaderAdditional, "header.");

        var requirementById = requirements.ToDictionary(item => item.RequirementId);
        var used = new HashSet<int>();
        var lines = new List<IReadOnlyDictionary<string, object?>>(operation.Lines.Count);
        var positions = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var line in operation.Lines)
        {
            var source = MatchLine(request.Lines, line, used);
            var quantity = request.Direction == "RED" ? -line.RequestedQty : line.RequestedQty;
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["cinvcode"] = line.MaterialCode,
                ["cbatch"] = Empty(line.BatchNo),
                ["cposition"] = Empty(line.LocationCode),
                ["iquantity"] = quantity,
                ["irowno"] = line.LineNo,
                ["iposflag"] = string.IsNullOrWhiteSpace(line.LocationCode) ? 0 : 1,
                ["cbmemo"] = Empty(line.Remark)
            };
            if (source?.RequirementId is int requirementId &&
                requirementById.TryGetValue(requirementId, out var requirement))
            {
                row["impoids"] = requirement.RequirementId;
                row["cmocode"] = requirement.SourceDocumentNo;
                row["imoseq"] = requirement.SourceLineNo;
                row["iopseq"] = requirement.OperationSequence;
            }
            else if (request.HasProductionSource)
            {
                throw new ApiException(StatusCodes.Status409Conflict,
                    "SOURCE_REQUIREMENT_CHANGED", "领料明细已变化，请重新制单。");
            }
            ApplyAdditional(row, source?.AdditionalFields, "body.");
            lines.Add(row);

            if (!string.IsNullOrWhiteSpace(line.LocationCode))
            {
                positions.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["cWhCode"] = line.WarehouseCode,
                    ["cPosCode"] = line.LocationCode,
                    ["cInvCode"] = line.MaterialCode,
                    ["cBatch"] = Empty(line.BatchNo),
                    ["iQuantity"] = quantity,
                    ["cvouchtype"] = "11"
                });
            }
        }
        return new GatewayDocumentRequest(
            operation.ClientRequestId,
            erp.Code,
            erp.Name,
            string.IsNullOrWhiteSpace(operation.DeviceId) ? "h5" : operation.DeviceId,
            operation.DeviceModel,
            null,
            null,
            null,
            request.Reason,
            null,
            header,
            lines,
            positions,
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["redVoucher"] = request.Direction == "RED"
            });
    }

    private static NormalizedLine? MatchLine(
        IReadOnlyList<NormalizedLine> candidates,
        MaterialOutboundExecutionLine target,
        HashSet<int> used)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (used.Contains(index)) continue;
            var candidate = candidates[index];
            if (!string.Equals(candidate.LabelCode, target.LabelCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.MaterialCode, target.MaterialCode, StringComparison.OrdinalIgnoreCase) ||
                candidate.SourceLineId != target.SourceLineId ||
                candidate.RequestedQty != target.RequestedQty) continue;
            used.Add(index);
            return candidate;
        }
        throw new ApiException(StatusCodes.Status409Conflict,
            "ARCHIVE_LINE_MISMATCH", "操作凭证与业务明细不一致，需要核对。");
    }

    private static NormalizedRequest Normalize(JsonElement payload)
    {
        if (payload.TryGetProperty("header", out _))
        {
            var generic = payload.Deserialize<DocumentPrepareRequest>(JsonOptions)
                ?? throw InvalidArchive();
            if (generic.Header.DocumentDirection is not ("BLUE" or "RED")) throw InvalidArchive();
            return new NormalizedRequest(
                generic.Header.DocumentDate,
                generic.Header.DocumentDirection.ToUpperInvariant(),
                generic.Header.DepartmentCode,
                generic.Header.CategoryCode,
                generic.Header.ProductionBatch,
                generic.Reason,
                !string.IsNullOrWhiteSpace(generic.Header.SourceType),
                generic.Header.AdditionalFields,
                generic.Lines.Select(line => new NormalizedLine(
                    line.LabelCode, line.MaterialCode, line.RequestedQty,
                    line.SourceLineId, null, line.AdditionalFields)).ToArray());
        }
        var material = payload.Deserialize<MaterialOutboundPrepareRequest>(JsonOptions)
            ?? throw InvalidArchive();
        if (material.DocumentDirection is not ("BLUE" or "RED")) throw InvalidArchive();
        return new NormalizedRequest(
            material.DocumentDate,
            material.DocumentDirection.ToUpperInvariant(),
            material.DepartmentCode,
            material.CategoryCode,
            material.ProductionBatch,
            material.Reason,
            true,
            material.AdditionalFields,
            material.Lines.Select(line => new NormalizedLine(
                line.LabelCode, line.MaterialCode, line.RequestedQty,
                line.SourceLineId, line.RequirementId, line.AdditionalFields)).ToArray());
    }

    private static IReadOnlyDictionary<int, (string? LineId, string? PositionId)> ReadErpRows(
        JsonElement? document)
    {
        var result = new Dictionary<int, (string? LineId, string? PositionId)>();
        if (document is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return result;
        var lineIdToNo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.EnumerateArray())
        {
            if (!TryInt(line, "irowno", out var lineNo)) continue;
            var lineId = Text(line, "autoid");
            result[lineNo] = (lineId, null);
            if (!string.IsNullOrWhiteSpace(lineId)) lineIdToNo[lineId] = lineNo;
        }
        if (!root.TryGetProperty("positions", out var positions) ||
            positions.ValueKind != JsonValueKind.Array) return result;
        foreach (var position in positions.EnumerateArray())
        {
            var lineId = Text(position, "rdsID");
            if (string.IsNullOrWhiteSpace(lineId) || !lineIdToNo.TryGetValue(lineId, out var lineNo))
                continue;
            result[lineNo] = (result[lineNo].LineId, Text(position, "autoID"));
        }
        return result;
    }

    private static void EnsureErpRows(
        MaterialOutboundExecutionOperation operation,
        IReadOnlyDictionary<int, (string? LineId, string? PositionId)> rows)
    {
        foreach (var line in operation.Lines)
        {
            if (!rows.TryGetValue(line.LineNo, out var row) ||
                string.IsNullOrWhiteSpace(row.LineId) ||
                (!string.IsNullOrWhiteSpace(line.LocationCode) &&
                 string.IsNullOrWhiteSpace(row.PositionId)))
                throw new InvalidOperationException("U8 单据已生成，但明细回查不完整。");
        }
    }

    private async Task ValidateGatewayAsync(
        GatewayDocumentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await gateway.ValidateDocumentAsync("material-outbound", request, cancellationToken);
        }
        catch (GatewayWriteException error)
        {
            throw new ApiException(
                error.StatusCode is >= 400 and < 500 ? error.StatusCode :
                    StatusCodes.Status503ServiceUnavailable,
                error.Code,
                error.Message);
        }
    }

    private static bool TryInt(JsonElement value, string name, out int result)
    {
        result = 0;
        if (!value.TryGetProperty(name, out var property)) return false;
        return property.ValueKind == JsonValueKind.Number
            ? property.TryGetInt32(out result)
            : int.TryParse(property.ToString(), out result);
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind is not JsonValueKind.Null
            ? property.ToString() : null;

    private static void ApplyAdditional(
        IDictionary<string, object?> target,
        JsonElement? additional,
        string prefix)
    {
        if (additional is not { ValueKind: JsonValueKind.Object } value) return;
        foreach (var property in value.EnumerateObject())
        {
            var logicalName = property.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? property.Name[prefix.Length..]
                : property.Name;
            var name = MapAdditionalField(prefix, logicalName);
            if (string.IsNullOrWhiteSpace(name) || target.ContainsKey(name)) continue;
            target[name] = ToObject(property.Value);
        }
    }

    private static string? MapAdditionalField(string prefix, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.StartsWith("custom", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name[6..], out var customNumber))
            return $"cdefine{customNumber}";
        if (prefix.Equals("header.", StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith("longCustom", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name[10..], out var longCustomNumber))
            return $"chdefine{longCustomNumber}";
        if (name.StartsWith("cdefine", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("chdefine", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("cfree", StringComparison.OrdinalIgnoreCase))
            return name;
        if (prefix.Equals("header.", StringComparison.OrdinalIgnoreCase))
            return name.ToLowerInvariant() switch
            {
                "remark" => "cmemo",
                "departmentcode" => "cdepcode",
                "categorycode" => "crdcode",
                "productionbatch" => "cprobatch",
                _ => null
            };
        return name.ToLowerInvariant() switch
        {
            "materialcode" => "cinvcode",
            "batchno" => "cbatch",
            "locationcode" => "cposition",
            "quantity" => "iquantity",
            "remark" => "cbmemo",
            "projectclasscode" => "citem_class",
            "projectcode" => "citemcode",
            _ => null
        };
    }

    private static object? ToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.ToString()
    };

    private static object? Empty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ApiException InvalidArchive() => new(
        StatusCodes.Status409Conflict, "ARCHIVE_INVALID", "操作凭证内容无效，需要核对。");

    private sealed record PreparedGatewayOperation(
        MaterialOutboundExecutionOperation Operation,
        GatewayDocumentRequest Request,
        bool RedVoucher);

    private sealed record NormalizedRequest(
        DateOnly DocumentDate,
        string Direction,
        string? DepartmentCode,
        string? CategoryCode,
        string? ProductionBatch,
        string? Reason,
        bool HasProductionSource,
        JsonElement? HeaderAdditional,
        IReadOnlyList<NormalizedLine> Lines);

    private sealed record NormalizedLine(
        string LabelCode,
        string MaterialCode,
        decimal RequestedQty,
        int? SourceLineId,
        int? RequirementId,
        JsonElement? AdditionalFields);
}
