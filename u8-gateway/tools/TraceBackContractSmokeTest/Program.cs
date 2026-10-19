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
                MapsLegacyPayloadSnapshot();
                RejectsNegativeBalance();
                RejectsDuplicateGoods();
                RejectsInvalidJson();
                RejectsCodeIdentityConflict();
                AllowsMissingOrderWithoutGuessingSource();
                RejectsInvalidSyncQuantity();
                RejectsMissingSyncTarget();
                BlocksNonTestWriteAccount();
                ValidatesInitialRegistrationWithoutSql();
                Console.WriteLine("traceback contract smoke passed");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void ValidatesInitialRegistrationWithoutSql()
        {
            var r=new InitialLabelRegistrationRequest {
                ContractVersion=1,ExpectedAbsent=true,EventKey="print-initial:test",
                Code="888|MAT-01|B01|12.5|000001",SerialNo="000001",Quantity=12.5m,
                OrderUuid="11111111-1111-4111-8111-111111111111",GoodsUuid="22222222-2222-4222-8222-222222222222",
                OperatorName="操作员",MaterialCode="MAT-01",MaterialName="测试物料",UnitCode="EA",BatchNo="B01",
                TargetWarehouseCode="WH1",TargetLocationCode="P1",ManufactureDate="2026-10-19",ExpiryDate="2027-10-19",
                AttributesJson="{}",SourceDocumentType="transfer",SourceDocumentId="T1",SourceDocumentNo="DB001",ErpDetailId="1"
            };
            r.PayloadHash=InitialRegistrationRules.Hash(r);
            Equal("1EB7CFC2390323655BEAAA9A4A4C7984281FDB16A8A3C810ABDD8E686D13D321",r.PayloadHash);
            InitialRegistrationRules.Validate(r);
            // Gate must reject before opening any SQL connection.
            try {
                new TraceBackSyncWriter("unused","unused","unused","unused","development",false,"008","unused","008").ApplyInitial(r);
                throw new InvalidOperationException("expected initial write gate");
            } catch(TraceBackSyncException e) { Equal("TRACEBACK_WRITE_ACCOUNT_BLOCKED",e.Code); }
            r.Quantity=12.6m;
            try { InitialRegistrationRules.Validate(r);throw new InvalidOperationException("expected payload conflict"); }
            catch(TraceBackSyncException e) { Equal("TRACEBACK_INITIAL_INVALID",e.Code); }
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

        private static void MapsLegacyPayloadSnapshot()
        {
            TraceBackRecord goods = new TraceBackRecord
            {
                Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                JsonPayloads = new[]
                {
                    JObject.Parse("{\"code\":\"MAT-01\",\"name\":\"物料\",\"warehouseCode\":\"WH1\",\"storageLocationCode\":\"P1\",\"Attribute\":{\"batchNumber\":\"B01\"},\"inventory\":{\"unit\":\"EA\",\"warehouse\":\"仓库\",\"storageLocation\":\"货位\"}}")
                }
            };
            TraceBackRecord order = new TraceBackRecord
            {
                Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["orderType"] = "材料出库"
                },
                JsonPayloads = new[]
                {
                    JObject.Parse("{\"documentNumber\":\"PDA1\",\"CurrencyOrderData\":{\"uploadedErpOrder\":\"MO1\"}}")
                }
            };

            TraceBackLabelSnapshot result = TraceBackSnapshotMapper.Map(
                Flow(12m), new[] { goods }, new[] { order });
            Equal("AVAILABLE", result.Status);
            Equal("MAT-01", result.MaterialCode);
            Equal("B01", result.BatchNo);
            Equal("EA", result.UnitCode);
            Equal("P1", result.LocationCode);
            Equal("材料出库", result.SourceBusinessType);
            Equal("PDA1", result.PdaDocumentNo);
            Equal("MO1", result.ErpDocumentNo);
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

        private static void RejectsInvalidSyncQuantity()
        {
            TraceBackSyncException error = ThrowsSync(Request("MATERIAL_ISSUE", 1m));
            Equal("TRACEBACK_EVENT_QUANTITY_INVALID", error.Code);
        }

        private static void BlocksNonTestWriteAccount()
        {
            TraceBackSyncException error = ThrowsSync(Request("MATERIAL_ISSUE", -1m));
            Equal("TRACEBACK_WRITE_ACCOUNT_BLOCKED", error.Code);
        }

        private static void RejectsMissingSyncTarget()
        {
            TraceBackSyncException error = ThrowsSync(Request("MATERIAL_RETURN", 1m));
            Equal("TRACEBACK_TARGET_MISSING", error.Code);
        }

        private static TraceBackSyncException ThrowsSync(TraceBackSyncEventRequest request)
        {
            try
            {
                new TraceBackSyncWriter(
                    "server", "production", "user", "password",
                    "development", false, "008", "production", "008").Apply(request);
            }
            catch (TraceBackSyncException error)
            {
                return error;
            }
            throw new InvalidOperationException("expected sync error");
        }

        private static TraceBackSyncEventRequest Request(string role, decimal quantity) =>
            new TraceBackSyncEventRequest
            {
                EventKey = "test-event",
                EventRole = role,
                Code = "008|MAT-01|B01|10.00|2601010001",
                Quantity = quantity,
                ExpectedLatestFlowId = 20,
                ExpectedBalance = 12m,
                OrderUuid = "22222222-2222-2222-2222-222222222222",
                GoodsUuid = "33333333-3333-3333-3333-333333333333",
                OrderType = "材料出库",
                PdaDocumentNo = "PDA1",
                ErpDocumentNo = "MO1",
                OperatorName = "tester"
            };

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
