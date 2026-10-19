import { useCallback, useEffect, useState } from "react";
import { useMe } from "../hooks/useMe";
import {
  ls,
  errorText,
  type Rule,
  type RuleSpec,
  type Segment,
  type CurrentRule,
} from "../lib/labelSystem";

const kinds: Record<string, string> = {
  FIELD: "业务字段",
  LITERAL: "固定内容",
  SEPARATOR: "分隔符",
  DATE: "日期",
  SEQUENCE: "流水号",
  CHECKSUM: "校验位",
};
const fields: Record<string, string> = {
  ERP_ACCOUNT_ID: "账套号",
  MATERIAL_CODE: "料号",
  BATCH_NO: "批号",
  ISSUED_QUANTITY: "首次数量",
  UNIT_CODE: "单位",
  MANUFACTURE_DATE: "生产日期",
  SOURCE_DOCUMENT_NO: "来源单号",
  LABEL_TYPE: "标签类型",
  LABEL_UNIQUE_VALUE: "标签唯一值",
};
const input = "rounded border border-slate-700 bg-slate-900 px-3 py-2 text-sm";
const button = "rounded bg-indigo-600 px-3 py-2 text-sm disabled:opacity-40";
const initial: RuleSpec = {
  schemaVersion: 1,
  symbologies: ["CODE128", "QR"],
  caseMode: "UPPER",
  segments: [
    { kind: "FIELD", field: "ERP_ACCOUNT_ID", validateCurrentAccount: true, maxLength: 20 },
    { kind: "SEPARATOR", value: "|" },
    { kind: "FIELD", field: "MATERIAL_CODE", maxLength: 100 },
    { kind: "SEPARATOR", value: "|" },
    { kind: "FIELD", field: "BATCH_NO", maxLength: 100 },
    { kind: "SEPARATOR", value: "|" },
    { kind: "FIELD", field: "ISSUED_QUANTITY", decimalScale: 6, maxLength: 30 },
    { kind: "SEPARATOR", value: "|" },
    { kind: "SEQUENCE", length: 10 },
  ],
};
const sample: Record<string, string> = {
  MATERIAL_CODE: "DEMO-888",
  BATCH_NO: "B001",
  ISSUED_QUANTITY: "100",
  UNIT_CODE: "KG",
  SOURCE_DOCUMENT_NO: "DEMO001",
  LABEL_TYPE: "P",
  LABEL_UNIQUE_VALUE: "2609270062",
  SEQUENCE: "1",
  MANUFACTURE_DATE: "2026-10-19",
};
function segment(kind: string): Segment {
  if (kind === "FIELD") return { kind, field: "MATERIAL_CODE", maxLength: 100 };
  if (kind === "LITERAL" || kind === "SEPARATOR")
    return { kind, value: kind === "LITERAL" ? "LS" : "|" };
  if (kind === "SEQUENCE") return { kind, length: 10 };
  if (kind === "DATE") return { kind, dateFormat: "yyyyMMdd" };
  return { kind, algorithm: "SHA256_8" };
}
export function PrintRulesPage() {
  const { data: me } = useMe();
  const manage = me?.role === "admin" && me.print_permissions?.includes("print.template.manage");
  const [rules, setRules] = useState<Rule[]>([]),
    [current, setCurrent] = useState<CurrentRule | null>(null);
  const [selected, setSelected] = useState<Rule | null>(null),
    [editing, setEditing] = useState(false);
  const [code, setCode] = useState(""),
    [name, setName] = useState(""),
    [priority, setPriority] = useState(100),
    [enabled, setEnabled] = useState(true);
  const [spec, setSpec] = useState<RuleSpec>(structuredClone(initial)),
    [values, setValues] = useState(sample);
  const [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [notice, setNotice] = useState(""),
    [preview, setPreview] = useState("");
  const refresh = useCallback(async () => {
    const [catalog, binding] = await Promise.all([
      manage ? ls<Rule[]>("/rules") : ls<{ rules: Rule[] }>("/rules/catalog").then((x) => x.rules),
      ls<CurrentRule | null>("/print/code-rule"),
    ]);
    setRules(catalog);
    setCurrent(binding);
  }, [manage]);
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
  function open(rule: Rule | null) {
    setSelected(rule);
    setCode(rule?.code ?? "");
    setName(rule?.name ?? "");
    setPriority(rule?.priority ?? 100);
    setEnabled(rule?.isEnabled ?? true);
    const version =
      rule?.versions.find((v) => v.status === "DRAFT") ??
      rule?.versions.find((v) => v.status === "ACTIVE") ??
      rule?.versions[0];
    setSpec(structuredClone(version?.spec ?? initial));
    setEditing(true);
    setPreview("");
    setError("");
  }
  function patch(index: number, value: Partial<Segment>) {
    setSpec((old) => ({
      ...old,
      segments: old.segments.map((s, i) => (i === index ? { ...s, ...value } : s)),
    }));
  }
  const draft = selected?.versions.find((v) => v.status === "DRAFT");
  async function save() {
    const payload = { name, priority, isEnabled: enabled, spec };
    const rule = selected
      ? await ls<Rule>(
          `/rules/${selected.id}/draft`,
          {
            ...payload,
            expectedRuleRowVersion: selected.rowVersion,
            expectedVersionRowVersion: draft?.rowVersion ?? null,
          },
          "PUT",
        )
      : await ls<Rule>("/rules", { ...payload, code, objectType: "PRODUCT_LABEL" });
    await refresh();
    open(rule);
    setNotice("草稿已保存");
  }
  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold">编码规则</h1>
        {manage && (
          <button className={button} disabled={busy} onClick={() => open(null)}>
            新建规则
          </button>
        )}
      </div>
      <p>
        当前打印规则：<strong>{current?.name ?? "尚未选择"}</strong>
      </p>
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
      <div className="overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead>
            <tr>
              <th className="p-3">名称</th>
              <th>规则代码</th>
              <th>版本</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            {rules
              .filter((r) => r.objectType === "PRODUCT_LABEL")
              .map((rule) => {
                const active = rule.versions.find((v) => v.status === "ACTIVE");
                return (
                  <tr key={rule.id} className="border-t border-slate-800">
                    <td className="p-3">
                      {rule.name}
                      {!rule.isEnabled && "（已停用）"}
                    </td>
                    <td>{rule.code}</td>
                    <td>{active ? `已生效 v${active.version}` : "未生效"}</td>
                    <td className="space-x-3">
                      <button className="underline" onClick={() => open(rule)}>
                        {manage ? "编辑" : "查看"}
                      </button>
                      {manage && active && rule.isEnabled && (
                        <button
                          className="underline"
                          disabled={busy || current?.versionId === active.id}
                          onClick={() =>
                            void run(async () => {
                              await ls(
                                "/print/code-rule",
                                {
                                  ruleVersionId: active.id,
                                  expectedRowVersion: current?.rowVersion ?? null,
                                },
                                "PUT",
                              );
                              await refresh();
                              setNotice("当前打印规则已更新");
                            })
                          }
                        >
                          {current?.versionId === active.id ? "当前使用" : "设为当前"}
                        </button>
                      )}
                    </td>
                  </tr>
                );
              })}
          </tbody>
        </table>
      </div>
      {editing && (
        <section className="space-y-4 rounded border border-slate-700 p-4">
          <div className="flex flex-wrap gap-3">
            <input
              aria-label="规则代码"
              className={input}
              placeholder="规则代码"
              value={code}
              disabled={!!selected || !manage}
              onChange={(e) => setCode(e.target.value)}
            />
            <input
              aria-label="规则名称"
              className={input}
              placeholder="规则名称"
              value={name}
              disabled={!manage}
              onChange={(e) => setName(e.target.value)}
            />
            <label>
              优先级{" "}
              <input
                aria-label="优先级"
                className={input + " w-24"}
                type="number"
                value={priority}
                disabled={!manage}
                onChange={(e) => setPriority(Number(e.target.value))}
              />
            </label>
            <label>
              <input
                type="checkbox"
                checked={enabled}
                disabled={!manage}
                onChange={(e) => setEnabled(e.target.checked)}
              />{" "}
              启用
            </label>
          </div>
          <div className="flex gap-4">
            <label>
              <input
                type="checkbox"
                checked={spec.symbologies.includes("CODE128")}
                disabled={!manage}
                onChange={(e) =>
                  setSpec({
                    ...spec,
                    symbologies: e.target.checked
                      ? [...spec.symbologies, "CODE128"]
                      : spec.symbologies.filter((x) => x !== "CODE128"),
                  })
                }
              />{" "}
              Code 128
            </label>
            <label>
              <input
                type="checkbox"
                checked={spec.symbologies.includes("QR")}
                disabled={!manage}
                onChange={(e) =>
                  setSpec({
                    ...spec,
                    symbologies: e.target.checked
                      ? [...spec.symbologies, "QR"]
                      : spec.symbologies.filter((x) => x !== "QR"),
                  })
                }
              />{" "}
              二维码
            </label>
            <select
              aria-label="大小写"
              className={input}
              value={spec.caseMode}
              disabled={!manage}
              onChange={(e) => setSpec({ ...spec, caseMode: e.target.value })}
            >
              <option value="PRESERVE">保留大小写</option>
              <option value="UPPER">转为大写</option>
              <option value="LOWER">转为小写</option>
            </select>
          </div>
          {spec.segments.map((s, index) => (
            <div className="flex flex-wrap items-center gap-2" key={index}>
              <span>{index + 1}</span>
              <select
                className={input}
                value={s.kind}
                disabled={!manage}
                aria-label={`第${index + 1}段类型`}
                onChange={(e) =>
                  setSpec({
                    ...spec,
                    segments: spec.segments.map((v, i) =>
                      i === index ? segment(e.target.value) : v,
                    ),
                  })
                }
              >
                {Object.entries(kinds).map(([k, v]) => (
                  <option key={k} value={k}>
                    {v}
                  </option>
                ))}
              </select>
              {(s.kind === "LITERAL" || s.kind === "SEPARATOR") && (
                <input
                  className={input}
                  aria-label="固定内容或分隔符"
                  value={s.value ?? ""}
                  disabled={!manage}
                  onChange={(e) => patch(index, { value: e.target.value })}
                />
              )}
              {s.kind === "FIELD" && (
                <>
                  <select
                    className={input}
                    aria-label="业务字段"
                    value={s.field}
                    disabled={!manage}
                    onChange={(e) => {
                      const next: Segment = {
                        kind: "FIELD",
                        field: e.target.value,
                        maxLength: 100,
                      };
                      if (e.target.value === "ISSUED_QUANTITY") next.decimalScale = 6;
                      if (e.target.value === "ERP_ACCOUNT_ID") next.validateCurrentAccount = true;
                      setSpec({
                        ...spec,
                        segments: spec.segments.map((v, i) => (i === index ? next : v)),
                      });
                    }}
                  >
                    {Object.entries(fields).map(([k, v]) => (
                      <option key={k} value={k}>
                        {v}
                      </option>
                    ))}
                  </select>
                  <select
                    className={input}
                    aria-label="字段编码"
                    value={s.encoding ?? "NONE"}
                    disabled={!manage}
                    onChange={(e) => patch(index, { encoding: e.target.value })}
                  >
                    <option value="NONE">原文</option>
                    <option value="PERCENT">转义</option>
                  </select>
                  <input
                    className={input + " w-24"}
                    aria-label="最大长度"
                    type="number"
                    min={1}
                    value={s.maxLength ?? 100}
                    disabled={!manage}
                    onChange={(e) => patch(index, { maxLength: Number(e.target.value) })}
                  />
                  {s.field === "ISSUED_QUANTITY" && (
                    <label>
                      小数位{" "}
                      <input
                        aria-label="数量小数位"
                        className={input + " w-20"}
                        type="number"
                        min={0}
                        max={6}
                        value={s.decimalScale ?? 6}
                        disabled={!manage}
                        onChange={(e) => patch(index, { decimalScale: Number(e.target.value) })}
                      />
                    </label>
                  )}
                  <input
                    className={input}
                    aria-label="固定校验值"
                    placeholder="固定校验值（可选）"
                    value={s.expectedValue ?? ""}
                    disabled={!manage}
                    onChange={(e) => patch(index, { expectedValue: e.target.value || undefined })}
                  />
                  {s.field === "ERP_ACCOUNT_ID" && (
                    <label>
                      <input
                        type="checkbox"
                        checked={s.validateCurrentAccount ?? false}
                        disabled={!manage}
                        onChange={(e) => patch(index, { validateCurrentAccount: e.target.checked })}
                      />{" "}
                      校验当前账套
                    </label>
                  )}
                </>
              )}
              {s.kind === "SEQUENCE" && (
                <label>
                  位数{" "}
                  <input
                    aria-label="流水号位数"
                    className={input + " w-24"}
                    type="number"
                    min={1}
                    max={20}
                    value={s.length ?? 10}
                    disabled={!manage}
                    onChange={(e) => patch(index, { length: Number(e.target.value) })}
                  />
                </label>
              )}
              {s.kind === "DATE" && (
                <select
                  aria-label="日期格式"
                  className={input}
                  value={s.dateFormat ?? "yyyyMMdd"}
                  disabled={!manage}
                  onChange={(e) => patch(index, { dateFormat: e.target.value })}
                >
                  {["yyyyMMdd", "yyMMdd", "yyyyMMddHHmmss"].map((x) => (
                    <option key={x}>{x}</option>
                  ))}
                </select>
              )}
              {s.kind === "CHECKSUM" && (
                <select
                  aria-label="校验方式"
                  className={input}
                  value={s.algorithm ?? "SHA256_8"}
                  disabled={!manage}
                  onChange={(e) => patch(index, { algorithm: e.target.value })}
                >
                  <option>SHA256_8</option>
                  <option>EAN_MOD10</option>
                </select>
              )}
              {manage && (
                <>
                  <button
                    disabled={busy || index === 0}
                    className="px-2"
                    onClick={() => {
                      const list = [...spec.segments];
                      [list[index - 1], list[index]] = [list[index], list[index - 1]];
                      setSpec({ ...spec, segments: list });
                    }}
                  >
                    上移
                  </button>
                  <button
                    className="px-2"
                    onClick={() =>
                      setSpec({ ...spec, segments: spec.segments.filter((_, i) => i !== index) })
                    }
                  >
                    删除
                  </button>
                </>
              )}
            </div>
          ))}
          {manage && (
            <div className="flex flex-wrap gap-3">
              <button
                className={button}
                disabled={busy}
                onClick={() => setSpec({ ...spec, segments: [...spec.segments, segment("FIELD")] })}
              >
                添加编码段
              </button>
              <button
                className={button}
                disabled={busy || !name || !code}
                onClick={() => void run(save)}
              >
                保存草稿
              </button>
              {draft && (
                <button
                  className={button}
                  disabled={busy}
                  onClick={() =>
                    void run(async () => {
                      const rule = await ls<Rule>(
                        `/rules/${selected!.id}/versions/${draft.id}/activate`,
                        {
                          expectedRuleRowVersion: selected!.rowVersion,
                          expectedVersionRowVersion: draft.rowVersion,
                        },
                      );
                      await refresh();
                      open(rule);
                      setNotice("版本已生效，请按需设为当前打印规则");
                    })
                  }
                >
                  启用草稿版本
                </button>
              )}
              {selected?.versions
                .filter((v) => v.status === "ACTIVE")
                .map((version) => (
                  <button
                    className={button}
                    key={version.id}
                    disabled={busy || current?.versionId === version.id}
                    onClick={() =>
                      void run(async () => {
                        const rule = await ls<Rule>(
                          `/rules/${selected.id}/versions/${version.id}/retire`,
                          {
                            expectedRuleRowVersion: selected.rowVersion,
                            expectedVersionRowVersion: version.rowVersion,
                          },
                        );
                        await refresh();
                        open(rule);
                        setNotice("版本已停用");
                      })
                    }
                  >
                    停用 v{version.version}
                  </button>
                ))}
            </div>
          )}
          {manage && (
            <details>
              <summary className="cursor-pointer">样例预览</summary>
              <div className="mt-3 flex flex-wrap gap-2">
                {Object.entries(values).map(([key, value]) => (
                  <label className="flex flex-col" key={key}>
                    {fields[key] ?? "样例流水号"}
                    <input
                      className={input}
                      value={value}
                      onChange={(e) => setValues({ ...values, [key]: e.target.value })}
                    />
                  </label>
                ))}
              </div>
              <button
                className={button + " mt-3"}
                disabled={busy}
                onClick={() =>
                  void run(async () => {
                    const result = await ls<{ code: string }>("/rules/preview", { spec, values });
                    setPreview(result.code);
                  })
                }
              >
                预览编码
              </button>
              <output className="ml-3 break-all font-mono">{preview}</output>
            </details>
          )}
        </section>
      )}
    </div>
  );
}
