using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using U8Gateway.Contracts.TraceBack;

namespace U8Gateway.U8.TraceBack
{
    public sealed partial class TraceBackSnapshotReader
    {
        public LegacyPrintEvidence GetInitialPrintEvidence(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > 100)
                throw new ArgumentException("invalid code");
            using (var c = new SqlConnection(_connectionString))
            {
                c.Open();
                var contract = ReadContract(c);
                TraceBackFlowAggregate initial;
                string printedAt;
                using (var cmd = new SqlCommand(@"
SELECT TOP(1) id,TRY_CONVERT(decimal(28,6),[sum]),pdaGoodsUuid,pdaOrderUuid,sn,[time]
FROM dbo.TraceBackOnlyCode WHERE traceBackCode=@code ORDER BY id;", c))
                {
                    AddKeyParameter(cmd, "@code", code, contract.Code);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read() || reader.IsDBNull(1) || reader.GetDecimal(1) <= 0 ||
                            reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(5))
                            throw new TraceBackContractException("历史首次打印依据不完整。");
                        initial = new TraceBackFlowAggregate
                        {
                            Code = code, FlowCount = 1, Balance = reader.GetDecimal(1), LatestFlowId = Convert.ToInt64(reader[0]),
                            GoodsUuid = Convert.ToString(reader[2]).Trim(), OrderUuid = Convert.ToString(reader[3]).Trim(),
                            SerialNo = reader.IsDBNull(4) ? "" : Convert.ToString(reader[4]).Trim()
                        };
                        printedAt = Convert.ToDateTime(reader[5], CultureInfo.InvariantCulture)
                            .ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "+08:00";
                    }
                }
                var goods = ReadRecords(c, "dbo.TraceBackGoods", "pdaGoodsUuid", new[] { initial.GoodsUuid }, contract.GoodsUuid);
                var orders = ReadRecords(c, "dbo.TraceBackOrder", "pdaOrderUuid", new[] { initial.OrderUuid }, contract.OrderUuid);
                IList<TraceBackRecord> g, o;
                if (!goods.TryGetValue(initial.GoodsUuid, out g) || g.Count != 1 ||
                    !orders.TryGetValue(initial.OrderUuid, out o) || o.Count != 1)
                    throw new TraceBackContractException("历史首次打印关联不唯一。");
                object type;
                if (!o[0].Values.TryGetValue("orderType", out type) || Convert.ToString(type) != "打印平台")
                    throw new TraceBackContractException("无法核验历史打印来源。");
                var snapshot = TraceBackSnapshotMapper.Map(initial, g, o);
                if (snapshot.Status != "AVAILABLE" || string.IsNullOrWhiteSpace(snapshot.MaterialName) ||
                    string.IsNullOrWhiteSpace(snapshot.SerialNo) ||
                    (string.IsNullOrWhiteSpace(snapshot.ErpDocumentNo) && string.IsNullOrWhiteSpace(snapshot.PdaDocumentNo)))
                    throw new TraceBackContractException("历史标签原始内容不完整。");
                var parts = code.Split('|');
                decimal quantity;
                if (parts.Length == 5 && (!decimal.TryParse(parts[3], NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out quantity) || quantity != initial.Balance))
                    throw new TraceBackContractException("历史标签原数量不一致。");
                string serialized = JsonConvert.SerializeObject(new { initial, printedAt, snapshot });
                string hash;
                using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(serialized))).Replace("-", "");
                return new LegacyPrintEvidence { Code = code, InitialFlowId = initial.LatestFlowId.Value,
                    InitialQuantity = initial.Balance.Value, PrintedAt = printedAt, EvidenceHash = hash, Original = snapshot };
            }
        }
    }
}
