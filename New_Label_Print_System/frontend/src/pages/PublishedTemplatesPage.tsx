import { useState } from "react";
import { Link } from "react-router-dom";
import { Button } from "../components/ui/Button";
import { useMe } from "../hooks/useMe";
import {
  usePublishedTemplates,
  useSetPublishedTemplateState,
} from "../hooks/usePublishedTemplates";
import { useTemplates } from "../hooks/useTemplates";
import { errorText, printFile, showPdf } from "../lib/labelSystem";

export function PublishedTemplatesPage({
  mode = "published",
}: {
  embedded?: boolean;
  mode?: "published" | "archived";
}) {
  const { data: me } = useMe();
  const drafts = useTemplates();
  const formal = usePublishedTemplates();
  const setState = useSetPublishedTemplateState();
  const manage = Boolean(me?.print_permissions?.includes("print.template.manage"));
  const enabled = mode === "published";
  const rows = (formal.data ?? []).filter((template) => template.enabled === enabled);
  const draftIds = new Set((drafts.data ?? []).map((draft) => draft.id));
  const [localError, setLocalError] = useState("");
  const orphans = rows.filter(
    (template) => template.sourceTemplateId !== null && !draftIds.has(template.sourceTemplateId),
  );
  const error = formal.error ?? setState.error;

  return (
    <section className="space-y-4">
      {(error || localError) && (
        <p role="alert" className="rounded-lg bg-rose-950/50 px-4 py-3 text-sm text-rose-300">
          {localError || errorText(error)}
        </p>
      )}

      {manage && enabled && orphans.length > 0 && (
        <div className="flex justify-end">
          <Button
            variant="secondary"
            disabled={setState.isPending}
            onClick={() => {
              setLocalError("");
              void (async () => {
                try {
                  for (const template of orphans) {
                    await setState.mutateAsync({ template, enabled: false });
                  }
                } catch (caught) {
                  setLocalError(errorText(caught));
                }
              })();
            }}
          >
            停用已删除设计稿（{orphans.length}）
          </Button>
        </div>
      )}

      {formal.isLoading && <p className="text-sm text-slate-400">正在加载模板…</p>}

      {!formal.isLoading && rows.length === 0 && (
        <div className="rounded-xl border border-dashed border-slate-700 bg-slate-900/30 px-6 py-12 text-center text-sm text-slate-400">
          {enabled ? "还没有已发布模板" : "没有已停用模板"}
        </div>
      )}

      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {rows.map((template) => {
          const hasDraft =
            template.sourceTemplateId !== null && draftIds.has(template.sourceTemplateId);
          return (
            <article
              key={template.id}
              className="rounded-xl border border-slate-800 bg-slate-900/60 p-5 shadow-sm"
            >
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <h3 className="truncate font-semibold text-slate-100">{template.name}</h3>
                  <p className="mt-1 text-sm text-slate-400">
                    {template.widthMm} × {template.heightMm} mm
                  </p>
                </div>
                <span
                  className={[
                    "shrink-0 rounded-full px-2.5 py-1 text-xs font-medium",
                    enabled ? "bg-emerald-950 text-emerald-300" : "bg-slate-800 text-slate-400",
                  ].join(" ")}
                >
                  {enabled ? "使用中" : "已停用"}
                </span>
              </div>

              <div className="mt-4 flex items-center gap-2 text-xs text-slate-500">
                <span>正式版本 {template.publishedVersionId ?? "—"}</span>
                {template.sourceTemplateId !== null && !hasDraft && (
                  <span className="rounded bg-amber-950/60 px-2 py-0.5 text-amber-300">
                    设计稿已删除
                  </span>
                )}
              </div>

              <div className="mt-5 flex flex-wrap items-center gap-2 border-t border-slate-800 pt-4">
                {hasDraft && (
                  <Link
                    className="rounded-md px-3 py-2 text-sm text-indigo-300 hover:bg-slate-800"
                    to={`/templates/${template.sourceTemplateId}/edit`}
                  >
                    编辑设计稿
                  </Link>
                )}
                <Button
                  variant="secondary"
                  disabled={!template.publishedVersionId}
                  onClick={() => {
                    if (!template.publishedVersionId) return;
                    setLocalError("");
                    void printFile("/previews", {
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
                    })
                      .then(showPdf)
                      .catch((caught) => setLocalError(errorText(caught)));
                  }}
                >
                  预览
                </Button>
                {manage && (
                  <Button
                    className="ml-auto"
                    variant={enabled ? "danger" : "secondary"}
                    disabled={setState.isPending}
                    onClick={() => setState.mutate({ template, enabled: !enabled })}
                  >
                    {enabled ? "停用" : "重新启用"}
                  </Button>
                )}
              </div>
            </article>
          );
        })}
      </div>
    </section>
  );
}
