<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'

import { ApiError } from '@/api/client'
import {
  activateLabelCodeRuleVersion,
  createDefaultLabelCodeRuleSpec,
  createLabelCodeRule,
  listLabelCodeRules,
  loadLabelCodeCapabilities,
  previewLabelCodeRule,
  refreshLabelCodeRuleCatalog,
  retireLabelCodeRuleVersion,
  saveLabelCodeRuleDraft,
  type LabelCodeCapabilities,
  type LabelCodeRule,
  type LabelCodeRuleSpec,
  type LabelCodeRuleVersion,
  type LabelCodeSegmentSpec,
  type LabelObjectType,
  type LabelSegmentKind,
} from '@/services/label-code-rules'

interface RuleForm {
  code: string
  name: string
  objectType: LabelObjectType
  priority: number
  isEnabled: boolean
  spec: LabelCodeRuleSpec
}

const router = useRouter()
const rules = ref<LabelCodeRule[]>([])
const selectedRule = ref<LabelCodeRule | null>(null)
const editing = ref(false)
const loading = ref(true)
const saving = ref(false)
const error = ref('')
const notice = ref('')
const previewCode = ref('')
const previewLength = ref(0)
const addKind = ref<LabelSegmentKind>('SEPARATOR')
const sampleValues = ref<Record<string, string>>({
  MATERIAL_CODE: '010203',
  BATCH_NO: '20261012A',
  ISSUED_QUANTITY: '30',
  UNIT_CODE: 'KG',
  MANUFACTURE_DATE: '2026-10-12',
  SOURCE_DOCUMENT_NO: 'TEST001',
  LABEL_TYPE: 'P',
  LABEL_UNIQUE_VALUE: '2609270062',
  SEQUENCE: '1',
})
const capabilities = ref<LabelCodeCapabilities>({
  schemaVersion: 1,
  symbologies: [],
  segmentKinds: [],
  fields: [],
  encodings: [],
  checksumAlgorithms: [],
})
const form = ref<RuleForm>(createForm())

const draftVersion = computed(() =>
  selectedRule.value?.versions.find((version) => version.status === 'DRAFT') ?? null,
)
const activeVersion = computed(() =>
  selectedRule.value?.versions.find((version) => version.status === 'ACTIVE') ?? null,
)
const usedFields = computed(() =>
  [...new Set(form.value.spec.segments
    .filter((segment) => segment.kind === 'FIELD' && segment.field)
    .map((segment) => segment.field!))],
)

const objectTypeLabels: Record<LabelObjectType, string> = {
  PRODUCT_LABEL: '产品标签',
  PALLET: '托盘标签',
  INVENTORY_OBJECT: '产品/托盘通用',
  EXTERNAL: '外部标签',
}
const segmentKindLabels: Record<LabelSegmentKind, string> = {
  LITERAL: '固定内容',
  SEPARATOR: '分隔符',
  FIELD: '业务字段',
  DATE: '日期',
  SEQUENCE: '流水号',
  CHECKSUM: '校验位',
}
const fieldLabels: Record<string, string> = {
  ERP_ACCOUNT_ID: '账套号',
  LABEL_TYPE: '标签类型',
  MATERIAL_CODE: '料号',
  BATCH_NO: '批号',
  ISSUED_QUANTITY: '签发数量',
  UNIT_CODE: '单位',
  MANUFACTURE_DATE: '生产日期',
  SOURCE_DOCUMENT_NO: '来源单号',
  LABEL_UNIQUE_VALUE: '标签唯一值',
}
const symbologyLabels: Record<string, string> = {
  CODE128: 'Code 128',
  CODE39: 'Code 39',
  CODE93: 'Code 93',
  EAN13: 'EAN-13',
  EAN8: 'EAN-8',
  UPCA: 'UPC-A',
  UPCE: 'UPC-E',
  ITF14: 'ITF-14',
  ITF: 'ITF',
  CODABAR: 'Codabar',
  QR: '二维码',
  DATAMATRIX: 'Data Matrix',
  PDF417: 'PDF417',
  AZTEC: 'Aztec',
  GS1_128: 'GS1-128',
  GS1_DATAMATRIX: 'GS1 Data Matrix',
}

function createForm(): RuleForm {
  return {
    code: '',
    name: '',
    objectType: 'PRODUCT_LABEL',
    priority: 100,
    isEnabled: true,
    spec: createDefaultLabelCodeRuleSpec(),
  }
}

function cloneSpec(spec: LabelCodeRuleSpec): LabelCodeRuleSpec {
  return JSON.parse(JSON.stringify(spec)) as LabelCodeRuleSpec
}

function sanitizeSpec(spec: LabelCodeRuleSpec): LabelCodeRuleSpec {
  const result = cloneSpec(spec)
  result.symbologies = [...new Set(result.symbologies)]
  result.segments = result.segments.map((segment) => {
    const clean = { ...segment } as LabelCodeSegmentSpec
    if (!Number.isFinite(Number(clean.length)) || Number(clean.length) <= 0) delete clean.length
    else clean.length = Number(clean.length)
    if (!Number.isFinite(Number(clean.maxLength)) || Number(clean.maxLength) <= 0) delete clean.maxLength
    else clean.maxLength = Number(clean.maxLength)
    if (!clean.expectedValue) delete clean.expectedValue
    if (clean.field !== 'ISSUED_QUANTITY') delete clean.decimalScale
    if (clean.field !== 'ERP_ACCOUNT_ID') delete clean.validateCurrentAccount
    return clean
  })
  return result
}

function openNew(): void {
  selectedRule.value = null
  form.value = createForm()
  previewCode.value = ''
  error.value = ''
  notice.value = ''
  editing.value = true
}

function openRule(rule: LabelCodeRule): void {
  selectedRule.value = rule
  const version = rule.versions.find((item) => item.status === 'DRAFT')
    ?? rule.versions.find((item) => item.status === 'ACTIVE')
    ?? rule.versions[0]
  form.value = {
    code: rule.code,
    name: rule.name,
    objectType: rule.objectType,
    priority: rule.priority,
    isEnabled: rule.isEnabled,
    spec: version ? cloneSpec(version.spec) : createDefaultLabelCodeRuleSpec(),
  }
  previewCode.value = ''
  error.value = ''
  notice.value = ''
  editing.value = true
}

function replaceRule(rule: LabelCodeRule): void {
  const index = rules.value.findIndex((item) => item.id === rule.id)
  if (index >= 0) rules.value.splice(index, 1, rule)
  else rules.value.push(rule)
  rules.value.sort((left, right) => left.priority - right.priority || left.id - right.id)
  openRule(rule)
}

function createSegment(kind: LabelSegmentKind): LabelCodeSegmentSpec {
  switch (kind) {
    case 'LITERAL': return { kind, value: 'LS' }
    case 'SEPARATOR': return { kind, value: '|' }
    case 'FIELD': return { kind, field: 'MATERIAL_CODE', maxLength: 100, encoding: 'NONE' }
    case 'DATE': return { kind, dateFormat: 'yyyyMMdd' }
    case 'SEQUENCE': return { kind, length: 8 }
    case 'CHECKSUM': return { kind, algorithm: 'SHA256_8' }
  }
}

function addSegment(): void {
  form.value.spec.segments.push(createSegment(addKind.value))
}

function changeSegmentKind(segment: LabelCodeSegmentSpec, kind: LabelSegmentKind): void {
  Object.keys(segment).forEach((key) => delete (segment as Record<string, unknown>)[key])
  Object.assign(segment, createSegment(kind))
}

function moveSegment(index: number, offset: number): void {
  const target = index + offset
  if (target < 0 || target >= form.value.spec.segments.length) return
  const [segment] = form.value.spec.segments.splice(index, 1)
  form.value.spec.segments.splice(target, 0, segment)
}

function toggleSymbology(symbology: string): void {
  const values = form.value.spec.symbologies
  const index = values.indexOf(symbology)
  if (index >= 0) values.splice(index, 1)
  else values.push(symbology)
}

async function preview(): Promise<void> {
  error.value = ''
  notice.value = ''
  try {
    const result = await previewLabelCodeRule(sanitizeSpec(form.value.spec), sampleValues.value)
    previewCode.value = result.code
    previewLength.value = result.length
  } catch (reason) {
    error.value = reason instanceof ApiError ? reason.message : '预览失败'
  }
}

async function save(): Promise<void> {
  if (saving.value) return
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    const spec = sanitizeSpec(form.value.spec)
    const result = selectedRule.value
      ? await saveLabelCodeRuleDraft(selectedRule.value, {
          name: form.value.name,
          priority: Number(form.value.priority),
          isEnabled: form.value.isEnabled,
          spec,
          expectedVersionRowVersion: draftVersion.value?.rowVersion ?? null,
        })
      : await createLabelCodeRule({
          code: form.value.code,
          name: form.value.name,
          objectType: form.value.objectType,
          priority: Number(form.value.priority),
          spec,
        })
    replaceRule(result)
    notice.value = '已保存'
  } catch (reason) {
    error.value = reason instanceof ApiError ? reason.message : '保存失败'
  } finally {
    saving.value = false
  }
}

async function activate(): Promise<void> {
  if (!selectedRule.value || !draftVersion.value || saving.value) return
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    const result = await activateLabelCodeRuleVersion(selectedRule.value, draftVersion.value)
    replaceRule(result)
    await refreshLabelCodeRuleCatalog()
    notice.value = '已启用'
  } catch (reason) {
    error.value = reason instanceof ApiError ? reason.message : '启用失败'
  } finally {
    saving.value = false
  }
}

async function retire(): Promise<void> {
  if (!selectedRule.value || !activeVersion.value || saving.value) return
  if (!window.confirm('停止生成后，历史标签仍可解析。是否继续？')) return
  saving.value = true
  error.value = ''
  notice.value = ''
  try {
    const result = await retireLabelCodeRuleVersion(selectedRule.value, activeVersion.value)
    replaceRule(result)
    await refreshLabelCodeRuleCatalog()
    notice.value = '已停止生成'
  } catch (reason) {
    error.value = reason instanceof ApiError ? reason.message : '操作失败'
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    const [ruleItems, ruleCapabilities] = await Promise.all([
      listLabelCodeRules(),
      loadLabelCodeCapabilities(),
    ])
    rules.value = ruleItems
    capabilities.value = ruleCapabilities
  } catch (reason) {
    error.value = reason instanceof ApiError ? reason.message : '读取失败'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <main class="rules-page">
    <header class="page-header-shell">
      <div class="page-header">
        <button class="back-button" type="button" aria-label="返回" @click="editing ? editing = false : router.back()">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>{{ editing ? (selectedRule ? selectedRule.name : '新建规则') : '标签编码规则' }}</h1>
        <button v-if="!editing" class="header-action" type="button" @click="openNew">新建</button>
      </div>
    </header>

    <div v-if="!editing" class="rule-list">
      <p v-if="loading" class="empty">加载中</p>
      <p v-else-if="!rules.length" class="empty">暂无规则</p>
      <button v-for="rule in rules" :key="rule.id" class="rule-card" type="button" @click="openRule(rule)">
        <span>
          <strong>{{ rule.name }}</strong>
          <small>{{ rule.code }} · {{ objectTypeLabels[rule.objectType] }}</small>
        </span>
        <span class="rule-state" :class="{ active: rule.versions.some((item) => item.status === 'ACTIVE') }">
          {{ rule.versions.some((item) => item.status === 'ACTIVE') ? '使用中' : '未启用' }}
        </span>
      </button>
      <p v-if="error" class="message error">{{ error }}</p>
    </div>

    <div v-else class="editor">
      <section class="card basic-grid">
        <label>规则编号<input v-model.trim="form.code" :disabled="!!selectedRule" maxlength="50" /></label>
        <label>规则名称<input v-model.trim="form.name" maxlength="100" /></label>
        <label>标签类型
          <select v-model="form.objectType" :disabled="!!selectedRule">
            <option v-for="(label, value) in objectTypeLabels" :key="value" :value="value">{{ label }}</option>
          </select>
        </label>
        <label>优先级<input v-model.number="form.priority" type="number" min="0" max="10000" /></label>
        <label v-if="selectedRule" class="switch-row">
          <input v-model="form.isEnabled" type="checkbox" />允许解析
        </label>
        <label>字母处理
          <select v-model="form.spec.caseMode">
            <option value="PRESERVE">保持原样</option>
            <option value="UPPER">转大写</option>
            <option value="LOWER">转小写</option>
          </select>
        </label>
      </section>

      <section class="card">
        <h2>条码类型</h2>
        <div class="chip-grid">
          <button
            v-for="symbology in capabilities.symbologies"
            :key="symbology"
            type="button"
            :class="{ selected: form.spec.symbologies.includes(symbology) }"
            @click="toggleSymbology(symbology)"
          >{{ symbologyLabels[symbology] ?? symbology }}</button>
        </div>
      </section>

      <section class="card">
        <h2>编码内容</h2>
        <div class="segments">
          <div v-for="(segment, index) in form.spec.segments" :key="index" class="segment-row">
            <div class="segment-head">
              <select :value="segment.kind" @change="changeSegmentKind(segment, ($event.target as HTMLSelectElement).value as LabelSegmentKind)">
                <option v-for="(label, value) in segmentKindLabels" :key="value" :value="value">{{ label }}</option>
              </select>
              <span class="segment-actions">
                <button type="button" :disabled="index === 0" @click="moveSegment(index, -1)">↑</button>
                <button type="button" :disabled="index === form.spec.segments.length - 1" @click="moveSegment(index, 1)">↓</button>
                <button type="button" @click="form.spec.segments.splice(index, 1)">删除</button>
              </span>
            </div>

            <input v-if="segment.kind === 'LITERAL'" v-model="segment.value" maxlength="64" placeholder="固定内容" />
            <input v-else-if="segment.kind === 'SEPARATOR'" v-model="segment.value" maxlength="8" placeholder="分隔符" />
            <template v-else-if="segment.kind === 'FIELD'">
              <div class="segment-fields">
                <select v-model="segment.field">
                  <option v-for="field in capabilities.fields" :key="field" :value="field">{{ fieldLabels[field] ?? field }}</option>
                </select>
                <select v-model="segment.encoding">
                  <option value="NONE">原值</option>
                  <option value="PERCENT">转义</option>
                </select>
                <label>最大长度<input v-model.number="segment.maxLength" type="number" min="1" max="200" /></label>
                <label>固定长度<input v-model.number="segment.length" type="number" min="1" max="200" placeholder="可选" /></label>
                <label>限定值<input v-model.trim="segment.expectedValue" maxlength="200" placeholder="可选" /></label>
                <label v-if="segment.field === 'ISSUED_QUANTITY'">小数位<input v-model.number="segment.decimalScale" type="number" min="0" max="6" placeholder="可选" /></label>
              </div>
              <label v-if="segment.field === 'ERP_ACCOUNT_ID'" class="switch-row">
                <input v-model="segment.validateCurrentAccount" type="checkbox" />校验当前账套
              </label>
            </template>
            <select v-else-if="segment.kind === 'DATE'" v-model="segment.dateFormat">
              <option value="yyyyMMdd">yyyyMMdd</option>
              <option value="yyMMdd">yyMMdd</option>
              <option value="yyyyMMddHHmmss">yyyyMMddHHmmss</option>
            </select>
            <label v-else-if="segment.kind === 'SEQUENCE'">长度<input v-model.number="segment.length" type="number" min="1" max="18" /></label>
            <select v-else-if="segment.kind === 'CHECKSUM'" v-model="segment.algorithm">
              <option value="SHA256_8">8位校验</option>
              <option value="EAN_MOD10">EAN 校验位</option>
            </select>
          </div>
        </div>
        <div class="add-row">
          <select v-model="addKind">
            <option v-for="(label, value) in segmentKindLabels" :key="value" :value="value">{{ label }}</option>
          </select>
          <button type="button" @click="addSegment">添加</button>
        </div>
      </section>

      <section class="card">
        <h2>预览</h2>
        <div v-if="usedFields.length" class="sample-grid">
          <label v-for="field in usedFields" :key="field">
            {{ fieldLabels[field] ?? field }}
            <input v-model="sampleValues[field]" :placeholder="field === 'ERP_ACCOUNT_ID' ? '可留空' : ''" />
          </label>
        </div>
        <label v-if="form.spec.segments.some((segment) => segment.kind === 'SEQUENCE')">
          流水号<input v-model="sampleValues.SEQUENCE" inputmode="numeric" />
        </label>
        <button class="secondary-action" type="button" @click="preview">生成预览</button>
        <div v-if="previewCode" class="preview-result">
          <code>{{ previewCode }}</code><span>{{ previewLength }} 字符</span>
        </div>
      </section>

      <p v-if="error" class="message error">{{ error }}</p>
      <p v-if="notice" class="message success">{{ notice }}</p>

      <div class="footer-actions">
        <button class="primary-action" type="button" :disabled="saving" @click="save">保存草稿</button>
        <button v-if="draftVersion" class="activate-action" type="button" :disabled="saving" @click="activate">启用版本</button>
        <button v-if="activeVersion" class="secondary-action" type="button" :disabled="saving" @click="retire">停止生成</button>
      </div>

      <section v-if="selectedRule" class="version-list">
        <span v-for="version in selectedRule.versions" :key="version.id">
          V{{ version.version }} · {{ version.status === 'ACTIVE' ? '使用中' : version.status === 'DRAFT' ? '草稿' : version.status === 'RETIRED' ? '历史' : '停用' }}
        </span>
      </section>
    </div>
  </main>
</template>

<style scoped>
.rules-page {
  min-height: 100dvh;
  background: #f3f6fa;
  color: #24344d;
}

.page-header-shell {
  position: sticky;
  z-index: 10;
  top: 0;
  height: var(--app-header-height);
  border-bottom: 1px solid #e5ebf2;
  background: #fff;
}

.page-header {
  width: min(100%, 760px);
  height: 100%;
  display: grid;
  grid-template-columns: 44px 1fr auto;
  align-items: center;
  gap: 12px;
  margin-inline: auto;
  padding: var(--app-safe-top) 16px 0;
}

h1 {
  overflow: hidden;
  color: #17365d;
  font-size: 23px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.back-button {
  width: 42px;
  height: 42px;
  display: grid;
  place-items: center;
  border: 0;
  border-radius: 11px;
  background: #f1f5f9;
  color: #365678;
}

.back-button svg {
  width: 22px;
  height: 22px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
}

.header-action,
.secondary-action,
.activate-action,
.primary-action,
.add-row button {
  min-height: 40px;
  padding: 0 14px;
  border: 0;
  border-radius: 10px;
  font-weight: 700;
}

.header-action,
.primary-action {
  background: #2368c4;
  color: #fff;
}

.activate-action {
  background: #16805c;
  color: #fff;
}

.secondary-action,
.add-row button {
  background: #e9f2fd;
  color: #2368c4;
}

.rule-list,
.editor {
  width: min(100%, 760px);
  display: grid;
  gap: 12px;
  margin-inline: auto;
  padding: 16px 16px 34px;
}

.rule-card {
  width: 100%;
  min-height: 76px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 14px 16px;
  border: 1px solid #e1e8f0;
  border-radius: 15px;
  background: #fff;
  color: inherit;
  text-align: left;
}

.rule-card span:first-child {
  min-width: 0;
  display: grid;
  gap: 5px;
}

.rule-card strong {
  overflow: hidden;
  font-size: 16px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.rule-card small {
  color: #7b899b;
}

.rule-state {
  flex: 0 0 auto;
  padding: 5px 8px;
  border-radius: 8px;
  background: #eef1f5;
  color: #7a8796;
  font-size: 12px;
}

.rule-state.active {
  background: #e5f5ee;
  color: #16805c;
}

.card {
  display: grid;
  gap: 13px;
  padding: 16px;
  border: 1px solid #e1e8f0;
  border-radius: 16px;
  background: #fff;
}

.card h2 {
  font-size: 16px;
}

.basic-grid,
.sample-grid,
.segment-fields {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}

label {
  display: grid;
  gap: 6px;
  color: #607086;
  font-size: 13px;
}

input,
select {
  width: 100%;
  min-height: 42px;
  padding: 0 11px;
  border: 1px solid #ced8e4;
  border-radius: 9px;
  background: #fff;
  color: #27384f;
  font: inherit;
}

input:disabled,
select:disabled {
  background: #f2f4f7;
  color: #8793a2;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 42px;
}

.switch-row input {
  width: 20px;
  min-height: 20px;
}

.chip-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.chip-grid button {
  min-height: 36px;
  padding: 0 11px;
  border: 1px solid #d4deea;
  border-radius: 9px;
  background: #fff;
  color: #607086;
}

.chip-grid button.selected {
  border-color: #8bb4e9;
  background: #e9f2fd;
  color: #2368c4;
  font-weight: 700;
}

.segments {
  display: grid;
  gap: 10px;
}

.segment-row {
  display: grid;
  gap: 9px;
  padding: 12px;
  border-radius: 12px;
  background: #f6f8fb;
}

.segment-head,
.add-row,
.footer-actions,
.preview-result,
.version-list {
  display: flex;
  align-items: center;
  gap: 8px;
}

.segment-head > select {
  flex: 1;
}

.segment-actions {
  display: flex;
  gap: 5px;
}

.segment-actions button {
  min-width: 34px;
  min-height: 34px;
  padding: 0 8px;
  border: 0;
  border-radius: 8px;
  background: #e9eef4;
  color: #52657b;
}

.segment-actions button:last-child {
  color: #a14343;
}

.add-row select {
  flex: 1;
}

.preview-result {
  justify-content: space-between;
  overflow: hidden;
  padding: 12px;
  border-radius: 10px;
  background: #17283f;
  color: #fff;
}

.preview-result code {
  overflow-wrap: anywhere;
}

.preview-result span {
  flex: 0 0 auto;
  color: #b8c5d5;
  font-size: 12px;
}

.footer-actions {
  flex-wrap: wrap;
}

.message,
.empty {
  margin: 0;
  padding: 11px 13px;
  border-radius: 10px;
  text-align: center;
}

.empty {
  color: #7b899b;
}

.message.error {
  background: #fceaea;
  color: #a13d3d;
}

.message.success {
  background: #e7f5ee;
  color: #167657;
}

.version-list {
  flex-wrap: wrap;
}

.version-list span {
  padding: 6px 9px;
  border-radius: 8px;
  background: #e9eef4;
  color: #52657b;
  font-size: 12px;
}

button:disabled {
  opacity: 0.55;
}

@media (max-width: 520px) {
  .basic-grid,
  .sample-grid,
  .segment-fields {
    grid-template-columns: 1fr;
  }

  .page-header,
  .rule-list,
  .editor {
    padding-right: 12px;
    padding-left: 12px;
  }
}
</style>
