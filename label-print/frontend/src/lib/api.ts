export const root = "/api/v1/print";
// getRandomValues is available on LAN HTTP too; randomUUID requires a secure context.
export function requestId(): string {
  const value=crypto.getRandomValues(new Uint8Array(16));
  value[6]=(value[6]&15)|64;value[8]=(value[8]&63)|128;
  const hex=Array.from(value,b=>b.toString(16).padStart(2,"0")).join("");
  return `${hex.slice(0,8)}-${hex.slice(8,12)}-${hex.slice(12,16)}-${hex.slice(16,20)}-${hex.slice(20)}`;
}
export class ApiError extends Error {
  constructor(public code: string, message: string, public status: number) { super(message); }
}
export async function api<T = any>(path: string, body?: unknown, method?: string): Promise<T> {
  const response = await fetch(path.startsWith("/api/") ? path : root + path, {
    credentials: "include", cache: "no-store",
    method: method ?? (body === undefined ? "GET" : "POST"),
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const result = await response.json();
  if (!response.ok || !result.success) throw new ApiError(result.code ?? "REQUEST_FAILED", result.message ?? "操作未完成", response.status);
  return result.data as T;
}
export async function binary(path: string, body?: unknown): Promise<Blob> {
  const response = await fetch(root + path, { credentials: "include", cache: "no-store",
    method: body === undefined ? "GET" : "POST", headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) {
    const result = await response.json().catch(() => ({}));
    throw new Error(result.message ?? "文件暂时不可用");
  }
  return response.blob();
}
export function base64(bytes: Uint8Array): string {
  let result = "";
  for (let offset = 0; offset < bytes.length; offset += 8192) result += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
  return btoa(result);
}
export function download(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob), a = document.createElement("a");
  a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 60000);
}
// Persist identity before the first HTTP request. An ambiguous response keeps the original identity.
export async function durable<T>(key: string, body: Record<string, unknown>, call: (request: any) => Promise<T>): Promise<T> {
  const fingerprint = JSON.stringify(body);
  const old = JSON.parse(sessionStorage.getItem(key) ?? "null");
  const request = old?.fingerprint === fingerprint ? old.request : { ...body, clientRequestId: requestId() };
  sessionStorage.setItem(key, JSON.stringify({ fingerprint, request }));
  const result = await call(request);
  sessionStorage.removeItem(key);
  return result;
}
