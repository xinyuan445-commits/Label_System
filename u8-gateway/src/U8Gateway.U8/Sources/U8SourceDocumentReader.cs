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
        private readonly string _connectionString;

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
