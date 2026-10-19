import { beforeEach, expect, it, vi } from "vitest";
import { ApiError } from "./api";
import {
  durable,
  configureStation,
  station,
  pendingCreations,
  recoverPrint,
  retryCreation,
  sendPrint,
  type PrintJob,
} from "./labelSystem";

const job = {
  operationId: 1,
  jobId: "11111111-1111-4111-8111-111111111111",
  deviceId: "device",
  deviceKeyHash: "keyhash",
  phase: "READY",
  rowVersion: "row1",
  printer: "Mock",
  outputs: [
    {
      outputId: "out1",
      kind: "FIRST",
      phase: "READY",
      flowId: null,
      snapshot: {
        labelId: 1,
        code: "full-code",
        quantity: "100",
        materialCode: "A",
        materialName: "A",
        variables: {},
      },
    },
  ],
  attemptId: "attempt",
  authorization: null,
  manifest: { scope: "dev|888" },
} satisfies PrintJob;
const response = (data: unknown) =>
  new Response(JSON.stringify(data), { headers: { "Content-Type": "application/json" } });
beforeEach(() => {
  configureStation({ simulationOnly: false });
  const storage = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    get length() {
      return storage.size;
    },
    key: (i: number) => Array.from(storage.keys())[i] ?? null,
    getItem: (k: string) => storage.get(k) ?? null,
    setItem: (k: string, v: string) => storage.set(k, v),
    removeItem: (k: string) => storage.delete(k),
  });
  vi.stubGlobal("document", { cookie: "csrf_token=test" });
  vi.stubGlobal("fetch", vi.fn());
});
it("normal sends contact the local assistant without a server simulation route", async () => {
  configureStation({ simulationOnly: false });
  vi.mocked(fetch).mockResolvedValue(response({ payload: "mock", signature: "signed" }));
  await station("/v2/print-labels", {
    authorization: { payload: "original", signature: "signed" },
    bundle: "bundle",
  });
  const [path, options] = vi.mocked(fetch).mock.calls[0];
  expect(path).toBe("http://127.0.0.1:9110/v2/print-labels");
  expect((options?.headers as Record<string, string>)["X-CSRF-Token"]).toBeUndefined();
});
it("receipt recovery retains the original job and attempt on the local assistant", async () => {
  configureStation({ simulationOnly: false });
  vi.mocked(fetch).mockImplementation(async (input) =>
    String(input).includes("127.0.0.1:9110/v2/label-jobs/")
      ? response({ payload: "saved", signature: "signed" })
      : response({ ...job, phase: "COMPLETED" }),
  );
  await recoverPrint({ ...job, phase: "DISPATCH_AUTHORIZED" });
  const calls = vi.mocked(fetch).mock.calls.map(([path]) => String(path));
  expect(calls[0]).toContain("127.0.0.1:9110/v2/label-jobs/");
  expect(calls[1]).toContain("/receipts");
  expect(
    calls.some((path) => path.includes("/simulation/") || path.endsWith("/v2/print-labels")),
  ).toBe(false);
});
it("retains the request identity across a lost first response", async () => {
  const attempts: Record<string, unknown>[] = [];
  const call = async (body: Record<string, unknown>) => {
    attempts.push(body);
    if (attempts.length === 1) throw new Error("lost");
    return "original-job";
  };
  await expect(durable("user:first", { targets: [1] }, call)).rejects.toThrow("lost");
  expect(await durable("user:first", { targets: [1] }, call)).toBe("original-job");
  expect(attempts[0].clientRequestId).toBe(attempts[1].clientRequestId);
});
it("releases a source request rejected before reservation but retains unknown outcomes", async () => {
  await expect(
    durable("1:source-stock:stock", { quantity: 1 }, async () => {
      throw new ApiError(409, { error: "PRINT_SOURCE_EXHAUSTED" });
    }),
  ).rejects.toThrow();
  expect(pendingCreations(1)).toEqual([]);
  await expect(
    durable("1:source-stock:stock", { quantity: 2 }, async () => {
      throw new ApiError(503, { error: "unavailable" });
    }),
  ).rejects.toThrow();
  expect(pendingCreations(1)).toHaveLength(1);
});
it("blocks changed payloads while an old creation request is unknown", async () => {
  await expect(
    durable("user:first", { targets: [1] }, async () => {
      throw new Error("lost");
    }),
  ).rejects.toThrow();
  const call = vi.fn();
  await expect(durable("user:first", { targets: [2] }, call)).rejects.toThrow("原请求");
  expect(call).not.toHaveBeenCalled();
});
it("does not contact a printer when new sends are disabled", async () => {
  vi.mocked(fetch).mockResolvedValue(response({ enabled: true, allowNewDispatch: false }));
  await expect(sendPrint(job)).rejects.toThrow("关闭");
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("recovers a lost receipt by GET of the original attempt, never POSTing labels", async () => {
  vi.mocked(fetch).mockImplementation(async (input) =>
    String(input).startsWith("http://127.0.0.1:9110/v2/label-jobs/")
      ? response({ payload: "original", signature: "signed" })
      : response({ ...job, phase: "COMPLETED" }),
  );
  expect((await recoverPrint({ ...job, phase: "DISPATCH_AUTHORIZED" })).phase).toBe("COMPLETED");
  const paths = vi.mocked(fetch).mock.calls.map(([path]) => String(path));
  expect(paths).toHaveLength(2);
  expect(paths[0]).toContain("/attempt?scope=dev%7C888");
  expect(paths[1]).toContain("/receipts");
  expect(paths.some((p) => p.endsWith("/v2/print-labels"))).toBe(false);
});
it("three-table failure recovery calls only retry-sync", async () => {
  vi.mocked(fetch).mockResolvedValue(response({ ...job, phase: "COMPLETED" }));
  await recoverPrint({ ...job, phase: "TRACEBACK_PENDING" });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(String(vi.mocked(fetch).mock.calls[0][0])).toContain("/retry-sync");
});
it("unknown jobs without an attempt cannot initiate another send", async () => {
  await expect(
    recoverPrint({ ...job, phase: "RECONCILIATION_REQUIRED", attemptId: null }),
  ).rejects.toThrow("原任务");
  expect(fetch).not.toHaveBeenCalled();
});

it.each(["first", "reprint", "legacy"])(
  "recovers a lost %s creation response using the saved original body",
  async (kind) => {
    let original: Record<string, unknown> | undefined;
    await expect(
      durable(`7:${kind}`, { labelId: 42 }, async (body) => {
        original = body;
        throw new Error("response lost after upstream commit");
      }),
    ).rejects.toThrow("response lost");
    expect(pendingCreations(8)).toEqual([]);
    const pending = pendingCreations(7);
    expect(pending).toHaveLength(1);
    vi.mocked(fetch).mockResolvedValue(response(job));
    await retryCreation(pending[0]);
    expect(JSON.parse(vi.mocked(fetch).mock.calls[0][1]!.body as string)).toEqual(original);
    expect(pendingCreations(7)).toEqual([]);
  },
);

it.each(["FIRST", "REPRINT"])(
  "%s recovers after the helper response or server receipt response is lost without resending",
  async (kind) => {
    const current = { ...job, outputs: [{ ...job.outputs[0], kind }] };
    const signed = { payload: "original-receipt", signature: "device-signature" };
    let sends = 0;
    let receiptPosts = 0;
    vi.mocked(fetch).mockImplementation(async (input, init) => {
      const path = String(input);
      if (path.endsWith("/capabilities"))
        return response({ enabled: true, allowNewDispatch: true, serverPublicKey: "pinned" });
      if (path.endsWith(`/jobs/${job.jobId}`)) return response(current);
      if (path.endsWith("/status"))
        return response({
          deviceId: job.deviceId,
          deviceKeyHash: job.deviceKeyHash,
          serverPublicKey: "pinned",
          capabilities: ["driver-labels-v2"],
          print_dispatch_enabled: true,
        });
      if (path.endsWith("/bundle")) return new Response(new Uint8Array([1, 2, 3]));
      if (path.endsWith("/dispatch-authorizations")) return response(job.authorization ?? signed);
      if (path.endsWith("/v2/print-labels")) {
        sends++;
        throw new Error("helper persisted submitted but response lost");
      }
      if (path.includes("/v2/label-jobs/")) return response(signed);
      if (path.endsWith("/receipts")) {
        expect(JSON.parse(init!.body as string)).toEqual(signed);
        receiptPosts++;
        if (receiptPosts === 1) throw new Error("server committed receipt but response lost");
        return response({ ...current, phase: "COMPLETED" });
      }
      throw new Error("unexpected path " + path);
    });
    await expect(sendPrint(current)).rejects.toThrow("helper persisted");
    const authorized = { ...current, phase: "DISPATCH_AUTHORIZED" };
    await expect(recoverPrint(authorized)).rejects.toThrow("server committed");
    expect((await recoverPrint(authorized)).phase).toBe("COMPLETED");
    expect(sends).toBe(1);
    expect(receiptPosts).toBe(2);
    expect(
      vi.mocked(fetch).mock.calls.filter(([p]) => String(p).endsWith("/dispatch-authorizations")),
    ).toHaveLength(1);
    expect(
      vi.mocked(fetch).mock.calls.some(([p]) => /jobs\/(first|reprint)$/.test(String(p))),
    ).toBe(false);
  },
);

it("repeated three-table failures keep the original job and never contact the helper", async () => {
  const original = { ...job, phase: "TRACEBACK_PENDING" };
  vi.mocked(fetch)
    .mockResolvedValueOnce(response(original))
    .mockResolvedValueOnce(response({ ...job, phase: "COMPLETED" }));
  expect((await recoverPrint(original)).phase).toBe("TRACEBACK_PENDING");
  expect((await recoverPrint(original)).phase).toBe("COMPLETED");
  expect(vi.mocked(fetch).mock.calls.map(([p]) => String(p))).toEqual([
    `/api/labelsystem/print/jobs/${job.jobId}/retry-sync`,
    `/api/labelsystem/print/jobs/${job.jobId}/retry-sync`,
  ]);
});

it("an unknown helper result cannot authorize a new send or recreate a job", async () => {
  vi.mocked(fetch).mockResolvedValue(new Response("result_unknown", { status: 404 }));
  await expect(recoverPrint({ ...job, phase: "RECONCILIATION_REQUIRED" })).rejects.toThrow(
    "不要重复发送",
  );
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(vi.mocked(fetch).mock.calls[0][1]!.method).toBe("GET");
});
