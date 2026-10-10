using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using U8Gateway.Contracts.Metadata;
using U8Gateway.Core.Documents;

namespace U8Gateway.U8.Xml
{
    /// <summary>
    /// Merges a validated PATCH payload into the XML returned by U8 Load.
    /// The native schema, concurrency fields and unsupported attributes remain
    /// untouched. New/deleted rows use ADO editprop markers.
    /// </summary>
    public sealed class AdoPersistedXmlPatchMerger
    {
        private static readonly XNamespace Row = "#RowsetSchema";
        private static readonly XNamespace Rowset = "urn:schemas-microsoft-com:rowset";
        private readonly IU8XmlCodec _codec;

        public AdoPersistedXmlPatchMerger(IU8XmlCodec codec)
        {
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }

        public U8XmlPacket Merge(
            DocumentDefinition definition,
            U8XmlPacket original,
            DocumentPayload patch)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (patch == null) throw new ArgumentNullException(nameof(patch));

            XDocument header = Parse(original.HeaderXml, "header", true);
            XDocument body = Parse(original.BodyXml, "line", true);
            XDocument position = Parse(original.PositionXml, "position", false);

            if (patch.Header != null)
                MergeHeader(definition, header, patch.Header);
            if (patch.Lines != null && patch.Lines.Count > 0)
                body = MergeRows(definition, "line", body, patch.Lines);
            if (patch.Positions != null && patch.Positions.Count > 0)
                position = MergeRows(definition, "position", position, patch.Positions);

            return new U8XmlPacket
            {
                HeaderXml = Serialize(header),
                BodyXml = Serialize(body),
                PositionXml = Serialize(position)
            };
        }

        private static void MergeHeader(
            DocumentDefinition definition,
            XDocument document,
            IDictionary<string, object> patch)
        {
            XElement row = Rows(document).SingleOrDefault();
            if (row == null)
                throw Error("HEADER_ROW_MISSING", "U8 原单表头不存在。");
            ApplyValues(definition, "header", row, patch, false);
            SetEditProp(row, "M");
        }

        private XDocument MergeRows(
            DocumentDefinition definition,
            string scope,
            XDocument document,
            IList<IDictionary<string, object>> patches)
        {
            DocumentScopeWritePolicy policy = (definition.WriteScopes ??
                new List<DocumentScopeWritePolicy>()).FirstOrDefault(x =>
                    string.Equals(x.Scope, scope, StringComparison.OrdinalIgnoreCase));
            if (policy == null)
                throw Error("WRITE_POLICY_MISSING", "缺少 " + scope + " 写入策略。");

            bool needsAdd = patches.Any(x => string.Equals(
                Action(x), "add", StringComparison.OrdinalIgnoreCase));
            if (document == null && needsAdd)
                document = EmptyScopeDocument(definition, scope);
            if (document == null)
                throw Error("ORIGINAL_SCOPE_XML_MISSING", "U8 原单缺少 " + scope + " XML。");

            XElement data = document.Descendants(Rowset + "data").SingleOrDefault();
            if (data == null)
                throw Error("ROWSET_DATA_MISSING", "U8 原单 " + scope + " XML 缺少数据节点。");

            foreach (IDictionary<string, object> item in patches)
            {
                string action = Action(item);
                if (string.Equals(action, "add", StringComparison.OrdinalIgnoreCase))
                {
                    if (!policy.AllowCreateRows)
                        throw Error("ROW_ADD_NOT_ALLOWED", "不允许新增 " + scope + " 记录。");
                    var row = new XElement(Row + "row");
                    ApplyValues(definition, scope, row, item, true);
                    ApplyNewRowDefaults(definition, scope, document, row);
                    SetEditProp(row, "A");
                    data.Add(row);
                    continue;
                }

                XElement existing = FindRow(definition, scope, document, item, policy);
                if (string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    if (!policy.AllowDeleteRows)
                        throw Error("ROW_DELETE_NOT_ALLOWED", "不允许删除 " + scope + " 记录。");
                    SetEditProp(existing, "D");
                    continue;
                }
                ApplyValues(definition, scope, existing, item, false);
                SetEditProp(existing, "M");
            }
            return document;
        }

        private static XElement FindRow(
            DocumentDefinition definition,
            string scope,
            XDocument document,
            IDictionary<string, object> patch,
            DocumentScopeWritePolicy policy)
        {
            Dictionary<string, DocumentFieldDefinition> fields = ScopeFields(definition, scope);
            foreach (string key in policy.MatchKeys ?? new List<string>())
            {
                object value;
                DocumentFieldDefinition field;
                if (!TryGet(patch, key, out value) || value == null ||
                    !fields.TryGetValue(key, out field)) continue;
                string expected = Format(value);
                List<XElement> matches = Rows(document).Where(row => string.Equals(
                    AttributeValue(row, U8Name(field)), expected,
                    StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1) return matches[0];
                if (matches.Count > 1)
                    throw Error("ROW_MATCH_AMBIGUOUS", scope + " 匹配键重复：" + key + "=" + expected);
            }
            throw Error("ROW_NOT_FOUND", scope + " 记录不存在或未提供有效匹配键。");
        }

        private static void ApplyValues(
            DocumentDefinition definition,
            string scope,
            XElement row,
            IDictionary<string, object> values,
            bool create)
        {
            Dictionary<string, DocumentFieldDefinition> fields = ScopeFields(definition, scope);
            foreach (KeyValuePair<string, object> pair in values)
            {
                if (string.Equals(pair.Key, "_action", StringComparison.OrdinalIgnoreCase))
                    continue;
                DocumentFieldDefinition field;
                if (!fields.TryGetValue(pair.Key, out field)) continue;
                if (create ? !field.CreateWritable : !field.UpdateWritable) continue;
                XAttribute existing = Attribute(row, U8Name(field));
                if (pair.Value == null)
                {
                    existing?.Remove();
                    continue;
                }
                if (existing == null)
                    row.Add(new XAttribute(U8Name(field), Format(pair.Value)));
                else
                    existing.Value = Format(pair.Value);
            }
        }

        private static void ApplyNewRowDefaults(
            DocumentDefinition definition,
            string scope,
            XDocument document,
            XElement row)
        {
            foreach (DocumentFieldDefinition field in ScopeFields(definition, scope).Values)
            {
                if (!string.Equals(field.DefaultRule, "rowNumber",
                    StringComparison.OrdinalIgnoreCase) ||
                    Attribute(row, U8Name(field)) != null) continue;
                long maximum = 0;
                foreach (XElement existing in Rows(document))
                {
                    long current;
                    if (long.TryParse(AttributeValue(existing, U8Name(field)),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out current) &&
                        current > maximum) maximum = current;
                }
                row.Add(new XAttribute(U8Name(field), maximum + 1));
            }
        }

        private XDocument EmptyScopeDocument(DocumentDefinition definition, string scope)
        {
            var payload = new DocumentPayload
            {
                Header = string.Equals(scope, "header", StringComparison.OrdinalIgnoreCase)
                    ? new Dictionary<string, object>() : null,
                Lines = string.Equals(scope, "line", StringComparison.OrdinalIgnoreCase)
                    ? new List<IDictionary<string, object>>() : null,
                Positions = string.Equals(scope, "position", StringComparison.OrdinalIgnoreCase)
                    ? new List<IDictionary<string, object>>() : null
            };
            U8XmlPacket packet = _codec.Encode(definition, payload);
            string xml = string.Equals(scope, "line", StringComparison.OrdinalIgnoreCase)
                ? packet.BodyXml : packet.PositionXml;
            return Parse(xml, scope, true);
        }

        private static Dictionary<string, DocumentFieldDefinition> ScopeFields(
            DocumentDefinition definition,
            string scope)
        {
            return (definition.Fields ?? new List<DocumentFieldDefinition>())
                .Where(x => string.Equals(x.Scope, scope, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<XElement> Rows(XDocument document)
        {
            return document == null
                ? Enumerable.Empty<XElement>()
                : document.Descendants(Row + "row");
        }

        private static XDocument Parse(string xml, string scope, bool required)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                if (required) throw Error("ORIGINAL_XML_MISSING", "U8 原单缺少 " + scope + " XML。");
                return null;
            }
            try
            {
                return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch (Exception exception)
            {
                throw new DocumentXmlMergeException(
                    "ORIGINAL_XML_INVALID", "U8 原单 " + scope + " XML 无效。", exception);
            }
        }

        private static string Serialize(XDocument document)
        {
            return document?.ToString(SaveOptions.DisableFormatting);
        }

        private static string Action(IDictionary<string, object> values)
        {
            object value;
            if (!TryGet(values, "_action", out value) || value == null) return "update";
            return Convert.ToString(value, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
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

        private static XAttribute Attribute(XElement element, string name)
        {
            return element.Attributes().FirstOrDefault(x => string.Equals(
                x.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string AttributeValue(XElement element, string name)
        {
            return Attribute(element, name)?.Value;
        }

        private static void SetEditProp(XElement row, string value)
        {
            XAttribute attribute = Attribute(row, "editprop");
            if (attribute == null) row.Add(new XAttribute("editprop", value));
            else attribute.Value = value;
        }

        private static string U8Name(DocumentFieldDefinition field)
        {
            return string.IsNullOrWhiteSpace(field.U8Field) ? field.Name : field.U8Field;
        }

        private static string Format(object value)
        {
            if (value is DateTime)
                return ((DateTime)value).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset)
                return ((DateTimeOffset)value).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
            if (value is bool) return (bool)value ? "true" : "false";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static DocumentXmlMergeException Error(string code, string message)
        {
            return new DocumentXmlMergeException(code, message);
        }
    }

    public sealed class DocumentXmlMergeException : InvalidOperationException
    {
        public DocumentXmlMergeException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public DocumentXmlMergeException(string code, string message, Exception innerException)
            : base(message, innerException)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
