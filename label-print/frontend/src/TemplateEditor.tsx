import { useState } from "react";
import { Canvas } from "./editor/Canvas";
import { LeftPanel } from "./editor/LeftPanel";
import { RightPanel } from "./editor/RightPanel";
import { AlignmentBar } from "./editor/AlignmentBar";
import { useEditorStore } from "./editor/store";
import type { CanvasData } from "./editor/types";
import { api, base64, download, requestId } from "./lib/api";
import { uploadFile } from "./hooks/useAssets";

type Props = { templates: any[]; refresh: () => Promise<void>; run: (action: () => Promise<void>) => void; busy: boolean };
export function TemplateEditor({ templates, refresh, run, busy }: Props) {
  const [current, setCurrent] = useState<any>(null), [name, setName] = useState("新标签");
  const [width, setWidth] = useState(76.2), [height, setHeight] = useState(50.8);
  const canvas = useEditorStore(s => s.canvas);
  async function open(id: number) {
    if (useEditorStore.getState().dirty && !window.confirm("放弃未保存的修改？")) return;
    const row = await api(`/templates/${id}`);
    setCurrent(row); setName(row.name); setWidth(row.width_mm); setHeight(row.height_mm);
    useEditorStore.getState().setCanvas(JSON.parse(row.draft_canvas_json));
  }
  async function save() {
    if (!canvas) return;
    const row = await api(current ? `/templates/${current.id}` : "/templates", {
      name, canvasData: canvas, widthMm: canvas.stage.width_mm, heightMm: canvas.stage.height_mm,
      expectedRowVersion: current?.row_version,
    }, current ? "PUT" : "POST");
    setCurrent(row); useEditorStore.getState().markClean(); await refresh(); return row;
  }
  async function publish() {
    const row = await save(); if (!row) return;
    await api(`/templates/${row.id}/publish`, { expectedRowVersion: row.row_version }); await open(row.id); await refresh();
  }
  async function importTemplate(file: File) {
    if (file.size > 40 * 1024 * 1024) throw new Error("模板文件过大");
    const bundle = JSON.parse(await file.text());
    if (bundle.$schema !== "blg-template/v1" || !Array.isArray(bundle.assets) || bundle.assets.length > 20) throw new Error("模板文件格式不支持");
    const tree = structuredClone(bundle.canvas_data) as CanvasData;
    if (tree.version !== 1 || !Array.isArray(tree.objects) || tree.objects.length < 1 || tree.objects.length > 50) throw new Error("模板内容无效");
    const references = new Map<string, number>();
    for (const asset of bundle.assets) {
      if (references.has(asset.ref) || !["image/png", "image/jpeg"].includes(asset.mime_type)) throw new Error("模板图片格式不支持");
      const data = Uint8Array.from(atob(asset.data_b64), c => c.charCodeAt(0));
      if (data.length !== asset.size_bytes || data.length > 5 * 1024 * 1024) throw new Error("模板图片校验失败");
      const saved = await uploadFile(new File([data], asset.original_filename, { type: asset.mime_type }));
      if(saved.sha256.toLowerCase()!==asset.sha256.toLowerCase())throw new Error("模板图片校验失败");
      references.set(asset.ref, saved.id);
    }
    for (const object of tree.objects) {
      const item = object as any;
      if (item.type === "image") {
        const id = references.get(item.assetRef);
        if (!id) throw new Error("模板图片缺失");
        item.assetId = id; delete item.assetRef;
      }
    }
    const w = Number(bundle.template.width_mm), h = Number(bundle.template.height_mm);
    tree.stage = { ...tree.stage, width_mm: w, height_mm: h };
    const saved = await api("/templates", { name: bundle.template.name, canvasData: tree, widthMm: w, heightMm: h });
    await refresh(); await open(saved.id);
  }
  async function exportTemplate() {
    const row = await save(); if (!row || !canvas) return;
    const tree = structuredClone(canvas), assets: any[] = [];
    const ids = [...new Set(tree.objects.filter(o => o.type === "image").map(o => (o as any).assetId as number))];
    for (const id of ids) {
      const response = await fetch(`/api/v1/print/assets/${id}`, { credentials: "include" });
      if (!response.ok) throw new Error("模板图片不可用");
      const blob = await response.blob(), bytes = new Uint8Array(await blob.arrayBuffer());
      const metadata=await api(`/assets/${id}/metadata`);
      const image = await createImageBitmap(blob);
      try {
        assets.push({ ref: "image-" + id, original_filename: id + ".image", mime_type: blob.type,
          width_px: image.width, height_px: image.height, size_bytes: bytes.length,
          sha256: metadata.sha256.toLowerCase(),
          data_b64: base64(bytes) });
      } finally { image.close(); }
    }
    tree.objects.forEach(o => { if (o.type === "image") { const item = o as any; item.assetRef = "image-" + o.assetId; delete item.assetId; } });
    download(new Blob([JSON.stringify({ $schema: "blg-template/v1", exportedAt: new Date().toISOString(),
      exporter: { app: "LabelSystem", version: "0.1.0" }, template: { name, width_mm: width, height_mm: height },
      canvas_data: tree, assets })], { type: "application/json" }), name + ".blg-template.json");
  }
  return <section className="panel">
    <div className="actions">
      <select aria-label="新标签尺寸" defaultValue="76.2,50.8" onChange={e=>{
        const [w,h]=e.target.value.split(",").map(Number);setWidth(w);setHeight(h);
      }}><option value="50,30">50 × 30 mm</option><option value="76.2,50.8">76.2 × 50.8 mm</option>
        <option value="101.6,152.4">101.6 × 152.4 mm</option></select>
      <select value={current?.id ?? ""} onChange={e => run(() => open(Number(e.target.value)))}><option value="">选择模板</option>
        {templates.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select>
      <button disabled={busy} onClick={() => {
        if (useEditorStore.getState().dirty && !window.confirm("放弃未保存的修改？")) return;
        setCurrent(null); useEditorStore.getState().setCanvas({ version: 1, stage: { width_mm: width, height_mm: height },
          objects: [{ id: requestId(), type: "barcode", x: 5, y: 5, width: Math.max(5, Math.min(width,height)-10), height: Math.max(5, Math.min(width,height)-10), barcodeType: "qr", data: "{{条码}}" }] });
      }}>新建</button>
      <label>导入<input type="file" accept=".json" onChange={e => { const f = e.target.files?.[0]; if (f) run(() => importTemplate(f)); e.target.value = ""; }}/></label>
    </div>
    {canvas && <>
      <div className="actions"><input aria-label="模板名称" value={name} onChange={e => setName(e.target.value)}/>
        <label>宽 mm<input type="number" min="1" max="1000" value={width} onChange={e => setWidth(Number(e.target.value))}/></label>
        <label>高 mm<input type="number" min="1" max="1000" value={height} onChange={e => setHeight(Number(e.target.value))}/></label>
        <button onClick={() => useEditorStore.getState().setStageSize(width, height)}>调整尺寸</button>
        <button disabled={busy} onClick={() => run(async () => { await save(); })}>保存</button>
        <button disabled={busy} onClick={() => run(publish)}>发布</button>
        <button disabled={busy} onClick={() => run(exportTemplate)}>导出</button>
      </div>
      <div className="editor"><LeftPanel/><div className="flex flex-1 min-w-0 flex-col"><AlignmentBar/><Canvas/></div><RightPanel/></div>
    </>}
  </section>;
}
