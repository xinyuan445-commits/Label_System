import { useMutation } from "@tanstack/react-query";
import { api, base64 } from "../lib/api";
export const assetImageUrl = (id: number) => `/api/v1/print/assets/${id}`;
export async function uploadFile(file: File) {
  if (!["image/png", "image/jpeg"].includes(file.type) || file.size > 5 * 1024 * 1024) throw new Error("请选择 5MB 内的 PNG/JPEG 图片");
  const bitmap = await createImageBitmap(file);
  try {
    return await api("/assets", { mediaType: file.type, dataBase64: base64(new Uint8Array(await file.arrayBuffer())),
      widthPx: bitmap.width, heightPx: bitmap.height });
  } finally { bitmap.close(); }
}
export function useUploadAsset() { return useMutation({ mutationFn: uploadFile }); }
