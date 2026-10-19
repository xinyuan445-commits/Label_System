import { expect, it, vi } from "vitest";
import { durable, ls, recoverPrint, sendPrint, type PrintJob } from "./labelSystem";

// Explicit live DEVELOPMENT SQL test only. Ordinary npm test skips it.
declare const process: { env: Record<string, string | undefined> };
type SqlAudit = {
  code: string;
  quantity: number;
  sourceFlows: number;
  printFlows: number;
  reprintFlows: number;
  printDelta: number;
  registeredCodes: number;
  targetTasks: number;
  targetEvent: string | null;
  lockOwner: number | null;
  rowVersion: string;
  sequence: number;
  traceback: { flows: number; goods: number; orders: number; balance: number; positives: number };
  initialRows?: { orders: number; goods: number; flows: number };
};
type Target = { labelId: number; originLineId: number; rowVersion: string };
const env = process.env;
const enabled = !!env.LABEL_SQL_ACCEPTANCE_WEB;
it.skipIf(!enabled)(
  "original frontend → Flask → existing development SQL → 888, mock sends only",
  async () => {
    const state = JSON.parse(env.LABEL_SQL_ACCEPTANCE_STATE!);
    const web = env.LABEL_SQL_ACCEPTANCE_WEB!;
    const control = env.LABEL_SQL_ACCEPTANCE_CONTROL!;
    const nativeFetch = globalThis.fetch;
    const cookies = new Map<string, string>();
    const local = new Map<string, string>();
    let dropHelper = false;
    let dropReceipt = false;
    let sends = 0;
    const receipts = new Map<string, unknown>();
    const ctl = async <T = Record<string, unknown>>(path: string, body = {}): Promise<T> => {
      const response = await nativeFetch(control + path, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Acceptance-Key": env.LABEL_SQL_ACCEPTANCE_KEY!,
        },
        body: JSON.stringify(body),
      });
      const data = await response.json();
      if (!response.ok) throw new Error(JSON.stringify(data));
      return data as T;
    };
    vi.stubGlobal("localStorage", {
      get length() {
        return local.size;
      },
      key: (i: number) => [...local.keys()][i] ?? null,
      getItem: (key: string) => local.get(key) ?? null,
      setItem: (key: string, value: string) => local.set(key, value),
      removeItem: (key: string) => local.delete(key),
    });
    vi.stubGlobal("document", {
      get cookie() {
        return "csrf_token=" + (cookies.get("csrf_token") ?? "");
      },
    });
    vi.stubGlobal("fetch", async (input: string | URL | Request, init?: RequestInit) => {
      const path = String(input);
      if (path.startsWith("http://127.0.0.1:9110")) {
        const helperPath = path.slice("http://127.0.0.1:9110".length);
        if (helperPath === "/status")
          return new Response(JSON.stringify(await ctl("/helper/status")));
        const body = init?.body ? JSON.parse(init.body as string) : undefined;
        const result = await ctl("/helper/request", {
          method: init?.method ?? "GET",
          path: helperPath,
          body,
        });
        if (helperPath === "/v2/print-labels") {
          sends++;
          const ticket = JSON.parse(atob(body.authorization.payload));
          receipts.set(ticket.jobId, result);
          if (dropHelper) {
            dropHelper = false;
            throw new Error("simulated lost helper response");
          }
        }
        return new Response(JSON.stringify(result), {
          headers: { "Content-Type": "application/json" },
        });
      }
      const headers = new Headers(init?.headers);
      headers.set("Cookie", [...cookies].map(([k, v]) => `${k}=${v}`).join("; "));
      const response = await nativeFetch(path.startsWith("http") ? path : web + path, {
        ...init,
        headers,
      });
      for (const value of response.headers.getSetCookie()) {
        const [pair] = value.split(";");
        const index = pair.indexOf("=");
        cookies.set(pair.slice(0, index), pair.slice(index + 1));
      }
      if (dropReceipt && path.endsWith("/receipts") && response.ok) {
        dropReceipt = false;
        await response.arrayBuffer();
        throw new Error("simulated lost committed receipt response");
      }
      return response;
    });
    const record = async (name: string, details: unknown) =>
      ctl("/record", { check: name, passed: true, details });
    const audit = async (labelId: number) => ctl<SqlAudit>("/sql", { method: "audit", labelId });
    const helperCount = async () => (await ctl<{ count: number }>("/helper/count")).count;
    const checkLedger = (
      a: SqlAudit,
      quantity: number,
      prints: number,
      reprints: number,
      oldFlows: number,
      balance: number,
      locked = false,
    ) => {
      expect(a.quantity).toBe(quantity);
      expect(a.sourceFlows).toBe(1);
      expect(a.printFlows).toBe(prints);
      expect(a.reprintFlows).toBe(reprints);
      expect(a.printDelta).toBe(0);
      expect(a.registeredCodes).toBe(1);
      expect(a.targetTasks).toBe(prints ? 1 : 0);
      expect(a.traceback.flows).toBe(oldFlows);
      expect(a.traceback.goods).toBe(oldFlows);
      expect(a.traceback.orders).toBe(oldFlows);
      expect(a.traceback.balance).toBe(balance);
      expect(a.traceback.positives).toBe(oldFlows ? 1 : 0);
      expect(a.lockOwner !== null).toBe(locked);
    };
    await fetch("/api/me");
    const login = await fetch("/api/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-CSRF-Token": cookies.get("csrf_token")! },
      body: JSON.stringify({ email: "admin", password: "admin" }),
    });
    expect(login.status).toBe(200);
    const beforeRule = await ls<{ rowVersion: string } | null>("/print/code-rule");
    await ls(
      "/print/code-rule",
      { ruleVersionId: state.ruleVersionId, expectedRowVersion: beforeRule?.rowVersion ?? null },
      "PUT",
    );
    await ls("/print/devices", {
      id: state.deviceId,
      name: state.prefix + "模拟助手",
      publicKey: state.devicePublicKey,
      ownerUserId: state.userId,
    });
    const formats = await (await fetch("/api/label-formats")).json();
    const format = formats.formats.find(
      (f: { width_mm: number; height_mm: number }) => f.width_mm === 76.2 && f.height_mm === 50.8,
    );
    const canvas = {
      version: 1,
      stage: { width_mm: 76.2, height_mm: 50.8 },
      objects: [
        {
          id: "code",
          type: "barcode",
          data: "{{条码}}",
          barcodeType: "qr",
          x: 2,
          y: 2,
          width: 25,
          height: 25,
        },
        {
          id: "qty",
          type: "text",
          text: "验收 数量 {{数量}}",
          x: 30,
          y: 3,
          width: 40,
          height: 8,
          fontSize: 3,
        },
      ],
    };
    const draftResponse = await fetch("/api/templates", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-CSRF-Token": cookies.get("csrf_token")! },
      body: JSON.stringify({
        name: state.prefix + "原设计器发布",
        format_id: format.id,
        canvas_data: canvas,
      }),
    });
    expect(draftResponse.status).toBe(201);
    const draft = await draftResponse.json();
    const published = await ls<{ templateId: number; versionId: number }>(
      `/designer-templates/${draft.id}/publish`,
      {},
    );
    expect(
      (await ls<{ reused: boolean }>(`/designer-templates/${draft.id}/publish`, {})).reused,
    ).toBe(true);
    await record("original-designer-publishes-once-to-existing-017", published);
    const first = async (scenario: string, quantity: number) => {
      const target = await ctl<Target>("/sql", { method: "seed", scenario, quantity });
      const pendingTargets = await ls<Target[]>("/print/pending-labels");
      const selected = pendingTargets.find((item) => item.labelId === target.labelId);
      expect(selected).toBeDefined();
      await record("read-current-target-" + scenario, { seeded: target, selected });
      const body = {
        templateVersionId: published.versionId,
        deviceId: state.deviceId,
        printer: "Acceptance mock driver",
        targets: [
          {
            labelId: target.labelId,
            originLineId: target.originLineId,
            expectedRowVersion: selected!.rowVersion,
          },
        ],
      };
      const job = await durable(`sql:${scenario}`, body, (request) =>
        ls<PrintJob>("/print/jobs/first", request),
      );
      expect(job.phase).toBe("READY");
      return { target, job, body };
    };
    const initial = await first("first", 100);
    const ready = await audit(initial.target.labelId);
    checkLedger(ready, 100, 0, 0, 0, 0, true);
    const firstJob = await sendPrint(initial.job);
    expect(firstJob.phase).toBe("COMPLETED");
    const countAfterFirst = await helperCount();
    expect(countAfterFirst).toBe(1);
    await Promise.all(
      Array.from({ length: 10 }, () =>
        ls(`/print/jobs/${firstJob.jobId}/receipts`, receipts.get(firstJob.jobId)),
      ),
    );
    const a = await audit(initial.target.labelId);
    checkLedger(a, 100, 1, 0, 1, 100);
    await record("first-plus-ten-concurrent-receipts-no-duplicate-ledger", {
      ...a,
      driverCalls: await helperCount(),
    });
    expect(await helperCount()).toBe(countAfterFirst);

    await ctl("/sql", { method: "consume", labelId: initial.target.labelId, quantity: 65 });
    await ls("/print/labels/resolve", { code: a.code });
    const consumed = await audit(initial.target.labelId);
    expect(consumed.quantity).toBe(35);
    const reprint = await durable(
      "sql:reprint",
      {
        labelId: initial.target.labelId,
        expectedRowVersion: consumed.rowVersion,
        copies: 2,
        reason: state.prefix + "补打验收",
      },
      (body) => ls<PrintJob>("/print/jobs/reprint", body),
    );
    expect(reprint.outputs).toHaveLength(2);
    expect(
      reprint.outputs.every(
        (output) => output.snapshot.code === a.code && output.snapshot.quantity === "100",
      ),
    ).toBe(true);
    await sendPrint(reprint);
    await ls(`/print/jobs/${reprint.jobId}/receipts`, receipts.get(reprint.jobId));
    const r = await audit(initial.target.labelId);
    checkLedger(r, 35, 1, 2, 2, 35);
    expect(r.sequence).toBe(consumed.sequence);
    await record("two-reprints-retain-paper-100-balance-35-without-new-code-or-positive", r);

    const lost = await first("lost-receipt", 12.5);
    dropHelper = true;
    await expect(sendPrint(lost.job)).rejects.toThrow("simulated lost helper");
    const submittedCount = await helperCount();
    const beforeReceipt = await audit(lost.target.labelId);
    checkLedger(beforeReceipt, 12.5, 0, 0, 0, 0, true);
    await ctl("/helper/restart");
    const authorized = await ls<PrintJob>(`/print/jobs/${lost.job.jobId}`);
    dropReceipt = true;
    await expect(recoverPrint(authorized)).rejects.toThrow("simulated lost committed");
    const committed = await audit(lost.target.labelId);
    checkLedger(committed, 12.5, 1, 0, 1, 12.5);
    expect((await recoverPrint(authorized)).phase).toBe("COMPLETED");
    const recovered = await audit(lost.target.labelId);
    checkLedger(recovered, 12.5, 1, 0, 1, 12.5);
    expect(await helperCount()).toBe(submittedCount);
    expect(recovered.targetEvent).toBe(committed.targetEvent);
    await record("lost-helper-and-committed-receipt-responses-restart-recovery-once", {
      beforeReceipt,
      committed,
      recovered,
      driverCalls: submittedCount,
    });

    const failed = await first("three-table-failure", 7);
    await ctl("/sql", { method: "fault", jobId: failed.job.jobId, enabled: true });
    const pending = await sendPrint(failed.job);
    expect(pending.phase).toBe("TRACEBACK_PENDING");
    const failedCount = await helperCount();
    const rolledBack = await audit(failed.target.labelId);
    checkLedger(rolledBack, 7, 1, 0, 0, 0, true);
    expect(rolledBack.initialRows).toEqual({ orders: 0, goods: 0, flows: 0 });
    expect((await recoverPrint(pending)).phase).toBe("TRACEBACK_PENDING");
    await ctl("/sql", { method: "fault", enabled: false });
    expect((await recoverPrint(pending)).phase).toBe("COMPLETED");
    await ls(`/print/jobs/${pending.jobId}/receipts`, receipts.get(pending.jobId));
    const restored = await audit(failed.target.labelId);
    checkLedger(restored, 7, 1, 0, 1, 7);
    expect(restored.initialRows).toEqual({ orders: 1, goods: 1, flows: 1 });
    expect(restored.targetEvent).toBe(rolledBack.targetEvent);
    expect(restored.sequence).toBe(rolledBack.sequence);
    expect(await helperCount()).toBe(failedCount);
    await record("actual-onlycode-insert-failure-rolls-back-all-three-then-restores-once", {
      rolledBack,
      restored,
      driverCalls: failedCount,
    });

    const wire = await first("legacy-commit-response-lost", 9);
    await ctl("/sql", { method: "drop-next-initial-response" });
    const wirePending = await sendPrint(wire.job);
    expect(wirePending.phase).toBe("TRACEBACK_PENDING");
    const wireCount = await helperCount();
    const oldCommitted = await audit(wire.target.labelId);
    checkLedger(oldCommitted, 9, 1, 0, 1, 9, true);
    expect((await recoverPrint(wirePending)).phase).toBe("COMPLETED");
    const wireRestored = await audit(wire.target.labelId);
    checkLedger(wireRestored, 9, 1, 0, 1, 9);
    expect(wireRestored.targetEvent).toBe(oldCommitted.targetEvent);
    expect(await helperCount()).toBe(wireCount);
    expect(sends).toBe(5);
    await record("actual-three-table-commit-response-lost-recovery-no-second-positive", {
      oldCommitted,
      wireRestored,
      driverCalls: wireCount,
    });
    await record("all-four-targets-complete-with-five-mock-driver-dispatches", {
      labels: [
        initial.target.labelId,
        lost.target.labelId,
        failed.target.labelId,
        wire.target.labelId,
      ],
      driverCalls: await helperCount(),
      realPrinterStarted: false,
    });
    vi.unstubAllGlobals();
  },
  360000,
);
