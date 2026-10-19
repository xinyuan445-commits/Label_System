import { lazy, Suspense, useEffect, useRef, useState } from "react";
import { api, ApiError, binary, download, durable } from "./lib/api";
import { type Job, printers, probe, recover, send } from "./lib/printing";
const TemplateEditor=lazy(()=>import("./TemplateEditor").then(m=>({default:m.TemplateEditor})));

const status: Record<string, string> = {
  PREPARING: "准备文件", READY: "待发送", DISPATCH_AUTHORIZED: "等待发送结果", AWAITING_CONFIRMATION: "等待发送结果",
  RECONCILIATION_REQUIRED: "结果待核对", TRACEBACK_PENDING: "登记未完成", COMPLETED: "已完成",
  FAILED_NO_OUTPUT: "尚未发送", CANCELLED: "已取消",
};
export function App() {
  const [tab, setTab] = useState("待打印"), [busy, setBusy] = useState(false), busyRef = useRef(false);
  const [editorOpened,setEditorOpened]=useState(false);
  const [message, setMessage] = useState(""), [enabled, setEnabled] = useState(false), [signedIn, setSignedIn] = useState(true);
  const [allowDispatch,setAllowDispatch]=useState(false);
  const [pending, setPending] = useState<any[]>([]), [summary, setSummary] = useState<any[]>([]);
  const [jobs, setJobs] = useState<any[]>([]), [job, setJob] = useState<Job | null>(null);
  const [templates, setTemplates] = useState<any[]>([]), [version, setVersion] = useState("");
  const [selection, setSelection] = useState<number[]>([]), [device, setDevice] = useState("");
  const [queueNames, setQueueNames] = useState<string[]>([]), [printer, setPrinter] = useState("");
  const [code, setCode] = useState(""), [label, setLabel] = useState<any>(null);
  const [copies, setCopies] = useState(1), [reason, setReason] = useState(""), [confirmInactive, setConfirmInactive] = useState(false);
  const [pairing, setPairing] = useState(""), [owner, setOwner] = useState(""), [devices, setDevices] = useState<any[]>([]);
  const [rule, setRule] = useState<any>(null), [ruleId, setRuleId] = useState("");
  const [rules,setRules]=useState<any[]>([]);
  async function refresh() {
    const [p, j, t, s] = await Promise.all([api<any[]>("/pending-labels"), api<any[]>("/jobs"), api<any[]>("/templates"), api<any[]>("/source-summary")]);
    setPending(p); setJobs(j); setTemplates(t); setSummary(s); setSelection(old => old.filter(id => p.some(x => x.labelId === id)));
  }
  async function run(action: () => Promise<void>) {
    if (busyRef.current) return;
    busyRef.current = true; setBusy(true); setMessage("");
    try { await action(); }
    catch (error) {
      if (error instanceof ApiError && error.status === 401) setSignedIn(false);
      setMessage(error instanceof Error ? error.message : "操作未完成");
      if (enabled) await refresh().catch(() => {});
      if (job) setJob(await api<Job>(`/jobs/${job.jobId}`).catch(() => job));
    } finally { busyRef.current = false; setBusy(false); }
  }
  useEffect(() => {
    void run(async () => {
      await api("/api/v1/auth/me");
      const cap = await api("/capabilities"); setEnabled(cap.enabled); setAllowDispatch(cap.allowNewDispatch);
      if (cap.enabled) await refresh(); else setMessage("打印功能尚未启用");
    });
  }, []);
  // Recovery only queries assistant receipts / resumes original registration. It never sends pages.
  useEffect(() => {
    if (!job || !["TRACEBACK_PENDING", "DISPATCH_AUTHORIZED", "RECONCILIATION_REQUIRED"].includes(job.phase)) return;
    const timer = setInterval(() => {
      if (busyRef.current) return;
      void run(async () => { setJob(await recover(await api(`/jobs/${job.jobId}`))); await refresh(); });
    }, 15000);
    return () => clearInterval(timer);
  }, [job?.jobId, job?.phase]);
  async function connect() {
    const result = await probe();
    if (!result.deviceId || !result.capabilities?.includes("driver-labels-v2")) throw new Error("请使用新版打印助手");
    setDevice(result.deviceId);
    const list = await printers();
    const names: string[] = (Array.isArray(list) ? list : list.printers ?? []).map((p: any) => typeof p === "string" ? p : p.name)
      .filter((name: string) => !name.toLowerCase().includes("pdf"));
    setQueueNames(names); setPrinter(names[0] ?? "");
  }
  async function createFirst() {
    const targets = pending.filter(p => selection.includes(p.labelId) && !p.recoveryJobId)
      .map(p => ({ labelId: p.labelId, originLineId: p.originLineId, expectedRowVersion: p.rowVersion }));
    if (!targets.length || !version || !device || !printer) throw new Error("请选择待打印标签、模板和打印机");
    const created = await durable("print-first", { templateVersionId: Number(version), deviceId: device, printer, targets },
      request => api<Job>("/jobs/first", request));
    setJob(created); setTab("任务"); await refresh();
  }
  async function resolve() {
    const result = await api("/labels/resolve", { code }); setLabel(result.label); setConfirmInactive(false);
  }
  async function createReprint() {
    if (!label) throw new Error("请先查询原标签");
    const created = await durable("print-reprint", { labelId: label.labelId, expectedRowVersion: label.rowVersion, copies, reason, confirmInactive },
      request => api<Job>("/jobs/reprint", request));
    setJob(created); setTab("任务"); await refresh();
  }
  async function preview() {
    if (!version) throw new Error("请选择已发布模板");
    const p = pending.find(x => selection.includes(x.labelId));
    download(await binary("/previews", { templateVersionId: Number(version), variables: {
      "物料编码": p?.materialCode ?? "示例物料", "物料名称": "示例物料", "数量": String(p?.quantity ?? 1),
      "批号": p?.batchNo || "示例批号", "单位": p?.unitCode ?? "件", "sn": "PREVIEW",
      "单据编码": p?.sourceDocumentNo || "示例单据", "行号": "1", "仓库编码": p?.warehouseCode || "示例仓库",
      "货位编码": p?.locationCode || "示例货位", "quantity": String(p?.quantity ?? 1),
    } }), "标签预览.pdf");
  }
  async function settings() {
    const [d, r, catalog] = await Promise.all([api<any[]>("/devices"), api("/code-rule"),api("/api/v1/label-code-rules/catalog")]);
    setDevices(d); setRule(r); setRuleId(String(r?.versionId ?? ""));
    setRules(catalog.rules.filter((item:any)=>item.isEnabled&&item.objectType==="PRODUCT_LABEL")
      .flatMap((item:any)=>item.versions.filter((v:any)=>v.status==="ACTIVE").map((v:any)=>({id:v.id,name:item.name+" · V"+v.version}))));
  }
  if (!signedIn) return <main className="workbench"><p>登录已失效，请先登录标签系统。</p><a href="/#/login" target="_top">前往登录</a></main>;
  const templatePicker = <select aria-label="打印模板" value={version} onChange={e => setVersion(e.target.value)}><option value="">选择已发布模板</option>
    {templates.filter(t => t.publishedVersionId).map(t => <option key={t.id} value={t.publishedVersionId}>{t.name}</option>)}</select>;
  const printerPicker = <><button disabled={busy || !allowDispatch} onClick={() => void run(connect)}>连接打印助手</button>
    <select aria-label="打印机" value={printer} onChange={e => setPrinter(e.target.value)}><option value="">选择打印机</option>
      {queueNames.map(p => <option key={p}>{p}</option>)}</select></>;
  return <main className="workbench">
    <header><h1>标签打印</h1><a href="/#/" target="_top">返回标签系统</a><button disabled={busy || !enabled} onClick={() => void run(refresh)}>刷新</button></header>
    <nav>{["待打印", "补打", "任务", "模板", "设置"].map(name => <button disabled={!enabled || busy} key={name} className={tab === name ? "active" : ""}
      onClick={() => { setTab(name); if(name==="模板")setEditorOpened(true);if (name === "设置") void run(settings); }}>{name}</button>)}</nav>
    {message && <p className="notice" role="status">{message}</p>}
    {enabled && !allowDispatch && <p className="notice" role="status">打印发送已关闭，可编辑模板和预览。</p>}
    {enabled && tab === "待打印" && <section className="panel">
      <div className="actions">{templatePicker}{printerPicker}<button disabled={busy} onClick={() => void run(preview)}>示例预览</button>
        <button disabled={busy || !allowDispatch || !selection.length} className="primary" onClick={() => void run(createFirst)}>生成首打任务</button></div>
      <table><thead><tr><th>选择</th><th>来源单据</th><th>物料</th><th>批号</th><th>数量</th><th>仓库 / 货位</th><th>处理</th></tr></thead>
        <tbody>{pending.map(p => <tr key={p.labelId}><td><input aria-label="选择标签" type="checkbox" disabled={!!p.recoveryJobId} checked={selection.includes(p.labelId)}
          onChange={e => setSelection(old => e.target.checked ? [...old, p.labelId] : old.filter(id => id !== p.labelId))}/></td>
          <td>{p.sourceDocumentNo}</td><td>{p.materialCode}</td><td>{p.batchNo}</td><td>{p.quantity} {p.unitCode}</td>
          <td>{p.warehouseCode} / {p.locationCode}</td><td>{p.recoveryJobId && <button onClick={() => void run(async () => {
            setJob(await api(`/jobs/${p.recoveryJobId}`)); setTab("任务"); })}>恢复原任务</button>}</td></tr>)}</tbody></table>
      {pending.length === 0 && <p>暂无待打印标签</p>}
      {summary.length > 0 && <table><thead><tr><th>单据</th><th>应首打</th><th>已首打</th><th>未首打</th><th>处理中</th></tr></thead>
        <tbody>{summary.map(s => <tr key={s.operationId + s.unitCode}><td>{s.documentNo}</td><td>{s.requiredQty} {s.unitCode}</td>
          <td>{s.firstRegisteredQty}</td><td>{s.remainingQty}</td><td>{s.reservedQty}</td></tr>)}</tbody></table>}
    </section>}
    {enabled && tab === "补打" && <section className="panel"><div className="actions">
      <input placeholder="完整标签码" value={code} onChange={e => { setCode(e.target.value); setLabel(null); }}/>
      <button disabled={busy} onClick={() => void run(resolve)}>查询</button></div>
      {label && <><p>{label.code} · 当前余额 {label.quantity} · {label.status}</p><div className="actions">
        <label>份数<input type="number" min="1" max="100" value={copies} onChange={e => setCopies(Number(e.target.value))}/></label>
        <input placeholder="补打原因" value={reason} onChange={e => setReason(e.target.value)}/>
        {label.status !== "ACTIVE" && <label><input type="checkbox" checked={confirmInactive} onChange={e => setConfirmInactive(e.target.checked)}/>已核对当前状态</label>}
        <button disabled={busy || !allowDispatch} className="primary" onClick={() => void run(createReprint)}>生成补打任务</button></div>
        <details><summary>历史标签核验</summary><div className="actions">{templatePicker}{printerPicker}
          <button disabled={busy} onClick={() => void run(async () => {
            if (!version || !device || !printer) throw new Error("请选择原标签模板和打印机");
            await durable("print-baseline", { code, templateVersionId: Number(version), deviceId: device, printer, expectedRowVersion: label.rowVersion },
              request => api("/labels/legacy-baseline", request)); setMessage("历史依据核验完成，可生成补打任务");
          })}>核验原始记录</button></div></details>
      </>}
    </section>}
    {enabled && tab === "任务" && <section className="panel">
      {job && <div><p>{status[job.phase] ?? job.phase} · {job.printer}</p><div className="actions">
        {job.phase === "READY" && <button disabled={busy || !allowDispatch} className="primary" onClick={() => void run(async () => {
          setJob(await send(await api(`/jobs/${job.jobId}`))); await refresh();
        })}>发送打印</button>}
        {["PREPARING", "DISPATCH_AUTHORIZED", "AWAITING_CONFIRMATION", "RECONCILIATION_REQUIRED", "TRACEBACK_PENDING"].includes(job.phase) &&
          <button disabled={busy} onClick={() => void run(async () => { setJob(await recover(await api(`/jobs/${job.jobId}`))); await refresh(); })}>恢复登记</button>}
        {["FAILED_NO_OUTPUT", "CANCELLED"].includes(job.phase) && <button disabled={busy} onClick={() => void run(async () => {
          setJob(await durable("print-resume:" + job.jobId, { expectedRowVersion: job.rowVersion }, req => api(`/jobs/${job.jobId}/resume`, req))); await refresh();
        })}>继续原任务</button>}
        {["PREPARING", "READY", "FAILED_NO_OUTPUT"].includes(job.phase) && <button disabled={busy} onClick={() => void run(async () => {
          setJob(await durable("print-cancel:" + job.jobId, { expectedRowVersion: job.rowVersion }, req => api(`/jobs/${job.jobId}/cancel`, req))); await refresh();
        })}>取消任务</button>}
      </div><table><thead><tr><th>标签码</th><th>物料</th><th>标签数量</th><th>登记</th><th>预览</th></tr></thead>
        <tbody>{job.outputs.map(o => <tr key={o.outputId}><td>{o.snapshot.code}</td><td>{o.snapshot.materialName || o.snapshot.materialCode}</td>
          <td>{o.snapshot.quantity}</td><td>{o.flowId ? "已登记" : "待登记"}</td><td>{job.manifest && <button onClick={() => void run(async () =>
            download(await binary(`/jobs/${job.jobId}/pages/${o.outputId}`), "标签预览.pdf"))}>查看</button>}</td></tr>)}</tbody></table></div>}
      <table><thead><tr><th>任务</th><th>状态</th><th>打印机</th><th></th></tr></thead><tbody>{jobs.map(j => <tr key={j.jobId}>
        <td>{j.jobId.slice(0, 8)}</td><td>{status[j.phase] ?? j.phase}</td><td>{j.printer}</td><td><button disabled={busy}
          onClick={() => void run(async () => { setJob(await api(`/jobs/${j.jobId}`)); })}>打开</button></td></tr>)}</tbody></table>
    </section>}
    {enabled && editorOpened && <div hidden={tab!=="模板"}><Suspense fallback={<p>加载模板编辑器…</p>}><TemplateEditor templates={templates} refresh={refresh} run={action => void run(action)} busy={busy}/></Suspense></div>}
    {enabled && tab === "设置" && <section className="panel"><div className="actions">
      <span>当前规则：{rule?.name ?? "尚未选择"}</span><select aria-label="当前打印规则" value={ruleId} onChange={e=>setRuleId(e.target.value)}>
        <option value="">选择打印规则</option>{rules.map(r=><option key={r.id} value={r.id}>{r.name}</option>)}</select>
      <button disabled={busy} onClick={() => void run(async () => { await api("/code-rule", { ruleVersionId: Number(ruleId), expectedRowVersion: rule?.rowVersion }, "PUT"); await settings(); })}>设为当前规则</button>
      <a href="/#/settings/label-code-rules" target="_top">管理编码规则</a></div>
      <div className="actions"><textarea placeholder="打印助手提供的设备配对信息" value={pairing} onChange={e => setPairing(e.target.value)}/>
        <input placeholder="使用人 ID（留空为本人）" value={owner} onChange={e => setOwner(e.target.value)}/>
        <button disabled={busy} onClick={() => void run(async () => { await api("/devices", { ...JSON.parse(pairing), ownerUserId: owner || null }); await settings(); })}>登记设备</button></div>
      <table><thead><tr><th>设备</th><th>状态</th><th></th></tr></thead><tbody>{devices.map(d => <tr key={d.id}>
        <td>{d.name}</td><td>{d.enabled ? "可用" : "已停用"}</td><td><button disabled={busy} onClick={() => void run(async () => {
          await api(`/devices/${d.id}`, { enabled: !d.enabled, expectedRowVersion: d.rowVersion }, "PUT"); await settings();
        })}>{d.enabled ? "停用" : "启用"}</button></td></tr>)}</tbody></table>
    </section>}
  </main>;
}
