import { api, base64, binary, durable } from "./api";
export type Signed = { payload: string; signature: string };
export type Output = { outputId: string; kind: string; phase: string; flowId: number | null; snapshot: {
  labelId: number; code: string; quantity: string; materialCode: string; materialName: string; variables: Record<string, string>;
} };
export type Job = { jobId: string; deviceId: string; deviceKeyHash: string; phase: string; rowVersion: string; printer: string; outputs: Output[];
  attemptId: string | null; authorization: Signed | null; manifest: { scope: string } | null };
export const assistant = "http://127.0.0.1:9110";
async function local<T>(path: string, body?: unknown): Promise<T> {
  const response = await fetch(assistant + path, { method: body === undefined ? "GET" : "POST",
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(120000) });
  if (!response.ok) throw new Error("打印助手未返回确定结果，请恢复原任务");
  return response.json();
}
export const probe = () => local<{ deviceId: string; deviceKeyHash: string; serverPublicKey: string; capabilities: string[] }>("/status");
export const printers = () => local<any>("/printers");
export async function send(job: Job): Promise<Job> {
  if (job.phase !== "READY") throw new Error("请先恢复原任务");
  const cap = await api<{enabled:boolean;allowNewDispatch:boolean;serverPublicKey:string}>("/capabilities");
  if (!cap.enabled || !cap.allowNewDispatch) throw new Error("新打印已暂停，可继续恢复登记");
  const station = await probe();
  if (station.deviceId !== job.deviceId || !station.capabilities.includes("driver-labels-v2") ||
      station.deviceKeyHash !== job.deviceKeyHash ||
      !cap.serverPublicKey || station.serverPublicKey !== cap.serverPublicKey)
    throw new Error("请连接与原任务配对的打印工作站");
  const archive = base64(new Uint8Array(await (await binary(`/jobs/${job.jobId}/bundle`)).arrayBuffer()));
  const authorization = await durable("print-dispatch:" + job.jobId,
    { expectedRowVersion: job.rowVersion, outputIds: job.outputs.filter(o => !o.flowId).map(o => o.outputId).sort() },
    request => api<Signed>(`/jobs/${job.jobId}/dispatch-authorizations`, request));
  // Authorization is durable on the server before the printer can be invoked.
  // A lost response MUST use recover(), never retry this POST.
  const receipt = await local<Signed>("/v2/print-labels", { authorization, bundle: archive });
  return api<Job>(`/jobs/${job.jobId}/receipts`, receipt);
}
export async function recover(job: Job): Promise<Job> {
  if (job.phase === "TRACEBACK_PENDING") return api(`/jobs/${job.jobId}/retry-sync`, {});
  if (job.phase === "PREPARING") return api(`/jobs/${job.jobId}/render`, {});
  if (!job.attemptId || !job.manifest) throw new Error("没有可恢复的发送记录");
  const receipt = await local<Signed>(`/v2/label-jobs/${job.jobId}/${job.attemptId}?scope=${encodeURIComponent(job.manifest.scope)}`);
  return api<Job>(`/jobs/${job.jobId}/receipts`, receipt);
}
