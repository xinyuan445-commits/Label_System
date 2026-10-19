import { afterEach, describe, expect, it, vi } from "vitest";
import { recover, send, type Job } from "./printing";
import { durable, requestId } from "./api";
const ready: Job = {
  jobId: "11111111-1111-4111-8111-111111111111", deviceId: "station", deviceKeyHash:"key", phase: "READY", rowVersion: "0000000000000001",
  printer: "mock", attemptId: null, authorization: null, manifest: { scope: "test|888" },
  outputs: [{ outputId: "22222222-2222-4222-8222-222222222222", kind: "FIRST", phase: "READY", flowId: null,
    snapshot: { labelId: 1, code: "code", quantity: "100", materialCode: "MAT", materialName: "物料", variables: {} } }],
};
function storage() {
  const values = new Map<string, string>();
  vi.stubGlobal("sessionStorage", { getItem: (k: string) => values.get(k) ?? null,
    setItem: (k: string, v: string) => values.set(k, v), removeItem: (k: string) => values.delete(k) });
}
function ok(data: unknown) { return new Response(JSON.stringify({ success: true, data })); }
afterEach(() => { vi.unstubAllGlobals(); });
describe("durable printing", () => {
  it("creates request and template ids without secure-context-only APIs",()=>{
    const random=crypto.getRandomValues.bind(crypto);
    vi.stubGlobal("crypto",{getRandomValues:random});
    const ids=Array.from({length:50},()=>requestId());
    expect(ids.every(id=>/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(id))).toBe(true);
    expect(new Set(ids).size).toBe(50);
  });
  it("paused dispatch never contacts the local assistant",async()=>{
    const calls:string[]=[];
    vi.stubGlobal("fetch",vi.fn(async(url:string)=>{
      calls.push(url);
      if(url.endsWith("/capabilities"))return ok({enabled:true,allowNewDispatch:false});
      throw new Error("assistant must not be contacted");
    }));
    await expect(send(ready)).rejects.toThrow("新打印已暂停");
    expect(calls).toEqual(["/api/v1/print/capabilities"]);
  });
  it("retains the request identity when a create response is lost", async () => {
    storage(); let first = "";
    await expect(durable("create", { labelId: 1 }, async request => { first = request.clientRequestId; throw new Error("network"); })).rejects.toThrow();
    const retried = await durable("create", { labelId: 1 }, async request => request.clientRequestId);
    expect(retried).toBe(first);
  });
  it("a lost assistant response recovers its receipt without sending the pages again", async () => {
    storage(); const calls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      calls.push(url);
      if(url.endsWith("/status"))return new Response(JSON.stringify({deviceId:"station",deviceKeyHash:"key",serverPublicKey:"pinned",capabilities:["driver-labels-v2"]}));
      if(url.endsWith("/capabilities"))return ok({enabled:true,allowNewDispatch:true,serverPublicKey:"pinned"});
      if (url.endsWith("/bundle")) return new Response(new Uint8Array([1, 2, 3]));
      if (url.endsWith("/dispatch-authorizations")) return ok({ payload: "ticket", signature: "server" });
      if (url.endsWith("/v2/print-labels")) throw new Error("lost response");
      if (url.includes("/v2/label-jobs/")) return new Response(JSON.stringify({ payload: "receipt", signature: "device" }));
      if (url.endsWith("/receipts")) return ok({ ...ready, phase: "COMPLETED" });
      throw new Error("unexpected " + url);
    }));
    await expect(send(ready)).rejects.toThrow("lost response");
    const job = await recover({ ...ready, phase: "DISPATCH_AUTHORIZED", attemptId: "attempt" });
    expect(job.phase).toBe("COMPLETED");
    expect(calls.filter(url => url.endsWith("/v2/print-labels"))).toHaveLength(1);
    expect(calls.filter(url => url.endsWith("/dispatch-authorizations"))).toHaveLength(1);
  });
  it("registration recovery never contacts the printer", async () => {
    const mock = vi.fn(async (_url: string) => ok({ ...ready, phase: "COMPLETED" }));
    vi.stubGlobal("fetch", mock);
    await recover({ ...ready, phase: "TRACEBACK_PENDING" });
    expect(mock).toHaveBeenCalledTimes(1);
    expect(mock.mock.calls[0][0]).toBe("/api/v1/print/jobs/" + ready.jobId + "/retry-sync");
  });
  it("never resends an authorized, unknown, or completed job", async () => {
    const mock = vi.fn(); vi.stubGlobal("fetch", mock);
    for (const phase of ["DISPATCH_AUTHORIZED", "RECONCILIATION_REQUIRED", "TRACEBACK_PENDING", "COMPLETED"])
      await expect(send({ ...ready, phase })).rejects.toThrow("请先恢复原任务");
    expect(mock).not.toHaveBeenCalled();
  });
  it("checks the actual device key before obtaining a send authorization",async()=>{
    const calls:string[]=[];
    vi.stubGlobal("fetch",vi.fn(async(url:string)=>{
      calls.push(url);
      if(url.endsWith("/status"))return new Response(JSON.stringify({
        deviceId:"station",deviceKeyHash:"wrong",serverPublicKey:"server",capabilities:["driver-labels-v2"]}));
      if(url.endsWith("/capabilities"))return ok({enabled:true,allowNewDispatch:true,serverPublicKey:"server"});
      throw new Error("must not authorize");
    }));
    await expect(send(ready)).rejects.toThrow("配对");
    expect(calls).toHaveLength(2);
  });
});
