import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api } from "../lib/api";
import { errorText, ls, printFile, showPdf, type PrintTemplate } from "../lib/labelSystem";

const button = "rounded bg-indigo-600 px-3 py-2 text-sm disabled:opacity-40";
export function PublishedTemplatesPage({ embedded = false }: { embedded?: boolean }) {
  const [drafts, setDrafts] = useState<{ id: number; name: string }[]>([]),
    [published, setPublished] = useState<PrintTemplate[]>([]);
  const [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [notice, setNotice] = useState("");
  const refresh = useCallback(async () => {
    const [a, b] = await Promise.all([
      api<{ templates: { id: number; name: string }[] }>("/api/templates"),
      ls<PrintTemplate[]>("/print/templates"),
    ]);
    setDrafts(a.templates);
    setPublished(b);
  }, []);
  useEffect(() => {
    void refresh().catch((e) => setError(errorText(e)));
  }, [refresh]);
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
    }
  }
  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        {embedded ? (
          <h2 className="font-semibold">正式版本</h2>
        ) : (
          <h1 className="text-2xl font-semibold">正式模板</h1>
        )}
        {!embedded && (
          <Link to="/templates" className="underline">
            模板设计
          </Link>
        )}
      </div>
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
      <section className="space-y-3">
        <h2 className="font-semibold">发布设计稿</h2>
        {drafts.map((draft) => (
          <div
            className="flex flex-wrap items-center gap-4 rounded border border-slate-800 p-3"
            key={draft.id}
          >
            <span className="flex-1">{draft.name}</span>
            <Link className="underline" to={`/templates/${draft.id}/edit`}>
              编辑
            </Link>
            <button
              className={button}
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  const value = await ls<{ versionId: number; reused: boolean }>(
                    `/designer-templates/${draft.id}/publish`,
                    {},
                  );
                  await refresh();
                  setNotice(
                    value.reused
                      ? "内容未变化，继续使用原发布版本"
                      : `已发布，版本编号 ${value.versionId}`,
                  );
                })
              }
            >
              发布为正式模板
            </button>
          </div>
        ))}
      </section>
      <section className="space-y-3">
        <h2 className="font-semibold">已发布模板</h2>
        {published.map((template) => (
          <div
            className="flex flex-wrap items-center gap-4 rounded border border-slate-800 p-3"
            key={template.id}
          >
            <span className="flex-1">
              {template.name} · {template.widthMm}×{template.heightMm} mm
            </span>
            <span>
              {template.publishedVersionId ? `版本 ${template.publishedVersionId}` : "未发布"}
            </span>
            <button
              className={button}
              disabled={busy || !template.publishedVersionId}
              onClick={() =>
                void run(async () => {
                  showPdf(
                    await printFile("/previews", {
                      templateVersionId: template.publishedVersionId,
                      variables: {
                        条码: "PREVIEW",
                        Code: "PREVIEW",
                        sn: "PREVIEW",
                        数量: "100",
                        quantity: "100",
                        单位: "KG",
                        物料编码: "DEMO-888",
                        物料名称: "预览物料",
                        批号: "B001",
                        单据编码: "DEMO001",
                        行号: "1",
                        仓库编码: "DEMO",
                        货位编码: "",
                      },
                    }),
                  );
                })
              }
            >
              预览
            </button>
          </div>
        ))}
      </section>
    </div>
  );
}
