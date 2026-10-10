using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using U8Gateway.Contracts.Metadata;

namespace U8Gateway.Core.Documents
{
    public sealed class DocumentValidationIssue
    {
        public string Code { get; set; }
        public string Path { get; set; }
        public string Message { get; set; }
    }

    public sealed class DocumentPreparationResult
    {
        public DocumentPayload Payload { get; set; }
        public List<DocumentValidationIssue> Issues { get; set; } =
            new List<DocumentValidationIssue>();
        public bool IsValid => Issues.Count == 0;
    }

    /// <summary>
    /// Applies server defaults and validates the public write contract. U8 and
    /// database-dependent rules are deliberately left to the document adapter.
    /// </summary>
    public sealed class DocumentWritePreparer
    {
        public DocumentPreparationResult PrepareCreate(
            DocumentDefinition definition,
            DocumentOperationRequest request,
            DateTimeOffset now)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var result = NewResult(request);
            if (request == null)
            {
                Add(result, "REQUEST_REQUIRED", "$", "请求体不能为空。");
                return result;
            }

            result.Payload.Header = PrepareRow(
                definition, "header", request.Header, "header", true,
                request, now, 0, result);
            result.Payload.Lines = PrepareRows(
                definition, "line", request.Lines, "lines", true,
                request, now, result);
            result.Payload.Positions = PrepareRows(
                definition, "position", request.Positions, "positions", true,
                request, now, result);

            if (Fields(definition, "header").Any() && result.Payload.Header == null)
            {
                Add(result, "HEADER_REQUIRED", "header", "表头不能为空。");
            }
            if (Fields(definition, "line").Any() && result.Payload.Lines.Count == 0)
            {
                Add(result, "LINES_REQUIRED", "lines", "至少需要一条明细。");
            }
            return result;
        }

        public DocumentPreparationResult PreparePatch(
            DocumentDefinition definition,
            DocumentOperationRequest request,
            DateTimeOffset now)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var result = NewResult(request);
            if (request == null)
            {
                Add(result, "REQUEST_REQUIRED", "$", "请求体不能为空。");
                return result;
            }

            result.Payload.Header = PrepareRow(
                definition, "header", request.Header, "header", false,
                request, now, 0, result);
            result.Payload.Lines = PrepareRows(
                definition, "line", request.Lines, "lines", false,
                request, now, result);
            result.Payload.Positions = PrepareRows(
                definition, "position", request.Positions, "positions", false,
                request, now, result);
            if (result.Payload.Header == null &&
                result.Payload.Lines.Count == 0 &&
                result.Payload.Positions.Count == 0)
            {
                Add(result, "PATCH_EMPTY", "$", "没有可修改的内容。");
            }
            return result;
        }

        private static DocumentPreparationResult NewResult(
            DocumentOperationRequest request)
        {
            return new DocumentPreparationResult
            {
                Payload = new DocumentPayload
                {
                    Header = null,
                    Lines = new List<IDictionary<string, object>>(),
                    Positions = new List<IDictionary<string, object>>(),
                    Auxiliary = request?.Auxiliary == null
                        ? new Dictionary<string, IList<IDictionary<string, object>>>(
                            StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, IList<IDictionary<string, object>>>(
                            request.Auxiliary,
                            StringComparer.OrdinalIgnoreCase)
                }
            };
        }

        private static IList<IDictionary<string, object>> PrepareRows(
            DocumentDefinition definition,
            string scope,
            IList<IDictionary<string, object>> source,
            string path,
            bool create,
            DocumentOperationRequest request,
            DateTimeOffset now,
            DocumentPreparationResult result)
        {
            var rows = new List<IDictionary<string, object>>();
            if (source == null) return rows;
            for (int index = 0; index < source.Count; index++)
            {
                IDictionary<string, object> row = PrepareRow(
                    definition, scope, source[index], path + "[" + index + "]",
                    create, request, now, index + 1, result);
                if (row != null) rows.Add(row);
            }
            return rows;
        }

        private static IDictionary<string, object> PrepareRow(
            DocumentDefinition definition,
            string scope,
            IDictionary<string, object> source,
            string path,
            bool create,
            DocumentOperationRequest request,
            DateTimeOffset now,
            int rowNumber,
            DocumentPreparationResult result)
        {
            if (source == null)
            {
                if (create || !string.Equals(scope, "header", StringComparison.OrdinalIgnoreCase))
                    Add(result, "ROW_REQUIRED", path, "记录不能为空。");
                return null;
            }

            var target = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DocumentFieldDefinition> fields = Fields(definition, scope)
                .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            DocumentScopeWritePolicy scopePolicy = ScopePolicy(definition, scope);
            string action = Action(source);
            object rawAction;
            if (TryGet(source, "_action", out rawAction) && rawAction != null &&
                !string.Equals(Convert.ToString(rawAction), "add", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Convert.ToString(rawAction), "update", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Convert.ToString(rawAction), "delete", StringComparison.OrdinalIgnoreCase))
                Add(result, "ROW_ACTION_INVALID", path + "._action", "_action 只允许 add、update 或 delete。");
            bool rowCreate = create || string.Equals(action, "add", StringComparison.OrdinalIgnoreCase);
            bool rowDelete = string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase);

            if (!create && rowCreate && scopePolicy?.AllowCreateRows != true)
                Add(result, "ROW_ADD_NOT_ALLOWED", path, "不允许新增该记录。");
            if (!create && rowDelete && scopePolicy?.AllowDeleteRows != true)
                Add(result, "ROW_DELETE_NOT_ALLOWED", path, "不允许删除该记录。");
            if (!string.Equals(scope, "header", StringComparison.OrdinalIgnoreCase) && !create)
                ValidateMatchKey(source, scopePolicy, path, rowCreate, result);

            foreach (KeyValuePair<string, object> pair in source)
            {
                if (IsReserved(pair.Key))
                {
                    if (string.Equals(pair.Key, "_action", StringComparison.OrdinalIgnoreCase))
                        target["_action"] = action;
                    continue;
                }
                DocumentFieldDefinition field;
                if (!fields.TryGetValue(pair.Key, out field))
                {
                    Add(result, "FIELD_UNKNOWN", path + "." + pair.Key, "字段不存在。");
                    continue;
                }

                bool matchKey = scopePolicy?.MatchKeys.Any(x => string.Equals(
                    x, field.Name, StringComparison.OrdinalIgnoreCase)) == true;
                bool writable = rowCreate ? field.CreateWritable : field.UpdateWritable;
                if (!writable && !(matchKey && !rowCreate))
                {
                    Add(result, "FIELD_READ_ONLY", path + "." + field.Name, "字段不可写。");
                    continue;
                }
                if (rowDelete && !matchKey) continue;
                ValidateValue(field, pair.Value, path + "." + field.Name, result);
                target[field.Name] = pair.Value;
            }

            if (rowCreate && !rowDelete)
            {
                foreach (DocumentFieldDefinition field in fields.Values)
                {
                    if (!target.ContainsKey(field.Name))
                    {
                        object value;
                        if ((!string.Equals(field.DefaultRule, "rowNumber",
                                StringComparison.OrdinalIgnoreCase) || create) &&
                            TryDefault(field, request, now, rowNumber, out value))
                            target[field.Name] = value;
                    }
                    if (IsCreateRequired(field) && IsMissing(target, field.Name))
                        Add(result, "FIELD_REQUIRED", path + "." + field.Name, "字段不能为空。");
                }
            }
            return target;
        }

        private static void ValidateMatchKey(
            IDictionary<string, object> source,
            DocumentScopeWritePolicy policy,
            string path,
            bool rowCreate,
            DocumentPreparationResult result)
        {
            if (rowCreate) return;
            if (policy == null || policy.MatchKeys == null || policy.MatchKeys.Count == 0)
            {
                Add(result, "MATCH_KEY_NOT_CONFIGURED", path, "未配置记录匹配键。");
                return;
            }
            bool hasKey = policy.MatchKeys.Any(key => !IsMissing(source, key));
            if (!hasKey)
                Add(result, "MATCH_KEY_REQUIRED", path, "修改或删除记录必须提供匹配键。");
        }

        private static void ValidateValue(
            DocumentFieldDefinition field,
            object value,
            string path,
            DocumentPreparationResult result)
        {
            if (value == null)
            {
                if (!field.Clearable) Add(result, "FIELD_NOT_CLEARABLE", path, "字段不能清空。");
                return;
            }
            string text = value as string;
            if (field.MaxLength.HasValue && text != null && text.Length > field.MaxLength.Value)
                Add(result, "FIELD_TOO_LONG", path, "字段长度超过 " + field.MaxLength.Value + "。");

            string type = (field.DataType ?? "string").Trim().ToLowerInvariant();
            bool valid;
            switch (type)
            {
                case "boolean":
                    bool boolean;
                    valid = value is bool || bool.TryParse(Convert.ToString(value), out boolean);
                    break;
                case "date":
                case "datetime":
                case "datetime.tz":
                    DateTime date;
                    valid = value is DateTime || value is DateTimeOffset || DateTime.TryParse(
                        Convert.ToString(value), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out date);
                    break;
                case "integer":
                case "long":
                case "int":
                case "i1": case "ui1": case "i2": case "ui2":
                case "i4": case "ui4": case "i8": case "ui8":
                    long integer;
                    valid = long.TryParse(Convert.ToString(value), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out integer);
                    break;
                case "number":
                case "decimal":
                case "fixed.14.4":
                case "r4": case "r8":
                    decimal number;
                    valid = decimal.TryParse(Convert.ToString(value),
                        NumberStyles.Number | NumberStyles.AllowExponent,
                        CultureInfo.InvariantCulture, out number);
                    if (valid && field.Scale.HasValue)
                    {
                        int scale = DecimalScale(number);
                        if (scale > field.Scale.Value)
                            Add(result, "FIELD_SCALE_EXCEEDED", path,
                                "小数位不能超过 " + field.Scale.Value + " 位。");
                    }
                    break;
                default:
                    valid = true;
                    break;
            }
            if (!valid) Add(result, "FIELD_TYPE_INVALID", path, "字段类型不正确。");
        }

        private static bool TryDefault(
            DocumentFieldDefinition field,
            DocumentOperationRequest request,
            DateTimeOffset now,
            int rowNumber,
            out object value)
        {
            value = null;
            string rule = (field.DefaultRule ?? string.Empty).Trim().ToLowerInvariant();
            switch (rule)
            {
                case "today": value = now.Date; return true;
                case "now": value = now; return true;
                case "operatorname": value = request.OperatorName; return value != null;
                case "operatorid": value = request.OperatorId; return value != null;
                case "rownumber": value = rowNumber; return rowNumber > 0;
            }
            if (field.DefaultValue != null)
            {
                value = field.DefaultValue;
                return true;
            }
            return false;
        }

        private static int DecimalScale(decimal value)
        {
            int[] bits = decimal.GetBits(value);
            return (bits[3] >> 16) & 0x7F;
        }

        private static bool IsCreateRequired(DocumentFieldDefinition field)
        {
            return string.Equals(field.RequiredMode, "always", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.RequiredMode, "create", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMissing(IDictionary<string, object> values, string name)
        {
            if (values == null) return true;
            object value;
            if (!TryGet(values, name, out value) || value == null) return true;
            string text = value as string;
            return text != null && string.IsNullOrWhiteSpace(text);
        }

        private static bool TryGet(
            IDictionary<string, object> values,
            string name,
            out object value)
        {
            value = null;
            if (values == null) return false;
            if (values.TryGetValue(name, out value)) return true;
            foreach (KeyValuePair<string, object> pair in values)
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            return false;
        }

        private static string Action(IDictionary<string, object> row)
        {
            object value;
            if (!TryGet(row, "_action", out value) || value == null) return "update";
            string action = Convert.ToString(value).Trim().ToLowerInvariant();
            return action == "add" || action == "delete" ? action : "update";
        }

        private static bool IsReserved(string name)
        {
            return string.Equals(name, "_action", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<DocumentFieldDefinition> Fields(
            DocumentDefinition definition,
            string scope)
        {
            return (definition.Fields ?? new List<DocumentFieldDefinition>()).Where(x =>
                string.Equals(x.Scope, scope, StringComparison.OrdinalIgnoreCase));
        }

        private static DocumentScopeWritePolicy ScopePolicy(
            DocumentDefinition definition,
            string scope)
        {
            return (definition.WriteScopes ?? new List<DocumentScopeWritePolicy>())
                .FirstOrDefault(x => string.Equals(x.Scope, scope, StringComparison.OrdinalIgnoreCase));
        }

        private static void Add(
            DocumentPreparationResult result,
            string code,
            string path,
            string message)
        {
            result.Issues.Add(new DocumentValidationIssue
            {
                Code = code,
                Path = path,
                Message = message
            });
        }
    }
}
