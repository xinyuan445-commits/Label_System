using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using U8Gateway.Contracts.Sources;

namespace U8Gateway.U8.Sources
{
    public sealed class U8SourceDocumentReader
    {
        private sealed class SourceProfile
        {
            public string SourceType { get; set; }
            public string HeaderTable { get; set; }
            public string LineTable { get; set; }
            public string HeaderId { get; set; }
            public string DocumentCode { get; set; }
            public string DocumentDate { get; set; }
            public string LineId { get; set; }
            public string LineNo { get; set; }
            public string InventoryCode { get; set; }
            public string Quantity { get; set; }
            public string UsedQuantity { get; set; }
            public string DepartmentCode { get; set; }
            public string AuditColumn { get; set; }
            public string HeaderCloseColumn { get; set; }
            public string LineCloseColumn { get; set; }
            public string BusinessTypeColumn { get; set; }
            public string BusinessTypeValue { get; set; }
            public bool ExcludeBusinessType { get; set; }
            public bool Sales { get; set; }
            public bool ReturnOrder { get; set; }
        }

        private static readonly string[] SupportedSourceTypes =
        {
            SourceDocumentTypeCodes.ProductionOrder,
            SourceDocumentTypeCodes.PurchaseOrder,
            SourceDocumentTypeCodes.PurchaseArrival,
            SourceDocumentTypeCodes.SalesOrder,
            SourceDocumentTypeCodes.SalesDelivery,
            SourceDocumentTypeCodes.SalesOrderReturn,
            SourceDocumentTypeCodes.SalesDeliveryReturn,
            SourceDocumentTypeCodes.OutsourcingOrder,
            SourceDocumentTypeCodes.SubcontractOrder
        };

        private readonly string _connectionString;

        public static bool IsSupportedSourceType(string sourceType)
        {
            return SupportedSourceTypes.Any(x => string.Equals(x, sourceType, StringComparison.OrdinalIgnoreCase));
        }

        public U8SourceDocumentReader(
            string server,
            string database,
            string user,
            string password)
        {
            _connectionString = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                UserID = user,
                Password = password,
                IntegratedSecurity = false,
                Encrypt = false,
                TrustServerCertificate = true,
                ConnectTimeout = 8,
                ApplicationName = "U8Gateway.SourceDocuments"
            }.ConnectionString;
        }

        public SourceDocumentSearchResult SearchProductionOrders(
            string keyword,
            DateTime? startDate,
            DateTime? endDate,
            int limit)
        {
            int boundedLimit = Math.Max(1, Math.Min(limit, 100));
            const string sql = @"
select top (@take)
    v.MoId_a,
    v.MoCode_a,
    v.MoDId_b,
    v.SortSeq_b,
    v.CreateDate_a,
    v.InvCode_b,
    isnull(p.cInvName, N'') as ProductName,
    isnull(v.Qty_b, 0) as OrderQuantity,
    isnull(v.MDeptCode_b, N'') as DepartmentCode,
    count(a.AllocateId) as MaterialCount
from dbo.momlist_yx v
left join dbo.mom_moallocate a on a.MoDId = v.MoDId_b
left join dbo.Inventory p on p.cInvCode = v.InvCode_b
where v.AuditStatus_b = 1
  and v.CloseDate_b is null
  and v.CloseTime_b is null
  and (@keyword = N'' or v.MoCode_a like N'%' + @keyword + N'%'
       or v.InvCode_b like N'%' + @keyword + N'%'
       or p.cInvName like N'%' + @keyword + N'%')
  and (@startDate is null or cast(v.CreateDate_a as date) >= @startDate)
  and (@endDate is null or cast(v.CreateDate_a as date) <= @endDate)
group by
    v.MoId_a, v.MoCode_a, v.MoDId_b, v.SortSeq_b, v.CreateDate_a,
    v.InvCode_b, p.cInvName, v.Qty_b, v.MDeptCode_b
having count(a.AllocateId) > 0
order by v.CreateDate_a desc, v.MoCode_a desc, v.SortSeq_b, v.MoDId_b";

            var items = new List<SourceDocumentLine>();
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 20;
                command.Parameters.Add("@take", SqlDbType.Int).Value = boundedLimit + 1;
                command.Parameters.Add("@keyword", SqlDbType.NVarChar, 100).Value =
                    (keyword ?? string.Empty).Trim();
                command.Parameters.Add("@startDate", SqlDbType.Date).Value =
                    (object)startDate?.Date ?? DBNull.Value;
                command.Parameters.Add("@endDate", SqlDbType.Date).Value =
                    (object)endDate?.Date ?? DBNull.Value;
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        items.Add(ReadSourceDocumentLine(reader));
                    }
                }
            }

            bool hasMore = items.Count > boundedLimit;
            if (hasMore) items.RemoveAt(items.Count - 1);
            return new SourceDocumentSearchResult
            {
                Items = items,
                Limit = boundedLimit,
                HasMore = hasMore
            };
        }

        public SourceDocumentSearchResult Search(
            string sourceType,
            string keyword,
            DateTime? startDate,
            DateTime? endDate,
            int limit)
        {
            if (string.Equals(sourceType, SourceDocumentTypeCodes.ProductionOrder,
                    StringComparison.OrdinalIgnoreCase))
                return SearchProductionOrders(keyword, startDate, endDate, limit);

            return SearchStandardSource(sourceType, keyword, startDate, endDate, limit, null);
        }

        public IList<SourceDocumentLine> GetLines(
            string sourceType,
            IEnumerable<int> sourceDocumentIds)
        {
            if (string.Equals(sourceType, SourceDocumentTypeCodes.ProductionOrder,
                    StringComparison.OrdinalIgnoreCase))
                return GetProductionOrderLines(sourceDocumentIds);

            int[] ids = (sourceDocumentIds ?? Enumerable.Empty<int>())
                .Where(x => x > 0)
                .Distinct()
                .Take(20)
                .ToArray();
            if (ids.Length == 0) return new List<SourceDocumentLine>();
            return SearchStandardSource(sourceType, null, null, null, 1000, ids).Items;
        }

        private SourceDocumentSearchResult SearchStandardSource(
            string sourceType,
            string keyword,
            DateTime? startDate,
            DateTime? endDate,
            int limit,
            int[] documentIds)
        {
            int boundedLimit = Math.Max(1, Math.Min(limit, documentIds == null ? 100 : 2000));
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                SourceProfile profile = ResolveProfile(connection, sourceType);
                string remaining = profile.UsedQuantity == null
                    ? "isnull(d." + Q(profile.Quantity) + ",0)"
                    : "isnull(d." + Q(profile.Quantity) + ",0)-isnull(d." + Q(profile.UsedQuantity) + ",0)";
                string planned = "isnull(d." + Q(profile.Quantity) + ",0)";
                string processed = profile.UsedQuantity == null ? "0" : "isnull(d." + Q(profile.UsedQuantity) + ",0)";
                if (profile.ReturnOrder)
                {
                    // iFHQuantity already includes returns: subtracting fretquantity again would understate availability.
                    remaining = "isnull(d.[iFHQuantity],0)";
                    processed = "isnull(d.[fretquantity],0)";
                    planned = remaining + "+" + processed;
                }
                bool dispatchReturn = string.Equals(profile.SourceType, SourceDocumentTypeCodes.SalesDeliveryReturn, StringComparison.OrdinalIgnoreCase);
                string orderAvailable = profile.ReturnOrder ? "isnull(d.[iFHQuantity],0)" :
                    dispatchReturn ?
                    "(select s.iFHQuantity from dbo.SO_SODetails s where s.iSOsID=d.iSOsID)" : "cast(null as decimal(28,6))";
                if (dispatchReturn)
                {
                    // Uninvoiced returns use iCorIdlsID and do not increment iRetQuantity.
                    processed += "+(select isnull(sum(abs(r.iQuantity)),0) from dbo.DispatchLists r join dbo.DispatchList rh on rh.DLID=r.DLID where rh.bReturnFlag=1 and r.iCorIdlsID=d.iDLsID and isnull(r.iCorID,0)=0)";
                    remaining = planned + "-(" + processed + ")";
                    remaining = "case when " + orderAvailable + "<" + remaining + " then " + orderAvailable + " else " + remaining + " end";
                }
                var filters = new List<string>
                {
                    remaining + " > 0"
                };
                if (profile.Sales && profile.HeaderTable == "DispatchList")
                    filters.Add("isnull(h.bReturnFlag,0)=0 and d.iQuantity>0");
                if (profile.AuditColumn != null)
                    filters.Add("nullif(ltrim(rtrim(convert(nvarchar(100),h." + Q(profile.AuditColumn) + "))),N'') is not null");
                if (profile.HeaderCloseColumn != null)
                    filters.Add("nullif(ltrim(rtrim(convert(nvarchar(100),h." + Q(profile.HeaderCloseColumn) + "))),N'') is null");
                if (profile.LineCloseColumn != null)
                    filters.Add("(d." + Q(profile.LineCloseColumn) + " is null or convert(nvarchar(20),d." + Q(profile.LineCloseColumn) + ") in (N'',N'0',N'False'))");
                if (profile.BusinessTypeColumn != null && profile.BusinessTypeValue != null)
                    filters.Add(profile.ExcludeBusinessType
                        ? "isnull(h." + Q(profile.BusinessTypeColumn) + ",N'')<>@businessType"
                        : "isnull(h." + Q(profile.BusinessTypeColumn) + ",N'')=@businessType");
                if (documentIds == null)
                {
                    filters.Add("(@keyword=N'' or h." + Q(profile.DocumentCode) + " like N'%'+@keyword+N'%' or d." +
                                Q(profile.InventoryCode) + " like N'%'+@keyword+N'%' or i.cInvName like N'%'+@keyword+N'%')");
                    filters.Add("(@startDate is null or cast(h." + Q(profile.DocumentDate) + " as date)>=@startDate)");
                    filters.Add("(@endDate is null or cast(h." + Q(profile.DocumentDate) + " as date)<=@endDate)");
                }
                else
                {
                    filters.Add("h." + Q(profile.HeaderId) + " in (" +
                                string.Join(",", documentIds.Select((x, index) => "@documentId" + index)) + ")");
                }

                string sql = "select top (@take) " +
                    "h." + Q(profile.HeaderId) + " SourceDocumentId," +
                    "h." + Q(profile.DocumentCode) + " SourceDocumentNo," +
                    "d." + Q(profile.LineId) + " SourceLineId," +
                    (profile.LineNo == null ? "cast(null as int)" : "try_convert(int,d." + Q(profile.LineNo) + ")") + " SourceLineNo," +
                    "h." + Q(profile.DocumentDate) + " DocumentDate," +
                    "d." + Q(profile.InventoryCode) + " ProductCode," +
                    "isnull(i.cInvName,N'') ProductName," + remaining + " OrderQuantity," +
                    (profile.DepartmentCode == null ? "N''" : "isnull(h." + Q(profile.DepartmentCode) + ",N'')") + " DepartmentCode " +
                    (profile.Sales ? "," + planned + " PlannedQuantity," + processed + " ProcessedQuantity," + remaining + " RemainingQuantity," +
                        "h.cCusCode PartnerCode,isnull((select top(1) c.cCusName from dbo.Customer c where c.cCusCode=h.cCusCode),N'') PartnerName," +
                        "h.cSTCode SalesTypeCode,h.cexch_name CurrencyName,h.iExchRate ExchangeRate," +
                        "d.iTaxRate TaxRate,d.iUnitPrice UnitPrice,d.iTaxUnitPrice TaxUnitPrice,d.iSOsID SalesOrderLineId," +
                        (profile.HeaderTable == "SO_SOMain" ? "h.cSOCode" : "d.cSOCode") + " SalesOrderNo," + orderAvailable + " SalesOrderAvailableQuantity " : "") +
                    "from dbo." + Q(profile.HeaderTable) + " h " +
                    "join dbo." + Q(profile.LineTable) + " d on d." + Q(profile.HeaderId) + "=h." + Q(profile.HeaderId) + " " +
                    "left join dbo.Inventory i on i.cInvCode=d." + Q(profile.InventoryCode) + " " +
                    "where " + string.Join(" and ", filters) + " " +
                    "order by h." + Q(profile.DocumentDate) + " desc,h." + Q(profile.DocumentCode) + " desc," +
                    (profile.LineNo == null ? "d." + Q(profile.LineId) : "d." + Q(profile.LineNo)) + ",d." + Q(profile.LineId);

                var items = new List<SourceDocumentLine>();
                using (var command = new SqlCommand(sql, connection))
                {
                    command.CommandTimeout = 20;
                    command.Parameters.Add("@take", SqlDbType.Int).Value = boundedLimit + (documentIds == null ? 1 : 0);
                    command.Parameters.Add("@keyword", SqlDbType.NVarChar, 100).Value = (keyword ?? string.Empty).Trim();
                    command.Parameters.Add("@startDate", SqlDbType.Date).Value = (object)startDate?.Date ?? DBNull.Value;
                    command.Parameters.Add("@endDate", SqlDbType.Date).Value = (object)endDate?.Date ?? DBNull.Value;
                    if (profile.BusinessTypeColumn != null && profile.BusinessTypeValue != null)
                        command.Parameters.Add("@businessType", SqlDbType.NVarChar, 50).Value = profile.BusinessTypeValue;
                    if (documentIds != null)
                        for (int index = 0; index < documentIds.Length; index++)
                            command.Parameters.Add("@documentId" + index, SqlDbType.Int).Value = documentIds[index];
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            items.Add(new SourceDocumentLine
                            {
                                SourceType = NormalizeSourceType(sourceType),
                                SourceDocumentId = Convert.ToInt32(reader["SourceDocumentId"]),
                                SourceDocumentNo = Convert.ToString(reader["SourceDocumentNo"]),
                                SourceLineId = Convert.ToInt32(reader["SourceLineId"]),
                                SourceLineNo = reader["SourceLineNo"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["SourceLineNo"]),
                                DocumentDate = reader["DocumentDate"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(reader["DocumentDate"]),
                                ProductCode = Convert.ToString(reader["ProductCode"]),
                                ProductName = Convert.ToString(reader["ProductName"]),
                                OrderQuantity = Math.Max(0m, Convert.ToDecimal(reader["OrderQuantity"])),
                                DepartmentCode = Convert.ToString(reader["DepartmentCode"]),
                                MaterialCount = 1,
                                PlannedQuantity = profile.Sales ? NullableDecimal(reader["PlannedQuantity"]) : null,
                                ProcessedQuantity = profile.Sales ? NullableDecimal(reader["ProcessedQuantity"]) : null,
                                RemainingQuantity = profile.Sales ? NullableDecimal(reader["RemainingQuantity"]) : null,
                                PartnerCode = profile.Sales ? Convert.ToString(reader["PartnerCode"]) : null,
                                PartnerName = profile.Sales ? Convert.ToString(reader["PartnerName"]) : null,
                                SalesTypeCode = profile.Sales ? Convert.ToString(reader["SalesTypeCode"]) : null,
                                CurrencyName = profile.Sales ? Convert.ToString(reader["CurrencyName"]) : null,
                                ExchangeRate = profile.Sales ? NullableDecimal(reader["ExchangeRate"]) : null,
                                TaxRate = profile.Sales ? NullableDecimal(reader["TaxRate"]) : null,
                                UnitPrice = profile.Sales ? NullableDecimal(reader["UnitPrice"]) : null,
                                TaxUnitPrice = profile.Sales ? NullableDecimal(reader["TaxUnitPrice"]) : null,
                                SalesOrderLineId = profile.Sales && reader["SalesOrderLineId"] != DBNull.Value ? (int?)Convert.ToInt32(reader["SalesOrderLineId"]) : null,
                                SalesOrderNo = profile.Sales ? Convert.ToString(reader["SalesOrderNo"]) : null,
                                SalesOrderAvailableQuantity = profile.Sales ? NullableDecimal(reader["SalesOrderAvailableQuantity"]) : null
                            });
                        }
                    }
                }
                bool hasMore = documentIds == null && items.Count > boundedLimit;
                if (hasMore) items.RemoveAt(items.Count - 1);
                return new SourceDocumentSearchResult { Items = items, Limit = boundedLimit, HasMore = hasMore };
            }
        }

        private static SourceProfile ResolveProfile(SqlConnection connection, string sourceType)
        {
            SourceProfile profile;
            if (string.Equals(sourceType, SourceDocumentTypeCodes.PurchaseOrder, StringComparison.OrdinalIgnoreCase))
                profile = new SourceProfile { SourceType=sourceType, HeaderTable="PO_Pomain", LineTable="PO_Podetails", HeaderId="POID", DocumentCode="cPOID", DocumentDate="dPODate", LineId="ID", LineNo="iRowNo", InventoryCode="cInvCode", Quantity="iQuantity", UsedQuantity="iArrQTY", DepartmentCode="cDepCode", AuditColumn="cVerifier", HeaderCloseColumn="cCloser", LineCloseColumn="cbCloser" };
            else if (string.Equals(sourceType, SourceDocumentTypeCodes.PurchaseArrival, StringComparison.OrdinalIgnoreCase))
                profile = new SourceProfile { SourceType=sourceType, HeaderTable="PU_ArrivalVouch", LineTable="PU_ArrivalVouchs", HeaderId="ID", DocumentCode="cCode", DocumentDate="dDate", LineId="Autoid", LineNo="iRowNo", InventoryCode="cInvCode", Quantity="fValidInQuan", DepartmentCode="cDepCode", AuditColumn="cVerifier", HeaderCloseColumn="cCloser", LineCloseColumn="cCloser", BusinessTypeColumn="cBusType", BusinessTypeValue="委外加工", ExcludeBusinessType=true };
            else if (string.Equals(sourceType, SourceDocumentTypeCodes.SalesOrder, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(sourceType, SourceDocumentTypeCodes.SalesOrderReturn, StringComparison.OrdinalIgnoreCase))
                profile = new SourceProfile { SourceType=sourceType, HeaderTable="SO_SOMain", LineTable="SO_SODetails", HeaderId="ID", DocumentCode="cSOCode", DocumentDate="dDate", LineId="iSOsID", LineNo="iRowNo", InventoryCode="cInvCode", Quantity="iQuantity", UsedQuantity="iFHQuantity", DepartmentCode="cDepCode", AuditColumn="cVerifier", HeaderCloseColumn="cCloser", LineCloseColumn="cSCloser", Sales=true, ReturnOrder=string.Equals(sourceType,SourceDocumentTypeCodes.SalesOrderReturn,StringComparison.OrdinalIgnoreCase) };
            else if (string.Equals(sourceType, SourceDocumentTypeCodes.SalesDelivery, StringComparison.OrdinalIgnoreCase))
                profile = new SourceProfile { SourceType=sourceType, HeaderTable="DispatchList", LineTable="DispatchLists", HeaderId="DLID", DocumentCode="cDLCode", DocumentDate="dDate", LineId="iDLsID", LineNo="iRowNo", InventoryCode="cInvCode", Quantity="iQuantity", UsedQuantity="fOutQuantity", DepartmentCode="cDepCode", AuditColumn="cVerifier", HeaderCloseColumn="cCloser", LineCloseColumn="cSCloser", BusinessTypeColumn="cBusType", BusinessTypeValue="普通销售", Sales=true };
            else if (string.Equals(sourceType, SourceDocumentTypeCodes.SalesDeliveryReturn, StringComparison.OrdinalIgnoreCase))
                profile = new SourceProfile { SourceType=sourceType, HeaderTable="DispatchList", LineTable="DispatchLists", HeaderId="DLID", DocumentCode="cDLCode", DocumentDate="dDate", LineId="iDLsID", LineNo="iRowNo", InventoryCode="cInvCode", Quantity="iQuantity", UsedQuantity="iRetQuantity", DepartmentCode="cDepCode", AuditColumn="cVerifier", BusinessTypeColumn="cBusType", BusinessTypeValue="普通销售", Sales=true };
            else if (string.Equals(sourceType, SourceDocumentTypeCodes.OutsourcingOrder, StringComparison.OrdinalIgnoreCase) || string.Equals(sourceType, SourceDocumentTypeCodes.SubcontractOrder, StringComparison.OrdinalIgnoreCase))
                profile = new SourceProfile { SourceType=SourceDocumentTypeCodes.OutsourcingOrder, HeaderTable="OM_MOMain", LineTable="OM_MODetails", HeaderId="MOID", DocumentCode="cCode", DocumentDate="dDate", LineId="MODetailsID", LineNo="iRowNo", InventoryCode="cInvCode", Quantity="iQuantity", UsedQuantity="iReceivedQTY", DepartmentCode="cDepCode", AuditColumn="cVerifier", HeaderCloseColumn="cCloser", LineCloseColumn="bClose" };
            else
                throw new InvalidOperationException("不支持的源单类型。");

            EnsureTable(connection, profile.HeaderTable);
            if (profile.ReturnOrder)
            {
                RequireColumn(connection, profile.LineTable, "fretquantity");
                RequireColumn(connection, profile.LineTable, "iFHQuantity");
            }
            EnsureTable(connection, profile.LineTable);
            profile.HeaderId = RequireColumn(connection, profile.HeaderTable, profile.HeaderId);
            profile.DocumentCode = RequireColumn(connection, profile.HeaderTable, profile.DocumentCode);
            profile.DocumentDate = RequireColumn(connection, profile.HeaderTable, profile.DocumentDate);
            profile.LineId = profile.Sales ? RequireColumn(connection, profile.LineTable, profile.LineId) :
                RequireColumn(connection, profile.LineTable, profile.LineId, "AutoID", "ID");
            profile.LineNo = OptionalColumn(connection, profile.LineTable, profile.LineNo, "iRowNo");
            profile.InventoryCode = RequireColumn(connection, profile.LineTable, profile.InventoryCode);
            profile.Quantity = RequireColumn(connection, profile.LineTable, profile.Quantity, "iQuantity", "fValidInQuan");
            profile.UsedQuantity = profile.Sales ? RequireColumn(connection, profile.LineTable, profile.UsedQuantity) :
                OptionalColumn(connection, profile.LineTable, profile.UsedQuantity, "iArrQTY", "iReceivedQTY", "iFHQuantity", "iSOutQuantity");
            profile.DepartmentCode = OptionalColumn(connection, profile.HeaderTable, profile.DepartmentCode, "cDepCode");
            profile.AuditColumn = OptionalColumn(connection, profile.HeaderTable, profile.AuditColumn, "cVerifier", "cAuditor");
            bool returnView = profile.ReturnOrder || string.Equals(sourceType, SourceDocumentTypeCodes.SalesDeliveryReturn, StringComparison.OrdinalIgnoreCase);
            profile.HeaderCloseColumn = returnView ? null : OptionalColumn(connection, profile.HeaderTable, profile.HeaderCloseColumn, "cCloser");
            profile.LineCloseColumn = returnView ? null : OptionalColumn(connection, profile.LineTable, profile.LineCloseColumn, "cbCloser", "cSCloser", "cCloser", "bClose");
            profile.BusinessTypeColumn = OptionalColumn(connection, profile.HeaderTable, profile.BusinessTypeColumn, "cBusType");
            return profile;
        }

        private static string NormalizeSourceType(string sourceType)
        {
            return string.Equals(sourceType, SourceDocumentTypeCodes.SubcontractOrder, StringComparison.OrdinalIgnoreCase)
                ? SourceDocumentTypeCodes.OutsourcingOrder
                : sourceType;
        }

        private static void EnsureTable(SqlConnection connection, string table)
        {
            using (var command = new SqlCommand("select object_id(@name,N'U')", connection))
            {
                command.Parameters.Add("@name", SqlDbType.NVarChar, 260).Value = "dbo." + table;
                object result = command.ExecuteScalar();
                if (result == DBNull.Value || result == null)
                    throw new InvalidOperationException("U8 源单表不存在。");
            }
        }

        private static string RequireColumn(SqlConnection connection, string table, params string[] candidates)
        {
            string value = OptionalColumn(connection, table, candidates);
            if (value == null) throw new InvalidOperationException("U8 源单字段不存在。");
            return value;
        }

        private static string OptionalColumn(SqlConnection connection, string table, params string[] candidates)
        {
            foreach (string candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using (var command = new SqlCommand(
                    "select top (1) name from sys.columns where object_id=object_id(@table) and name=@column", connection))
                {
                    command.Parameters.Add("@table", SqlDbType.NVarChar, 260).Value = "dbo." + table;
                    command.Parameters.Add("@column", SqlDbType.NVarChar, 128).Value = candidate;
                    object result = command.ExecuteScalar();
                    if (result != null && result != DBNull.Value) return Convert.ToString(result);
                }
            }
            return null;
        }

        private static string Q(string identifier)
        {
            return "[" + identifier.Replace("]", "]]" ) + "]";
        }
        private static decimal? NullableDecimal(object value) => value == DBNull.Value ? (decimal?)null : Convert.ToDecimal(value);

        public IList<SourceDocumentLine> GetProductionOrderLines(
            IEnumerable<int> sourceDocumentIds)
        {
            int[] ids = (sourceDocumentIds ?? Enumerable.Empty<int>())
                .Where(x => x > 0)
                .Distinct()
                .Take(20)
                .ToArray();
            if (ids.Length == 0) return new List<SourceDocumentLine>();

            string[] names = ids.Select((_, index) => "@documentId" + index).ToArray();
            string sql = @"
select
    v.MoId_a,
    v.MoCode_a,
    v.MoDId_b,
    v.SortSeq_b,
    v.CreateDate_a,
    v.InvCode_b,
    isnull(p.cInvName, N'') as ProductName,
    isnull(v.Qty_b, 0) as OrderQuantity,
    isnull(v.MDeptCode_b, N'') as DepartmentCode,
    count(a.AllocateId) as MaterialCount
from dbo.momlist_yx v
left join dbo.mom_moallocate a on a.MoDId = v.MoDId_b
left join dbo.Inventory p on p.cInvCode = v.InvCode_b
where v.MoId_a in (" + string.Join(",", names) + @")
  and v.AuditStatus_b = 1
  and v.CloseDate_b is null
  and v.CloseTime_b is null
group by
    v.MoId_a, v.MoCode_a, v.MoDId_b, v.SortSeq_b, v.CreateDate_a,
    v.InvCode_b, p.cInvName, v.Qty_b, v.MDeptCode_b
having count(a.AllocateId) > 0
order by v.CreateDate_a desc, v.MoCode_a desc, v.SortSeq_b, v.MoDId_b";

            var items = new List<SourceDocumentLine>();
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 20;
                for (int index = 0; index < ids.Length; index++)
                {
                    command.Parameters.Add(names[index], SqlDbType.Int).Value = ids[index];
                }
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        items.Add(ReadSourceDocumentLine(reader));
                    }
                }
            }
            return items;
        }

        public IList<MaterialRequirementLine> GetProductionRequirements(
            IEnumerable<int> sourceLineIds)
        {
            int[] ids = (sourceLineIds ?? Enumerable.Empty<int>())
                .Where(x => x > 0)
                .Distinct()
                .Take(1000)
                .ToArray();
            if (ids.Length == 0) return new List<MaterialRequirementLine>();

            string[] names = ids.Select((_, index) => "@id" + index).ToArray();
            string sql = @"
select
    v.MoId_a,
    v.MoCode_a,
    v.MoDId_b,
    v.SortSeq_b,
    v.InvCode_b as ProductCode,
    isnull(p.cInvName, N'') as ProductName,
    isnull(v.MDeptCode_b, N'') as DepartmentCode,
    a.AllocateId,
    isnull(a.OpSeq, N'') as OpSeq,
    a.InvCode as MaterialCode,
    isnull(i.cInvName, N'') as MaterialName,
    isnull(a.Qty, 0) as PlannedQuantity,
    isnull(a.IssQty, 0) as IssuedQuantity,
    cast(isnull(i.bInvBatch, 0) as bit) as RequiresBatch
from dbo.momlist_yx v
join dbo.mom_moallocate a on a.MoDId = v.MoDId_b
left join dbo.Inventory p on p.cInvCode = v.InvCode_b
left join dbo.Inventory i on i.cInvCode = a.InvCode
where v.MoDId_b in (" + string.Join(",", names) + @")
  and v.AuditStatus_b = 1
  and v.CloseDate_b is null
  and v.CloseTime_b is null
order by v.MoCode_a, v.SortSeq_b, a.OpSeq, a.AllocateId";

            var items = new List<MaterialRequirementLine>();
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 20;
                for (int index = 0; index < ids.Length; index++)
                {
                    command.Parameters.Add(names[index], SqlDbType.Int).Value = ids[index];
                }
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        decimal planned = Convert.ToDecimal(reader["PlannedQuantity"]);
                        decimal issued = Convert.ToDecimal(reader["IssuedQuantity"]);
                        items.Add(new MaterialRequirementLine
                        {
                            SourceType = SourceDocumentTypeCodes.ProductionOrder,
                            SourceDocumentId = Convert.ToInt32(reader["MoId_a"]),
                            SourceDocumentNo = Convert.ToString(reader["MoCode_a"]),
                            SourceLineId = Convert.ToInt32(reader["MoDId_b"]),
                            SourceLineNo = reader["SortSeq_b"] == DBNull.Value
                                ? (int?)null
                                : Convert.ToInt32(reader["SortSeq_b"]),
                            RequirementId = Convert.ToInt32(reader["AllocateId"]),
                            ProductCode = Convert.ToString(reader["ProductCode"]),
                            ProductName = Convert.ToString(reader["ProductName"]),
                            MaterialCode = Convert.ToString(reader["MaterialCode"]),
                            MaterialName = Convert.ToString(reader["MaterialName"]),
                            DepartmentCode = Convert.ToString(reader["DepartmentCode"]),
                            OperationSequence = Convert.ToString(reader["OpSeq"]),
                            RequiresBatch = Convert.ToBoolean(reader["RequiresBatch"]),
                            PlannedQuantity = planned,
                            IssuedQuantity = issued,
                            RemainingQuantity = Math.Max(0m, planned - issued)
                        });
                    }
                }
            }
            return items;
        }

        private static SourceDocumentLine ReadSourceDocumentLine(SqlDataReader reader)
        {
            return new SourceDocumentLine
            {
                SourceType = SourceDocumentTypeCodes.ProductionOrder,
                SourceDocumentId = Convert.ToInt32(reader["MoId_a"]),
                SourceDocumentNo = Convert.ToString(reader["MoCode_a"]),
                SourceLineId = Convert.ToInt32(reader["MoDId_b"]),
                SourceLineNo = reader["SortSeq_b"] == DBNull.Value
                    ? (int?)null
                    : Convert.ToInt32(reader["SortSeq_b"]),
                DocumentDate = reader["CreateDate_a"] == DBNull.Value
                    ? (DateTime?)null
                    : Convert.ToDateTime(reader["CreateDate_a"]),
                ProductCode = Convert.ToString(reader["InvCode_b"]),
                ProductName = Convert.ToString(reader["ProductName"]),
                OrderQuantity = Convert.ToDecimal(reader["OrderQuantity"]),
                DepartmentCode = Convert.ToString(reader["DepartmentCode"]),
                MaterialCount = Convert.ToInt32(reader["MaterialCount"])
            };
        }
    }
}
