using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MSXML2;
using U8Gateway.Contracts.Documents;
using U8Gateway.Contracts.Sources;
using U8Gateway.Core.Documents;
using U8Gateway.U8.Sources;
using U8Gateway.U8.Xml;
using U8Gateway.UserpcoBridge;
using U8Gateway.U8.TraceBack;

namespace U8Gateway.U8.Documents
{
    public sealed class CountSaveLabel
    {
        public string Code { get; set; }
        public string MaterialCode { get; set; }
        public string BatchNo { get; set; }
        public string WarehouseCode { get; set; }
        public string LocationCode { get; set; }
        public string TargetWarehouseCode { get; set; }
        public string TargetLocationCode { get; set; }
        public decimal Balance { get; set; }
        public long LatestFlowId { get; set; }
        public decimal ActualQuantity { get; set; }
    }
    public static class InventoryCountSaveRules
    {
        public static decimal AccumulatedActual(IEnumerable<CountSaveLabel> current, IEnumerable<CountSaveLabel> previous)
        {
            var quantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in previous)
            {
                if (string.IsNullOrWhiteSpace(label.Code) || label.ActualQuantity < 0 || quantities.ContainsKey(label.Code))
                    throw new InvalidOperationException("已上传盘点记录需核对。");
                quantities.Add(label.Code, label.ActualQuantity);
            }
            foreach (var label in current)
            {
                if (string.IsNullOrWhiteSpace(label.Code) || label.ActualQuantity < 0)
                    throw new InvalidOperationException("盘点数量无效。");
                quantities[label.Code] = label.ActualQuantity;
            }
            return quantities.Values.Sum();
        }

        public static string Identity(InventoryCountSource source, bool version = true) =>
            JsonConvert.SerializeObject(new {
                source.AccountId, source.DatabaseName, source.Document.Id, source.Document.DocumentNo,
                source.Document.DocumentDate, source.Document.WarehouseCode, source.Document.CountByLocation,
                source.Document.WarehousePositionManaged, source.Document.InCategoryCode, source.Document.OutCategoryCode,
                Version = version ? source.Document.Version : null,
                Lines = source.Lines.OrderBy(l => l.Id).Select(l => new {
                    l.Id, l.MaterialCode, l.BatchNo, l.WarehouseCode, l.LocationCode, l.UnitCode,
                    Book = l.BookQuantity.ToString("G29", CultureInfo.InvariantCulture),
                    Actual = l.ActualQuantity?.ToString("G29", CultureInfo.InvariantCulture),
                    l.FreeItems, l.RequiresAuxiliaryUnit }) });

        public static void CheckSource(InventoryCountSource expected, InventoryCountSource current)
        {
            if (expected == null || current == null || expected.AccountId != "888" ||
                expected.DatabaseName != "UFDATA_888_2021" || Identity(expected) != Identity(current))
                throw new InvalidOperationException("原盘点单已变化，请重选同一原单。");
        }

        public static InventoryCountSource ExpectedAfter(InventoryCountSource source, DocumentOperationRequest request)
        {
            var expected = JsonConvert.DeserializeObject<InventoryCountSource>(JsonConvert.SerializeObject(source));
            var ids = new HashSet<long>();
            foreach (var patch in request.Lines)
            {
                long id = Convert.ToInt64(patch["autoid"]);
                decimal quantity = Convert.ToDecimal(patch["icvcquantity"], CultureInfo.InvariantCulture);
                var rows = expected.Lines.Where(l => l.Id == id).ToArray();
                if (!ids.Add(id) || rows.Length != 1 || quantity < 0 || rows[0].RequiresAuxiliaryUnit ||
                    rows[0].FreeItems == null || rows[0].FreeItems.Any(v => !string.IsNullOrWhiteSpace(v)))
                    throw new InvalidOperationException("盘点明细需核对。");
                rows[0].ActualQuantity = quantity;
            }
            object value;
            if (request.Header.TryGetValue("cirdcode", out value)) expected.Document.InCategoryCode = Convert.ToString(value);
            if (request.Header.TryGetValue("cordcode", out value)) expected.Document.OutCategoryCode = Convert.ToString(value);
            return expected;
        }
    }

    public sealed partial class LateBoundU8DocumentRuntime
    {
        private static bool CountTestAccount(U8GatewayRuntimeOptions options) =>
            options.SqlDatabase == "UFDATA_888_2021" &&
            U8InventoryCountReader.NormalizeAccount(options.U8.AccountId) == "888" &&
            (string.Equals(options.EnvironmentName, "development", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(options.EnvironmentName, "test", StringComparison.OrdinalIgnoreCase));

        private static U8InventoryCountReader CountReader(U8GatewayRuntimeOptions o) =>
            new U8InventoryCountReader(o.SqlServer, o.SqlDatabase, o.SqlUser, o.SqlPassword, o.U8.AccountId);

        private static CountSaveLabel[] ValidateCountLabels(U8GatewayRuntimeOptions options, DocumentOperationRequest request)
        {
            object raw;
            if (request.Options == null || !request.Options.TryGetValue("countLabels", out raw))
                throw new InvalidOperationException("盘点标签资料不完整。");
            var labels = JsonConvert.DeserializeObject<CountSaveLabel[]>(JsonConvert.SerializeObject(raw));
            if (labels == null || labels.Length == 0 || labels.Length > 1000 ||
                labels.Any(l => string.IsNullOrWhiteSpace(l.Code) || l.LatestFlowId <= 0 || l.ActualQuantity < 0) ||
                labels.Select(l => l.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Length)
                throw new InvalidOperationException("盘点标签资料无效。");
            var reader = new TraceBackSnapshotReader(options.SqlServer, options.SqlDatabase, options.SqlUser, options.SqlPassword);
            for (int offset = 0; offset < labels.Length; offset += 100)
            {
                var batch = labels.Skip(offset).Take(100).ToArray();
                var current = reader.GetSnapshots(batch.Select(l => l.Code).ToList()).ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
                foreach (var expected in batch)
                {
                    U8Gateway.Contracts.TraceBack.TraceBackLabelSnapshot label;
                    if (!current.TryGetValue(expected.Code, out label) ||
                        (label.Status != "AVAILABLE" && label.Status != "USED_UP") || label.Anomalies == null || label.Anomalies.Count > 0 ||
                        label.Balance != expected.Balance || label.LatestFlowId != expected.LatestFlowId ||
                        !SameCount(label.MaterialCode, expected.MaterialCode) || !SameCount(label.BatchNo, expected.BatchNo) ||
                        !SameCount(label.WarehouseCode, expected.WarehouseCode) || !SameCount(label.LocationCode, expected.LocationCode))
                        throw new InvalidOperationException("标签资料已变化，请重新核对。");
                }
            }
            return labels;
        }

        private static bool SameCount(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private static string CountTargetWarehouse(CountSaveLabel label) => label.TargetWarehouseCode ?? label.WarehouseCode;
        private static string CountTargetLocation(CountSaveLabel label) => label.TargetWarehouseCode == null ? label.LocationCode : label.TargetLocationCode;

        private static void ValidateCountTargets(U8GatewayRuntimeOptions options, CountSaveLabel[] labels, string warehouse)
        {
            var target = CountReader(options).GetWarehouse(warehouse);
            if (target == null) throw new InvalidOperationException("盘点仓库需核对。");
            using (var db = new SqlConnection(ConnectionString(options)))
            {
                db.Open();
                if (labels.Any(l => !SameCount(CountTargetWarehouse(l), warehouse))) throw new InvalidOperationException("盘点目标仓库不一致。");
                var locations = labels.Select(l => CountTargetLocation(l) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (target.PositionManaged == true)
                {
                    if (locations.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("请选择盘点仓库的有效货位。");
                    using (var check = new SqlCommand())
                    {
                        check.Connection = db;
                        check.Parameters.AddWithValue("@wh", warehouse);
                        for (int i = 0; i < locations.Length; i++) check.Parameters.AddWithValue("@p" + i, locations[i]);
                        check.CommandText = "SELECT COUNT(DISTINCT cPosCode) FROM dbo.Position WHERE cWhCode=@wh AND cPosCode IN (" +
                            string.Join(",", Enumerable.Range(0, locations.Length).Select(i => "@p" + i)) + ");";
                        if (Convert.ToInt32(check.ExecuteScalar()) != locations.Length) throw new InvalidOperationException("请选择盘点仓库的有效货位。");
                    }
                }
                else if (locations.Any(l => !string.IsNullOrWhiteSpace(l))) throw new InvalidOperationException("盘点仓库不管理货位。");
            }
        }

        private static CountSaveLabel[] PreviousCountLabels(DocumentOperationRequest request)
        {
            object raw;
            if (!request.Options.TryGetValue("countPreviousLabels", out raw))
                throw new InvalidOperationException("盘点累计记录缺失，请更新服务。");
            var previous = JsonConvert.DeserializeObject<CountSaveLabel[]>(JsonConvert.SerializeObject(raw));
            if (previous == null) throw new InvalidOperationException("盘点累计记录无效。");
            InventoryCountSaveRules.AccumulatedActual(new CountSaveLabel[0], previous);
            return previous;
        }

        private DocumentOperationResult UpdateCount(U8GatewayRuntimeOptions options, U8DocumentExecution execution)
        {
            long id;
            object raw;
            if (!long.TryParse(execution.VoucherCode, out id) || execution.Request.Options == null ||
                !execution.Request.Options.TryGetValue("countSource", out raw) || raw == null)
                return Failure("COUNT_SOURCE_REQUIRED", "请选择原盘点单。", execution.VoucherCode);
            var source = JsonConvert.DeserializeObject<InventoryCountSource>(JsonConvert.SerializeObject(raw));
            var reader = CountReader(options);
            InventoryCountSource current, expected;
            try
            {
                if (source.Document.Id != id) throw new InvalidOperationException("原单编号不一致。");
                current = reader.Get(id);
                InventoryCountSaveRules.CheckSource(source, current);
                var labels = ValidateCountLabels(options, execution.Request);
                ValidateCountTargets(options, labels, current.Document.WarehouseCode);
                var previous = PreviousCountLabels(execution.Request);
                var expectedPatches = labels.GroupBy(l => new { l.MaterialCode, l.BatchNo,
                    Location = source.Document.CountByLocation == true ? CountTargetLocation(l) ?? "" : "" }).ToArray();
                if (expectedPatches.Length != execution.Request.Lines.Count) throw new InvalidOperationException("盘点汇总不一致。");
                foreach (var group in expectedPatches)
                {
                    var matches = current.Lines.Where(l => SameCount(l.MaterialCode, group.Key.MaterialCode) && SameCount(l.BatchNo, group.Key.BatchNo) &&
                        (source.Document.CountByLocation != true || SameCount(l.LocationCode, group.Key.Location))).ToArray();
                    if (matches.Length != 1)
                        throw new InvalidOperationException("标签不属于原盘点单。");
                    var patch = execution.Request.Lines.SingleOrDefault(l => Convert.ToInt64(l["autoid"]) == matches[0].Id);
                    var prior = previous.Where(l => SameCount(l.MaterialCode, group.Key.MaterialCode) && SameCount(l.BatchNo, group.Key.BatchNo) &&
                        SameCount(l.WarehouseCode, current.Document.WarehouseCode) &&
                        (source.Document.CountByLocation != true || SameCount(l.LocationCode, group.Key.Location)));
                    if (patch == null || Convert.ToDecimal(patch["icvcquantity"]) != InventoryCountSaveRules.AccumulatedActual(group, prior))
                        throw new InvalidOperationException("盘点累计数量不一致。");
                }
                if (source.Document.Version != execution.Request.ExpectedVersion)
                    throw new InvalidOperationException("原盘点单版本不一致。");
                expected = InventoryCountSaveRules.ExpectedAfter(current, execution.Request);
            }
            catch (InvalidOperationException e) { return Failure("COUNT_SOURCE_CHANGED", e.Message, execution.VoucherCode); }
            var definition = _definitions.Get(DocumentTypeCodes.InventoryCount);
            var original = QueryInventory(options.U8, execution.Profile, id);
            var merged = new AdoPersistedXmlPatchMerger(_codec).Merge(definition, original,
                new DocumentPayload { Header = execution.Request.Header, Lines = execution.Request.Lines,
                    Positions = execution.Request.Positions, Auxiliary = execution.Request.Auxiliary });
            try { InventoryCountSaveRules.CheckSource(current, reader.Get(id)); }
            catch (InvalidOperationException e) { return Failure("COUNT_SOURCE_CHANGED", e.Message, execution.VoucherCode); }
            U8Login.clsLogin login = null;
            object voucher = null;
            IXMLDOMDocument2 head = null, body = null, position = null;
            try
            {
                login = Login(options.U8); voucher = Create("USERPCO.VoucherCO");
                string error = string.Empty;
                if (!((dynamic)voucher).IniLogin(login, ref error)) throw new InvalidOperationException(error);
                head = ParseXml(merged.HeaderXml, "盘点表头"); body = ParseXml(merged.BodyXml, "盘点明细");
                position = string.IsNullOrWhiteSpace(merged.PositionXml) ? new DOMDocument26Class() : ParseXml(merged.PositionXml, "盘点货位");
                InventoryCountSaveRules.CheckSource(current, reader.Get(id));
                ValidateCountTargets(options, ValidateCountLabels(options, execution.Request), current.Document.WarehouseCode);
                var result = UserpcoWriter.Update(voucher, "18", head, body, position);
                var after = reader.Get(id);
                if (InventoryCountSaveRules.Identity(expected, false) != InventoryCountSaveRules.Identity(after, false))
                {
                    if (!result.Succeeded && InventoryCountSaveRules.Identity(current, false) == InventoryCountSaveRules.Identity(after, false))
                        return Failure("U8_COUNT_UPDATE_REJECTED", Empty(result.Error, "U8盘点保存失败。"), source.Document.DocumentNo);
                    throw new InvalidOperationException("U8盘点保存结果需核对，记录已保留。");
                }
                if (!result.Succeeded && current.Document.Version == after.Document.Version)
                    return Failure("U8_COUNT_UPDATE_REJECTED", Empty(result.Error, "U8盘点保存失败。"), source.Document.DocumentNo);
                return new DocumentOperationResult { Confirmed = true, OutcomeKnown = true, Code = "OK",
                    Message = "已保存，未审核", VoucherId = id.ToString(), VoucherCode = source.Document.DocumentNo,
                    Version = after.Document.Version, Document = _codec.Decode(definition, QueryInventory(options.U8, execution.Profile, id)) };
            }
            finally { Shutdown(login); Release(position); Release(body); Release(head); Release(voucher); Release(login); }
        }

        private static void PrepareCountCreate(U8GatewayRuntimeOptions options, DocumentOperationRequest request, bool verifyOnly = false)
        {
            var labels = ValidateCountLabels(options, request);
            bool byLocation = OptionBoolean(request.Options, "countByLocation");
            if (PreviousCountLabels(request).Length != 0) throw new InvalidOperationException("新盘点单不能带入历史累计。");
            if (request.Lines.Select(l => Convert.ToString(l["cinvcode"]) + "|" + Convert.ToString(l["ccvbatch"]) + "|" +
                (byLocation ? Convert.ToString(l["cposition"]) : "")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Lines.Count)
                throw new InvalidOperationException("盘点汇总重复。");
            string warehouse = Convert.ToString(request.Header["cwhcode"]);
            ValidateCountTargets(options, labels, warehouse);
            var info = CountReader(options).GetWarehouse(warehouse);
            if (info == null || info.PositionManaged != byLocation) throw new InvalidOperationException("仓库货位设置已变化。");
            using (var connection = new SqlConnection(ConnectionString(options)))
            {
                connection.Open();
                for (int n = 0; n < request.Lines.Count; n++)
                {
                    var line = request.Lines[n];
                    using (var material = new SqlCommand("select cComUnitCode,cSTComUnitCode from dbo.Inventory where cInvCode=@code", connection))
                    {
                        material.Parameters.AddWithValue("@code", line["cinvcode"]);
                        using (var units = material.ExecuteReader())
                        {
                            if (!units.Read()) throw new InvalidOperationException("物料已变化，请核对。");
                            if (!string.IsNullOrWhiteSpace(Convert.ToString(units[1])) && !SameCount(Convert.ToString(units[0]), Convert.ToString(units[1])))
                                throw new InvalidOperationException("该物料辅计量需另行核对。");
                        }
                    }
                    var scoped = labels.Where(l => SameCount(l.MaterialCode, Convert.ToString(line["cinvcode"])) &&
                        SameCount(l.BatchNo, Convert.ToString(line["ccvbatch"])) &&
                        (!byLocation || SameCount(CountTargetLocation(l), Convert.ToString(line["cposition"])))).ToArray();
                    decimal actual = InventoryCountSaveRules.AccumulatedActual(scoped, new CountSaveLabel[0]);
                    if (scoped.Length == 0 || Convert.ToDecimal(line["icvcquantity"]) != actual)
                        throw new InvalidOperationException("盘点汇总不一致。");
                    string table = byLocation ? "InvPositionSum" : "CurrentStock";
                    string free = string.Join(" or ", Enumerable.Range(1, 10).Select(i => "nullif(ltrim(rtrim(cFree" + i + ")),N'') is not null"));
                    using (var command = new SqlCommand("select isnull(sum(iQuantity),0),isnull(sum(case when " + free +
                        " then 1 else 0 end),0) from dbo." + table + " where cWhCode=@wh and cInvCode=@inv and isnull(cBatch,N'')=@batch" +
                        (byLocation ? " and cPosCode=@pos" : ""), connection))
                    {
                        command.CommandTimeout = 30;
                        command.Parameters.AddWithValue("@wh", warehouse);
                        command.Parameters.AddWithValue("@inv", line["cinvcode"]);
                        command.Parameters.AddWithValue("@batch", line["ccvbatch"] ?? "");
                        command.Parameters.AddWithValue("@pos", line["cposition"] ?? "");
                        using (var row = command.ExecuteReader())
                        {
                            row.Read();
                            if (Convert.ToInt32(row[1]) != 0) throw new InvalidOperationException("该物料自由项需核对。");
                            decimal book = Convert.ToDecimal(row[0]);
                            if (book < 0) throw new InvalidOperationException("库存账面小于0，请核对。");
                            if (verifyOnly && Convert.ToDecimal(line["icvquantity"]) != book)
                                throw new InvalidOperationException("U8库存已变化，请重试。");
                            line["icvquantity"] = book; line["icvcquantity"] = actual;
                        }
                    }
                }
            }
        }

        private static void VerifyCountCreated(InventoryCountSource saved, DocumentOperationRequest request)
        {
            if (saved == null || saved.Lines.Count != request.Lines.Count) throw new InvalidOperationException("盘点新增明细需核对。");
            foreach (var expected in request.Lines)
            {
                var rows = saved.Lines.Where(l => l.MaterialCode == Convert.ToString(expected["cinvcode"]) &&
                    l.BatchNo == Convert.ToString(expected["ccvbatch"]) &&
                    (saved.Document.CountByLocation != true || l.LocationCode == Convert.ToString(expected["cposition"]))).ToArray();
                if (rows.Length != 1 || rows[0].BookQuantity != Convert.ToDecimal(expected["icvquantity"]) ||
                    rows[0].ActualQuantity != Convert.ToDecimal(expected["icvcquantity"]))
                    throw new InvalidOperationException("盘点新增数量需核对。");
            }
        }
    }
}
