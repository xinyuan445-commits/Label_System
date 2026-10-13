using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using U8Gateway.Contracts;
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
                    definitions.GetAll().All(x => x.Capabilities.Validate),
                    "无写入字段校验应对全部单据开放。");
                Require(
                    !definitions.Get("subcontract-arrival").Capabilities.Query,
                    "暂缓的委外到货不应开放查询。");
                Require(
                    definitions.GetAll().All(x =>
                        x.Capabilities.Create &&
                        x.Capabilities.Verify &&
                        x.Capabilities.Unverify &&
                        !x.Capabilities.Update &&
                        !x.Capabilities.Delete),
                    "九类单据应只开放新增、审核和弃审。");

                DocumentDefinition material = definitions.Get("material-outbound");
                Require(
                    material.WritePolicyStatus == "candidate-reference-derived",
                    "材料出库写入策略没有载入。");
                Require(
                    material.Fields.Single(x => x.Scope == "header" && x.Name == "cwhcode")
                        .CreateWritable,
                    "材料出库仓库字段应允许新增。");
                Require(
                    !material.Fields.Single(x => x.Scope == "header" && x.Name == "id")
                        .CreateWritable,
                    "U8 系统 ID 不应允许新增写入。");

                TestWritePreparationAndMerge(material, codec);

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

        private static void TestWritePreparationAndMerge(
            DocumentDefinition definition,
            AdoPersistedXmlCodec codec)
        {
            var preparer = new DocumentWritePreparer();
            var create = new DocumentOperationRequest
            {
                OperatorName = "测试员",
                Header = new Dictionary<string, object>
                {
                    ["cwhcode"] = "A01",
                    ["crdcode"] = "201",
                    ["cdepcode"] = "01"
                },
                Lines = new List<IDictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["cinvcode"] = "MAT-001",
                        ["iquantity"] = 5m
                    }
                }
            };
            DocumentPreparationResult preparedCreate = preparer.PrepareCreate(
                definition,
                create,
                new DateTimeOffset(2026, 10, 11, 8, 30, 0, TimeSpan.FromHours(8)));
            Require(preparedCreate.IsValid, "新增字段校验失败。");
            Require(Equals(preparedCreate.Payload.Header["cmaker"], "测试员"),
                "制单人默认值失败。");
            Require(preparedCreate.Payload.Header.ContainsKey("ddate"),
                "单据日期默认值失败。");
            Require(Equals(preparedCreate.Payload.Lines[0]["irowno"], 1),
                "新增行号默认值失败。");

            create.Header["id"] = 99;
            DocumentPreparationResult rejected = preparer.PrepareCreate(
                definition, create, BusinessClock.Now);
            Require(rejected.Issues.Any(x => x.Code == "FIELD_READ_ONLY" && x.Path == "header.id"),
                "只读字段没有被拒绝。");
            create.Header.Remove("id");

            var original = new DocumentPayload
            {
                Header = new Dictionary<string, object>
                {
                    ["id"] = 10L,
                    ["cmemo"] = "原备注",
                    ["caccounter"] = "系统审核人"
                },
                Lines = new List<IDictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["autoid"] = 100L,
                        ["irowno"] = 1L,
                        ["cinvcode"] = "MAT-001",
                        ["iquantity"] = 5m,
                        ["cbaccounter"] = "系统值"
                    }
                }
            };
            U8XmlPacket originalXml = codec.Encode(definition, original);
            var patchRequest = new DocumentOperationRequest
            {
                Header = new Dictionary<string, object> { ["cmemo"] = null },
                Lines = new List<IDictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["autoid"] = 100L,
                        ["iquantity"] = 7.5m
                    },
                    new Dictionary<string, object>
                    {
                        ["_action"] = "add",
                        ["cinvcode"] = "MAT-002",
                        ["iquantity"] = 2m
                    }
                }
            };
            DocumentPreparationResult preparedPatch = preparer.PreparePatch(
                definition, patchRequest, BusinessClock.Now);
            Require(preparedPatch.IsValid, "PATCH 字段校验失败。");
            var merger = new AdoPersistedXmlPatchMerger(codec);
            U8XmlPacket mergedXml = merger.Merge(
                definition, originalXml, preparedPatch.Payload);
            DocumentPayload merged = codec.Decode(definition, mergedXml);
            Require(!merged.Header.ContainsKey("cmemo"), "显式 null 没有清空可清空字段。");
            Require(Equals(merged.Header["caccounter"], "系统审核人"),
                "XML 合并丢失了系统字段。");
            Require(Equals(merged.Lines[0]["iquantity"], 7.5m),
                "XML 合并没有覆盖允许修改的字段。");
            Require(Equals(merged.Lines[0]["cbaccounter"], "系统值"),
                "XML 合并丢失了明细系统字段。");
            Require(merged.Lines.Count == 2, "XML 合并新增明细失败。");
            Require(Equals(merged.Lines[1]["irowno"], 2L),
                "PATCH 新增明细没有生成连续行号。");
            Require(Equals(merged.Lines[1]["editprop"], "A"),
                "PATCH 新增明细没有写入 ADO 新增标记。");
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
