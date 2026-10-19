import { api, ApiError } from "./api";
import { readCsrfCookie } from "./csrf";

export const printRoot = "/api/labelsystem/print";
export type Signed = { payload: string; signature: string };
export type Output = {
  outputId: string;
  kind: string;
  phase: string;
  flowId: number | null;
  snapshot: {
    labelId: number;
    code: string;
    quantity: string;
    materialCode: string;
    materialName: string;
    variables: Record<string, string>;
  };
};
export type PrintJob = {
  jobId: string;
  operationId: number;
  deviceId: string;
  deviceKeyHash: string;
  phase: string;
  rowVersion: string;
  printer: string;
  outputs: Output[];
  attemptId: string | null;
  authorization: Signed | null;
  manifest: { scope: string } | null;
};
export type PrintTemplate = {
  id: number;
  name: string;
  widthMm: number;
  heightMm: number;
  publishedVersionId: number | null;
  rowVersion: string;
};
export type PendingLabel = {
  labelId: number;
  originLineId: number;
  quantity: string | number;
  materialCode: string;
  batchNo: string;
  unitCode: string;
  rowVersion: string;
  sourceDocumentNo: string;
  recoveryJobId: string | null;
};
export type Device = {
  id: string;
  name: string;
  enabled: boolean;
  rowVersion: string;
};
export type Capabilities = {
  enabled: boolean;
  allowNewDispatch: boolean;
  serverPublicKey: string;
  protocol: string;
  simulationOnly?: boolean;
  simulationDeviceId?: string;
};
export type Segment = {
  kind: string;
  value?: string;
  field?: string;
  length?: number;
  maxLength?: number;
  encoding?: string;
  decimalScale?: number;
  validateCurrentAccount?: boolean;
  dateFormat?: string;
  algorithm?: string;
  expectedValue?: string;
};
export type RuleSpec = {
  schemaVersion: number;
  symbologies: string[];
  caseMode: string;
  segments: Segment[];
};
export type RuleVersion = {
  id: number;
  version: number;
  status: string;
  spec: RuleSpec;
  rowVersion: string;
};
export type Rule = {
  id: number;
  code: string;
  name: string;
  objectType: string;
  priority: number;
  isEnabled: boolean;
  rowVersion: string;
  versions: RuleVersion[];
};
export type CurrentRule = { versionId: number; rowVersion: string; name: string; code: string };
export type Station = {
  deviceId: string;
  name?: string;
  publicKey?: string;
  deviceKeyHash: string;
  serverPublicKey: string;
  capabilities: string[];
  print_dispatch_enabled: boolean;
};

export function errorText(error: unknown): string {
  if (error instanceof ApiError && typeof error.detail === "string") return error.detail;
  return error instanceof Error ? error.message : "操作未完成，请稍后重试。";
}
export function ls<T>(path: string, body?: unknown, method?: string): Promise<T> {
  return api<T>("/api/labelsystem" + path, {
    method: method ?? (body === undefined ? "GET" : "POST"),
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}
export async function printFile(path: string, body?: unknown): Promise<Blob> {
  const response = await fetch(printRoot + path, {
    method: body === undefined ? "GET" : "POST",
    credentials: "include",
    headers:
      body === undefined
        ? undefined
        : { "Content-Type": "application/json", "X-CSRF-Token": readCsrfCookie() ?? "" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    const value = await response.json().catch(() => ({}));
    throw new Error(value.detail ?? "文件暂不可用。");
  }
  return response.blob();
}
export function showPdf(blob: Blob) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.target = "_blank";
  link.rel = "noopener";
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}
export function requestId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 15) | 64;
  bytes[8] = (bytes[8] & 63) | 128;
  const hex = Array.from(bytes, (x) => x.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
export async function durable<T>(
  key: string,
  body: Record<string, unknown>,
  call: (body: Record<string, unknown>) => Promise<T>,
): Promise<T> {
  const storageKey = "labelsystem.request.v2:" + key;
  const fingerprint = JSON.stringify(body);
  const old = JSON.parse(localStorage.getItem(storageKey) ?? "null") as {
    fingerprint: string;
    request: Record<string, unknown>;
  } | null;
  if (old && old.fingerprint !== fingerprint)
    throw new Error("有未确认的原请求，请保持原选择重试或恢复原任务。");
  const request = old?.request ?? { ...body, clientRequestId: requestId() };
  localStorage.setItem(storageKey, JSON.stringify({ fingerprint, request }));
  let result: T;
  try {
    result = await call(request);
  } catch (error) {
    // These source validations precede a committed reservation. Unknown results retain their identity.
    if (
      error instanceof ApiError &&
      [
        "PRINT_SOURCE_EXHAUSTED",
        "print_source_not_required",
        "PRINT_SOURCE_OVERLAP",
        "PRINT_SOURCE_INVALID",
        "print_template_unpublished",
        "print_bill_changed",
        "print_stock_changed",
        "print_line_invalid",
        "print_quantity_invalid",
        "print_no_labels",
      ].includes(error.code)
    )
      localStorage.removeItem(storageKey);
    throw error;
  }
  localStorage.removeItem(storageKey);
  return result;
}
export type PendingCreation = { key: string; kind: string; request: Record<string, unknown> };
export function pendingCreations(userId: number): PendingCreation[] {
  const prefix = `labelsystem.request.v2:${userId}:`;
  const result: PendingCreation[] = [];
  for (let i = 0; i < localStorage.length; i++) {
    const key = localStorage.key(i);
    if (!key?.startsWith(prefix)) continue;
    const kind = key.slice(prefix.length).split(":")[0];
    if (!["first", "reprint", "legacy", "source-bill", "source-stock"].includes(kind)) continue;
    try {
      const value = JSON.parse(localStorage.getItem(key) ?? "null");
      if (typeof value?.request?.clientRequestId === "string")
        result.push({ key, kind, request: value.request });
    } catch {
      /* Corrupt local data cannot create or replace a print request. */
    }
  }
  return result;
}
export async function retryCreation(pending: PendingCreation): Promise<PrintJob | null> {
  const paths: Record<string, string> = {
    first: "/print/jobs/first",
    reprint: "/print/jobs/reprint",
    legacy: "/print/labels/legacy-baseline",
    "source-bill": "/source-print/bill",
    "source-stock": "/source-print/stock",
  };
  if (!paths[pending.kind]) throw new Error("请求类型无效。");
  const value = await ls<PrintJob | null>(paths[pending.kind], pending.request);
  localStorage.removeItem(pending.key);
  return value?.jobId ? value : null;
}
export function configureStation(cap: Pick<Capabilities, "simulationOnly">) {
  if (cap.simulationOnly) throw new Error("服务配置尚未更新，请联系管理员。");
}
export async function station<T = Station>(path = "/status", body?: unknown): Promise<T> {
  const response = await fetch("http://127.0.0.1:9110" + path, {
    method: body === undefined ? "GET" : "POST",
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(120000),
  });
  if (!response.ok) throw new Error("助手未返回确定结果，请查询原任务，不要重复发送。");
  return response.json() as Promise<T>;
}
function base64(bytes: Uint8Array): string {
  let value = "";
  for (let i = 0; i < bytes.length; i += 8192)
    value += String.fromCharCode(...bytes.subarray(i, i + 8192));
  return btoa(value);
}
export async function sendPrint(job: PrintJob): Promise<PrintJob> {
  const cap = await ls<Capabilities>("/print/capabilities");
  configureStation(cap);
  if (!cap.enabled || !cap.allowNewDispatch) throw new Error("打印发送已关闭。");
  const latest = await ls<PrintJob>(`/print/jobs/${job.jobId}`);
  if (latest.phase !== "READY") throw new Error("请恢复原任务。");
  const helper = await station();
  if (
    !helper.print_dispatch_enabled ||
    helper.deviceId !== latest.deviceId ||
    !helper.capabilities.includes("driver-labels-v2") ||
    helper.deviceKeyHash !== latest.deviceKeyHash ||
    helper.serverPublicKey !== cap.serverPublicKey
  )
    throw new Error("请使用已配对且允许发送的打印助手。");
  const bundle = base64(
    new Uint8Array(await (await printFile(`/jobs/${latest.jobId}/bundle`)).arrayBuffer()),
  );
  const authorization = await durable(
    "authorize:" + latest.jobId,
    {
      expectedRowVersion: latest.rowVersion,
      outputIds: latest.outputs
        .filter((o) => !o.flowId)
        .map((o) => o.outputId)
        .sort(),
    },
    (body) => ls<Signed>(`/print/jobs/${latest.jobId}/dispatch-authorizations`, body),
  );
  const receipt = await station<Signed>("/v2/print-labels", { authorization, bundle });
  return ls<PrintJob>(`/print/jobs/${latest.jobId}/receipts`, receipt);
}
export async function recoverPrint(job: PrintJob): Promise<PrintJob> {
  if (job.phase === "TRACEBACK_PENDING") return ls(`/print/jobs/${job.jobId}/retry-sync`, {});
  if (job.phase === "PREPARING") return ls(`/print/jobs/${job.jobId}/render`, {});
  if (!job.attemptId || !job.manifest) throw new Error("没有可恢复的发送记录，请先查询原任务。");
  const receipt = await station<Signed>(
    `/v2/label-jobs/${job.jobId}/${job.attemptId}?scope=${encodeURIComponent(job.manifest.scope)}`,
  );
  return ls<PrintJob>(`/print/jobs/${job.jobId}/receipts`, receipt);
}
