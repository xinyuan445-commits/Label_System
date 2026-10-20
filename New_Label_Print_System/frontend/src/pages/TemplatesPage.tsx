import { useState } from "react";
import { useMe } from "../hooks/useMe";
import { PublishedTemplatesPage } from "./PublishedTemplatesPage";
import { useTranslation } from "react-i18next";
import { Link, useNavigate } from "react-router-dom";
import { ImportTemplateModal } from "../components/ImportTemplateModal";
import { Button } from "../components/ui/Button";
import { Input } from "../components/ui/Input";
import { Modal } from "../components/ui/Modal";
import { Select } from "../components/ui/Select";
import {
  type TemplateSummary,
  exportTemplateToFile,
  useCreateTemplate,
  useDeleteTemplate,
  useLabelFormats,
  useTemplates,
  useUpdateTemplate,
} from "../hooks/useTemplates";
import {
  type FolderSummary,
  useCreateFolder,
  useDeleteFolder,
  useFolders,
  useUpdateFolder,
} from "../hooks/useFolders";
import { featuredImageUrl } from "../hooks/useTemplates";
import { useUploadAsset } from "../hooks/useAssets";
import {
  usePublishedTemplates,
  useRefreshPublishedTemplates,
  useSetPublishedTemplateState,
} from "../hooks/usePublishedTemplates";
import { errorText, ls } from "../lib/labelSystem";
import { emptyCanvas } from "../editor/types";

/** Fixed palette for folder colours — dot in the rail + on cards. */
const FOLDER_COLORS = [
  "#ef4444",
  "#f97316",
  "#eab308",
  "#22c55e",
  "#06b6d4",
  "#3b82f6",
  "#8b5cf6",
  "#ec4899",
] as const;

function ColorDot({ color, className = "" }: { color: string | null; className?: string }) {
  if (!color) return null;
  return (
    <span
      className={`inline-block h-2.5 w-2.5 shrink-0 rounded-full ${className}`}
      style={{ backgroundColor: color }}
    />
  );
}

type FolderFilter = "all" | "none" | number;

export function TemplatesPage() {
  const { data: me } = useMe();
  const { t } = useTranslation();
  const integrated = me?.authentication_source === "labelsystem";
  const manageFormal = Boolean(
    integrated && me?.print_permissions?.includes("print.template.manage"),
  );
  const requested = new URLSearchParams(window.location.search).get("section");
  const [section, setSection] = useState<"drafts" | "published" | "archived">(
    requested === "published" || requested === "archived" ? requested : "drafts",
  );
  const [folderFilter, setFolderFilter] = useState<FolderFilter>("all");
  const templates = useTemplates(folderFilter === "all" ? undefined : folderFilter);
  const allTemplates = useTemplates();
  const formal = usePublishedTemplates(integrated);
  const refreshFormal = useRefreshPublishedTemplates();
  const setFormalState = useSetPublishedTemplateState();
  const folders = useFolders();
  const folderColor = (id: number | null) =>
    id === null ? null : (folders.data?.find((f) => f.id === id)?.color ?? null);
  const del = useDeleteTemplate();
  const [showCreate, setShowCreate] = useState(false);
  const [showImport, setShowImport] = useState(false);
  const [settingsFor, setSettingsFor] = useState<TemplateSummary | null>(null);
  const [renameFor, setRenameFor] = useState<TemplateSummary | null>(null);
  const [deleteFor, setDeleteFor] = useState<TemplateSummary | null>(null);
  const [query, setQuery] = useState("");
  const [publishingId, setPublishingId] = useState<number | null>(null);
  const [notice, setNotice] = useState("");
  const [actionError, setActionError] = useState("");

  const filtered = templates.data
    ? query.trim()
      ? templates.data.filter((tpl) => tpl.name.toLowerCase().includes(query.trim().toLowerCase()))
      : templates.data
    : [];
  const activeCount = (formal.data ?? []).filter((item) => item.enabled).length;
  const archivedCount = (formal.data ?? []).filter((item) => !item.enabled).length;
  const linkedFormal = (templateId: number) =>
    (formal.data ?? []).find((item) => item.sourceTemplateId === templateId);

  async function publish(template: TemplateSummary) {
    setPublishingId(template.id);
    setNotice("");
    setActionError("");
    try {
      const value = await ls<{ versionId: number; reused: boolean }>(
        `/designer-templates/${template.id}/publish`,
        {},
      );
      await refreshFormal();
      setNotice(value.reused ? "正式模板已启用" : `已发布正式版本 ${value.versionId}`);
    } catch (error) {
      setActionError(errorText(error));
    } finally {
      setPublishingId(null);
    }
  }

  async function removeDraft(stopFormal: boolean) {
    if (!deleteFor) return;
    const formalTemplate = linkedFormal(deleteFor.id);
    setActionError("");
    try {
      if (stopFormal && formalTemplate?.enabled) {
        await setFormalState.mutateAsync({ template: formalTemplate, enabled: false });
      }
      await del.mutateAsync(deleteFor.id);
      setDeleteFor(null);
    } catch (error) {
      setActionError(errorText(error));
    }
  }

  const tabs = [
    { id: "drafts" as const, label: "设计稿", count: allTemplates.data?.length ?? 0 },
    ...(integrated
      ? [
          { id: "published" as const, label: "已发布", count: activeCount },
          { id: "archived" as const, label: "已停用", count: archivedCount },
        ]
      : []),
  ];
  const visibleSection = integrated ? section : "drafts";

  return (
    <div className="space-y-6">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold">{t("nav.templates")}</h1>
          <p className="mt-1 text-sm text-slate-400">设计、发布和停用标签模板</p>
        </div>
        {visibleSection === "drafts" && (
          <div className="flex flex-wrap items-center gap-2">
            {allTemplates.data && allTemplates.data.length > 0 && (
              <Input
                type="search"
                placeholder={t("templates.searchPlaceholder")}
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                className="w-56"
              />
            )}
            <Button variant="secondary" onClick={() => setShowImport(true)}>
              {t("templates.import")}
            </Button>
            <Button onClick={() => setShowCreate(true)}>+ {t("templates.new")}</Button>
          </div>
        )}
      </header>

      <nav className="flex gap-1 rounded-xl border border-slate-800 bg-slate-900/60 p-1">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            onClick={() => {
              setSection(tab.id);
              setNotice("");
              setActionError("");
            }}
            className={[
              "flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium transition-colors",
              visibleSection === tab.id
                ? "bg-indigo-600 text-white shadow"
                : "text-slate-400 hover:bg-slate-800 hover:text-slate-100",
            ].join(" ")}
          >
            {tab.label}
            <span className="rounded-full bg-black/20 px-2 py-0.5 text-xs">{tab.count}</span>
          </button>
        ))}
      </nav>

      {notice && (
        <p
          role="status"
          className="rounded-lg bg-emerald-950/50 px-4 py-3 text-sm text-emerald-300"
        >
          {notice}
        </p>
      )}
      {actionError && (
        <p role="alert" className="rounded-lg bg-rose-950/50 px-4 py-3 text-sm text-rose-300">
          {actionError}
        </p>
      )}

      {visibleSection === "drafts" ? (
        <div className="flex gap-6">
          <FolderRail active={folderFilter} onSelect={setFolderFilter} />
          <div className="min-w-0 flex-1">
            {templates.isLoading && <p className="text-slate-400">{t("common.loading")}</p>}
            {templates.data && templates.data.length === 0 && (
              <div className="rounded-xl border border-dashed border-slate-700 bg-slate-900/30 p-12 text-center text-slate-400">
                {t("templates.empty")}
              </div>
            )}
            {templates.data && templates.data.length > 0 && filtered.length === 0 && (
              <div className="rounded-xl border border-dashed border-slate-700 bg-slate-900/30 p-12 text-center text-slate-400">
                {t("templates.noMatches", { query })}
              </div>
            )}
            {filtered.length > 0 && (
              <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
                {filtered.map((tpl) => {
                  const publication = linkedFormal(tpl.id);
                  return (
                    <article
                      key={tpl.id}
                      className="overflow-hidden rounded-xl border border-slate-800 bg-slate-900/60 shadow-sm transition hover:border-slate-700 hover:shadow-lg"
                    >
                      <Link to={`/templates/${tpl.id}/edit`} className="block">
                        {tpl.featured_asset_id !== null ? (
                          <img
                            src={featuredImageUrl(tpl)}
                            alt=""
                            loading="lazy"
                            className="h-32 w-full object-cover"
                          />
                        ) : (
                          <div className="flex h-32 items-center justify-center bg-gradient-to-br from-slate-800 to-slate-900 text-sm text-slate-500">
                            {tpl.width_mm} × {tpl.height_mm} mm
                          </div>
                        )}
                      </Link>
                      <div className="p-4">
                        <div className="flex items-start justify-between gap-3">
                          <div className="min-w-0">
                            <h3 className="truncate font-semibold text-slate-100">{tpl.name}</h3>
                            <p className="mt-1 text-xs text-slate-500">
                              <ColorDot color={folderColor(tpl.folder_id)} className="mr-1" />v
                              {tpl.version} · {new Date(tpl.updated_at).toLocaleDateString()}
                            </p>
                          </div>
                          <span
                            className={[
                              "shrink-0 rounded-full px-2.5 py-1 text-xs font-medium",
                              publication?.enabled
                                ? "bg-emerald-950 text-emerald-300"
                                : publication
                                  ? "bg-slate-800 text-slate-400"
                                  : "bg-amber-950/60 text-amber-300",
                            ].join(" ")}
                          >
                            {publication?.enabled ? "已发布" : publication ? "已停用" : "未发布"}
                          </span>
                        </div>
                        {tpl.description && (
                          <p className="mt-3 line-clamp-2 text-sm text-slate-400">
                            {tpl.description}
                          </p>
                        )}
                        <div className="mt-4 flex flex-wrap items-center gap-2 border-t border-slate-800 pt-4">
                          <Link
                            to={`/templates/${tpl.id}/edit`}
                            className="rounded-md bg-slate-800 px-3 py-2 text-sm text-slate-100 hover:bg-slate-700"
                          >
                            编辑
                          </Link>
                          {manageFormal && (
                            <Button
                              className="!px-3"
                              disabled={publishingId !== null}
                              onClick={() => void publish(tpl)}
                            >
                              {publishingId === tpl.id
                                ? "发布中…"
                                : publication?.enabled
                                  ? "重新发布"
                                  : "发布"}
                            </Button>
                          )}
                          <button
                            type="button"
                            onClick={() => setRenameFor(tpl)}
                            className="rounded px-2 py-2 text-sm text-slate-400 hover:bg-slate-800 hover:text-slate-100"
                          >
                            重命名
                          </button>
                          <button
                            type="button"
                            onClick={() => setSettingsFor(tpl)}
                            className="rounded px-2 py-2 text-sm text-slate-400 hover:bg-slate-800 hover:text-slate-100"
                          >
                            设置
                          </button>
                          <button
                            type="button"
                            onClick={() => {
                              const safe =
                                tpl.name.replace(/[^A-Za-z0-9._-]+/g, "_").replace(/^_|_$/g, "") ||
                                "template";
                              void exportTemplateToFile(tpl.id, `${safe}.blg-template.json`);
                            }}
                            className="rounded px-2 py-2 text-sm text-slate-400 hover:bg-slate-800 hover:text-slate-100"
                          >
                            导出
                          </button>
                          <button
                            type="button"
                            onClick={() => setDeleteFor(tpl)}
                            className="ml-auto rounded px-2 py-2 text-sm text-rose-400 hover:bg-rose-950/50"
                          >
                            删除
                          </button>
                        </div>
                      </div>
                    </article>
                  );
                })}
              </div>
            )}
          </div>
        </div>
      ) : (
        <PublishedTemplatesPage embedded mode={visibleSection} />
      )}

      {showCreate && <NewTemplateModal onClose={() => setShowCreate(false)} />}
      {showImport && <ImportTemplateModal onClose={() => setShowImport(false)} />}
      {settingsFor && (
        <TemplateSettingsModal template={settingsFor} onClose={() => setSettingsFor(null)} />
      )}
      {renameFor && <RenameTemplateModal template={renameFor} onClose={() => setRenameFor(null)} />}
      {deleteFor && (
        <Modal
          open
          onClose={() => {
            if (!del.isPending && !setFormalState.isPending) setDeleteFor(null);
          }}
          title={`删除“${deleteFor.name}”`}
          footer={
            <>
              <Button
                variant="secondary"
                disabled={del.isPending || setFormalState.isPending}
                onClick={() => setDeleteFor(null)}
              >
                取消
              </Button>
              {linkedFormal(deleteFor.id)?.enabled && manageFormal && (
                <Button
                  variant="secondary"
                  disabled={del.isPending || setFormalState.isPending}
                  onClick={() => void removeDraft(false)}
                >
                  只删除设计稿
                </Button>
              )}
              <Button
                variant="danger"
                disabled={del.isPending || setFormalState.isPending}
                onClick={() =>
                  void removeDraft(Boolean(manageFormal && linkedFormal(deleteFor.id)?.enabled))
                }
              >
                {linkedFormal(deleteFor.id)?.enabled && manageFormal ? "停用并删除" : "删除"}
              </Button>
            </>
          }
        >
          <p className="text-sm text-slate-300">
            {linkedFormal(deleteFor.id)?.enabled
              ? manageFormal
                ? "此模板正在使用。建议同时停用正式模板。"
                : "正式模板会继续保留。"
              : "删除后无法恢复。"}
          </p>
        </Modal>
      )}
    </div>
  );
}

function FolderRail({
  active,
  onSelect,
}: {
  active: FolderFilter;
  onSelect: (f: FolderFilter) => void;
}) {
  const { t } = useTranslation();
  const folders = useFolders();
  const createFolder = useCreateFolder();
  const deleteFolder = useDeleteFolder();
  const [newName, setNewName] = useState("");
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<FolderSummary | null>(null);

  const item = (
    key: FolderFilter,
    label: string,
    count?: number | null,
    folder?: FolderSummary,
  ) => (
    <button
      key={String(key)}
      type="button"
      onClick={() => onSelect(key)}
      className={[
        "group/f flex w-full items-center justify-between rounded px-2 py-1.5 text-left text-sm",
        active === key ? "bg-indigo-600 text-white" : "text-slate-300 hover:bg-slate-800",
      ].join(" ")}
    >
      <span className="flex min-w-0 items-center gap-1.5">
        <span>📁</span>
        <ColorDot color={folder?.color ?? null} />
        <span className="truncate">{label}</span>
      </span>
      <span className="flex items-center gap-1">
        {folder && (
          <span className="hidden gap-0.5 group-hover/f:flex">
            <span
              role="button"
              tabIndex={0}
              title={t("folders.edit")}
              className="rounded px-1 hover:bg-slate-700"
              onClick={(e) => {
                e.stopPropagation();
                setEditing(folder);
              }}
            >
              ✎
            </span>
            <span
              role="button"
              tabIndex={0}
              title={t("common.delete")}
              className="rounded px-1 text-rose-400 hover:bg-rose-950/60"
              onClick={(e) => {
                e.stopPropagation();
                if (window.confirm(t("folders.confirmDelete", { name: label }))) {
                  deleteFolder.mutate(folder.id);
                  if (active === folder.id) onSelect("all");
                }
              }}
            >
              ✕
            </span>
          </span>
        )}
        {count != null && <span className="text-xs opacity-70">{count}</span>}
      </span>
    </button>
  );

  return (
    <aside className="w-52 shrink-0 space-y-1">
      <h2 className="px-2 pb-1 text-xs font-semibold uppercase tracking-wider text-slate-500">
        {t("folders.title")}
      </h2>
      {item("all", t("folders.all"))}
      {folders.data?.map((f) => item(f.id, f.name, f.template_count, f))}
      {item("none", t("folders.unfiled"))}
      {editing && <FolderEditModal folder={editing} onClose={() => setEditing(null)} />}
      {adding ? (
        <form
          className="flex gap-1 px-1 pt-1"
          onSubmit={(e) => {
            e.preventDefault();
            if (!newName.trim()) return;
            createFolder.mutate(newName.trim(), {
              onSuccess: () => {
                setNewName("");
                setAdding(false);
              },
            });
          }}
        >
          <input
            autoFocus
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => e.key === "Escape" && setAdding(false)}
            placeholder={t("folders.namePlaceholder")}
            className="w-full rounded border border-slate-700 bg-slate-900 px-2 py-1 text-sm text-slate-100"
          />
          <Button type="submit" className="!px-2 !py-1 text-xs">
            ✓
          </Button>
        </form>
      ) : (
        <button
          type="button"
          onClick={() => setAdding(true)}
          className="w-full rounded px-2 py-1.5 text-left text-sm text-indigo-400 hover:bg-slate-800"
        >
          + {t("folders.new")}
        </button>
      )}
      {createFolder.error && (
        <p className="px-2 text-xs text-rose-400">{t("folders.errors.nameTaken")}</p>
      )}
    </aside>
  );
}

function FolderEditModal({ folder, onClose }: { folder: FolderSummary; onClose: () => void }) {
  const { t } = useTranslation();
  const update = useUpdateFolder();
  const [name, setName] = useState(folder.name);
  const [color, setColor] = useState<string | null>(folder.color);

  return (
    <Modal
      open
      onClose={onClose}
      title={t("folders.editTitle")}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            disabled={update.isPending || !name.trim()}
            onClick={() =>
              update.mutate({ id: folder.id, name: name.trim(), color }, { onSuccess: onClose })
            }
          >
            {t("common.save")}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Input
          label={t("folders.namePlaceholder")}
          value={name}
          onChange={(e) => setName(e.target.value)}
          required
        />
        <div className="space-y-1">
          <span className="block text-sm font-medium text-slate-200">{t("folders.color")}</span>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => setColor(null)}
              title={t("folders.colorNone")}
              className={[
                "flex h-7 w-7 items-center justify-center rounded-full border text-xs",
                color === null ? "border-white text-white" : "border-slate-600 text-slate-500",
              ].join(" ")}
            >
              ∅
            </button>
            {FOLDER_COLORS.map((c) => (
              <button
                key={c}
                type="button"
                onClick={() => setColor(c)}
                aria-label={c}
                className={[
                  "h-7 w-7 rounded-full border-2",
                  color === c ? "border-white" : "border-transparent",
                ].join(" ")}
                style={{ backgroundColor: c }}
              />
            ))}
          </div>
        </div>
        {update.error && <p className="text-sm text-rose-400">{t("folders.errors.nameTaken")}</p>}
      </div>
    </Modal>
  );
}

function RenameTemplateModal({
  template,
  onClose,
}: {
  template: TemplateSummary;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const update = useUpdateTemplate();
  const [name, setName] = useState(template.name);
  const trimmedName = name.trim();

  return (
    <Modal
      open
      onClose={() => {
        if (!update.isPending) onClose();
      }}
      title={t("templates.rename")}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={update.isPending}>
            {t("common.cancel")}
          </Button>
          <Button
            type="submit"
            form="rename-template-form"
            disabled={update.isPending || !trimmedName || trimmedName === template.name}
          >
            {update.isPending ? t("common.loading") : t("common.save")}
          </Button>
        </>
      }
    >
      <form
        id="rename-template-form"
        className="space-y-4"
        onSubmit={(e) => {
          e.preventDefault();
          if (update.isPending || !trimmedName || trimmedName === template.name) return;
          update.mutate({ id: template.id, patch: { name: trimmedName } }, { onSuccess: onClose });
        }}
      >
        <Input
          id="rename-template-name"
          label={t("templates.name")}
          autoFocus
          required
          maxLength={200}
          disabled={update.isPending}
          value={name}
          onChange={(e) => {
            setName(e.target.value);
            update.reset();
          }}
          onFocus={(e) => e.target.select()}
        />
        {update.error && (
          <p role="alert" className="text-sm text-rose-400">
            {t("auth.errors.generic")}
          </p>
        )}
      </form>
    </Modal>
  );
}

function TemplateSettingsModal({
  template,
  onClose,
}: {
  template: TemplateSummary;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const folders = useFolders();
  const update = useUpdateTemplate();
  const upload = useUploadAsset();
  const [folderId, setFolderId] = useState<number | "">(template.folder_id ?? "");
  const [shared, setShared] = useState(template.is_shared);
  const [featuredId, setFeaturedId] = useState<number | null>(template.featured_asset_id);
  const [featuredPreview, setFeaturedPreview] = useState<string | null>(
    template.featured_asset_id !== null ? featuredImageUrl(template) : null,
  );

  return (
    <Modal
      open
      onClose={onClose}
      title={`${t("templates.settingsTitle")} — ${template.name}`}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            disabled={update.isPending}
            onClick={() =>
              update.mutate(
                {
                  id: template.id,
                  patch: {
                    folder_id: folderId === "" ? null : folderId,
                    is_shared: shared,
                    featured_asset_id: featuredId,
                  },
                },
                { onSuccess: onClose },
              )
            }
          >
            {t("common.save")}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Select
          label={t("folders.moveTo")}
          value={folderId}
          onChange={(e) => setFolderId(e.target.value === "" ? "" : Number(e.target.value))}
        >
          <option value="">— {t("folders.unfiled")} —</option>
          {folders.data?.map((f) => (
            <option key={f.id} value={f.id}>
              {f.name}
            </option>
          ))}
        </Select>
        <label className="flex cursor-pointer items-start gap-2 text-sm text-slate-200">
          <input
            type="checkbox"
            checked={shared}
            onChange={(e) => setShared(e.target.checked)}
            className="mt-0.5 h-4 w-4 rounded border-slate-700 bg-slate-900 text-indigo-600"
          />
          <span>
            📚 {t("templates.shareToggle")}
            <span className="block text-xs text-slate-500">{t("templates.shareHint")}</span>
          </span>
        </label>

        <div className="space-y-2">
          <span className="block text-sm font-medium text-slate-200">
            {t("templates.featuredImage")}
          </span>
          {featuredPreview && (
            <img
              src={featuredPreview}
              alt=""
              className="h-24 w-full rounded border border-slate-700 object-cover"
            />
          )}
          <div className="flex items-center gap-2">
            <label className="cursor-pointer rounded-md border border-slate-700 bg-slate-900 px-3 py-1.5 text-xs text-slate-200 hover:bg-slate-800">
              {upload.isPending ? t("common.loading") : t("templates.featuredUpload")}
              <input
                type="file"
                accept="image/png,image/jpeg,image/svg+xml"
                className="hidden"
                onChange={(e) => {
                  const file = e.target.files?.[0];
                  if (!file) return;
                  upload.mutate(file, {
                    onSuccess: (asset) => {
                      setFeaturedId(asset.id);
                      setFeaturedPreview(URL.createObjectURL(file));
                    },
                  });
                }}
              />
            </label>
            {featuredId !== null && (
              <Button
                variant="ghost"
                onClick={() => {
                  setFeaturedId(null);
                  setFeaturedPreview(null);
                }}
              >
                {t("templates.featuredRemove")}
              </Button>
            )}
          </div>
          <p className="text-xs text-slate-500">{t("templates.featuredHint")}</p>
          {upload.error && (
            <p className="text-xs text-rose-400">
              {upload.error instanceof Error ? upload.error.message : String(upload.error)}
            </p>
          )}
        </div>
        {update.error && <p className="text-sm text-rose-400">{t("auth.errors.generic")}</p>}
      </div>
    </Modal>
  );
}

function NewTemplateModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const formats = useLabelFormats();
  const create = useCreateTemplate();

  const [name, setName] = useState("");
  const [formatId, setFormatId] = useState<number | "">("");
  const [orientation, setOrientation] = useState<"portrait" | "landscape">("portrait");
  const [customW, setCustomW] = useState<number>(100);
  const [customH, setCustomH] = useState<number>(180);
  const [cornerRadius, setCornerRadius] = useState<number>(0);

  const fmt = formats.data?.find((f) => f.id === formatId) ?? null;
  const isCustom = fmt?.kind === "custom";
  const isSquare = !!fmt && (isCustom ? customW === customH : fmt.width_mm === fmt.height_mm);
  const showOrientation = !!fmt && !isSquare;

  // Resolve the dimensions the user is about to commit to — same math
  // we'll send to the backend below. Used for the live "Wybrano: …"
  // hint and for validation.
  const resolved = (() => {
    if (!fmt) return null;
    const width = isCustom ? customW : fmt.width_mm;
    const height = isCustom ? customH : fmt.height_mm;
    if (orientation === "landscape") {
      return { w: Math.max(width, height), h: Math.min(width, height) };
    }
    return { w: Math.min(width, height), h: Math.max(width, height) };
  })();

  const customInRange =
    !isCustom || (customW >= 10 && customW <= 1000 && customH >= 10 && customH <= 1000);
  const canSubmit =
    name.length > 0 &&
    formatId !== "" &&
    customInRange &&
    !!resolved &&
    cornerRadius >= 0 &&
    cornerRadius <= Math.min(resolved.w, resolved.h) / 2;

  // Group formats so the custom row sits below a divider rather than
  // mixed in with the system presets.
  const presetFormats = formats.data?.filter((f) => f.kind !== "custom") ?? [];
  const customFormats = formats.data?.filter((f) => f.kind === "custom") ?? [];

  return (
    <Modal
      open
      onClose={onClose}
      title={t("templates.new")}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            disabled={!canSubmit || create.isPending}
            onClick={() => {
              if (formatId === "" || !resolved) return;
              create.mutate(
                {
                  name,
                  format_id: Number(formatId),
                  width_mm: resolved.w,
                  height_mm: resolved.h,
                  canvas_data: {
                    ...emptyCanvas(resolved.w, resolved.h),
                    stage: {
                      width_mm: resolved.w,
                      height_mm: resolved.h,
                      corner_radius_mm: cornerRadius,
                    },
                  },
                },
                {
                  onSuccess: (created) => {
                    onClose();
                    void navigate(`/templates/${created.id}/edit`);
                  },
                },
              );
            }}
          >
            {t("common.create")}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Input
          label={t("templates.name")}
          value={name}
          onChange={(e) => setName(e.target.value)}
          required
        />
        <Select
          label={t("templates.format")}
          value={formatId}
          disabled={formats.isPending || formats.isError || formats.data?.length === 0}
          onChange={(e) => setFormatId(e.target.value === "" ? "" : Number(e.target.value))}
        >
          <option value="">— {t("templates.choose")} —</option>
          {presetFormats.length > 0 && (
            <optgroup label={t("templates.presetGroup")}>
              {presetFormats.map((f) => (
                <option key={f.id} value={f.id}>
                  {f.name}
                </option>
              ))}
            </optgroup>
          )}
          {customFormats.length > 0 && (
            <optgroup label={t("templates.customGroup")}>
              {customFormats.map((f) => (
                <option key={f.id} value={f.id}>
                  {t("templates.customSize")}
                </option>
              ))}
            </optgroup>
          )}
        </Select>
        {formats.isPending && <p className="text-sm text-slate-400">正在读取规格…</p>}
        {formats.isError && (
          <p role="alert" className="text-sm text-rose-400">
            规格加载失败，请刷新重试
          </p>
        )}
        {formats.isSuccess && formats.data.length === 0 && (
          <p role="alert" className="text-sm text-rose-400">
            暂无可用规格
          </p>
        )}

        {showOrientation && (
          <div className="space-y-1">
            <label className="block text-sm font-medium text-slate-200">
              {t("templates.orientation")}
            </label>
            <div className="inline-flex overflow-hidden rounded-md border border-slate-700">
              <button
                type="button"
                onClick={() => setOrientation("portrait")}
                className={[
                  "px-3 py-1.5 text-xs",
                  orientation === "portrait"
                    ? "bg-indigo-600 text-white"
                    : "bg-slate-900 text-slate-300 hover:bg-slate-800",
                ].join(" ")}
              >
                ▯ {t("templates.portrait")}
              </button>
              <button
                type="button"
                onClick={() => setOrientation("landscape")}
                className={[
                  "px-3 py-1.5 text-xs",
                  orientation === "landscape"
                    ? "bg-indigo-600 text-white"
                    : "bg-slate-900 text-slate-300 hover:bg-slate-800",
                ].join(" ")}
              >
                ▭ {t("templates.landscape")}
              </button>
            </div>
          </div>
        )}

        {isCustom && (
          <div className="grid grid-cols-2 gap-2">
            <Input
              label={t("templates.customWidthMm")}
              type="number"
              min={10}
              max={1000}
              step={1}
              value={customW}
              onChange={(e) => setCustomW(Number(e.target.value) || 0)}
            />
            <Input
              label={t("templates.customHeightMm")}
              type="number"
              min={10}
              max={1000}
              step={1}
              value={customH}
              onChange={(e) => setCustomH(Number(e.target.value) || 0)}
            />
          </div>
        )}

        {resolved && (
          <Input
            label={t("labelSize.cornerRadius")}
            type="number"
            min={0}
            max={Math.min(resolved.w, resolved.h) / 2}
            step="0.1"
            value={cornerRadius}
            onChange={(e) => setCornerRadius(Number(e.target.value))}
          />
        )}

        {resolved && (
          <p className="rounded border border-slate-800 bg-slate-900/40 px-3 py-2 text-xs text-slate-400">
            {t("templates.resolvedSize", { w: resolved.w, h: resolved.h })}
          </p>
        )}

        {create.error && (
          <p className="text-sm text-rose-400">
            {create.error instanceof Error ? create.error.message : String(create.error)}
          </p>
        )}
      </div>
    </Modal>
  );
}
