using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using U8Gateway.Contracts.ReferenceData;

namespace U8Gateway.U8.ReferenceData
{
    public sealed class U8ReferenceDataReader
    {
        private readonly string _connectionString;

        public U8ReferenceDataReader(string server, string database, string user, string password)
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
                ApplicationName = "U8Gateway.ReferenceData"
            }.ConnectionString;
        }

        public IList<RdStyleItem> GetRdStyles()
        {
            const string sql = @"
select
    r.cRdCode,
    r.cRdName,
    r.iRdGrade,
    r.bRdFlag,
    r.bRdEnd,
    r.cOppHead,
    isnull(r.bRetail, 0),
    r.pubufts
from dbo.Rd_Style r
where nullif(ltrim(rtrim(r.cRdCode)), N'') is not null
order by r.bRdFlag desc, r.cRdCode;";

            var result = new List<RdStyleItem>();
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 20;
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int flag = reader.GetInt32(3);
                        result.Add(new RdStyleItem
                        {
                            Code = reader.GetString(0).Trim(),
                            Name = reader.GetString(1).Trim(),
                            Grade = reader.GetByte(2),
                            Direction = flag == 1 ? "IN" : flag == 0 ? "OUT" : "UNKNOWN",
                            IsTerminal = reader.GetBoolean(4),
                            OppositeCode = reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
                            IsRetail = reader.GetBoolean(6),
                            SourceRowVersion = reader.IsDBNull(7)
                                ? null
                                : Convert.ToBase64String((byte[])reader[7])
                        });
                    }
                }
            }
            return result;
        }

        public IList<WarehousePositionItem> GetWarehousePositions()
        {
            const string sql = @"
select
    wh.cWhCode,
    wh.cWhName,
    ps.cPosCode,
    ps.cPosName
from dbo.Warehouse wh
left join dbo.Position ps on wh.cWhCode = ps.cWhCode
where wh.dWhEndDate is null
order by wh.cWhCode, ps.cPosCode;";

            var result = new List<WarehousePositionItem>();
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 20;
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new WarehousePositionItem
                        {
                            WarehouseCode = reader.GetString(0).Trim(),
                            WarehouseName = reader.GetString(1).Trim(),
                            PositionCode = reader.IsDBNull(2) ? null : reader.GetString(2).Trim(),
                            PositionName = reader.IsDBNull(3) ? null : reader.GetString(3).Trim()
                        });
                    }
                }
            }
            return result;
        }

        public IList<DepartmentItem> GetDepartments()
        {
            const string sql = @"
select
    d.cDepCode,
    d.cDepName
from dbo.Department d
where d.dDepEndDate is null
order by d.cDepCode;";

            var result = new List<DepartmentItem>();
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 20;
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new DepartmentItem
                        {
                            Code = reader.GetString(0).Trim(),
                            Name = reader.GetString(1).Trim()
                        });
                    }
                }
            }
            return result;
        }
    }
}
