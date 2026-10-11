<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import { getPdaBridge, isMockPdaBridge } from '@/bridge/pda-bridge'
import type { PdaScanEvent } from '@/bridge/types'
import { getBusinessDocumentConfig } from '@/services/business-document-config'
import {
  ensureDocumentPolicy,
  loadBusinessDocumentSettings,
} from '@/services/business-document-settings'
import { lookupInventoryObject } from '@/services/inventory-object-lookup'
import {
  allocateMaterialScans,
  summarizeMaterialAllocations,
  type SourceQuantityAllocation,
} from '@/services/material-outbound-allocation'
import {
  clearOutboundDocumentWorkingState,
  consumeOutboundDocumentEditRequest,
  consumeOutboundDocumentWorkingState,
  getOutboundDocument,
  migrateLegacyOutboundDraft,
  saveOutboundDocument,
  saveOutboundDocumentWorkingState,
  type OutboundDocumentSummary,
  type StoredOutboundDocument,
} from '@/services/outbound-document-drafts'
import { uploadOutboundDocument } from '@/services/outbound-document-upload'
import {
  consumeSourceDocumentSelection,
  type MaterialRequirementLine,
  type OutboundDocumentType,
  type SourceDocumentLine,
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
  sourcePalletCode?: string
  sourceAllocations?: SourceQuantityAllocation[]
  unallocatedQty?: number
}

interface MaterialOutboundSubmissionLine {
  scanLineId: string
  labelCode: string
  objectType: LineObjectType
  sourcePalletCode: string
  materialCode: string
  materialName: string
  batchNo: string
  locationCode: string
  requirementId: number
  sourceLineId: number
  sourceLineNo: number | null
  productCode: string
  normalQty: number
  overQty: number
  quantity: number
}

interface MaterialOutboundSubmissionGroup {
  groupKey: string
  sourceDocumentId: number
  sourceDocumentNo: string
  warehouseCode: string
  lines: MaterialOutboundSubmissionLine[]
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
  selectedSourceLines: SourceDocumentLine[]
  materialRequirements: MaterialRequirementLine[]
  overIssueTargets: Record<string, number>
  lines: OutboundLine[]
  submissionGroups: MaterialOutboundSubmissionGroup[]
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
const selectedSourceLines = ref<SourceDocumentLine[]>([])
const materialRequirements = ref<MaterialRequirementLine[]>([])
const overIssueTargets = ref<Record<string, number>>({})
const scanCode = ref('')
const lines = ref<OutboundLine[]>([])
const message = ref('')
const messageTone = ref<'success' | 'error' | 'info'>('info')
const showLeaveConfirm = ref(false)
let allowNavigation = false
let baselineSnapshot = ''
let stopScanListener: (() => void) | null = null
let messageTimer: ReturnType<typeof setTimeout> | null = null
const resolvingBarcodes = new Set<string>()

const pageConfig = computed(() => getBusinessDocumentConfig(props.documentType))
const documentSettings = computed(() => loadBusinessDocumentSettings())
const documentPolicy = computed(() =>
  ensureDocumentPolicy(documentSettings.value, props.documentType),
)
const allocationByLine = computed(() =>
  props.documentType === 'material-outbound'
    ? allocateMaterialScans(
        lines.value,
        materialRequirements.value,
        documentPolicy.value.allowOverIssue,
        overIssueTargets.value,
      )
    : {},
)
const materialSummaries = computed(() =>
  props.documentType === 'material-outbound'
    ? summarizeMaterialAllocations(lines.value, materialRequirements.value, allocationByLine.value)
    : [],
)
const pendingOverIssueLine = computed(() =>
  documentPolicy.value.allowOverIssue
    ? lines.value.find((line) => allocationByLine.value[line.id]?.requiresOverIssueTarget)
    : undefined,
)
const pendingOverIssueMaterial = computed(() =>
  materialSummaries.value.find(
    (item) => item.materialCode === pendingOverIssueLine.value?.materialCode,
  ),
)
const overIssueChoices = computed(() => {
  const materialCode = pendingOverIssueMaterial.value?.materialCode
  if (!materialCode) return []
  return materialRequirements.value.filter((item) => item.materialCode === materialCode)
})

const totalQty = computed(() =>
  lines.value.reduce((total, line) => total + normalizeQty(line.requestedQty), 0),
)
const showHeaderForm = computed(() => currentStep.value === 1)
const showScanContent = computed(() => currentStep.value === 2)
const sourceDetailButtonLabel = computed(() =>
  props.documentType === 'material-outbound' ? '领料明细' : '源单明细',
)
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

async function addBarcode(rawCode: string): Promise<void> {
  const barcode = rawCode.trim()
  if (!barcode) return
  if (
    resolvingBarcodes.has(barcode) ||
    lines.value.some((line) => line.labelCode === barcode)
  ) {
    notify('标签已添加', 'error')
    scanCode.value = ''
    return
  }
  resolvingBarcodes.add(barcode)

  try {
    const found = await lookupInventoryObject(barcode)
    const isPalletScan = isPalletCode(barcode) || found.length > 1
    if (
      !isPalletScan &&
      found.some((item) => item.palletCode) &&
      !documentSettings.value.globalPallet.allowPalletizedLabelDirectUse
    ) {
      notify('请扫描托盘码', 'error')
      return
    }
    if (
      props.documentType === 'material-outbound' &&
      found.some((item) =>
        !materialRequirements.value.some(
          (requirement) => requirement.materialCode === item.materialCode,
        ),
      )
    ) {
      notify('该物料不在领料范围', 'error')
      return
    }
    if (found.some((item) => lines.value.some((line) => line.labelCode === item.labelCode))) {
      notify('标签已添加', 'error')
      return
    }
    const additions = found.map<OutboundLine>((item) => ({
      id: createLineId(),
      labelCode: item.labelCode,
      objectType: isPalletScan ? 'PALLET' : item.objectType,
      materialCode: item.materialCode,
      materialName: item.materialName,
      batchNo: item.batchNo,
      warehouseCode: item.warehouseCode,
      locationCode: item.locationCode,
      availableQty: item.availableQty,
      requestedQty: item.availableQty,
      sourcePalletCode: item.palletCode,
    }))
    lines.value = [...lines.value, ...additions]
    notify('已添加', 'success')
  } catch (error) {
    notify(error instanceof Error ? error.message : '标签查询失败', 'error')
  } finally {
    resolvingBarcodes.delete(barcode)
    scanCode.value = ''
  }
}

function handleScan(event: PdaScanEvent): void {
  if (currentStep.value !== 2) return
  void addBarcode(event.barcode)
}

function simulateOrAdd(): void {
  if (!scanCode.value.trim()) return
  if (isMockPdaBridge(bridge)) {
    bridge.simulateScan(scanCode.value)
    return
  }
  void addBarcode(scanCode.value)
}

function removeLine(id: string): void {
  lines.value = lines.value.filter((line) => line.id !== id)
  if (overIssueTargets.value[id]) {
    const next = { ...overIssueTargets.value }
    delete next[id]
    overIssueTargets.value = next
  }
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
  selectedSourceLines.value = []
  materialRequirements.value = []
  overIssueTargets.value = {}
}

function buildMaterialOutboundSubmissionGroups(
  draftLines: OutboundLine[],
): MaterialOutboundSubmissionGroup[] {
  if (props.documentType !== 'material-outbound') return []
  const groups = new Map<string, MaterialOutboundSubmissionGroup>()
  for (const line of draftLines) {
    for (const allocation of line.sourceAllocations ?? []) {
      const warehouse = line.warehouseCode.trim()
      const groupKey = `${allocation.sourceDocumentId}::${warehouse}`
      let group = groups.get(groupKey)
      if (!group) {
        group = {
          groupKey,
          sourceDocumentId: allocation.sourceDocumentId,
          sourceDocumentNo: allocation.sourceDocumentNo,
          warehouseCode: warehouse,
          lines: [],
        }
        groups.set(groupKey, group)
      }
      group.lines.push({
        scanLineId: line.id,
        labelCode: line.labelCode,
        objectType: line.objectType,
        sourcePalletCode: line.sourcePalletCode ?? '',
        materialCode: line.materialCode,
        materialName: line.materialName,
        batchNo: line.batchNo,
        locationCode: line.locationCode,
        requirementId: allocation.requirementId,
        sourceLineId: allocation.sourceLineId,
        sourceLineNo: allocation.sourceLineNo,
        productCode: allocation.productCode,
        normalQty: allocation.normalQty,
        overQty: allocation.overQty,
        quantity: allocation.normalQty + allocation.overQty,
      })
    }
  }
  return [...groups.values()].sort((left, right) =>
    left.sourceDocumentNo.localeCompare(right.sourceDocumentNo) ||
    left.warehouseCode.localeCompare(right.warehouseCode),
  )
}

function createDraft(): OutboundDraft {
  const draftLines = lines.value.map((line) => ({
    ...line,
    requestedQty: normalizeQty(line.requestedQty),
    sourceAllocations: allocationByLine.value[line.id]?.allocations ?? [],
    unallocatedQty: allocationByLine.value[line.id]?.unallocatedQty ?? 0,
  }))
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
    selectedSourceLines: selectedSourceLines.value,
    materialRequirements: materialRequirements.value,
    overIssueTargets: overIssueTargets.value,
    lines: draftLines,
    submissionGroups: buildMaterialOutboundSubmissionGroups(draftLines),
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
  selectedSourceLines.value = Array.isArray(draft.selectedSourceLines)
    ? draft.selectedSourceLines
    : []
  materialRequirements.value = Array.isArray(draft.materialRequirements)
    ? draft.materialRequirements
    : []
  overIssueTargets.value = draft.overIssueTargets && typeof draft.overIssueTargets === 'object'
    ? draft.overIssueTargets
    : {}
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

function saveCompletedDocument(): StoredOutboundDocument | null {
  const error = validate()
  if (error) {
    notify(error, 'error')
    return null
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
  return stored
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

async function openSourceDetails(): Promise<void> {
  saveWorkingState()
  allowNavigation = true
  await router.replace({
    name: 'source-document-details',
    params: { code: props.documentType },
    query: route.query,
  })
}

function validateHeader(): string {
  if (!documentDate.value) return '请选择日期'
  if (pageConfig.value.sourceTypes.length && !selectedSourceLines.value.length) {
    return '请选择源单明细'
  }
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
  if (!lines.value.length && !selectedSourceLines.value.length) return '请扫描标签或托盘'
  if (lines.value.some((line) => normalizeQty(line.requestedQty) <= 0)) {
    return `${pageConfig.value.quantityLabel}必须大于0`
  }
  if (
    lines.value.some(
      (line) => line.availableQty !== null && normalizeQty(line.requestedQty) > line.availableQty,
    )
  ) return '本次处理数量不能超过标签当前数量'
  if (props.documentType === 'material-outbound') {
    if (lines.value.some((line) => !line.warehouseCode.trim())) return '标签缺少仓库'
    if (materialSummaries.value.some((item) => item.unallocatedQty > 0)) {
      return documentPolicy.value.allowOverIssue ? '请选择超领产品' : '存在超过可领数量的物料'
    }
    if (
      materialSummaries.value.some((item) => item.overQty > 0) &&
      documentSettings.value.globalOperation.reasonRequired &&
      !remark.value.trim()
    ) return '请输入超领原因'
  }
  return ''
}

function chooseOverIssueTarget(requirementId: number): void {
  const lineId = pendingOverIssueLine.value?.id
  if (!lineId) return
  overIssueTargets.value = { ...overIssueTargets.value, [lineId]: requirementId }
}

function cancelOverIssue(): void {
  const pending = pendingOverIssueLine.value
  if (!pending) return
  const quantityToRemove = allocationByLine.value[pending.id]?.unallocatedQty ?? 0
  const current = normalizeQty(pending.requestedQty)
  const nextQuantity = normalizeQty(current - quantityToRemove)
  if (nextQuantity <= 0) {
    lines.value = lines.value.filter((line) => line.id !== pending.id)
    return
  }
  pending.requestedQty = nextQuantity
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
  const stored = saveCompletedDocument()
  if (!stored) return
  if (documentPolicy.value.autoUploadOnComplete && lines.value.length) {
    try {
      await uploadOutboundDocument(stored, documentPolicy.value.autoApproveAfterUpload)
    } catch (error) {
      notify(error instanceof Error ? error.message : '上传失败', 'error')
    }
  }
  allowNavigation = true
  await router.replace({
    name: 'outbound-document-manager',
    params: { code: props.documentType },
    query: route.query,
  })
}

async function finishAndLeave(): Promise<void> {
  const stored = saveCompletedDocument()
  if (!stored) {
    showLeaveConfirm.value = false
    return
  }
  if (documentPolicy.value.autoUploadOnComplete && lines.value.length) {
    try {
      await uploadOutboundDocument(stored, documentPolicy.value.autoApproveAfterUpload)
    } catch (error) {
      notify(error instanceof Error ? error.message : '上传失败', 'error')
    }
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
    selectedSourceLines.value = sourceSelection.selectedLines
    materialRequirements.value = sourceSelection.requirements
    overIssueTargets.value = {}
    const departments = [...new Set(
      sourceSelection.selectedLines.map((line) => line.departmentCode).filter(Boolean),
    )]
    if (!departmentCode.value && departments.length === 1) {
      departmentCode.value = departments[0] ?? ''
    }
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
        <div v-if="currentStep === 2" class="topbar-actions">
          <button
            v-if="selectedSourceLines.length"
            class="source-details-button"
            type="button"
            @click="openSourceDetails"
          >
            {{ sourceDetailButtonLabel }}
          </button>
          <button class="switch-settings-button" type="button" @click="openSwitchSettings">
            开关设置
          </button>
        </div>
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
          <h2>扫码记录</h2>
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

            <div
              v-if="allocationByLine[line.id]?.allocations.length"
              class="allocation-list"
            >
              <div
                v-for="allocation in allocationByLine[line.id]?.allocations"
                :key="allocation.requirementId"
              >
                <span>{{ allocation.productCode }} {{ allocation.productName }}</span>
                <strong>
                  {{ allocation.normalQty + allocation.overQty }}
                  <i v-if="allocation.overQty > 0">超{{ allocation.overQty }}</i>
                </strong>
              </div>
            </div>
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

    <div v-if="pendingOverIssueMaterial" class="confirm-mask over-issue-mask">
      <section class="over-issue-dialog" role="dialog" aria-modal="true" aria-label="选择超领产品">
        <div class="over-issue-heading">
          <div>
            <strong>{{ pendingOverIssueMaterial.materialCode }}</strong>
            <span>超领{{ allocationByLine[pendingOverIssueLine?.id ?? '']?.unallocatedQty ?? 0 }}</span>
          </div>
        </div>
        <button
          v-for="choice in overIssueChoices"
          :key="choice.requirementId"
          type="button"
          @click="chooseOverIssueTarget(choice.requirementId)"
        >
          <span>{{ choice.productCode }} {{ choice.productName }}</span>
          <small>
            {{ choice.sourceDocumentNo }} · 行{{ choice.sourceLineNo ?? '-' }}
            <template v-if="choice.operationSequence"> · 工序{{ choice.operationSequence }}</template>
          </small>
        </button>
        <button class="cancel-over-issue" type="button" @click="cancelOverIssue">取消</button>
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

.topbar-actions {
  display: flex;
  gap: 7px;
}

.switch-settings-button,
.source-details-button {
  height: 38px;
  padding: 0 10px;
  border: 0;
  border-radius: 10px;
  background: #edf4fd;
  color: #3269aa;
  font-size: 13px;
  font-weight: 700;
  white-space: nowrap;
}

.source-details-button {
  background: #2368c4;
  color: #fff;
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

.material-summary-list {
  display: grid;
  gap: 9px;
}

.material-summary-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 13px 14px;
  border: 1px solid #dce6f1;
  border-radius: 14px;
  background: #fff;
}

.material-summary-card.over {
  border-color: #edc28e;
  background: #fffaf3;
}

.material-summary-card > div {
  min-width: 0;
  display: grid;
  gap: 3px;
}

.material-summary-card > div strong {
  color: #203552;
  font: 700 14px ui-monospace, SFMono-Regular, Consolas, monospace;
}

.material-summary-card > div span {
  color: #718096;
  font-size: 12px;
}

.material-summary-card dl {
  display: flex;
  gap: 13px;
}

.material-summary-card dl div {
  display: grid;
  gap: 2px;
  text-align: right;
}

.material-summary-card dt {
  color: #8a96a6;
  font-size: 11px;
}

.material-summary-card dd {
  margin: 0;
  color: #2b405c;
  font-size: 13px;
  font-weight: 750;
}

.material-summary-card.over dd {
  color: #b56825;
}

.allocation-list {
  display: grid;
  gap: 7px;
  margin-top: 11px;
  padding-top: 10px;
  border-top: 1px solid #edf1f5;
}

.allocation-list > div {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  color: #617186;
  font-size: 12px;
}

.allocation-list strong {
  color: #253b58;
}

.allocation-list i {
  margin-left: 6px;
  padding: 2px 5px;
  border-radius: 6px;
  background: #fff0df;
  color: #b56825;
  font-style: normal;
  font-size: 10px;
}

.over-issue-mask {
  z-index: 46;
}

.over-issue-dialog {
  width: min(100%, 400px);
  overflow: hidden;
  border-radius: 17px;
  background: #fff;
  box-shadow: 0 18px 44px rgba(23, 54, 93, 0.24);
}

.over-issue-heading {
  padding: 17px;
  border-bottom: 1px solid #edf1f5;
}

.over-issue-heading > div {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.over-issue-heading strong {
  color: #24344d;
  font: 700 15px ui-monospace, SFMono-Regular, Consolas, monospace;
}

.over-issue-heading span {
  color: #b56825;
  font-size: 13px;
  font-weight: 750;
}

.over-issue-dialog > button {
  width: 100%;
  min-height: 58px;
  display: grid;
  gap: 4px;
  padding: 10px 17px;
  border: 0;
  border-bottom: 1px solid #edf1f5;
  background: #fff;
  color: #2b405c;
  text-align: left;
}

.over-issue-dialog > button:last-child {
  border-bottom: 0;
}

.over-issue-dialog > button span {
  font-size: 14px;
  font-weight: 700;
}

.over-issue-dialog > button small {
  color: #7a8798;
  font-size: 12px;
}

.over-issue-dialog > button.cancel-over-issue {
  display: block;
  color: #718096;
  text-align: center;
  font-weight: 700;
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
