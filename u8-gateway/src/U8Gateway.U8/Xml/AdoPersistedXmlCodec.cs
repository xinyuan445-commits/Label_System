using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using U8Gateway.Contracts.Metadata;
using U8Gateway.Core.Documents;

namespace U8Gateway.U8.Xml
{
    public sealed class AdoPersistedXmlCodec : IU8XmlCodec
    {
        private static readonly XNamespace Schema =
            "uuid:BDC6E3F0-6DA3-11d1-A2A3-00AA00C14882";
        private static readonly XNamespace DataType =
            "uuid:C2F41010-65B3-11d1-A29F-00AA00C14882";
        private static readonly XNamespace Rowset =
            "urn:schemas-microsoft-com:rowset";
        private static readonly XNamespace Row = "#RowsetSchema";

        public U8XmlPacket Encode(
            DocumentDefinition definition,
            DocumentPayload payload)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            return new U8XmlPacket
            {
                HeaderXml = EncodeScope(
                    definition,
                    "header",
                    payload.Header == null
                        ? Enumerable.Empty<IDictionary<string, object>>()
                        : (IEnumerable<IDictionary<string, object>>)new[]
                        {
                            payload.Header
                        }),
                BodyXml = EncodeScope(
                    definition,
                    "line",
                    payload.Lines),
                PositionXml = EncodeScope(
                    definition,
                    "position",
                    payload.Positions)
            };
        }

        public DocumentPayload Decode(
            DocumentDefinition definition,
            U8XmlPacket packet)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            if (packet == null)
            {
                throw new ArgumentNullException(nameof(packet));
            }

            IList<IDictionary<string, object>> header = DecodeScope(
                definition,
                "header",
                packet.HeaderXml);
            return new DocumentPayload
            {
                Header = header.FirstOrDefault(),
                Lines = DecodeScope(definition, "line", packet.BodyXml),
                Positions = DecodeScope(
                    definition,
                    "position",
                    packet.PositionXml),
                Auxiliary = new Dictionary<string, IList<IDictionary<string, object>>>(
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private static string EncodeScope(
            DocumentDefinition definition,
            string scope,
            IEnumerable<IDictionary<string, object>> rows)
        {
            List<DocumentFieldDefinition> fields = FieldsForScope(
                definition,
                scope).ToList();
            if (fields.Count == 0)
            {
                return null;
            }

            var root = new XElement("xml",
                new XAttribute(XNamespace.Xmlns + "s", Schema),
                new XAttribute(XNamespace.Xmlns + "dt", DataType),
                new XAttribute(XNamespace.Xmlns + "rs", Rowset),
                new XAttribute(XNamespace.Xmlns + "z", Row));
            var elementType = new XElement(Schema + "ElementType",
                new XAttribute("name", "row"),
                new XAttribute("content", "eltOnly"),
                new XAttribute(Rowset + "updatable", "true"));

            int ordinal = 1;
            foreach (DocumentFieldDefinition field in fields)
            {
                string u8Name = U8Name(field);
                var attributeType = new XElement(Schema + "AttributeType",
                    new XAttribute("name", u8Name),
                    new XAttribute(Rowset + "number", ordinal++));
                if (field.AdoSchemaNullable == true)
                {
                    attributeType.Add(new XAttribute(Rowset + "nullable", "true"));
                }
                attributeType.Add(new XAttribute(Rowset + "writeunknown", "true"));
                AddOptional(attributeType, Rowset + "basetable", field.AdoBaseTable);
                AddOptional(attributeType, Rowset + "basecolumn", field.AdoBaseColumn);
                if (field.AdoKeyColumn == true)
                {
                    attributeType.Add(new XAttribute(Rowset + "keycolumn", "true"));
                }

                var dataType = new XElement(Schema + "datatype",
                    new XAttribute(DataType + "type", ToAdoType(field.DataType)));
                AddOptional(dataType, Rowset + "dbtype", field.AdoDbType);
                if (field.MaxLength.HasValue)
                {
                    dataType.Add(new XAttribute(
                        DataType + "maxLength",
                        field.MaxLength.Value));
                }
                if (field.Precision.HasValue)
                {
                    dataType.Add(new XAttribute(
                        Rowset + "precision",
                        field.Precision.Value));
                }
                if (field.Scale.HasValue)
                {
                    dataType.Add(new XAttribute(
                        Rowset + "scale",
                        field.Scale.Value));
                }
                if (field.AdoFixedLength == true)
                {
                    dataType.Add(new XAttribute(Rowset + "fixedlength", "true"));
                }
                if (field.AdoMayBeNull.HasValue)
                {
                    dataType.Add(new XAttribute(
                        Rowset + "maybenull",
                        field.AdoMayBeNull.Value ? "true" : "false"));
                }
                attributeType.Add(dataType);
                elementType.Add(attributeType);
            }
            elementType.Add(new XElement(
                Schema + "extends",
                new XAttribute("type", "rs:rowbase")));
            root.Add(new XElement(
                Schema + "Schema",
                new XAttribute("id", "RowsetSchema"),
                elementType));

            var data = new XElement(Rowset + "data");
            foreach (IDictionary<string, object> source in
                rows ?? Enumerable.Empty<IDictionary<string, object>>())
            {
                var row = new XElement(Row + "row");
                foreach (DocumentFieldDefinition field in fields)
                {
                    object value;
                    if (TryGetValue(source, field.Name, out value) && value != null)
                    {
                        row.Add(new XAttribute(
                            U8Name(field),
                            FormatValue(value)));
                    }
                }
                data.Add(row);
            }
            root.Add(data);
            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                root).ToString(SaveOptions.DisableFormatting);
        }

        private static IList<IDictionary<string, object>> DecodeScope(
            DocumentDefinition definition,
            string scope,
            string xml)
        {
            var result = new List<IDictionary<string, object>>();
            if (string.IsNullOrWhiteSpace(xml))
            {
                return result;
            }

            XDocument document = XDocument.Parse(xml, LoadOptions.None);
            Dictionary<string, DocumentFieldDefinition> fields = FieldsForScope(
                definition,
                scope).ToDictionary(
                    U8Name,
                    StringComparer.OrdinalIgnoreCase);
            IEnumerable<XElement> rows = document
                .Descendants(Rowset + "data")
                .Elements(Row + "row");
            foreach (XElement xmlRow in rows)
            {
                var item = new Dictionary<string, object>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (XAttribute attribute in xmlRow.Attributes())
                {
                    DocumentFieldDefinition field;
                    if (!fields.TryGetValue(attribute.Name.LocalName, out field))
                    {
                        continue;
                    }
                    item[field.Name] = ParseValue(attribute.Value, field.DataType);
                }
                result.Add(item);
            }
            return result;
        }

        private static IEnumerable<DocumentFieldDefinition> FieldsForScope(
            DocumentDefinition definition,
            string scope)
        {
            return (definition.Fields ?? new List<DocumentFieldDefinition>())
                .Where(x => string.Equals(
                    x.Scope,
                    scope,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string U8Name(DocumentFieldDefinition field)
        {
            return string.IsNullOrWhiteSpace(field.U8Field)
                ? field.Name
                : field.U8Field;
        }

        private static bool TryGetValue(
            IDictionary<string, object> values,
            string name,
            out object value)
        {
            value = null;
            if (values == null)
            {
                return false;
            }
            if (values.TryGetValue(name, out value))
            {
                return true;
            }
            KeyValuePair<string, object> match = values.FirstOrDefault(x =>
                string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase));
            if (match.Key == null)
            {
                return false;
            }
            value = match.Value;
            return true;
        }

        private static string ToAdoType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return "string";
            }
            switch (type.Trim().ToLowerInvariant())
            {
                case "integer": return "int";
                case "long": return "i8";
                case "boolean": return "boolean";
                case "date":
                case "datetime": return "dateTime";
                case "number":
                case "decimal": return "number";
                default: return type.Trim();
            }
        }

        private static void AddOptional(
            XElement element,
            XName name,
            string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                element.Add(new XAttribute(name, value));
            }
        }

        private static string FormatValue(object value)
        {
            if (value is DateTime)
            {
                return ((DateTime)value).ToString(
                    "yyyy-MM-ddTHH:mm:ss.fff",
                    CultureInfo.InvariantCulture);
            }
            if (value is DateTimeOffset)
            {
                return ((DateTimeOffset)value).ToString(
                    "yyyy-MM-ddTHH:mm:ss.fffzzz",
                    CultureInfo.InvariantCulture);
            }
            if (value is bool)
            {
                return (bool)value ? "true" : "false";
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static object ParseValue(string value, string type)
        {
            string normalized = ToAdoType(type).ToLowerInvariant();
            long integer;
            decimal number;
            bool boolean;
            DateTime dateTime;
            switch (normalized)
            {
                case "i1":
                case "ui1":
                case "i2":
                case "ui2":
                case "i4":
                case "ui4":
                case "i8":
                case "int":
                    return long.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out integer) ? (object)integer : value;
                case "r4":
                case "r8":
                case "number":
                case "decimal":
                case "fixed.14.4":
                    return decimal.TryParse(
                        value,
                        NumberStyles.Number | NumberStyles.AllowExponent,
                        CultureInfo.InvariantCulture,
                        out number) ? (object)number : value;
                case "boolean":
                    return bool.TryParse(value, out boolean)
                        ? (object)boolean
                        : value;
                case "datetime":
                case "datetime.tz":
                    return DateTime.TryParse(
                        value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out dateTime) ? (object)dateTime : value;
                default:
                    return value;
            }
        }
    }
}
