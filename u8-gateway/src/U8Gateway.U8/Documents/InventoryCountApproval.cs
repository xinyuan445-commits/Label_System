using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using U8Gateway.Contracts;
using U8Gateway.Contracts.Documents;
using U8Gateway.Core.Documents;

namespace U8Gateway.U8.Documents
{
    public sealed class CountLinkedVoucher
    {
        public string Type { get; set; }
        public long Id { get; set; }
        public string Code { get; set; }
        public bool Verified { get; set; }
    }

    public sealed class CountApprovalSnapshot
    {
        public bool ParentVerified { get; set; }
        public bool NeedsInbound { get; set; }
        public bool NeedsOutbound { get; set; }
        public IReadOnlyList<CountLinkedVoucher> Linked { get; set; } = new List<CountLinkedVoucher>();
        public bool HasRequiredVouchers =>
            (!NeedsInbound || Linked.Any(x => x.Type == "08")) &&
            (!NeedsOutbound || Linked.Any(x => x.Type == "09"));
    }

    public sealed class CountApprovalException : InvalidOperationException
    {
        public CountApprovalException(string code, string message, bool known = true) : base(message)
        { Code = code; OutcomeKnown = known; }
        public string Code { get; }
        public bool OutcomeKnown { get; }
    }

    // Shared orchestration is testable without SQL or COM. Real state is always read again.
    public static class InventoryCountApproval
    {
        public static void Verify(Action verifyParent, Func<CountApprovalSnapshot> read,
            Action<CountLinkedVoucher> verifyChild)
        {
            verifyParent();
            var saved = read();
            if (!saved.ParentVerified || !saved.HasRequiredVouchers)
                throw new CountApprovalException("U8_COUNT_LINKED_NOT_READY",
                    "盘点关联出入库单尚未生成完整，请重试审核。");
            foreach (var item in saved.Linked.OrderBy(x => x.Type == "09" ? 0 : 1).ThenBy(x => x.Id))
                if (!item.Verified) verifyChild(item);
            var after = read();
            var beforeIds = saved.Linked.Select(x => x.Type + ":" + x.Id).OrderBy(x => x).ToArray();
            var afterIds = after.Linked.Select(x => x.Type + ":" + x.Id).OrderBy(x => x).ToArray();
            if (!after.ParentVerified || !after.HasRequiredVouchers ||
                !beforeIds.SequenceEqual(afterIds) || after.Linked.Any(x => !x.Verified))
                throw new CountApprovalException("U8_COUNT_STATE_NOT_CONFIRMED",
                    "盘点或关联出入库单审核未全部确认，请重试审核。", false);
        }
    }

    public sealed partial class LateBoundU8DocumentRuntime
    {
        private DocumentOperationResult VerifyInventoryCount(U8GatewayRuntimeOptions options,
            U8DocumentExecution execution, long id)
        {
            string code = ReadVoucherCode(options, execution.Profile, id);
            try
            {
                InventoryCountApproval.Verify(() => {
                    var result = EnsureInventoryState(options, execution.Profile, id, true, "盘点单");
                    if (!result.Succeeded)
                        throw new CountApprovalException("U8_VERIFY_REJECTED", result.Message);
                }, () => {
                    CountApprovalSnapshot snapshot = null;
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        snapshot = ReadCountApproval(options, id, code);
                        if (snapshot.HasRequiredVouchers) break;
                        if (attempt < 9) Thread.Sleep(100);
                    }
                    return snapshot;
                }, item => {
                    var result = EnsureInventoryState(options, LinkedTransferProfile(item.Type),
                        item.Id, true, (item.Type == "08" ? "其他入库单 " : "其他出库单 ") + item.Code);
                    if (!result.Succeeded)
                        throw new CountApprovalException("U8_COUNT_LINKED_VERIFY_REJECTED", result.Message);
                });
                var state = ReadVoucherState(options, execution.Profile, id);
                return Success(execution, id, state.Version, "盘点及关联出入库单审核成功。");
            }
            catch (CountApprovalException error)
            { return Failure(error.Code, error.Message, code, error.OutcomeKnown); }
        }

        private static CountApprovalSnapshot ReadCountApproval(U8GatewayRuntimeOptions options, long id, string code)
        {
            // Native 888 evidence: cSource='盘点', cBusCode=CheckVouch.cCVCode;
            // detail iCheckIds is null, so it cannot be used as a linkage key.
            const string sql = @"
select case when nullif(ltrim(rtrim(h.cAccounter)),N'') is null then 0 else 1 end,
    case when exists(select 1 from dbo.CheckVouchs d where d.ID=h.ID and d.iCVCQuantity>d.iCVQuantity) then 1 else 0 end,
    case when exists(select 1 from dbo.CheckVouchs d where d.ID=h.ID and d.iCVCQuantity<d.iCVQuantity) then 1 else 0 end
from dbo.CheckVouch h where h.ID=@id and h.cCVCode=@code;
select N'08',convert(bigint,r.ID),r.cCode,
    case when nullif(ltrim(rtrim(r.cHandler)),N'') is null then 0 else 1 end
from dbo.RdRecord08 r join dbo.CheckVouch h on h.ID=@id and r.cWhCode=h.cWhCode
where r.cSource=N'盘点' and r.cBusCode=@code
union all
select N'09',convert(bigint,r.ID),r.cCode,
    case when nullif(ltrim(rtrim(r.cHandler)),N'') is null then 0 else 1 end
from dbo.RdRecord09 r join dbo.CheckVouch h on h.ID=@id and r.cWhCode=h.cWhCode
where r.cSource=N'盘点' and r.cBusCode=@code;";
            using (var db = new SqlConnection(ConnectionString(options)))
            using (var query = new SqlCommand(sql, db))
            {
                query.Parameters.AddWithValue("@id", id);
                query.Parameters.AddWithValue("@code", code);
                db.Open();
                using (var reader = query.ExecuteReader())
                {
                    if (!reader.Read()) throw new CountApprovalException("VOUCHER_NOT_FOUND", "盘点单不存在。");
                    var snapshot = new CountApprovalSnapshot { ParentVerified = reader.GetInt32(0) == 1,
                        NeedsInbound = reader.GetInt32(1) == 1, NeedsOutbound = reader.GetInt32(2) == 1 };
                    var linked = new List<CountLinkedVoucher>();
                    reader.NextResult();
                    while (reader.Read()) linked.Add(new CountLinkedVoucher { Type = reader.GetString(0),
                        Id = reader.GetInt64(1), Code = reader.GetString(2), Verified = reader.GetInt32(3) == 1 });
                    snapshot.Linked = linked;
                    return snapshot;
                }
            }
        }
    }
}
