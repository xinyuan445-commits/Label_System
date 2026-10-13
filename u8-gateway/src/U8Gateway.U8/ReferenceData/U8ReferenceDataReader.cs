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

        public DocumentMasterData GetDocumentMasterData()
        {
            return new DocumentMasterData
            {
                PurchaseTypes = ReadDocumentTypes(
                    "dbo.PurchaseType", "cPTCode", "cPTName"),
                SalesTypes = ReadDocumentTypes(
                    "dbo.SaleType", "cSTCode", "cSTName"),
                Currencies = ReadCurrencies()
            };
        }

        private IList<DocumentTypeItem> ReadDocumentTypes(
            string tableName,
            string codeColumn,
            string nameColumn)
        {
            string sql = string.Format(@"
select {0}, {1}, cRdCode, isnull(bDefault, 0), pubufts
from {2}
where nullif(ltrim(rtrim({0})), N'') is not null
order by {0};", codeColumn, nameColumn, tableName);
            var result = new List<DocumentTypeItem>();
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 20;
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new DocumentTypeItem
                        {
                            Code = reader.GetString(0).Trim(),
                            Name = reader.GetString(1).Trim(),
                            RdStyleCode = reader.IsDBNull(2) ? null : reader.GetString(2).Trim(),
                            IsDefault = reader.GetBoolean(3),
                            SourceRowVersion = reader.IsDBNull(4)
                                ? null
                                : Convert.ToBase64String((byte[])reader[4])
                        });
                    }
                }
            }
            return result;
        }

        private IList<CurrencyItem> ReadCurrencies()
        {
            const string sql = @"
select
    currency.cexch_code,
    currency.cexch_name,
    convert(bit, case when currency.bcal = 0 then 1 else 0 end),
    currency.bcal,
    currency.idec,
    latest.nflat,
    latest.iYear,
    latest.iperiod,
    latest.cdate,
    currency.pubufts
from dbo.foreigncurrency currency
outer apply
(
    select top (1) rate.nflat, rate.iYear, rate.iperiod, rate.cdate
    from dbo.exch rate
    where rate.cexch_name = currency.cexch_name
    order by rate.iYear desc, rate.iperiod desc,
             try_convert(int, rate.cdate) desc, rate.i_id desc
) latest
where nullif(ltrim(rtrim(currency.cexch_code)), N'') is not null
order by currency.cexch_code;";
            var result = new List<CurrencyItem>();
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 20;
                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new CurrencyItem
                        {
                            Code = reader.GetString(0).Trim(),
                            Name = reader.GetString(1).Trim(),
                            IsLocal = reader.GetBoolean(2),
                            UsesExchangeRate = reader.GetBoolean(3),
                            DecimalDigits = reader.GetByte(4),
                            LatestRate = reader.IsDBNull(5)
                                ? (reader.GetBoolean(2) ? 1m : (decimal?)null)
                                : Convert.ToDecimal(reader.GetDouble(5)),
                            RateYear = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6),
                            RatePeriod = reader.IsDBNull(7) ? (byte?)null : reader.GetByte(7),
                            RateDate = reader.IsDBNull(8) ? null : reader.GetString(8).Trim(),
                            SourceRowVersion = reader.IsDBNull(9)
                                ? null
                                : Convert.ToBase64String((byte[])reader[9])
                        });
                    }
                }
            }
            return result;
        }
    }
}
