using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using U8Gateway.Contracts.TraceBack;
using U8Gateway.U8.TraceBack;

namespace U8Gateway.TraceBackContractSmokeTest
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                MapsAvailableSnapshot();
                RejectsNegativeBalance();
                RejectsDuplicateGoods();
                RejectsInvalidJson();
                RejectsCodeIdentityConflict();
                AllowsMissingOrderWithoutGuessingSource();
                Console.WriteLine("traceback contract smoke passed");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void MapsAvailableSnapshot()
        {
            TraceBackLabelSnapshot result = Map(12m);
            Equal("AVAILABLE", result.Status);
            Equal("MAT-01", result.MaterialCode);
            Equal("B01", result.BatchNo);
            Equal("WH1", result.WarehouseCode);
            Equal("2601010001", result.SerialNo);
            Equal(12m, result.Balance);
            Equal(0, result.Anomalies.Count);
        }

        private static void RejectsNegativeBalance()
        {
            TraceBackLabelSnapshot result = Map(-1m);
            Equal("RECONCILIATION_REQUIRED", result.Status);
            Has(result, "TRACEBACK_NEGATIVE_BALANCE");
        }

        private static void RejectsDuplicateGoods()
        {
            TraceBackFlowAggregate flow = Flow(1m);
            TraceBackLabelSnapshot result = TraceBackSnapshotMapper.Map(
                flow, new[] { Goods(), Goods() }, new[] { Order() });
            Has(result, "TRACEBACK_GOODS_DUPLICATED");
        }

        private static void RejectsInvalidJson()
        {
            TraceBackRecord goods = Goods();
            goods.HasInvalidJson = true;
            TraceBackLabelSnapshot result = TraceBackSnapshotMapper.Map(
                Flow(1m), new[] { goods }, new[] { Order() });
            Has(result, "TRACEBACK_GOODS_JSON_INVALID");
        }

        private static void RejectsCodeIdentityConflict()
        {
            TraceBackRecord goods = Goods();
            goods.JsonPayloads = new[] { JObject.Parse("{\"materialCode\":\"OTHER\",\"batchNo\":\"B01\",\"unitCode\":\"EA\"}") };
            TraceBackLabelSnapshot result = TraceBackSnapshotMapper.Map(
                Flow(1m), new[] { goods }, new[] { Order() });
            Has(result, "TRACEBACK_CODE_MATERIAL_CONFLICT");
        }

        private static void AllowsMissingOrderWithoutGuessingSource()
        {
            TraceBackLabelSnapshot result = TraceBackSnapshotMapper.Map(
                Flow(1m), new[] { Goods() }, new TraceBackRecord[0]);
            Equal("AVAILABLE", result.Status);
            Equal(null, result.SourceBusinessType);
        }

        private static TraceBackLabelSnapshot Map(decimal balance) =>
            TraceBackSnapshotMapper.Map(Flow(balance), new[] { Goods() }, new[] { Order() });

        private static TraceBackFlowAggregate Flow(decimal balance) => new TraceBackFlowAggregate
        {
            Code = "008|MAT-01|B01|10.00|2601010001",
            FlowCount = 2,
            Balance = balance,
            LatestFlowId = 20,
            GoodsUuid = "11111111-1111-1111-1111-111111111111",
            OrderUuid = "22222222-2222-2222-2222-222222222222"
        };

        private static TraceBackRecord Goods() => new TraceBackRecord
        {
            Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
            JsonPayloads = new[]
            {
                JObject.Parse("{\"materialCode\":\"MAT-01\",\"materialName\":\"物料\",\"batchNo\":\"B01\",\"unitCode\":\"EA\",\"warehouseCode\":\"WH1\",\"locationCode\":\"P1\"}")
            }
        };

        private static TraceBackRecord Order() => new TraceBackRecord
        {
            Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
            JsonPayloads = new[] { JObject.Parse("{\"businessType\":\"材料出库\",\"erpDocumentNo\":\"MO1\"}") }
        };

        private static void Has(TraceBackLabelSnapshot result, string code)
        {
            foreach (TraceBackAnomaly anomaly in result.Anomalies)
                if (anomaly.Code == code) return;
            throw new InvalidOperationException("missing anomaly " + code);
        }

        private static void Equal(object expected, object actual)
        {
            if (!Equals(expected, actual))
                throw new InvalidOperationException("expected " + expected + ", actual " + actual);
        }
    }
}
