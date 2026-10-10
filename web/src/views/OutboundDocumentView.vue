<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import { getPdaBridge, isMockPdaBridge } from '@/bridge/pda-bridge'
import type { PdaScanEvent } from '@/bridge/types'
import { getBusinessDocumentConfig } from '@/services/business-document-config'
import {
  clearOutboundDocumentWorkingState,
  consumeOutboundDocumentEditRequest,
  consumeOutboundDocumentWorkingState,
  getOutboundDocument,
  migrateLegacyOutboundDraft,
  saveOutboundDocument,
  saveOutboundDocumentWorkingState,
  type OutboundDocumentSummary,
} from '@/services/outbound-document-drafts'
import {
  consumeSourceDocumentSelection,
  type OutboundDocumentType,
} from '@/services/source-document-selection'

type LineObjectType = 'PRODUCT_LABEL' | 'PALLET'

interface Props {
  documentType: OutboundDocumentType
}

interface OutboundLine {
  id: string
  labelCode: string
  objectType: LineObjectType
  materialCode: string
  materialName: string
  batchNo: string
  warehouseCode: string
  locationCode: string
  availableQty: number | null
  requestedQty: number
}

interface OutboundDraft {
  documentDate: string
  sourceDocumentType: string
  sourceDocumentTypeLabel: string
  sourceDocumentNo: string
  customerCode: string
  warehouseCode: string
  targetWarehouseCode: string
  sourceLocationCode: string
  targetLocationCode: string
  departmentCode: string
  outboundCategoryCode: string
  productionBatch: string
  remark: string
  lines: OutboundLine[]
}

const props = defineProps<Props>()
const route = useRoute()
const router = useRouter()
const bridge = getPdaBridge()

const draftId = ref<string | null>(null)
const currentStep = ref<1 | 2>(1)
const documentDate = ref(beijingDate())
const sourceDocumentType = ref('')
const sourceDocumentTypeLabel = ref('')
const sourceDocumentNo = ref('')
const customerCode = ref('')
const warehouseCode = ref('')
const targetWarehouseCode = ref('')
const sourceLocationCode = ref('')
const targetLocationCode = ref('')
const departmentCode = ref('')
const outboundCategoryCode = ref('')
const productionBatch = ref('')
const remark = ref('')
const scanCode = ref('')
const lines = ref<OutboundLine[]>([])
const message = ref('')
const messageTone = ref<'success' | 'error' | 'info'>('info')
const showLeaveConfirm = ref(false)
let allowNavigation = false
let baselineSnapshot = ''
let stopScanListener: (() => void) | null = null
let messageTimer: ReturnType<typeof setTimeout> | null = null

const pageConfig = computed(() => getBusinessDocumentConfig(props.documentType))

const totalQty = computed(() =>
  lines.value.reduce((total, line) => total + normalizeQty(line.requestedQty), 0),
)
const showHeaderForm = computed(() => currentStep.value === 1)
const showScanContent = computed(() => currentStep.value === 2)
const returnRouteName = computed(() =>
  route.query.origin === 'all-functions' ? 'all-functions' : 'home',
)

function beijingDate(): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(new Date())
  const value = Object.fromEntries(parts.map((part) => [part.type, part.value]))
  return `${value.year}-${value.month}-${value.day}`
}

function normalizeQty(value: number): number {
  const normalized = Number(value)
  return Number.isFinite(normalized) && normalized > 0 ? normalized : 0
}

function isPalletCode(barcode: string): boolean {
  return /^(TP|PALLET|托盘)[-_]?/i.test(barcode)
}

function createLineId(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  return `line-${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function notify(text: string, tone: 'success' | 'error' | 'info' = 'info'): void {
  message.value = text
  messageTone.value = tone
  if (messageTimer) clearTimeout(messageTimer)
  messageTimer = setTimeout(() => {
    message.value = ''
  }, 2400)
}

function addBarcode(rawCode: string): void {
  const barcode = rawCode.trim()
  if (!barcode) return

  if (lines.value.some((line) => line.labelCode === barcode)) {
    notify('标签已添加', 'error')
    scanCode.value = ''
    return
  }

  lines.value.unshift({
    id: createLineId(),
    labelCode: barcode,
    objectType: isPalletCode(barcode) ? 'PALLET' : 'PRODUCT_LABEL',
    materialCode: '',
    materialName: '',
    batchNo: '',
    warehouseCode: '',
    locationCode: '',
    availableQty: null,
    requestedQty: 1,
  })
  scanCode.value = ''
  notify('已添加', 'success')
}

function handleScan(event: PdaScanEvent): void {
  if (currentStep.value !== 2) return
  addBarcode(event.barcode)
}

function simulateOrAdd(): void {
  if (!scanCode.value.trim()) return
  if (isMockPdaBridge(bridge)) {
    bridge.simulateScan(scanCode.value)
    return
  }
  addBarcode(scanCode.value)
}

function removeLine(id: string): void {
  lines.value = lines.value.filter((line) => line.id !== id)
}

async function openSourceDocumentSelection(): Promise<void> {
  saveWorkingState()
  allowNavigation = true
  await router.replace({
    name: 'source-document-select',
    params: { code: props.documentType },
    query: route.query,
  })
}

function clearSourceDocument(): void {
  sourceDocumentType.value = ''
  sourceDocumentTypeLabel.value = ''
  sourceDocumentNo.value = ''
}

function createDraft(): OutboundDraft {
  return {
    documentDate: documentDate.value,
    sourceDocumentType: sourceDocumentType.value,
    sourceDocumentTypeLabel: sourceDocumentTypeLabel.value,
    sourceDocumentNo: sourceDocumentNo.value,
    customerCode: customerCode.value.trim(),
    warehouseCode: warehouseCode.value.trim(),
    targetWarehouseCode: targetWarehouseCode.value.trim(),
    sourceLocationCode: sourceLocationCode.value.trim(),
    targetLocationCode: targetLocationCode.value.trim(),
    departmentCode: departmentCode.value.trim(),
    outboundCategoryCode: outboundCategoryCode.value.trim(),
    productionBatch: productionBatch.value.trim(),
    remark: remark.value.trim(),
    lines: lines.value.map((line) => ({ ...line, requestedQty: normalizeQty(line.requestedQty) })),
  }
}

function snapshotDraft(): string {
  return JSON.stringify(createDraft())
}

function hasUnsavedChanges(): boolean {
  return snapshotDraft() !== baselineSnapshot
}

function applyDraft(draft: Partial<OutboundDraft>): void {
  currentStep.value = 1
  documentDate.value = draft.documentDate || beijingDate()
  sourceDocumentType.value = draft.sourceDocumentType || ''
  sourceDocumentTypeLabel.value = draft.sourceDocumentTypeLabel || ''
  sourceDocumentNo.value = draft.sourceDocumentNo || ''
  customerCode.value = draft.customerCode || ''
  warehouseCode.value = draft.warehouseCode || ''
  targetWarehouseCode.value = draft.targetWarehouseCode || ''
  sourceLocationCode.value = draft.sourceLocationCode || ''
  targetLocationCode.value = draft.targetLocationCode || ''
  departmentCode.value = draft.departmentCode || ''
  outboundCategoryCode.value = draft.outboundCategoryCode || ''
  productionBatch.value = draft.productionBatch || ''
  remark.value = draft.remark || ''
  lines.value = Array.isArray(draft.lines) ? draft.lines : []
}

function createSummary(payload: unknown): OutboundDocumentSummary {
  const draft = (payload && typeof payload === 'object' ? payload : {}) as Partial<OutboundDraft>
  const draftLines = Array.isArray(draft.lines) ? draft.lines : []
  return {
    documentDate: draft.documentDate || beijingDate(),
    sourceTypeLabel: draft.sourceDocumentTypeLabel || '',
    sourceDocumentNo: draft.sourceDocumentNo || '',
    lineCount: draftLines.length,
    totalQty: draftLines.reduce(
      (total, line) => total + normalizeQty(line.requestedQty),
      0,
    ),
  }
}

function loadRequestedDraft(): void {
  migrateLegacyOutboundDraft(props.documentType, pageConfig.value.draftKey, createSummary)
  const request = consumeOutboundDocumentEditRequest(props.documentType)
  if (request) {
    clearOutboundDocumentWorkingState()
    const stored = getOutboundDocument(request.documentId)
    if (!stored || stored.documentType !== props.documentType) return
    draftId.value = stored.id
    applyDraft(stored.payload as Partial<OutboundDraft>)
    baselineSnapshot = snapshotDraft()
    return
  }

  const workingState = consumeOutboundDocumentWorkingState(props.documentType)
  if (!workingState) return
  draftId.value = workingState.documentId
  applyDraft(workingState.payload as Partial<OutboundDraft>)
  currentStep.value = workingState.currentStep
  baselineSnapshot = workingState.baselineSnapshot || snapshotDraft()
}

function saveWorkingState(): void {
  saveOutboundDocumentWorkingState({
    documentType: props.documentType,
    documentId: draftId.value,
    currentStep: currentStep.value,
    payload: createDraft(),
    baselineSnapshot,
  })
}

function saveCompletedDocument(): boolean {
  const error = validate()
  if (error) {
    notify(error, 'error')
    return false
  }

  const payload = createDraft()
  const stored = saveOutboundDocument({
    id: draftId.value || undefined,
    documentType: props.documentType,
    summary: createSummary(payload),
    payload,
  })
  draftId.value = stored.id
  baselineSnapshot = snapshotDraft()
  clearOutboundDocumentWorkingState()
  return true
}

function hasDraftContent(): boolean {
  return Boolean(
    sourceDocumentNo.value ||
      customerCode.value.trim() ||
      warehouseCode.value.trim() ||
      targetWarehouseCode.value.trim() ||
      sourceLocationCode.value.trim() ||
      targetLocationCode.value.trim() ||
      departmentCode.value.trim() ||
      outboundCategoryCode.value.trim() ||
      productionBatch.value.trim() ||
      remark.value.trim() ||
      lines.value.length,
  )
}

async function openDocumentManager(): Promise<void> {
  saveWorkingState()
  allowNavigation = true
  await router.replace({
    name: 'outbound-document-manager',
    params: { code: props.documentType },
    query: route.query,
  })
}

async function openSwitchSettings(): Promise<void> {
  saveWorkingState()
  allowNavigation = true
  await router.replace({
    name: 'business-document-settings',
    params: { code: props.documentType },
    query: route.query,
  })
}

function validateHeader(): string {
  if (!documentDate.value) return '请选择日期'
  if (pageConfig.value.partnerRequired && !customerCode.value.trim()) {
    return `请输入${pageConfig.value.partnerLabel}`
  }
  if (pageConfig.value.warehouseRequired && !warehouseCode.value.trim()) {
    return `请输入${pageConfig.value.warehouseLabel}`
  }
  if (pageConfig.value.targetWarehouseRequired && !targetWarehouseCode.value.trim()) {
    return `请输入${pageConfig.value.targetWarehouseLabel}`
  }
  if (pageConfig.value.sourceLocationRequired && !sourceLocationCode.value.trim()) {
    return `请输入${pageConfig.value.sourceLocationLabel}`
  }
  if (pageConfig.value.targetLocationRequired && !targetLocationCode.value.trim()) {
    return `请输入${pageConfig.value.targetLocationLabel}`
  }
  if (pageConfig.value.departmentRequired && !departmentCode.value.trim()) return '请输入部门'
  if (pageConfig.value.categoryRequired && !outboundCategoryCode.value.trim()) {
    return `请输入${pageConfig.value.categoryLabel}`
  }
  return ''
}

function validate(): string {
  const headerError = validateHeader()
  if (headerError) return headerError
  if (!lines.value.length) return '请扫描标签或托盘'
  if (lines.value.some((line) => normalizeQty(line.requestedQty) <= 0)) {
    return `${pageConfig.value.quantityLabel}必须大于0`
  }
  return ''
}

function goToScanStep(): void {
  const error = validateHeader()
  if (error) {
    notify(error, 'error')
    return
  }

  currentStep.value = 2
  window.scrollTo({ top: 0, behavior: 'smooth' })
}

function resetDocument(): void {
  if (!window.confirm('清空当前内容？')) return
  draftId.value = null
  applyDraft({})
  baselineSnapshot = snapshotDraft()
  clearOutboundDocumentWorkingState()
}

async function finishDocument(): Promise<void> {
  if (!saveCompletedDocument()) return
  allowNavigation = true
  await router.replace({
    name: 'outbound-document-manager',
    params: { code: props.documentType },
    query: route.query,
  })
}

async function finishAndLeave(): Promise<void> {
  if (!saveCompletedDocument()) {
    showLeaveConfirm.value = false
    return
  }
  showLeaveConfirm.value = false
  allowNavigation = true
  await router.replace({ name: returnRouteName.value })
}

async function discardAndLeave(): Promise<void> {
  showLeaveConfirm.value = false
  clearOutboundDocumentWorkingState()
  allowNavigation = true
  await router.replace({ name: returnRouteName.value })
}

async function leaveDocument(): Promise<void> {
  await router.replace({ name: returnRouteName.value })
}

watch(
  () => props.documentType,
  () => {
    draftId.value = null
    applyDraft({})
    baselineSnapshot = snapshotDraft()
    loadRequestedDraft()
  },
)

onMounted(() => {
  baselineSnapshot = snapshotDraft()
  loadRequestedDraft()
  const sourceSelection = consumeSourceDocumentSelection(props.documentType)
  if (sourceSelection) {
    sourceDocumentType.value = sourceSelection.sourceType
    sourceDocumentTypeLabel.value = sourceSelection.sourceTypeLabel
    sourceDocumentNo.value = sourceSelection.documentNo
  }
  stopScanListener = bridge.subscribeScan(handleScan)
})

onBeforeUnmount(() => {
  stopScanListener?.()
  if (messageTimer) clearTimeout(messageTimer)
})

onBeforeRouteLeave((to) => {
  if (allowNavigation) {
    allowNavigation = false
    return true
  }
  if (lines.value.length && hasUnsavedChanges()) {
    showLeaveConfirm.value = true
    return false
  }
  if (to.name !== returnRouteName.value) {
    return { name: returnRouteName.value, replace: true }
  }
  return true
})
</script>

<template>
  <main class="outbound-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button class="back-button" type="button" aria-label="返回" @click="leaveDocument">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>{{ pageConfig.title }}</h1>
        <button
          v-if="currentStep === 2"
          class="switch-settings-button"
          type="button"
          @click="openSwitchSettings"
        >
          开关设置
        </button>
        <span v-else aria-hidden="true"></span>
      </div>
    </header>

    <div class="page-content">
      <section v-if="showHeaderForm" class="card document-card">
        <div class="form-grid">
          <div v-if="pageConfig.sourceTypes.length" class="field full-width">
            <span>源单</span>
            <div class="source-document-row">
              <button class="source-document-button" type="button" @click="openSourceDocumentSelection">
                <span v-if="sourceDocumentNo" class="selected-source">
                  <strong>{{ sourceDocumentNo }}</strong>
                  <small>{{ sourceDocumentTypeLabel }}</small>
                </span>
                <span v-else class="source-placeholder">请选择</span>
                <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9 6 6 6-6 6" /></svg>
              </button>
              <button
                v-if="sourceDocumentNo"
                class="clear-source-button"
                type="button"
                aria-label="清除源单"
                @click="clearSourceDocument"
              >
                ×
              </button>
            </div>
          </div>

          <label class="field">
            <span><i>*</i>日期</span>
            <input v-model="documentDate" type="date" />
          </label>

          <label v-if="pageConfig.partnerLabel" class="field">
            <span><i v-if="pageConfig.partnerRequired">*</i>{{ pageConfig.partnerLabel }}</span>
            <input v-model="customerCode" autocomplete="off" placeholder="编码或名称" />
          </label>

          <label v-if="pageConfig.warehouseLabel" class="field">
            <span><i v-if="pageConfig.warehouseRequired">*</i>{{ pageConfig.warehouseLabel }}</span>
            <input v-model="warehouseCode" autocomplete="off" placeholder="编码或名称" />
          </label>

          <label v-if="pageConfig.targetWarehouseLabel" class="field">
            <span><i v-if="pageConfig.targetWarehouseRequired">*</i>{{ pageConfig.targetWarehouseLabel }}</span>
            <input v-model="targetWarehouseCode" autocomplete="off" placeholder="编码或名称" />
          </label>

          <label v-if="pageConfig.sourceLocationLabel" class="field">
            <span><i v-if="pageConfig.sourceLocationRequired">*</i>{{ pageConfig.sourceLocationLabel }}</span>
            <input v-model="sourceLocationCode" autocomplete="off" placeholder="编码或名称" />
          </label>

          <label v-if="pageConfig.targetLocationLabel" class="field">
            <span><i v-if="pageConfig.targetLocationRequired">*</i>{{ pageConfig.targetLocationLabel }}</span>
            <input v-model="targetLocationCode" autocomplete="off" placeholder="编码或名称" />
          </label>

          <label v-if="pageConfig.departmentRequired" class="field">
            <span><i>*</i>部门</span>
            <input v-model="departmentCode" autocomplete="off" placeholder="编码或名称" />
          </label>

          <label v-if="pageConfig.categoryLabel" class="field">
            <span><i v-if="pageConfig.categoryRequired">*</i>{{ pageConfig.categoryLabel }}</span>
            <input
              v-model="outboundCategoryCode"
              autocomplete="off"
              placeholder="编码或名称"
            />
          </label>

          <label v-if="pageConfig.productionBatch" class="field">
            <span>生产批号</span>
            <input v-model="productionBatch" autocomplete="off" placeholder="选填" />
          </label>

          <label class="field full-width">
            <span>备注</span>
            <input v-model="remark" autocomplete="off" placeholder="选填" />
          </label>
        </div>
      </section>

      <section v-if="showScanContent" class="card scan-card">
        <div class="scan-title">
          <span class="scan-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24">
              <path d="M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3M7 12h10" />
            </svg>
          </span>
          <strong>扫描标签</strong>
        </div>
        <div class="scan-input-row">
          <input
            v-model="scanCode"
            autocomplete="off"
            enterkeyhint="done"
            placeholder="标签码或托盘码"
            @keyup.enter="simulateOrAdd"
          />
          <button type="button" @click="simulateOrAdd">添加</button>
        </div>
      </section>

      <section v-if="showScanContent" class="line-section">
        <div class="section-title">
          <h2>{{ pageConfig.detailTitle }}</h2>
          <span>{{ lines.length }}项</span>
        </div>

        <div v-if="lines.length" class="line-list">
          <article v-for="line in lines" :key="line.id" class="line-card">
            <div class="line-heading">
              <div class="code-wrap">
                <span class="object-badge" :class="{ pallet: line.objectType === 'PALLET' }">
                  {{ line.objectType === 'PALLET' ? '托盘' : '标签' }}
                </span>
                <strong>{{ line.labelCode }}</strong>
              </div>
              <button type="button" aria-label="删除" @click="removeLine(line.id)">
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path d="M5 7h14M9 7V4h6v3M8 10v8M12 10v8M16 10v8M7 7l1 14h8l1-14" />
                </svg>
              </button>
            </div>

            <dl class="line-info">
              <div><dt>料号</dt><dd>{{ line.materialCode || '--' }}</dd></div>
              <div><dt>批号</dt><dd>{{ line.batchNo || '--' }}</dd></div>
              <div><dt>仓库</dt><dd>{{ line.warehouseCode || '--' }}</dd></div>
              <div><dt>货位</dt><dd>{{ line.locationCode || '--' }}</dd></div>
              <div><dt>可用量</dt><dd>{{ line.availableQty ?? '--' }}</dd></div>
            </dl>

            <label class="qty-field">
              <span>{{ pageConfig.quantityLabel }}</span>
              <input v-model.number="line.requestedQty" type="number" inputmode="decimal" min="0.000001" step="0.000001" />
            </label>
          </article>
        </div>

        <div v-else class="empty-lines">暂无明细</div>
      </section>
    </div>

    <footer
      class="action-shell"
      :class="{
        'header-actions': currentStep === 1,
        'with-reset': currentStep === 1 && hasDraftContent(),
        'scan-actions': currentStep === 2,
      }"
    >
      <div class="summary">
        <template v-if="currentStep === 2">
          <span>{{ lines.length }}项</span>
          <strong>
          {{ totalQty.toFixed(6).replace(/\.?0+$/, '') }}
          </strong>
        </template>
      </div>
      <button
        v-if="currentStep === 1"
        class="secondary-action"
        type="button"
        @click="openDocumentManager"
      >
        单据管理
      </button>
      <button
        v-if="currentStep === 1 && hasDraftContent()"
        class="reset-action"
        type="button"
        @click="resetDocument"
      >
        重置
      </button>
      <button
        class="primary-action"
        type="button"
        @click="currentStep === 1 ? goToScanStep() : finishDocument()"
      >
        {{ currentStep === 1 ? '下一步' : '完成做单' }}
      </button>
    </footer>

    <div v-if="showLeaveConfirm" class="confirm-mask">
      <section class="confirm-dialog" role="dialog" aria-modal="true" aria-label="完成做单">
        <h2>是否完成做单？</h2>
        <div class="confirm-actions">
          <button type="button" @click="showLeaveConfirm = false">继续操作</button>
          <button class="discard" type="button" @click="discardAndLeave">放弃返回</button>
          <button class="finish" type="button" @click="finishAndLeave">完成做单</button>
        </div>
      </section>
    </div>

    <Transition name="toast">
      <div v-if="message" class="toast-message" :class="messageTone" role="status">{{ message }}</div>
    </Transition>
  </main>
</template>

<style scoped>
.outbound-page {
  min-height: 100dvh;
  padding: 72px 0 94px;
  background: #f3f6fa;
  color: #21324a;
}

.topbar-shell {
  position: fixed;
  z-index: 20;
  top: 0;
  right: 0;
  left: 0;
  height: 72px;
  border-bottom: 1px solid #e4ebf3;
  background: #fff;
  box-shadow: 0 3px 12px rgba(23, 54, 93, 0.05);
}

.topbar,
.page-content,
.action-shell {
  width: min(100%, 760px);
  margin-inline: auto;
}

.topbar {
  height: 100%;
  display: grid;
  grid-template-columns: 44px minmax(0, 1fr) auto;
  align-items: center;
  gap: 12px;
  padding: 0 18px;
}

.back-button {
  width: 42px;
  height: 42px;
  display: grid;
  place-items: center;
  border: 0;
  border-radius: 12px;
  background: #f3f6fa;
  color: #365678;
}

.back-button svg,
.scan-icon svg,
.line-heading button svg {
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 2;
}

.back-button svg {
  width: 23px;
  height: 23px;
}

h1 {
  overflow: hidden;
  color: #17365d;
  font-size: 22px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.switch-settings-button {
  height: 38px;
  padding: 0 12px;
  border: 0;
  border-radius: 10px;
  background: #edf4fd;
  color: #3269aa;
  font-size: 13px;
  font-weight: 700;
  white-space: nowrap;
}

.page-content {
  display: grid;
  gap: 14px;
  padding: 16px 16px 28px;
}

.card,
.line-card {
  border: 1px solid rgba(23, 54, 93, 0.08);
  background: #fff;
  box-shadow: 0 8px 24px rgba(23, 54, 93, 0.055);
}

.card {
  padding: 16px;
  border-radius: 17px;
}

.form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px 12px;
}

.field {
  min-width: 0;
  display: grid;
  gap: 7px;
}

.field.full-width {
  grid-column: 1 / -1;
}

.field span,
.qty-field span {
  color: #5b6c82;
  font-size: 13px;
  font-weight: 650;
}

.field i {
  margin-right: 3px;
  color: #d64040;
  font-style: normal;
}

.source-document-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 8px;
}

.source-document-button {
  min-width: 0;
  min-height: 48px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 7px 11px;
  border: 1px solid #d4deea;
  border-radius: 10px;
  background: #fff;
  color: #2b405c;
  text-align: left;
}

.source-document-button svg {
  width: 18px;
  height: 18px;
  flex: 0 0 auto;
  fill: none;
  stroke: #8a98aa;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 2;
}

.selected-source {
  min-width: 0;
  display: grid;
  gap: 2px;
}

.selected-source strong {
  overflow: hidden;
  font: 700 14px ui-monospace, SFMono-Regular, Consolas, monospace;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.selected-source small {
  color: #7b899b;
  font-size: 12px;
}

.source-placeholder {
  color: #8a97a8;
  font-size: 14px;
}

.clear-source-button {
  width: 48px;
  border: 0;
  border-radius: 10px;
  background: #f2f5f8;
  color: #77869a;
  font-size: 24px;
}

.field input,
.scan-input-row input,
.qty-field input {
  width: 100%;
  border: 1px solid #d4deea;
  background: #fff;
  color: #17233d;
  outline: none;
}

.field input {
  height: 44px;
  padding: 0 11px;
  border-radius: 10px;
  font-size: 14px;
}

.field input:focus,
.scan-input-row input:focus,
.qty-field input:focus {
  border-color: #3378cf;
  box-shadow: 0 0 0 3px rgba(51, 120, 207, 0.1);
}

.scan-card {
  display: grid;
  gap: 13px;
}

.scan-title {
  display: flex;
  align-items: center;
  gap: 9px;
  color: #253b58;
  font-size: 15px;
}

.scan-icon {
  width: 34px;
  height: 34px;
  display: grid;
  place-items: center;
  border-radius: 10px;
  background: #e9f2fd;
  color: #2368c4;
}

.scan-icon svg {
  width: 20px;
  height: 20px;
}

.scan-input-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 9px;
}

.scan-input-row input {
  min-width: 0;
  height: 46px;
  padding: 0 12px;
  border-radius: 11px;
  font: 600 14px ui-monospace, SFMono-Regular, Consolas, monospace;
}

.scan-input-row button {
  min-width: 68px;
  border: 0;
  border-radius: 11px;
  background: #2368c4;
  color: #fff;
  font-size: 14px;
  font-weight: 700;
}

.line-section {
  display: grid;
  gap: 10px;
}

.section-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 2px 2px 0;
}

.section-title h2 {
  color: #24344d;
  font-size: 16px;
}

.section-title span {
  color: #7d8a9b;
  font-size: 13px;
}

.line-list {
  display: grid;
  gap: 10px;
}

.line-card {
  padding: 14px;
  border-radius: 15px;
}

.line-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.code-wrap {
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 9px;
}

.code-wrap strong {
  overflow-wrap: anywhere;
  color: #203552;
  font: 700 15px ui-monospace, SFMono-Regular, Consolas, monospace;
}

.object-badge {
  flex: 0 0 auto;
  padding: 4px 7px;
  border-radius: 7px;
  background: #e8f4ee;
  color: #167152;
  font-size: 11px;
  font-weight: 750;
}

.object-badge.pallet {
  background: #fff0e3;
  color: #a95b17;
}

.line-heading button {
  width: 34px;
  height: 34px;
  flex: 0 0 auto;
  display: grid;
  place-items: center;
  border: 0;
  border-radius: 9px;
  background: #f5f7fa;
  color: #8a97a8;
}

.line-heading button svg {
  width: 18px;
  height: 18px;
  stroke-width: 1.8;
}

.line-info {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px 16px;
  margin: 14px 0;
  padding: 12px;
  border-radius: 11px;
  background: #f7f9fc;
}

.line-info div {
  min-width: 0;
  display: flex;
  justify-content: space-between;
  gap: 8px;
}

.line-info dt,
.line-info dd {
  margin: 0;
  font-size: 12px;
}

.line-info dt {
  color: #8290a2;
}

.line-info dd {
  overflow: hidden;
  color: #344760;
  font-weight: 650;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.qty-field {
  display: grid;
  grid-template-columns: auto minmax(110px, 150px);
  align-items: center;
  justify-content: space-between;
  gap: 14px;
}

.qty-field input {
  height: 42px;
  padding: 0 10px;
  border-radius: 10px;
  font-weight: 700;
  text-align: right;
}

.empty-lines {
  min-height: 112px;
  display: grid;
  place-items: center;
  border: 1px dashed #cbd7e4;
  border-radius: 15px;
  background: rgba(255, 255, 255, 0.6);
  color: #8b98a8;
  font-size: 14px;
}

.action-shell {
  position: fixed;
  z-index: 18;
  right: 0;
  bottom: 0;
  left: 0;
  min-height: 76px;
  display: grid;
  grid-template-columns: minmax(76px, 1fr) 88px 108px;
  align-items: center;
  gap: 10px;
  padding: 10px 16px max(10px, env(safe-area-inset-bottom));
  border-top: 1px solid #e1e8f0;
  background: rgba(255, 255, 255, 0.97);
  box-shadow: 0 -6px 20px rgba(23, 54, 93, 0.07);
}

.action-shell.header-actions {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.action-shell.header-actions.with-reset {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.action-shell.scan-actions {
  grid-template-columns: minmax(0, 1fr) minmax(140px, 1fr);
}

.action-shell.header-actions .summary {
  display: none;
}

.summary {
  min-width: 0;
  display: grid;
  gap: 2px;
}

.summary span {
  color: #7b899b;
  font-size: 12px;
}

.summary strong {
  overflow: hidden;
  color: #17365d;
  font-size: 18px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.secondary-action,
.reset-action,
.primary-action {
  height: 46px;
  border-radius: 11px;
  font-size: 14px;
  font-weight: 750;
}

.secondary-action {
  border: 1px solid #bfcde0;
  background: #fff;
  color: #365678;
}

.reset-action {
  border: 1px solid #d4dce7;
  background: #f5f7fa;
  color: #68788c;
}

.primary-action {
  border: 0;
  background: #2368c4;
  color: #fff;
}

.toast-message {
  position: fixed;
  z-index: 40;
  right: 18px;
  bottom: 90px;
  left: 18px;
  width: fit-content;
  max-width: min(420px, calc(100% - 36px));
  margin-inline: auto;
  padding: 10px 15px;
  border-radius: 10px;
  background: #31445e;
  color: #fff;
  font-size: 13px;
  font-weight: 650;
  box-shadow: 0 8px 22px rgba(23, 54, 93, 0.2);
}

.toast-message.success {
  background: #197052;
}

.toast-message.error {
  background: #ad3d3d;
}

.confirm-mask {
  position: fixed;
  z-index: 45;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 20px;
  background: rgba(20, 32, 48, 0.42);
}

.confirm-dialog {
  width: min(100%, 380px);
  padding: 20px;
  border-radius: 17px;
  background: #fff;
  box-shadow: 0 18px 44px rgba(23, 54, 93, 0.24);
}

.confirm-dialog h2 {
  margin: 0 0 18px;
  color: #24344d;
  font-size: 18px;
  text-align: center;
}

.confirm-actions {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
}

.confirm-actions button {
  min-width: 0;
  height: 42px;
  border: 0;
  border-radius: 10px;
  background: #eef2f6;
  color: #52647a;
  font-size: 13px;
  font-weight: 700;
}

.confirm-actions .discard {
  background: #fbecec;
  color: #b64141;
}

.confirm-actions .finish {
  background: #2368c4;
  color: #fff;
}

.toast-enter-active,
.toast-leave-active {
  transition: opacity 0.18s ease, transform 0.18s ease;
}

.toast-enter-from,
.toast-leave-to {
  opacity: 0;
  transform: translateY(8px);
}

@media (max-width: 560px) {
  .topbar {
    padding: 0 14px;
  }

  .page-content {
    padding: 14px 12px 24px;
  }

  .form-grid {
    grid-template-columns: 1fr;
  }

  .field.full-width {
    grid-column: auto;
  }

  .card {
    padding: 14px;
  }

  .action-shell {
    grid-template-columns: minmax(66px, 1fr) 78px 96px;
    padding-inline: 12px;
  }
}
</style>
