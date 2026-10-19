"""Recover evidence after an output-only runner failure. Never reruns SQL mutations."""
from pathlib import Path
import argparse
import hashlib
import json
import re

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--repo", type=Path, required=True)
parser.add_argument("--run", required=True)
args = parser.parse_args()
assert re.fullmatch(r"[a-f0-9]{12}", args.run)
work = args.repo / ".tools/print-development-acceptance" / args.run
read = lambda name: json.loads((work / name).read_text(encoding="utf-8-sig"))
original = read("result.json")
final = read("final-sql-evidence.json")
checks = read("assertions.json")
log = (work / "frontend-test.log").read_text(encoding="utf-8")
assert re.search(r"Test Files\s+1 passed", log) and re.search(r"Tests\s+1 passed", log)
assert "FAIL " not in log and "1 failed" not in log
assert len(checks) == 11 and all(check["passed"] for check in checks)
required = {
    "first-plus-ten-concurrent-receipts-no-duplicate-ledger",
    "two-reprints-retain-paper-100-balance-35-without-new-code-or-positive",
    "lost-helper-and-committed-receipt-responses-restart-recovery-once",
    "actual-onlycode-insert-failure-rolls-back-all-three-then-restores-once",
    "actual-three-table-commit-response-lost-recovery-no-second-positive",
}
assert required <= {check["check"] for check in checks}
assert checks[-1]["details"]["driverCalls"] == 5
assert sorted(checks[-1]["details"]["labels"]) == sorted(final["ownedLabels"])
assert len(final["originalRowsUnchanged"]) == 14 and all(final["originalRowsUnchanged"].values())
assert all(v["beforeSha256"] == v["afterSha256"] for v in final["hashEvidence"].values())
assert final["ruleBindingRestored"] and final["faultTriggerRemoved"]
assert final["remainingOwnedLocks"] == 0 and final["deviceDisabled"] and final["ruleDisabled"]
before, after = read("public-before.json"), read("public-after.json")
assert before == after
assert all(x["unchanged"] for x in after["productionFiles"])
assert after["account"] == "888" and not any(after[k] for k in ("allowNewDispatch", "u8Writes", "recovery"))
post = read("post-readonly.json")
assert post["sqlTotals"]["printDelta"] == 0
assert post["sqlTotals"]["printFlows"] == 4 and post["sqlTotals"]["reprintFlows"] == 2
assert post["sqlTotals"]["targetTasks"] == post["sqlTotals"]["codes"] == 4
assert post["sqlTotals"]["completedTasks"] == 4
assert post["tracebackTotals"]["positives"] == 4 and post["tracebackTotals"]["flows"] == 5
assert post["tracebackTotals"]["balance"] == 63.5
assert post["remainingOwnedLocks"] == post["faultTriggers"] == post["ruleBindingCount"] == 0
sources = [
    "label-api/tools/PrintDevelopmentAcceptance/Program.cs",
    "u8-gateway/tools/PrintDevelopmentAcceptanceBridge/Program.cs",
    "New_Label_Print_System/frontend/src/lib/labelSystem.sql-acceptance.test.ts",
    "New_Label_Print_System/frontend/src/lib/labelSystem.ts",
    "New_Label_Print_System/backend/app/routes/labelsystem_print.py",
    "New_Label_Print_System/connector/labeljobs_v2.go",
    "New_Label_Print_System/connector/development_acceptance_bridge_test.go",
]
evidence = {
    **original, "passed": True, "scope": "Requested four live development SQL scenarios plus lost legacy commit response",
    "assertions": checks, "final": final, "postReadOnly": post, "publicProtectionBefore": before,
    "publicProtectionAfter": after, "originalRunnerPassed": original["passed"],
    "reportRecovery": {
        "reason": "Windows GBK stdout could not emit the Vitest success checkmark after scenarios had passed",
        "frontendLogPassed": True, "sqlProofPassed": True, "newSqlWritesDuringRecovery": False,
        "originalResultPreserved": True, "stdoutEncodingFixed": True,
        "frontendLogSha256": hashlib.sha256((work / "frontend-test.log").read_bytes()).hexdigest(),
    },
    "sourceSha256": {p: hashlib.sha256((args.repo / p).read_bytes()).hexdigest() for p in sources},
    "notVerified": ["Employee browser full UI", "Physical printer and loopback network policy", "All historical label and movement fixtures", "Backup restoration"],
}
target = args.repo / "docs/test-builds/2026-10-19-print-development-sql-evidence.json"
target.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print("PASS: verified completed frontend log, 11 checkpoints, 14 original-data hashes and final SQL state; evidence recovered without repeating writes.")
