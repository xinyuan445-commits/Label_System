using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LabelSystem.Api;

public sealed class LabelCodeRuleSpec
{
    public int SchemaVersion { get; set; } = 1;
    public string[] Symbologies { get; set; } = [];
    public string CaseMode { get; set; } = "PRESERVE";
    public List<LabelCodeSegmentSpec> Segments { get; set; } = [];
}

public sealed class LabelCodeSegmentSpec
{
    public string Kind { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? Field { get; set; }
    public int? Length { get; set; }
    public int? MaxLength { get; set; }
    public string? Encoding { get; set; }
    public string? ExpectedValue { get; set; }
    public int? DecimalScale { get; set; }
    public bool ValidateCurrentAccount { get; set; }
    public string? DateFormat { get; set; }
    public string? Algorithm { get; set; }
}

public sealed record ValidatedLabelCodeRuleSpec(
    LabelCodeRuleSpec Spec,
    string Json,
    string Sha256,
    int MaximumLength);

public sealed record LabelCodeParseAttempt(
    bool Matched,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    string NormalizedCode,
    IReadOnlyDictionary<string, string>? Fields);

public sealed class LabelCodeRuleEngine
{
    private const int MaximumCodeLength = 2048;
    private static readonly HashSet<string> SupportedSymbologies = new(StringComparer.OrdinalIgnoreCase)
    {
        "CODE128", "CODE39", "CODE93", "EAN13", "EAN8", "UPCA", "UPCE",
        "ITF14", "ITF", "CODABAR", "QR", "DATAMATRIX", "PDF417", "AZTEC",
        "GS1_128", "GS1_DATAMATRIX"
    };

    private static readonly HashSet<string> SupportedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "ERP_ACCOUNT_ID", "LABEL_TYPE", "MATERIAL_CODE", "BATCH_NO",
        "ISSUED_QUANTITY", "UNIT_CODE", "MANUFACTURE_DATE", "SOURCE_DOCUMENT_NO",
        "LABEL_UNIQUE_VALUE"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public IReadOnlyList<string> GetSupportedSymbologies() =>
        SupportedSymbologies.Order(StringComparer.Ordinal).ToArray();

    public ValidatedLabelCodeRuleSpec Validate(JsonElement element)
    {
        LabelCodeRuleSpec? spec;
        try
        {
            spec = element.Deserialize<LabelCodeRuleSpec>(JsonOptions);
        }
        catch (JsonException)
        {
            throw Invalid("规则格式无效。");
        }

        if (spec is null || spec.SchemaVersion != 1)
            throw Invalid("规则版本无效。");

        spec.CaseMode = NormalizeChoice(spec.CaseMode, "PRESERVE", "UPPER", "LOWER");
        spec.Symbologies = spec.Symbologies
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (spec.Symbologies.Length is < 1 or > 16 ||
            spec.Symbologies.Any(value => !SupportedSymbologies.Contains(value)))
            throw Invalid("请选择支持的条码类型。");
        if (spec.Segments.Count is < 1 or > 50)
            throw Invalid("编码字段数量无效。");

        var maximumLength = 0;
        var dynamicFieldIndexes = new List<int>();
        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequenceCount = 0;
        var checksumCount = 0;
        for (var index = 0; index < spec.Segments.Count; index++)
        {
            var segment = spec.Segments[index];
            segment.Kind = segment.Kind.Trim().ToUpperInvariant();
            switch (segment.Kind)
            {
                case "LITERAL":
                    segment.Value = ValidateText(segment.Value, 1, 64, "固定内容无效。");
                    maximumLength += segment.Value.Length;
                    break;
                case "SEPARATOR":
                    segment.Value = ValidateText(segment.Value, 1, 8, "分隔符无效。");
                    maximumLength += segment.Value.Length;
                    break;
                case "FIELD":
                    segment.Field = segment.Field?.Trim().ToUpperInvariant();
                    if (segment.Field is null || !SupportedFields.Contains(segment.Field) ||
                        !fieldNames.Add(segment.Field))
                        throw Invalid("业务字段无效或重复。");
                    segment.Encoding = NormalizeChoice(segment.Encoding ?? "NONE", "NONE", "PERCENT");
                    if (!string.IsNullOrWhiteSpace(segment.ExpectedValue))
                        segment.ExpectedValue = ValidateText(
                            segment.ExpectedValue, 1, 200, "字段限定值无效。");
                    else
                        segment.ExpectedValue = null;
                    if (segment.DecimalScale.HasValue)
                    {
                        if (segment.Field != "ISSUED_QUANTITY" ||
                            segment.DecimalScale is < 0 or > 6 ||
                            segment.Encoding != "NONE")
                            throw Invalid("小数位数配置无效。");
                    }
                    if (segment.Length is <= 0 or > 200)
                        segment.Length = null;
                    if (segment.MaxLength is <= 0 or > 200)
                        segment.MaxLength = segment.Length ?? 100;
                    if (segment.Length.HasValue && segment.MaxLength != segment.Length)
                        segment.MaxLength = segment.Length;
                    if (segment.ValidateCurrentAccount && segment.Field != "ERP_ACCOUNT_ID")
                        throw Invalid("只有账套字段可以校验当前账套。");
                    if (!segment.Length.HasValue) dynamicFieldIndexes.Add(index);
                    maximumLength += segment.Encoding == "PERCENT"
                        ? segment.MaxLength!.Value * 3
                        : segment.MaxLength!.Value;
                    break;
                case "DATE":
                    segment.DateFormat = NormalizeChoice(
                        segment.DateFormat ?? "yyyyMMdd", "yyyyMMdd", "yyMMdd", "yyyyMMddHHmmss");
                    maximumLength += segment.DateFormat.Length;
                    break;
                case "SEQUENCE":
                    sequenceCount++;
                    if (segment.Length is < 1 or > 18)
                        throw Invalid("流水号长度应为 1 到 18 位。");
                    maximumLength += segment.Length.GetValueOrDefault();
                    break;
                case "CHECKSUM":
                    checksumCount++;
                    segment.Algorithm = NormalizeChoice(
                        segment.Algorithm ?? "SHA256_8", "SHA256_8", "EAN_MOD10");
                    if (index != spec.Segments.Count - 1)
                        throw Invalid("校验位必须放在编码末尾。");
                    maximumLength += segment.Algorithm == "EAN_MOD10" ? 1 : 8;
                    break;
                default:
                    throw Invalid("编码字段类型无效。");
            }
        }

        if (sequenceCount > 1 || checksumCount > 1)
            throw Invalid("流水号和校验位最多各配置一次。");
        foreach (var index in dynamicFieldIndexes)
        {
            if (index == spec.Segments.Count - 1) continue;
            var next = spec.Segments[index + 1];
            if (next.Kind is not ("LITERAL" or "SEPARATOR" or "CHECKSUM"))
                throw Invalid("可变长度字段后需要分隔符或固定内容。");
        }
        if (maximumLength > MaximumCodeLength)
            throw Invalid("编码最大长度超过限制。");

        var json = JsonSerializer.Serialize(spec, JsonOptions);
        return new ValidatedLabelCodeRuleSpec(
            spec,
            json,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            maximumLength);
    }

    public LabelCodePreviewResult Preview(
        JsonElement specElement,
        JsonElement valuesElement,
        string currentAccount,
        string? requestedSymbology)
    {
        var validated = Validate(specElement);
        var values = ReadValues(valuesElement);
        var code = Render(validated.Spec, values, currentAccount);
        var symbology = string.IsNullOrWhiteSpace(requestedSymbology)
            ? validated.Spec.Symbologies[0]
            : requestedSymbology.Trim().ToUpperInvariant();
        if (!validated.Spec.Symbologies.Contains(symbology, StringComparer.Ordinal))
            throw Invalid("当前规则不支持该条码类型。");
        ValidateCarrier(symbology, code);
        var parsed = ParseCode(validated.Spec, code, symbology, currentAccount);
        if (!parsed.Success || parsed.Fields is null)
            throw Invalid(parsed.ErrorMessage ?? "生成结果无法按当前规则解析。");
        return new LabelCodePreviewResult(
            code, code.Length, parsed.Fields, validated.Spec.Symbologies, validated.Sha256);
    }

    public string Render(
        LabelCodeRuleSpec spec,
        IReadOnlyDictionary<string, string> values,
        string currentAccount)
    {
        var builder = new StringBuilder();
        foreach (var segment in spec.Segments)
        {
            switch (segment.Kind)
            {
                case "LITERAL":
                case "SEPARATOR":
                    builder.Append(ApplyCase(segment.Value!, spec.CaseMode));
                    break;
                case "FIELD":
                    var hasValue = values.TryGetValue(segment.Field!, out var value);
                    var raw = hasValue
                        ? value!
                        : segment.ExpectedValue ??
                          (segment.Field == "ERP_ACCOUNT_ID" ? currentAccount : string.Empty);
                    if (string.IsNullOrEmpty(raw))
                        throw Invalid($"缺少字段 {segment.Field}。",
                            "LABEL_CODE_VALUE_REQUIRED");
                    raw = NormalizeFieldValue(segment, raw);
                    if (segment.ExpectedValue is not null &&
                        !string.Equals(
                            ApplyCase(raw, spec.CaseMode),
                            ApplyCase(segment.ExpectedValue, spec.CaseMode),
                            StringComparison.Ordinal))
                        throw Invalid($"字段 {segment.Field} 不符合限定值。",
                            segment.Field == "ERP_ACCOUNT_ID"
                                ? "ERP_ACCOUNT_MISMATCH"
                                : "LABEL_CODE_VALUE_INVALID");
                    var encoded = segment.Encoding == "PERCENT"
                        ? Uri.EscapeDataString(raw)
                        : raw;
                    encoded = ApplyCase(encoded, spec.CaseMode);
                    if (segment.Length.HasValue && encoded.Length != segment.Length.Value)
                        throw Invalid($"字段 {segment.Field} 长度不符合规则。",
                            "LABEL_CODE_VALUE_INVALID");
                    if (encoded.Length > segment.MaxLength)
                        throw Invalid($"字段 {segment.Field} 过长。",
                            "LABEL_CODE_VALUE_INVALID");
                    if (segment.Encoding == "NONE" && ContainsFutureSeparator(spec, segment, raw))
                        throw Invalid($"字段 {segment.Field} 包含规则分隔符。",
                            "LABEL_CODE_SEPARATOR_CONFLICT");
                    builder.Append(encoded);
                    break;
                case "DATE":
                    var date = values.TryGetValue("CURRENT_DATE", out var dateValue) &&
                               DateTimeOffset.TryParse(dateValue, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AssumeLocal, out var parsedDate)
                        ? parsedDate
                        : BusinessClock.Now;
                    builder.Append(date.ToString(segment.DateFormat, CultureInfo.InvariantCulture));
                    break;
                case "SEQUENCE":
                    var sequence = values.TryGetValue("SEQUENCE", out var sequenceValue) &&
                                   long.TryParse(sequenceValue, NumberStyles.None,
                                       CultureInfo.InvariantCulture, out var parsedSequence)
                        ? parsedSequence
                        : 1;
                    if (sequence < 0)
                        throw Invalid("流水号无效。", "LABEL_CODE_VALUE_INVALID");
                    var sequenceText = sequence.ToString(CultureInfo.InvariantCulture)
                        .PadLeft(segment.Length!.Value, '0');
                    if (sequenceText.Length > segment.Length.Value)
                        throw Invalid("流水号超过规则长度。", "LABEL_CODE_SEQUENCE_OVERFLOW");
                    builder.Append(sequenceText);
                    break;
                case "CHECKSUM":
                    builder.Append(CalculateChecksum(builder.ToString(), segment.Algorithm!));
                    break;
            }
        }
        var code = builder.ToString();
        if (code.Length is < 1 or > MaximumCodeLength || code.Any(char.IsControl))
            throw Invalid("生成的编码无效。", "LABEL_CODE_VALUE_INVALID");
        return code;
    }

    public LabelCodeParseAttempt ParseCode(
        LabelCodeRuleSpec spec,
        string code,
        string? symbology,
        string currentAccount)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaximumCodeLength)
            return Failed(false, "LABEL_CODE_INVALID", "标签编码无效。", code);
        var normalized = ApplyCase(code.Trim(), spec.CaseMode);
        if (!string.IsNullOrWhiteSpace(symbology))
        {
            var carrier = symbology.Trim().ToUpperInvariant();
            if (!spec.Symbologies.Contains(carrier, StringComparer.Ordinal))
                return Failed(false, "LABEL_CODE_SYMBOLOGY_MISMATCH", "条码类型不匹配。", normalized);
            try { ValidateCarrier(carrier, normalized); }
            catch (ApiException exception)
            {
                return Failed(true, exception.Code, exception.Message, normalized);
            }
        }

        var pattern = BuildPattern(spec);
        Match match;
        try
        {
            match = Regex.Match(normalized, pattern,
                RegexOptions.CultureInvariant | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException)
        {
            return Failed(false, "LABEL_CODE_PARSE_TIMEOUT", "标签编码解析超时。", normalized);
        }
        if (!match.Success)
            return Failed(false, "LABEL_CODE_RULE_NOT_MATCHED", "标签编码不符合规则。", normalized);

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < spec.Segments.Count; index++)
        {
            var segment = spec.Segments[index];
            var group = match.Groups[$"s{index}"];
            if (!group.Success) continue;
            if (segment.Kind == "FIELD")
            {
                string value;
                try
                {
                    value = segment.Encoding == "PERCENT"
                        ? Uri.UnescapeDataString(group.Value)
                        : group.Value;
                }
                catch (UriFormatException)
                {
                    return Failed(true, "LABEL_CODE_ENCODING_INVALID",
                        "标签字段编码无效。", normalized);
                }
                fields[segment.Field!] = value;
                if (!ValidateParsedField(segment, value, spec.CaseMode, out var fieldError))
                    return Failed(true,
                        segment.Field == "ERP_ACCOUNT_ID" && segment.ExpectedValue is not null
                            ? "ERP_ACCOUNT_MISMATCH"
                            : "LABEL_CODE_VALUE_INVALID",
                        fieldError!, normalized);
                if (segment.ValidateCurrentAccount &&
                    !string.Equals(value, currentAccount, StringComparison.OrdinalIgnoreCase))
                    return Failed(true, "ERP_ACCOUNT_MISMATCH",
                        "该标签不属于当前账套。", normalized);
            }
            else if (segment.Kind == "DATE")
            {
                fields["CURRENT_DATE"] = group.Value;
            }
            else if (segment.Kind == "SEQUENCE")
            {
                fields["SEQUENCE"] = group.Value;
            }
        }

        var checksum = spec.Segments.LastOrDefault(segment => segment.Kind == "CHECKSUM");
        if (checksum is not null)
        {
            var length = checksum.Algorithm == "EAN_MOD10" ? 1 : 8;
            var expected = CalculateChecksum(normalized[..^length], checksum.Algorithm!);
            if (!string.Equals(normalized[^length..], expected, StringComparison.OrdinalIgnoreCase))
                return Failed(true, "LABEL_CODE_CHECKSUM_INVALID", "标签校验位错误。", normalized);
        }
        return new LabelCodeParseAttempt(true, true, null, null, normalized, fields);
    }

    public void ValidateCarrier(string symbology, string code)
    {
        symbology = symbology.Trim().ToUpperInvariant();
        if (!SupportedSymbologies.Contains(symbology))
            throw Invalid("不支持该条码类型。", "LABEL_CODE_SYMBOLOGY_INVALID");
        if (code.Length is < 1 or > MaximumCodeLength || code.Any(char.IsControl))
            throw Invalid("编码内容无效。", "LABEL_CODE_VALUE_INVALID");

        switch (symbology)
        {
            case "EAN13": ValidateNumericCarrier(code, 13, true); break;
            case "EAN8": ValidateNumericCarrier(code, 8, true); break;
            case "UPCA": ValidateNumericCarrier(code, 12, true); break;
            case "UPCE": ValidateNumericCarrier(code, 8, false); break;
            case "ITF14": ValidateNumericCarrier(code, 14, true); break;
            case "ITF":
                if (code.Length % 2 != 0 || !code.All(char.IsDigit))
                    throw Invalid("ITF 编码必须为偶数位数字。", "LABEL_CODE_CARRIER_CONSTRAINT");
                break;
            case "CODE39":
                const string code39 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
                if (code.Any(character => !code39.Contains(character)))
                    throw Invalid("Code 39 内容包含不支持的字符。", "LABEL_CODE_CARRIER_CONSTRAINT");
                break;
            case "CODABAR":
                const string codabar = "0123456789-$:/.+ABCD";
                if (code.Any(character => !codabar.Contains(char.ToUpperInvariant(character))))
                    throw Invalid("Codabar 内容包含不支持的字符。", "LABEL_CODE_CARRIER_CONSTRAINT");
                break;
            case "CODE128":
            case "CODE93":
            case "GS1_128":
                if (code.Any(character => character is < ' ' or > '~'))
                    throw Invalid("一维码内容必须使用可打印 ASCII 字符。", "LABEL_CODE_CARRIER_CONSTRAINT");
                break;
        }
    }

    private static Dictionary<string, string> ReadValues(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid("预览字段无效。", "LABEL_CODE_VALUE_INVALID");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name.Trim().ToUpperInvariant()] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }
        return result;
    }

    private static string NormalizeFieldValue(LabelCodeSegmentSpec segment, string value)
    {
        if (!segment.DecimalScale.HasValue) return value;
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number))
            throw Invalid($"字段 {segment.Field} 不是有效数量。", "LABEL_CODE_VALUE_INVALID");
        return number.ToString($"F{segment.DecimalScale.Value}", CultureInfo.InvariantCulture);
    }

    private static bool ValidateParsedField(
        LabelCodeSegmentSpec segment,
        string value,
        string caseMode,
        out string? error)
    {
        if (segment.ExpectedValue is not null &&
            !string.Equals(
                ApplyCase(value, caseMode),
                ApplyCase(segment.ExpectedValue, caseMode),
                StringComparison.Ordinal))
        {
            error = segment.Field == "ERP_ACCOUNT_ID"
                ? "该标签不属于当前限定账套。"
                : $"字段 {segment.Field} 不符合限定值。";
            return false;
        }
        if (segment.DecimalScale.HasValue)
        {
            var scale = segment.DecimalScale.Value;
            var pattern = scale == 0 ? @"^-?[0-9]+$" : $@"^-?[0-9]+\.[0-9]{{{scale}}}$";
            if (!Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)))
            {
                error = $"字段 {segment.Field} 必须保留 {scale} 位小数。";
                return false;
            }
        }
        error = null;
        return true;
    }

    private static string BuildPattern(LabelCodeRuleSpec spec)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < spec.Segments.Count; index++)
        {
            var segment = spec.Segments[index];
            switch (segment.Kind)
            {
                case "LITERAL":
                case "SEPARATOR":
                    builder.Append(Regex.Escape(ApplyCase(segment.Value!, spec.CaseMode)));
                    break;
                case "FIELD":
                    builder.Append(segment.Length.HasValue
                        ? $"(?<s{index}>.{{{segment.Length.Value}}})"
                        : $"(?<s{index}>.{{1,{segment.MaxLength}}}?)");
                    break;
                case "DATE":
                    builder.Append($"(?<s{index}>[0-9]{{{segment.DateFormat!.Length}}})");
                    break;
                case "SEQUENCE":
                    builder.Append($"(?<s{index}>[0-9]{{{segment.Length}}})");
                    break;
                case "CHECKSUM":
                    builder.Append(segment.Algorithm == "EAN_MOD10"
                        ? $"(?<s{index}>[0-9])"
                        : $"(?<s{index}>[0-9A-Fa-f]{{8}})");
                    break;
            }
        }
        builder.Append('$');
        return builder.ToString();
    }

    private static string CalculateChecksum(string value, string algorithm)
    {
        if (algorithm == "EAN_MOD10")
        {
            if (!value.All(char.IsDigit))
                throw Invalid("EAN 校验位之前只能包含数字。", "LABEL_CODE_CHECKSUM_INVALID");
            var sum = 0;
            var weightThree = true;
            for (var index = value.Length - 1; index >= 0; index--)
            {
                sum += (value[index] - '0') * (weightThree ? 3 : 1);
                weightThree = !weightThree;
            }
            return ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
    }

    private static void ValidateNumericCarrier(string code, int length, bool validateCheckDigit)
    {
        if (code.Length != length || !code.All(char.IsDigit))
            throw Invalid($"编码必须为 {length} 位数字。", "LABEL_CODE_CARRIER_CONSTRAINT");
        if (validateCheckDigit && CalculateChecksum(code[..^1], "EAN_MOD10") != code[^1..])
            throw Invalid("编码校验位错误。", "LABEL_CODE_CARRIER_CONSTRAINT");
    }

    private static bool ContainsFutureSeparator(
        LabelCodeRuleSpec spec,
        LabelCodeSegmentSpec current,
        string value)
    {
        var foundCurrent = false;
        foreach (var segment in spec.Segments)
        {
            if (ReferenceEquals(segment, current))
            {
                foundCurrent = true;
                continue;
            }
            if (foundCurrent && segment.Kind == "SEPARATOR" &&
                value.Contains(segment.Value!, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string ValidateText(string? value, int minimum, int maximum, string message)
    {
        if (value is null || value.Length < minimum || value.Length > maximum || value.Any(char.IsControl))
            throw Invalid(message);
        return value;
    }

    private static string NormalizeChoice(string value, params string[] choices)
    {
        var matched = choices.FirstOrDefault(choice =>
            string.Equals(choice, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return matched ?? throw Invalid("规则选项无效。");
    }

    private static string ApplyCase(string value, string caseMode) => caseMode switch
    {
        "UPPER" => value.ToUpperInvariant(),
        "LOWER" => value.ToLowerInvariant(),
        _ => value
    };

    private static LabelCodeParseAttempt Failed(
        bool matched,
        string code,
        string message,
        string normalized) => new(matched, false, code, message, normalized, null);

    private static ApiException Invalid(
        string message,
        string code = "LABEL_CODE_RULE_INVALID") =>
        new(StatusCodes.Status400BadRequest, code, message);
}
