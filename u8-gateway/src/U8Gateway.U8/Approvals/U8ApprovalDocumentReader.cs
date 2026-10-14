using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using System.Globalization;
using System.Linq;
using U8Gateway.Contracts.Approvals;
using U8Gateway.Contracts.Documents;

namespace U8Gateway.U8.Approvals
{
    public sealed class U8ApprovalDocumentReader
    {
        private sealed class Profile
        {
            public string Type, Header, Line, HeaderId, LineHeaderId, Code, Date,
                Verifier, VerifyDate, Maker, Warehouse, LineId, LineNo, Inventory,
                Quantity, Batch, LineWarehouse, Location, TargetLocation,
                PartnerLabel, PartnerExpression, BusinessFilter;
        }

        private readonly string _connectionString;

        public U8ApprovalDocumentReader(string server, string database, string user, string password)
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
                ApplicationName = "U8Gateway.ApprovalDocuments"
            }.ConnectionString;
        }

        public static bool IsSupported(string documentType)
        {
            return Resolve(documentType) != null;
        }

        public ApprovalDocumentSearchResult Search(
            string documentType,
            string status,
            string documentNo,
            DateTime? startDate,
            DateTime? endDate,
            int limit)
        {
            Profile p = RequireProfile(documentType);
            int take = Math.Max(1, Math.Min(limit, 100));
            string auditedPredicate = AuditedPredicate(p);
            var filters = new List<string>
            {
                status == ApprovalDocumentStatuses.Audited
                    ? auditedPredicate
                    : "not (" + auditedPredicate + ")",
                "(@documentNo=N'' or h." + Q(p.Code) + " like N'%'+@documentNo+N'%')",
                "(@startDate is null or cast(h." + Q(p.Date) + " as date)>=@startDate)",
                "(@endDate is null or cast(h." + Q(p.Date) + " as date)<=@endDate)"
            };
            if (!string.IsNullOrWhiteSpace(p.BusinessFilter)) filters.Add(p.BusinessFilter);

            string sql = "select top (@take) " + HeaderSelect(p) + "," +
                "isnull(x.LineCount,0) LineCount,isnull(x.TotalQuantity,0) TotalQuantity " +
                "from dbo." + Q(p.Header) + " h " +
                "outer apply (select count(*) LineCount,sum(isnull(d." + Q(p.Quantity) + ",0)) TotalQuantity " +
                "from dbo." + Q(p.Line) + " d where d." + Q(p.LineHeaderId) + "=h." + Q(p.HeaderId) + ") x " +
                "where " + string.Join(" and ", filters) + " " +
                "order by h." + Q(p.Date) + " desc,h." + Q(p.HeaderId) + " desc";

            var items = new List<ApprovalDocumentSummary>();
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 20;
                command.Parameters.Add("@take", SqlDbType.Int).Value = take + 1;
                command.Parameters.Add("@documentNo", SqlDbType.NVarChar, 100).Value = (documentNo ?? "").Trim();
                command.Parameters.Add("@startDate", SqlDbType.Date).Value = (object)startDate?.Date ?? DBNull.Value;
                command.Parameters.Add("@endDate", SqlDbType.Date).Value = (object)endDate?.Date ?? DBNull.Value;
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                    while (reader.Read()) items.Add(ReadSummary(reader, p.Type));
            }
            bool hasMore = items.Count > take;
            if (hasMore) items.RemoveAt(items.Count - 1);
            return new ApprovalDocumentSearchResult { Items = items, Limit = take, HasMore = hasMore };
        }

        public ApprovalDocumentDetail Get(string documentType, long id)
        {
            Profile p = RequireProfile(documentType);
            string headerSql = "select top (1) " + HeaderSelect(p) + "," +
                "isnull(x.LineCount,0) LineCount,isnull(x.TotalQuantity,0) TotalQuantity " +
                "from dbo." + Q(p.Header) + " h " +
                "outer apply (select count(*) LineCount,sum(isnull(d." + Q(p.Quantity) + ",0)) TotalQuantity " +
                "from dbo." + Q(p.Line) + " d where d." + Q(p.LineHeaderId) + "=h." + Q(p.HeaderId) + ") x " +
                "where h." + Q(p.HeaderId) + "=@id";
            string lineSql = "select d." + Q(p.LineId) + " LineId," +
                Expr("d", p.LineNo, "cast(null as int)") + " [LineNo]," +
                "isnull(d." + Q(p.Inventory) + ",N'') MaterialCode,isnull(i.cInvName,N'') MaterialName," +
                Expr("d", p.Batch, "N''") + " BatchNo," +
                Expr("d", p.LineWarehouse, Expr("h", p.Warehouse, "N''")) + " WarehouseCode," +
                Expr("d", p.Location, "N''") + " LocationCode," +
                Expr("d", p.TargetLocation, "N''") + " TargetLocationCode," +
                "isnull(d." + Q(p.Quantity) + ",0) Quantity " +
                "from dbo." + Q(p.Line) + " d join dbo." + Q(p.Header) + " h on h." + Q(p.HeaderId) + "=d." + Q(p.LineHeaderId) + " " +
                "left join dbo.Inventory i on i.cInvCode=d." + Q(p.Inventory) + " " +
                "where h." + Q(p.HeaderId) + "=@id order by " +
                (p.LineNo == null ? "d." + Q(p.LineId) : "d." + Q(p.LineNo)) + ",d." + Q(p.LineId);

            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                ApprovalDocumentDetail detail;
                using (var command = new SqlCommand(headerSql, connection))
                {
                    command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        ApprovalDocumentSummary summary = ReadSummary(reader, p.Type);
                        detail = new ApprovalDocumentDetail
                        {
                            Id = summary.Id, DocumentType = summary.DocumentType,
                            DocumentNo = summary.DocumentNo, DocumentDate = summary.DocumentDate,
                            PartnerLabel = summary.PartnerLabel, PartnerName = summary.PartnerName,
                            WarehouseCode = summary.WarehouseCode, CreatedBy = summary.CreatedBy,
                            Status = summary.Status, AuditedBy = summary.AuditedBy,
                            AuditedAt = summary.AuditedAt, Version = summary.Version,
                            LineCount = summary.LineCount, TotalQuantity = summary.TotalQuantity
                        };
                    }
                }
                using (var command = new SqlCommand(lineSql, connection))
                {
                    command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            detail.Lines.Add(new ApprovalDocumentLine
                            {
                                Id = AsLong(reader["LineId"]),
                                LineNo = reader["LineNo"] == DBNull.Value ? (int?)null : AsInt(reader["LineNo"]),
                                MaterialCode = Convert.ToString(reader["MaterialCode"]),
                                MaterialName = Convert.ToString(reader["MaterialName"]),
                                BatchNo = Convert.ToString(reader["BatchNo"]),
                                WarehouseCode = Convert.ToString(reader["WarehouseCode"]),
                                LocationCode = Convert.ToString(reader["LocationCode"]),
                                TargetLocationCode = Convert.ToString(reader["TargetLocationCode"]),
                                Quantity = AsDecimal(reader["Quantity"])
                            });
                        }
                    }
                }
                return detail;
            }
        }

        private static string HeaderSelect(Profile p)
        {
            return "convert(bigint,h." + Q(p.HeaderId) + ") DocumentId," +
                "convert(nvarchar(100),h." + Q(p.Code) + ") DocumentNo,h." + Q(p.Date) + " DocumentDate," +
                "N'" + p.PartnerLabel.Replace("'", "''") + "' PartnerLabel," + p.PartnerExpression + " PartnerName," +
                Expr("h", p.Warehouse, "N''") + " WarehouseCode," +
                Expr("h", p.Maker, "N''") + " CreatedBy," +
                "case when " + AuditedPredicate(p) + " then N'audited' else N'pending' end Status," +
                "isnull(convert(nvarchar(100),h." + Q(p.Verifier) + "),N'') AuditedBy," +
                Expr("h", p.VerifyDate, "cast(null as datetime)") + " AuditedAt," +
                "master.dbo.fn_varbintohexstr(h.[ufts]) Version";
        }

        private static string AuditedPredicate(Profile p)
        {
            string parent = "nullif(ltrim(rtrim(convert(nvarchar(100),h." +
                Q(p.Verifier) + "))),N'') is not null";
            if (!Eq(p.Type, DocumentTypeCodes.Transfer)) return parent;

            string outboundLink = "(r9.cBusCode=h.cTVCode or r9.cSourceCodeLs=h.cTVCode)";
            string inboundLink = "(r8.cBusCode=h.cTVCode or r8.cSourceCodeLs=h.cTVCode)";
            return "(" + parent +
                " and exists (select 1 from dbo.RdRecord09 r9 where " + outboundLink + ")" +
                " and not exists (select 1 from dbo.RdRecord09 r9 where " + outboundLink +
                " and nullif(ltrim(rtrim(isnull(r9.cHandler,N''))),N'') is null)" +
                " and exists (select 1 from dbo.RdRecord08 r8 where " + inboundLink + ")" +
                " and not exists (select 1 from dbo.RdRecord08 r8 where " + inboundLink +
                " and nullif(ltrim(rtrim(isnull(r8.cHandler,N''))),N'') is null))";
        }

        private static ApprovalDocumentSummary ReadSummary(IDataRecord reader, string documentType)
        {
            return new ApprovalDocumentSummary
            {
                Id = AsLong(reader["DocumentId"]), DocumentType = documentType,
                DocumentNo = Convert.ToString(reader["DocumentNo"]),
                DocumentDate = AsDate(reader["DocumentDate"]),
                PartnerLabel = Convert.ToString(reader["PartnerLabel"]), PartnerName = Convert.ToString(reader["PartnerName"]),
                WarehouseCode = Convert.ToString(reader["WarehouseCode"]), CreatedBy = Convert.ToString(reader["CreatedBy"]),
                Status = Convert.ToString(reader["Status"]), AuditedBy = Convert.ToString(reader["AuditedBy"]),
                AuditedAt = AsDate(reader["AuditedAt"]),
                Version = Convert.ToString(reader["Version"]), LineCount = AsInt(reader["LineCount"]),
                TotalQuantity = AsDecimal(reader["TotalQuantity"])
            };
        }

        private static long AsLong(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            long parsed;
            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static int AsInt(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static DateTime? AsDate(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            if (value is DateTime) return (DateTime)value;
            DateTime parsed;
            return DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) ? parsed : (DateTime?)null;
        }

        private static decimal AsDecimal(object value)
        {
            if (value == null || value == DBNull.Value) return 0m;
            if (value is SqlDecimal) return ((SqlDecimal)value).Value;
            if (value is decimal) return (decimal)value;
            if (value is double) return Convert.ToDecimal((double)value);
            if (value is float) return Convert.ToDecimal((float)value);
            decimal parsed;
            return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Any, CultureInfo.InvariantCulture, out parsed) ? parsed : 0m;
        }

        private static Profile RequireProfile(string type)
        {
            Profile profile = Resolve(type);
            if (profile == null) throw new InvalidOperationException("不支持的审核单据类型。");
            return profile;
        }

        private static Profile Resolve(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return null;
            if (Eq(type, DocumentTypeCodes.PurchaseArrival) || Eq(type, DocumentTypeCodes.SubcontractArrival))
                return new Profile { Type=Eq(type, DocumentTypeCodes.PurchaseArrival)?DocumentTypeCodes.PurchaseArrival:DocumentTypeCodes.SubcontractArrival, Header="PU_ArrivalVouch",Line="PU_ArrivalVouchs",HeaderId="ID",LineHeaderId="ID",Code="cCode",Date="dDate",Verifier="cVerifier",VerifyDate=null,Maker="cMaker",Warehouse=null,LineId="Autoid",LineNo="irowno",Inventory="cInvCode",Quantity="iQuantity",Batch="cBatch",LineWarehouse="cWhCode",PartnerLabel="供应商",PartnerExpression="isnull((select top (1) v.cVenName from dbo.Vendor v where v.cVenCode=h.cVenCode),isnull(h.cVenCode,N''))",BusinessFilter=Eq(type, DocumentTypeCodes.PurchaseArrival)?"isnull(h.cBusType,N'')<>N'委外加工'":"isnull(h.cBusType,N'')=N'委外加工'" };
            if (Eq(type, DocumentTypeCodes.PurchaseReceipt)) return Rd(type,"RdRecord01","RdRecords01","供应商","isnull((select top (1) v.cVenName from dbo.Vendor v where v.cVenCode=h.cVenCode),isnull(h.cVenCode,N''))");
            if (Eq(type, DocumentTypeCodes.SalesDelivery)) return new Profile { Type=DocumentTypeCodes.SalesDelivery,Header="DispatchList",Line="DispatchLists",HeaderId="DLID",LineHeaderId="DLID",Code="cDLCode",Date="dDate",Verifier="cVerifier",VerifyDate="dverifysystime",Maker="cMaker",Warehouse=null,LineId="iDLsID",LineNo="irowno",Inventory="cInvCode",Quantity="iQuantity",Batch="cBatch",LineWarehouse="cWhCode",Location="cPosition",PartnerLabel="客户",PartnerExpression="isnull(nullif(h.cCusName,N''),isnull(h.cCusCode,N''))" };
            if (Eq(type, DocumentTypeCodes.SalesOutbound)) return Rd(type,"RdRecord32","RdRecords32","客户","isnull((select top (1) c.cCusName from dbo.Customer c where c.cCusCode=h.cCusCode),isnull(h.cCusCode,N''))");
            if (Eq(type, DocumentTypeCodes.MaterialOutbound)) return Rd(type,"RdRecord11","RdRecords11","部门","isnull((select top (1) d.cDepName from dbo.Department d where d.cDepCode=h.cDepCode),isnull(h.cDepCode,N''))");
            if (Eq(type, DocumentTypeCodes.Transfer)) return new Profile { Type=DocumentTypeCodes.Transfer,Header="TransVouch",Line="TransVouchs",HeaderId="ID",LineHeaderId="ID",Code="cTVCode",Date="dTVDate",Verifier="cVerifyPerson",VerifyDate="dVerifyDate",Maker="cMaker",Warehouse="cOWhCode",LineId="autoID",LineNo="irowno",Inventory="cInvCode",Quantity="iTVQuantity",Batch="cTVBatch",Location="coutposcode",TargetLocation="cinposcode",PartnerLabel="调入仓库",PartnerExpression="isnull((select top (1) w.cWhName from dbo.Warehouse w where w.cWhCode=h.cIWhCode),isnull(h.cIWhCode,N''))" };
            if (Eq(type, DocumentTypeCodes.LocationAdjustment)) return new Profile { Type=DocumentTypeCodes.LocationAdjustment,Header="AdjustPVouch",Line="AdjustPVouchs",HeaderId="ID",LineHeaderId="ID",Code="cVouchCode",Date="dDate",Verifier="chandler",VerifyDate="dVeriDate",Maker="cMaker",Warehouse="cWhCode",LineId="autoID",LineNo="irowno",Inventory="cInvCode",Quantity="iQuantity",Batch="cBatch",Location="cBPosCode",TargetLocation="cAPosCode",PartnerLabel="目标货位",PartnerExpression="isnull((select top (1) d.cAPosCode from dbo.AdjustPVouchs d where d.ID=h.ID order by d.autoID),N'')" };
            if (Eq(type, DocumentTypeCodes.InventoryCount)) return new Profile { Type=DocumentTypeCodes.InventoryCount,Header="CheckVouch",Line="CheckVouchs",HeaderId="ID",LineHeaderId="ID",Code="cCVCode",Date="dCVDate",Verifier="cAccounter",VerifyDate="dveridate",Maker="cMaker",Warehouse="cWhCode",LineId="autoID",LineNo="irowno",Inventory="cInvCode",Quantity="iCVCQuantity",Batch="cCVBatch",Location="cPosition",PartnerLabel="仓库",PartnerExpression="isnull((select top (1) w.cWhName from dbo.Warehouse w where w.cWhCode=h.cWhCode),isnull(h.cWhCode,N''))" };
            return null;
        }

        private static Profile Rd(string type, string header, string line, string partnerLabel, string partnerExpression)
        {
            return new Profile { Type=type,Header=header,Line=line,HeaderId="ID",LineHeaderId="ID",Code="cCode",Date="dDate",Verifier="cHandler",VerifyDate="dnverifytime",Maker="cMaker",Warehouse="cWhCode",LineId="AutoID",LineNo="irowno",Inventory="cInvCode",Quantity="iQuantity",Batch="cBatch",Location="cPosition",PartnerLabel=partnerLabel,PartnerExpression=partnerExpression };
        }

        private static bool Eq(string a, string b) { return string.Equals(a,b,StringComparison.OrdinalIgnoreCase); }
        private static string Expr(string alias, string column, string fallback) { return column == null ? fallback : "isnull(convert(nvarchar(200)," + alias + "." + Q(column) + "),N'')"; }
        private static string Q(string value) { return "[" + value.Replace("]", "]]" ) + "]"; }
    }
}
