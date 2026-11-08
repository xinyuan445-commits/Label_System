using System;
using System.Collections.Generic;
using System.Linq;
using U8Gateway.U8.Documents;

internal static class CountApprovalTests
{
    public static void Run()
    {
        // Parent already audited: finish every pending child, outbound first, skip audited children.
        var state = new CountApprovalSnapshot { ParentVerified = true, NeedsInbound = true, NeedsOutbound = true,
            Linked = new[] { Child("08", 1), Child("09", 2), Child("08", 3, true) } };
        var calls = new List<long>();
        Action<CountLinkedVoucher> verify = x => { calls.Add(x.Id); x.Verified = true; };
        InventoryCountApproval.Verify(() => { }, () => state, verify);
        Require(calls.SequenceEqual(new long[] { 2, 1 }));
        calls.Clear();
        InventoryCountApproval.Verify(() => { }, () => state, verify);
        Require(calls.Count == 0); // Whole retry never re-audits or creates anything.

        // Mixed gain/loss, interrupted on inbound; retry resumes only the unfinished child.
        state.Linked = new[] { Child("08", 1), Child("09", 2) };
        Reject("SIMULATED_REJECTION", () => InventoryCountApproval.Verify(() => { }, () => state, x => {
            if (x.Type == "08") throw new CountApprovalException("SIMULATED_REJECTION", "rejected");
            verify(x);
        }));
        Require(state.Linked.Single(x => x.Type == "09").Verified);
        calls.Clear();
        InventoryCountApproval.Verify(() => { }, () => state, verify);
        Require(calls.SequenceEqual(new long[] { 1 }));

        // No difference legitimately generates no child; gain-only and loss-only require just their side.
        foreach (var type in new[] { "", "08", "09" })
        {
            state = new CountApprovalSnapshot { ParentVerified = true, NeedsInbound = type == "08",
                NeedsOutbound = type == "09", Linked = type == "" ? new CountLinkedVoucher[0] : new[] { Child(type, 1) } };
            InventoryCountApproval.Verify(() => { }, () => state, verify);
        }
        state = new CountApprovalSnapshot { ParentVerified = true, NeedsInbound = true };
        Reject("U8_COUNT_LINKED_NOT_READY", () => InventoryCountApproval.Verify(() => { }, () => state, verify));
        state = new CountApprovalSnapshot { ParentVerified = false };
        Reject("U8_COUNT_LINKED_NOT_READY", () => InventoryCountApproval.Verify(() => { }, () => state, verify));

        // COM response alone cannot confirm success, including a new child appearing during the audit.
        state = new CountApprovalSnapshot { ParentVerified = true, NeedsOutbound = true, Linked = new[] { Child("09", 2) } };
        Reject("U8_COUNT_STATE_NOT_CONFIRMED", () => InventoryCountApproval.Verify(() => { }, () => state, x => { }));
        int reads = 0;
        Reject("U8_COUNT_STATE_NOT_CONFIRMED", () => InventoryCountApproval.Verify(() => { }, () => {
            reads++;
            return new CountApprovalSnapshot { ParentVerified = true,
                Linked = reads == 1 ? new CountLinkedVoucher[0] : new[] { Child("08", 3, true) } };
        }, verify));
        bool readCalled = false;
        Reject("PARENT_REJECTED", () => InventoryCountApproval.Verify(
            () => { throw new CountApprovalException("PARENT_REJECTED", "rejected"); },
            () => { readCalled = true; return state; }, verify));
        Require(!readCalled);
        var states = new Dictionary<long, bool> { [1] = true, [2] = true };
        bool assigned = true;
        CountApprovalSnapshot ReadUnverify() => new CountApprovalSnapshot
        {
            ParentVerified = true, HasAssignedPositions = assigned,
            Linked = new[] { Child("08", 1, states[1]), Child("09", 2, states[2]) }
        };
        Action<CountLinkedVoucher> unverify = item => states[item.Id] = false;
        Action<CountLinkedVoucher> restore = item => states[item.Id] = true;
        Reject("U8_COUNT_POSITIONS_ASSIGNED",
            () => InventoryCountApproval.Unverify(() => { }, ReadUnverify, unverify, restore));
        Require(states.Values.All(x => x));
        assigned = false;
        Reject("PARENT_REJECTED", () => InventoryCountApproval.Unverify(
            () => { throw new CountApprovalException("PARENT_REJECTED", "rejected"); },
            ReadUnverify, unverify, restore));
        Require(states.Values.All(x => x));
        Console.WriteLine("inventory count linked audit contracts passed (retry, partial failure, readiness, final readback)");
    }
    private static CountLinkedVoucher Child(string type, long id, bool audited = false) =>
        new CountLinkedVoucher { Type = type, Id = id, Verified = audited };
    private static void Require(bool value)
    { if (!value) throw new InvalidOperationException("Count linked audit assertion failed."); }
    private static void Reject(string code, Action action)
    {
        try { action(); }
        catch (CountApprovalException error) { Require(error.Code == code); return; }
        throw new InvalidOperationException("Expected count approval failure: " + code);
    }
}
