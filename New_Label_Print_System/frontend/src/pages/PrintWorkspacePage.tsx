import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { PrintPageTabs } from "../components/PrintPageTabs";
import { useMe } from "../hooks/useMe";
import {
  durable,
  errorText,
  ls,
  printFile,
  showPdf,
  station,
  configureStation,
  sendPrint,
  recoverPrint,
  pendingCreations,
  retryCreation,
  type Capabilities,
  type CurrentRule,
  type Device,
  type PendingLabel,
  type PrintJob,
  type PrintTemplate,
} from "../lib/labelSystem";

const button = "rounded bg-indigo-600 px-3 py-2 text-sm disabled:opacity-40";
const input = "rounded border border-slate-700 bg-slate-900 px-3 py-2 text-sm";
const phases: Record<string, string> = {
  PREPARING: "准备中",
  READY: "待发送",
  DISPATCH_AUTHORIZED: "待确认发送结果",
  AWAITING_CONFIRMATION: "待回执",
  RECONCILIATION_REQUIRED: "结果待核对",
  TRACEBACK_PENDING: "追溯登记待恢复",
  COMPLETED: "已完成",
  FAILED_NO_OUTPUT: "未发送",
  CANCELLED: "已取消",
};
type JobRow = { jobId: string; phase: string; printer: string; created_at: string };
export function PrintWorkspacePage({ initialTab = "pending" }: { initialTab?: string }) {
  const { data: me } = useMe();
  const navigate = useNavigate();
  const has = (code: string) => Boolean(me?.print_permissions?.includes(code));
  const [creations, setCreations] = useState(() => pendingCreations(me?.id ?? 0));
  const [tab, setTab] = useState(initialTab),
    [cap, setCap] = useState<Capabilities | null>(null),
    [rule, setRule] = useState<CurrentRule | null>(null);
  const [pending, setPending] = useState<PendingLabel[]>([]),
    [jobs, setJobs] = useState<JobRow[]>([]),
    [templates, setTemplates] = useState<PrintTemplate[]>([]),
    [devices, setDevices] = useState<Device[]>([]);
  const [selected, setSelected] = useState<number[]>([]),
    [template, setTemplate] = useState(""),
    [device, setDevice] = useState(""),
    [printer, setPrinter] = useState("");
  const [printerNames, setPrinterNames] = useState<string[]>([]),
    [job, setJob] = useState<PrintJob | null>(null),
    [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [notice, setNotice] = useState("");
  const refresh = useCallback(async () => {
    const [a, b, c, d, e, f] = await Promise.all([
      ls<Capabilities>("/print/capabilities"),
      ls<PendingLabel[]>("/print/pending-labels"),
      ls<JobRow[]>("/print/jobs"),
      ls<PrintTemplate[]>("/print/templates"),
      ls<Device[]>("/print/devices"),
      ls<CurrentRule | null>("/print/code-rule"),
    ]);
    setCap(a);
    configureStation(a);
    setPending(b);
    setJobs(c);
    setTemplates(d);
    setDevices(e);
    setRule(f);
    setTemplate(
      (old) => old || String(d.find((t) => t.publishedVersionId)?.publishedVersionId ?? ""),
    );
  }, []);
  useEffect(() => {
    void refresh().catch((e) => setError(errorText(e)));
  }, [refresh]);
  useEffect(() => {
    if (!jobs.some((item) => item.phase === "PREPARING")) return;
    const timer = window.setInterval(
      () => void refresh().catch((e) => setError(errorText(e))),
      2000,
    );
    return () => window.clearInterval(timer);
  }, [jobs, refresh]);
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await action();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
      setCreations(pendingCreations(me?.id ?? 0));
    }
  }
  async function open(id: string) {
    setJob(await ls<PrintJob>(`/print/jobs/${id}`));
    setTab("jobs");
  }
  async function connect(pair = false) {
    const helper = await station();
    if (!helper.capabilities.includes("driver-labels-v2"))
      throw new Error("请下载并安装新版打印助手。");
    const names = await station<{ printers: { name: string }[] }>("/printers");
    setPrinterNames(names.printers.map((x) => x.name));
    setDevice(helper.deviceId);
    if (pair) {
      const identity = await station<{ id: string; name: string; publicKey: string }>("/v2/device");
      await ls("/print/devices", identity);
      await refresh();
      setNotice("打印助手已登记");
    } else setNotice("打印助手已连接");
  }
  const disabled = busy || !cap?.allowNewDispatch;
  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold">打印记录</h1>
        <button className={button} disabled={busy} onClick={() => void run(refresh)}>
          刷新
        </button>
      </div>
      <PrintPageTabs />
      <div className="flex flex-wrap gap-3">
        {[
          ["pending", "待打印标签"],
          ["jobs", "打印任务"],
          ["devices", "打印助手"],
        ].map(([key, name]) => (
          <button
            className={tab === key ? button : "rounded border border-slate-700 px-3 py-2 text-sm"}
            key={key}
            onClick={() => setTab(key)}
          >
            {name}
          </button>
        ))}
      </div>
      <div className="flex flex-wrap gap-4 text-sm">
        <Link to="/print-rules" className="underline">
          当前规则：{rule?.name ?? "尚未选择"}
        </Link>
        {me?.permissions.templates && (
          <Link to="/templates?section=published" className="underline">
            模板管理
          </Link>
        )}
        <span>{cap?.allowNewDispatch ? "" : "打印发送已关闭"}</span>
      </div>
      {creations.map((pending) => (
        <div key={pending.key} role="status" className="flex gap-3 text-amber-300">
          <span>
            有未确认的
            {pending.kind === "first"
              ? "首打"
              : pending.kind === "reprint"
                ? "补打"
                : pending.kind.startsWith("source-")
                  ? "来源登记"
                  : "历史核验"}
            创建请求
          </span>
          <button
            className="underline"
            disabled={busy}
            onClick={() =>
              void run(async () => {
                const original = await retryCreation(pending);
                if (original) {
                  setJob(original);
                  setTab("jobs");
                } else setNotice("原登记请求已恢复");
                await refresh();
              })
            }
          >
            恢复原创建请求
          </button>
        </div>
      ))}
      {error && (
        <p role="alert" className="text-rose-400">
          {error}
        </p>
      )}
      {notice && (
        <p role="status" className="text-emerald-400">
          {notice}
        </p>
      )}
      {(tab === "pending" || tab === "devices") && (
        <div className="flex flex-wrap gap-3 rounded border border-slate-800 p-3">
          <select
            aria-label="正式模板"
            className={input}
            value={template}
            onChange={(e) => setTemplate(e.target.value)}
          >
            <option value="">选择正式模板</option>
            {templates
              .filter((t) => t.publishedVersionId)
              .map((t) => (
                <option key={t.id} value={t.publishedVersionId!}>
                  {t.name} · 版本{t.publishedVersionId}
                </option>
              ))}
          </select>
          <select
            aria-label="打印设备"
            className={input}
            value={device}
            onChange={(e) => setDevice(e.target.value)}
          >
            <option value="">选择设备</option>
            {devices
              .filter((d) => d.enabled)
              .map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name}
                </option>
              ))}
          </select>
          <input
            aria-label="打印机"
            list="formal-printers"
            className={input}
            placeholder="打印机名称"
            value={printer}
            onChange={(e) => setPrinter(e.target.value)}
          />
          <datalist id="formal-printers">
            {printerNames.map((p) => (
              <option key={p}>{p}</option>
            ))}
          </datalist>
          <button className={button} disabled={busy} onClick={() => void run(() => connect())}>
            连接本机助手
          </button>
        </div>
      )}
      {tab === "pending" && (
        <>
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead>
                <tr>
                  <th>选择</th>
                  <th>来源单号</th>
                  <th>料号</th>
                  <th>批号</th>
                  <th>数量</th>
                  <th>状态</th>
                </tr>
              </thead>
              <tbody>
                {pending.map((row) => (
                  <tr className="border-t border-slate-800" key={row.labelId}>
                    <td className="py-3">
                      <input
                        aria-label={`选择标签${row.labelId}`}
                        type="checkbox"
                        disabled={!!row.recoveryJobId || busy}
                        checked={selected.includes(row.labelId)}
                        onChange={(e) =>
                          setSelected(
                            e.target.checked
                              ? [...selected, row.labelId]
                              : selected.filter((x) => x !== row.labelId),
                          )
                        }
                      />
                    </td>
                    <td>{row.sourceDocumentNo}</td>
                    <td>{row.materialCode}</td>
                    <td>{row.batchNo}</td>
                    <td>
                      {row.quantity} {row.unitCode}
                    </td>
                    <td>
                      {row.recoveryJobId ? (
                        <button
                          className="underline"
                          onClick={() => void run(() => open(row.recoveryJobId!))}
                        >
                          恢复原任务
                        </button>
                      ) : (
                        "待首打"
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {pending.length === 0 && <p className="py-4 text-slate-400">暂无待打印标签</p>}
          </div>
          <button
            className={button}
            disabled={
              disabled ||
              !has("print.first") ||
              !selected.length ||
              !template ||
              !device ||
              !printer ||
              !rule
            }
            onClick={() =>
              void run(async () => {
                const targets = pending
                  .filter((p) => selected.includes(p.labelId))
                  .sort((a, b) => a.labelId - b.labelId)
                  .map((p) => ({
                    labelId: p.labelId,
                    originLineId: p.originLineId,
                    expectedRowVersion: p.rowVersion,
                  }));
                const result = await durable(
                  `${me?.id}:first:${selected.slice().sort().join(",")}`,
                  { templateVersionId: Number(template), deviceId: device, printer, targets },
                  (body) => ls<PrintJob>("/print/jobs/first", body),
                );
                setJob(result);
                setSelected([]);
                setTab("jobs");
                await refresh();
              })
            }
          >
            准备首次打印
          </button>
        </>
      )}
      {tab === "jobs" && (
        <section className="space-y-4">
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead>
                <tr>
                  <th>任务</th>
                  <th>状态</th>
                  <th>打印机</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {jobs.map((row) => (
                  <tr key={row.jobId} className="border-t border-slate-800">
                    <td className="py-3">{row.jobId.slice(0, 8)}</td>
                    <td>{phases[row.phase] ?? row.phase}</td>
                    <td>{row.printer}</td>
                    <td>
                      <button
                        className="underline"
                        disabled={busy}
                        onClick={() => void run(() => open(row.jobId))}
                      >
                        查看
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!jobs.length && <p className="py-3 text-slate-400">暂无打印记录</p>}
          </div>
          {job && (
            <div className="space-y-3 rounded border border-slate-700 p-4">
              <p>
                任务 {job.jobId.slice(0, 8)} · {phases[job.phase] ?? job.phase}
              </p>
              {job.outputs.map((output) => (
                <div className="flex flex-wrap gap-3 text-sm" key={output.outputId}>
                  <span>
                    {output.kind === "FIRST" ? "首打" : "补打"} · {output.snapshot.materialCode} ·
                    原始数量 {output.snapshot.quantity}
                  </span>
                  <span className="break-all font-mono">{output.snapshot.code}</span>
                  <span>{output.flowId ? "已登记" : "未登记"}</span>
                  {job.manifest && (
                    <button
                      className="underline"
                      onClick={() =>
                        void run(async () =>
                          showPdf(await printFile(`/jobs/${job.jobId}/pages/${output.outputId}`)),
                        )
                      }
                    >
                      预览原标签
                    </button>
                  )}
                </div>
              ))}
              <div className="flex flex-wrap gap-3">
                <button
                  className={button}
                  disabled={busy}
                  onClick={() => void run(() => open(job.jobId))}
                >
                  查询任务
                </button>
                <button
                  className={button}
                  disabled={disabled || !has("print.confirm") || job.phase !== "READY"}
                  onClick={() =>
                    void run(async () => {
                      try {
                        setJob(await sendPrint(job));
                      } finally {
                        setJob(await ls<PrintJob>(`/print/jobs/${job.jobId}`));
                        await refresh();
                      }
                    })
                  }
                >
                  发送打印
                </button>
                {job.phase === "COMPLETED" && job.outputs.length > 0 && (
                  <button
                    className={button}
                    disabled={busy}
                    onClick={() =>
                      navigate(`/reprint?code=${encodeURIComponent(job.outputs[0].snapshot.code)}`)
                    }
                  >
                    补打此标签
                  </button>
                )}
                <button
                  className={button}
                  disabled={
                    busy ||
                    !has("print.confirm") ||
                    job.phase === "COMPLETED" ||
                    job.phase === "CANCELLED" ||
                    job.phase === "READY" ||
                    job.phase === "FAILED_NO_OUTPUT"
                  }
                  onClick={() =>
                    void run(async () => {
                      setJob(await recoverPrint(await ls<PrintJob>(`/print/jobs/${job.jobId}`)));
                      await refresh();
                    })
                  }
                >
                  恢复登记
                </button>
                {["PREPARING", "READY", "FAILED_NO_OUTPUT"].includes(job.phase) && (
                  <button
                    className={button}
                    disabled={busy || !has("print.confirm")}
                    onClick={() =>
                      void run(async () => {
                        setJob(
                          await durable(
                            `${me?.id}:cancel:${job.jobId}`,
                            { expectedRowVersion: job.rowVersion },
                            (body) => ls<PrintJob>(`/print/jobs/${job.jobId}/cancel`, body),
                          ),
                        );
                        await refresh();
                      })
                    }
                  >
                    取消未发送任务
                  </button>
                )}
                {job.phase === "FAILED_NO_OUTPUT" && (
                  <button
                    className={button}
                    disabled={disabled || !has("print.confirm")}
                    onClick={() =>
                      void run(async () => {
                        setJob(
                          await durable(
                            `${me?.id}:resume:${job.jobId}`,
                            { expectedRowVersion: job.rowVersion },
                            (body) => ls<PrintJob>(`/print/jobs/${job.jobId}/resume`, body),
                          ),
                        );
                        await refresh();
                      })
                    }
                  >
                    继续原任务
                  </button>
                )}
              </div>
            </div>
          )}
        </section>
      )}
      {tab === "devices" && (
        <section className="space-y-4">
          <div className="flex flex-wrap gap-3">
            <a className="underline" href="/api/print-assistant/windows">
              下载安装打印助手
            </a>
            {me?.role === "admin" && has("print.device.manage") && (
              <button
                className={button}
                disabled={busy}
                onClick={() => void run(() => connect(true))}
              >
                登记本机助手
              </button>
            )}
          </div>
          {devices.map((d) => (
            <p key={d.id}>
              {d.name} · {d.enabled ? "已启用" : "已停用"}
              {me?.role === "admin" && has("print.device.manage") && (
                <button
                  className="ml-3 underline"
                  disabled={busy}
                  onClick={() =>
                    void run(async () => {
                      await ls(
                        `/print/devices/${d.id}`,
                        { expectedRowVersion: d.rowVersion, enabled: !d.enabled },
                        "PUT",
                      );
                      await refresh();
                    })
                  }
                >
                  {d.enabled ? "停用" : "启用"}
                </button>
              )}
            </p>
          ))}
        </section>
      )}
    </div>
  );
}
