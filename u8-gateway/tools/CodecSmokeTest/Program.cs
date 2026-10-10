using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using U8Gateway.Contracts.Metadata;
using U8Gateway.Core.Documents;
using U8Gateway.Metadata;
using U8Gateway.U8.Documents;
using U8Gateway.U8.Xml;

namespace U8Gateway.CodecSmokeTest
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                DocumentDefinition definition = Definition();
                var header = new Dictionary<string, object>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["warehouseCode"] = "A01"
                };
                var line = new Dictionary<string, object>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["quantity"] = 12.5m
                };
                var payload = new DocumentPayload
                {
                    Header = header,
                    Lines = new List<IDictionary<string, object>> { line }
                };

                var codec = new AdoPersistedXmlCodec();
                U8XmlPacket packet = codec.Encode(definition, payload);
                XDocument.Parse(packet.HeaderXml);
                XDocument.Parse(packet.BodyXml);
                DocumentPayload decoded = codec.Decode(definition, packet);
                Require(
                    Equals(decoded.Header["warehouseCode"], "A01"),
                    "表头字符串往返失败。");
                Require(
                    Equals(decoded.Lines[0]["quantity"], 12.5m),
                    "表体数值往返失败。");
                Require(
                    packet.PositionXml == null,
                    "没有货位字段时不应生成货位 XML。");

                var definitions = new ContractDocumentDefinitionStore();
                Require(definitions.GetAll().Count == 9, "单据类型数量不正确。");
                Require(
                    definitions.Get("purchase-receipt").Fields.Count == 348,
                    "采购入库字段合同没有载入。");
                Require(
                    definitions.Get("sales-delivery").Fields.Count == 473,
                    "发货单字段合同没有载入。");
                int fieldCount = 0;
                foreach (DocumentDefinition item in definitions.GetAll())
                {
                    fieldCount += item.Fields.Count;
                }
                Require(fieldCount == 2584, "字段合同总数不正确。");
                Require(
                    definitions.GetAll().Count(x =>
                        x.Capabilities != null && x.Capabilities.Query) == 8,
                    "已开放的只读查询单据数量不正确。");
                Require(
                    !definitions.Get("subcontract-arrival").Capabilities.Query,
                    "暂缓的委外到货不应开放查询。");
                Require(
                    definitions.GetAll().All(x =>
                        !x.Capabilities.Create &&
                        !x.Capabilities.Update &&
                        !x.Capabilities.Verify &&
                        !x.Capabilities.Unverify &&
                        !x.Capabilities.Delete),
                    "写能力在验收前必须保持关闭。");

                var profiles = new U8DocumentProfileCatalog();
                Require(profiles.GetAll().Count == 9, "U8 操作映射数量不正确。");
                Require(
                    profiles.Get("purchase-arrival").Operations.Create ==
                    "GetVoucherNO + VoucherSave",
                    "采购到货新增方法映射不正确。");
                Require(
                    profiles.Get("sales-delivery").Operations.Readiness ==
                    "read-runtime-verified-888; test-account-write-validation-required",
                    "发货单读取状态不正确。");
                Require(
                    profiles.Get("subcontract-arrival").Operations.Readiness ==
                    "read-schema-captured; implementation-deferred",
                    "委外到货暂缓状态不正确。");

                var adapters = new DocumentAdapterCatalog(
                    profiles,
                    new UnavailableU8DocumentRuntime());
                Require(adapters.GetAll().Count == 9, "单据适配器数量不正确。");
                Require(
                    adapters.Get("purchase-arrival") is PurchaseDocumentAdapter,
                    "采购单据适配器分组不正确。");
                Require(
                    adapters.Get("sales-delivery") is SalesDocumentAdapter,
                    "销售单据适配器分组不正确。");
                Require(
                    adapters.Get("purchase-receipt") is InventoryDocumentAdapter,
                    "库存单据适配器分组不正确。");

                Console.WriteLine(
                    "codec-round-trip: ok; contracts: ok; operation-map: ok; adapters: ok");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static DocumentDefinition Definition()
        {
            var definition = new DocumentDefinition
            {
                DocumentType = "codec-test"
            };
            definition.Fields.Add(new DocumentFieldDefinition
            {
                Name = "warehouseCode",
                U8Field = "cwhcode",
                Scope = "header",
                DataType = "string",
                MaxLength = 10,
                RequiredMode = "always"
            });
            definition.Fields.Add(new DocumentFieldDefinition
            {
                Name = "quantity",
                U8Field = "iquantity",
                Scope = "line",
                DataType = "decimal",
                Precision = 28,
                Scale = 6,
                RequiredMode = "always"
            });
            return definition;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
