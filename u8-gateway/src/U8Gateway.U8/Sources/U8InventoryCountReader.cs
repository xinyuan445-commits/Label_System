using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using U8Gateway.Contracts.Approvals;
using U8Gateway.Contracts.Sources;
using U8Gateway.U8.Approvals;

namespace U8Gateway.U8.Sources
{
    public sealed class U8InventoryCountReader
    {
        public const int MaximumLines = 5000;
        private readonly string _connectionString, _account, _database;
        private readonly U8ApprovalDocumentReader _documents;

        public U8InventoryCountReader(string server, string database, string user, string password, string account)
        {
            _database = database;
            _account = NormalizeAccount(account);
            _documents = new U8ApprovalDocumentReader(server, database, user, password);
            _connectionString = new SqlConnectionStringBuilder
            {
                DataSource = server, InitialCatalog = database, UserID = user, Password = password,
                Encrypt = false, TrustServerCertificate = true, ConnectTimeout = 8,
                ApplicationName = "U8Gateway.InventoryCount.ReadOnly"
            }.ConnectionString;
        }

        public ApprovalDocumentSearchResult Search(string documentNo, DateTime? start, DateTime? end, int limit)
        {
            if (start.HasValue && end.HasValue && start.Value.Date > end.Value.Date)
                throw new ArgumentException("开始日期不能晚于结束日期。");
            return _documents.Search("inventory-count", ApprovalDocumentStatuses.Pending, documentNo, start, end, limit);
        }

        public InventoryCountSource Get(long id)
        {
            if (id <= 0) throw new ArgumentException("盘点单参数无效。");
            ApprovalDocumentDetail before = _documents.Get("inventory-count", id);
            if (before == null) return null;
            RequirePending(before);
            var result = new InventoryCountSource { AccountId = _account, DatabaseName = _database };
            var header = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            // Preserve the native line identity. SELECT d.* lets optional free-item columns be
            // read when present without inventing fields absent from a particular U8 schema.
            const string sql = @"
select top (@take) d.*, i.cInvName as _MaterialName, i.cComUnitCode as _UnitCode,
       i.cSTComUnitCode as _StockUnitCode
from dbo.CheckVouchs d left join dbo.Inventory i on i.cInvCode=d.cInvCode
where d.ID=@id order by d.autoID;";
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 30;
                command.Parameters.Add("@take", SqlDbType.Int).Value = MaximumLines + 1;
                command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                connection.Open();
                using (var headerCommand = new SqlCommand(@"
select h.cIRdCode InCategoryCode, i.cRdName InCategoryName,
       h.cORdCode OutCategoryCode, o.cRdName OutCategoryName
from dbo.CheckVouch h
left join dbo.Rd_Style i on i.cRdCode=h.cIRdCode
left join dbo.Rd_Style o on o.cRdCode=h.cORdCode
where h.ID=@id;", connection))
                {
                    headerCommand.CommandTimeout = 30;
                    headerCommand.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                    using (SqlDataReader reader = headerCommand.ExecuteReader())
                    {
                        if (!reader.Read()) throw new InvalidOperationException("盘点单已变化，请重新查询。");
                        for (int n = 0; n < reader.FieldCount; n++) header[reader.GetName(n)] = reader.GetValue(n);
                        if (reader.Read()) throw new InvalidOperationException("盘点类别重复，请核对原单。");
                    }
                }
                using (SqlDataReader reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        for (int n = 0; n < reader.FieldCount; n++) row[reader.GetName(n)] = reader.GetValue(n);
                        result.Lines.Add(MapLine(row, before.WarehouseCode, result.Lines.Count + 1));
                        if (result.Lines.Count > MaximumLines) throw new InvalidOperationException("盘点明细过多，请拆分盘点单。");
                    }
            }
            ApprovalDocumentDetail after = _documents.Get("inventory-count", id);
            RequirePending(after);
            if (after == null || string.IsNullOrWhiteSpace(before.Version) || before.Version != after.Version ||
                before.LineCount != result.Lines.Count || after.LineCount != result.Lines.Count ||
                result.Lines.Select(l => l.Id).Distinct().Count() != result.Lines.Count)
                throw new InvalidOperationException("盘点单已变化，请重新查询。");
            // The source has a separate, complete line collection; do not serialize the
            // approval reader's second, incomplete projection of the same lines.
            result.Document = MapHeader(after, header);
            return result;
        }

        public static InventoryCountHeader MapHeader(ApprovalDocumentSummary after, IDictionary<string, object> header)
        {
            return new InventoryCountHeader
            {
                Id = after.Id, DocumentType = after.DocumentType, DocumentNo = after.DocumentNo,
                DocumentDate = after.DocumentDate, WarehouseCode = after.WarehouseCode,
                PartnerName = after.PartnerName, CreatedBy = after.CreatedBy, Status = after.Status,
                AuditedBy = after.AuditedBy, AuditedAt = after.AuditedAt, Version = after.Version,
                LineCount = after.LineCount, TotalQuantity = after.TotalQuantity,
                InCategoryCode = Text(header, "InCategoryCode"), InCategoryName = Text(header, "InCategoryName"),
                OutCategoryCode = Text(header, "OutCategoryCode"), OutCategoryName = Text(header, "OutCategoryName")
            };
        }

        public static string NormalizeAccount(string account)
        {
            string value = (account ?? "").Trim();
            int separator = value.LastIndexOf('@');
            return separator < 0 ? value : value.Substring(separator + 1);
        }

        public static void RequirePending(ApprovalDocumentSummary document)
        {
            if (document == null || document.Status != ApprovalDocumentStatuses.Pending ||
                !string.IsNullOrWhiteSpace(document.AuditedBy) || document.AuditedAt.HasValue)
                throw new InvalidOperationException("盘点单已审核或不存在，请重新选择。");
        }

        public static InventoryCountSourceLine MapLine(IDictionary<string, object> row, string warehouse, int fallbackLine)
        {
            string unit = Text(row, "_UnitCode"), stockUnit = Text(row, "_StockUnitCode");
            return new InventoryCountSourceLine
            {
                Id = Convert.ToInt64(Value(row, "autoID"), CultureInfo.InvariantCulture),
                LineNo = Value(row, "irowno") == null ? fallbackLine : Convert.ToInt32(Value(row, "irowno"), CultureInfo.InvariantCulture),
                MaterialCode = Text(row, "cInvCode"), MaterialName = Text(row, "_MaterialName"),
                BatchNo = Text(row, "cCVBatch"), WarehouseCode = warehouse ?? "",
                LocationCode = Text(row, "cPosition"), UnitCode = unit,
                BookQuantity = Number(row, "iCVQuantity", false).Value,
                ActualQuantity = Number(row, "iCVCQuantity", true),
                FreeItems = Enumerable.Range(1, 10).Select(n => Text(row, "cFree" + n)).ToArray(),
                RequiresAuxiliaryUnit = stockUnit.Length > 0 && !string.Equals(unit, stockUnit, StringComparison.OrdinalIgnoreCase)
            };
        }

        private static object Value(IDictionary<string, object> row, string field)
        {
            object value;
            return row.TryGetValue(field, out value) && value != DBNull.Value ? value : null;
        }
        private static string Text(IDictionary<string, object> row, string field) =>
            Convert.ToString(Value(row, field), CultureInfo.InvariantCulture)?.Trim() ?? "";
        private static decimal? Number(IDictionary<string, object> row, string field, bool nullable)
        {
            object value = Value(row, field);
            if (nullable && value == null) return null;
            decimal parsed;
            if (value == null || !decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                throw new InvalidOperationException("盘点数量数据无效，请核对原单。");
            return parsed;
        }
    }
}
