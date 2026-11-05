import { getOutboundDocument, isLocalDocumentRecordHidden, saveOutboundDocument } from './outbound-document-drafts'
import { inventoryCountStore } from './inventory-count-store'
import { assertCountSource, countCodeKey, countQuantity, type CountDraft, type CountScope, type CountSource } from './inventory-count-model'
import type { InventoryObjectLine } from './inventory-object-lookup'
import type { SourceDocumentLine } from './source-document-selection'
import type { CountCheck } from './inventory-count-model'
import type { OutboundDocumentWorkingState } from './outbound-document-drafts'
import type { DirectCountCheck } from './inventory-count-api'

export interface CountFormLine {
  id: string
  labelCode: string
  objectType: 'PRODUCT_LABEL'
  materialCode: string
  materialName: string
  batchNo: string
  unitCode: string
  unitName?: string
  warehouseCode: string
  warehouseName?: string
  locationCode: string
  locationName?: string
  countOriginalWarehouseCode?: string
  countOriginalLocationCode?: string
  availableQty: number | null
  requestedQty: number
  fieldValues: Record<string, string | number>
  countQuantityConfirmed: boolean
  countPendingIdentity: boolean
}
const same = (a: string | null | undefined, b: string | null | undefined) =>
  (a ?? '').trim().toUpperCase() === (b ?? '').trim().toUpperCase()

export function countSourceLines(source: CountSource): SourceDocumentLine[] {
  assertCountSource(source)
  return source.lines.map(line => ({
    sourceType: 'inventory-count', sourceDocumentId: source.document.id,
    sourceDocumentNo: source.document.documentNo, sourceLineId: line.id, sourceLineNo: line.lineNo,
    documentDate: source.document.documentDate?.slice(0, 10) ?? '',
    productCode: line.materialCode, productName: line.materialName, orderQuantity: line.bookQuantity,
    departmentCode: '', materialCount: 1, batchNo: line.batchNo,
    warehouseCode: line.warehouseCode, locationCode: line.locationCode,
  }))
}

export function countFormLine(source: CountSource | null, code: string, id: string, label?: InventoryObjectLine,
  warehouse = source?.document.warehouseCode ?? '', countByLocation = source?.document.countByLocation ?? false,
  targetLocation?: string): CountFormLine {
  countCodeKey(code)
  if (source) assertCountSource(source)
  if (!warehouse.trim()) throw new Error('请选择仓库')
  const result: CountFormLine = { id, labelCode: code.trim(), objectType: 'PRODUCT_LABEL',
    materialCode: '', materialName: '', batchNo: '', unitCode: '', warehouseCode: warehouse,
    locationCode: '', availableQty: null, requestedQty: 0, fieldValues: {},
    countQuantityConfirmed: false, countPendingIdentity: true }
  if (!label) return result
  if (label.objectType !== 'PRODUCT_LABEL' || !same(code, label.labelCode) ||
      !['ACTIVE', 'USED'].includes(label.status) || !Number.isFinite(label.availableQty) || label.availableQty < 0) {
    throw new Error('标签当前不可用')
  }
  if (!label.materialCode || (!source && !label.unitCode)) throw new Error('标签资料需核对')
  const managesLocation = source?.document.warehousePositionManaged ?? countByLocation
  let location = managesLocation ? targetLocation ?? (same(warehouse, label.warehouseCode) ? label.locationCode : '') : ''
  const materialCandidates = source?.lines.filter(line => same(line.materialCode, label.materialCode) && same(line.batchNo, label.batchNo))
  if (materialCandidates && materialCandidates.length === 0) throw new Error('标签不属于所选源单')
  if (source && countByLocation && !materialCandidates?.some(line => same(line.locationCode, location))) location = ''
  const candidates = materialCandidates?.filter(line => !countByLocation || !location || same(line.locationCode, location))
  if (candidates && location && candidates.length !== 1) throw new Error('原单明细重复，请核对')
  const line = candidates?.[0]
  if (line && (line.requiresAuxiliaryUnit || !Array.isArray(line.freeItems) ||
      line.freeItems.some(value => typeof value !== 'string' || value.trim()))) {
    throw new Error('此明细的单位或自由项需核对')
  }
  return { ...result, materialCode: label.materialCode, materialName: label.materialName || line?.materialName || '',
    batchNo: label.batchNo, unitCode: line?.unitCode ?? label.unitCode, warehouseCode: warehouse,
    locationCode: location, unitName: line?.unitName || label.unitName || label.unitCode,
    countOriginalWarehouseCode: label.warehouseCode, countOriginalLocationCode: label.locationCode,
    warehouseName: same(warehouse, label.warehouseCode) ? label.warehouseName : undefined,
    locationName: same(warehouse, label.warehouseCode) && same(location, label.locationCode) ? label.locationName : undefined,
    availableQty: label.availableQty,
    requestedQty: label.availableQty, countQuantityConfirmed: true, countPendingIdentity: false }
}

export function requireCountForm(source: CountSource | null | undefined,
  lines: Array<{ labelCode: string; requestedQty: number; countQuantityConfirmed?: boolean }>, warehouse = source?.document.warehouseCode ?? ''): void {
  if (source) assertCountSource(source)
  if (!warehouse.trim()) throw new Error('请选择仓库')
  if (!lines.length) throw new Error('请添加明细')
  if (lines.length > 1000 || new Set(lines.map(row => countCodeKey(row.labelCode))).size !== lines.length) {
    throw new Error('标签重复或明细过多')
  }
  for (const row of lines) {
    if (row.countQuantityConfirmed === false) throw new Error('请填写盘点数量')
    countQuantity(String(row.requestedQty))
  }
}

export function applyCountChecks<T extends { labelCode: string; requestedQty: number; locationCode?: string; locationName?: string; unitName?: string }>(
  source: CountSource, lines: T[], checks: CountCheck[]): Array<T & Partial<CountFormLine>> {
  const map = new Map(checks.map(check => [countCodeKey(check.code), check]))
  if (map.size !== lines.length || checks.length !== lines.length) throw new Error('标签校验结果不完整')
  return lines.map(row => {
    const check = map.get(countCodeKey(row.labelCode))
    if (!check || check.status !== 'MATCHED') throw new Error(check?.message || '标签资料需核对')
    const line = source.lines.find(item => item.id === check.sourceLineId)
    if (!line) throw new Error('原单明细不一致')
    const location = check.locationCode ?? line.locationCode
    return { ...row, materialCode: line.materialCode, materialName: check.materialName || line.materialName,
      batchNo: line.batchNo, warehouseCode: line.warehouseCode, locationCode: location,
      countOriginalWarehouseCode: check.originalWarehouseCode ?? (row as Partial<CountFormLine>).countOriginalWarehouseCode ?? line.warehouseCode,
      countOriginalLocationCode: check.originalLocationCode ?? (row as Partial<CountFormLine>).countOriginalLocationCode ?? location,
      locationName: row.locationCode === location ? row.locationName : undefined,
      unitCode: line.unitCode, unitName: line.unitName || row.unitName,
      availableQty: check.balance ?? null, countPendingIdentity: false }
  })
}

export function applyDirectCountChecks<T extends { id: string; labelCode: string; requestedQty: number; countQuantityConfirmed?: boolean }>(
  warehouse: string, lines: T[], checks: DirectCountCheck[], countByLocation = false): Array<T & CountFormLine> {
  const map = new Map(checks.map(row => [countCodeKey(row.code), row]))
  if (checks.length !== lines.length || map.size !== lines.length) throw new Error('标签校验结果不完整')
  return lines.map(row => {
    const check = map.get(countCodeKey(row.labelCode))
    if (!check || check.status !== 'MATCHED' || !check.label) throw new Error(check?.message || '标签资料需核对')
    return { ...row, ...countFormLine(null, row.labelCode, row.id, check.label, warehouse, countByLocation,
        (row as Partial<CountFormLine>).locationCode),
      requestedQty: row.requestedQty, countQuantityConfirmed: row.countQuantityConfirmed !== false }
  })
}

export async function saveCountSourceDraft(source: CountSource, working: OutboundDocumentWorkingState | null) {
  assertCountSource(source)
  const previous = (working?.payload && typeof working.payload === 'object' ? working.payload : {}) as Record<string, unknown>
  const previousLines = Array.isArray(previous.lines) ? previous.lines : []
  const previousSource = previous.inventoryCountSource as CountSource | undefined
  if (previousLines.length && previousSource?.document.id !== source.document.id) throw new Error('请先重置当前单据再更换源单')
  const fields = (previous.headerFieldValues && typeof previous.headerFieldValues === 'object'
    ? previous.headerFieldValues : {}) as Record<string, unknown>
  const payload = { ...previous, inventoryCountSource: source,
    inventoryCountContext: { accountId: source.accountId, databaseName: source.databaseName },
    documentDate: source.document.documentDate?.slice(0, 10) ?? '',
    warehouseCode: source.document.warehouseCode, documentDirection: 'BLUE',
    sourceDocumentType: 'inventory-count', sourceDocumentTypeLabel: '盘点单', sourceDocumentNo: source.document.documentNo,
    selectedSourceLines: countSourceLines(source), materialRequirements: [], overIssueTargets: {}, lines: previousLines,
    headerFieldValues: { ...fields, 'header.inCategoryCode': source.document.inCategoryCode ?? '',
      'header.outCategoryCode': source.document.outCategoryCode ?? '',
      'header.countByLocation': typeof source.document.countByLocation === 'boolean' ? (source.document.countByLocation ? '1' : '0') : '' } }
  return saveOutboundDocument({ id: working?.documentId ?? undefined, documentType: 'inventory-count', payload,
    summary: { documentDate: payload.documentDate, sourceTypeLabel: '盘点单', sourceDocumentNo: payload.sourceDocumentNo,
      lineCount: previousLines.length, totalQty: previousLines.reduce((total, row) => total + (Number(row.requestedQty) || 0), 0) } })
}

export function legacyCountPayload(draft: CountDraft) {
  const source = draft.task.source
  return { documentDate: source.document.documentDate?.slice(0, 10) ?? '', documentDirection: 'BLUE',
    sourceDocumentType: 'inventory-count', sourceDocumentTypeLabel: '盘点单',
    sourceDocumentNo: source.document.documentNo, warehouseCode: source.document.warehouseCode,
    departmentCode: '', inventoryCountSource: source, selectedSourceLines: countSourceLines(source),
    materialRequirements: [], overIssueTargets: {}, submissionGroups: [],
    lines: draft.observations.map(row => {
      const line = source.lines.find(item => item.id === row.check?.sourceLineId)
      return { ...countFormLine(source, row.code, row.codeKey), materialCode: line?.materialCode ?? '',
        materialName: line?.materialName ?? '', batchNo: line?.batchNo ?? '', unitCode: line?.unitCode ?? '',
        warehouseCode: line?.warehouseCode ?? source.document.warehouseCode, locationCode: line?.locationCode ?? '',
        requestedQty: row.quantity === null ? 0 : Number(countQuantity(row.quantity)),
        countQuantityConfirmed: row.quantity !== null, countPendingIdentity: !line,
        availableQty: row.check?.balance ?? null }
    }) }
}

export async function preserveLegacyCountDocuments(scope: CountScope): Promise<void> {
  for (const task of await inventoryCountStore.list(scope)) {
    if (isLocalDocumentRecordHidden({ id: task.id, serverOperationId: '' })) continue
    const existing = await getOutboundDocument(task.id)
    const payload = existing?.payload as { inventoryCountSource?: CountSource; inventoryCount?: { draft?: CountDraft } } | null
    if (existing && (payload?.inventoryCountSource || !payload?.inventoryCount?.draft ||
        existing.ownerUserId !== scope.userId || existing.uploadStatus !== 'pending')) continue
    const saved = payload?.inventoryCount?.draft ?? await inventoryCountStore.read(scope, task.id)
    const standard = legacyCountPayload(saved)
    await saveOutboundDocument({ id: task.id, documentType: 'inventory-count', payload: standard,
      summary: { documentDate: standard.documentDate, sourceTypeLabel: '盘点单',
        sourceDocumentNo: standard.sourceDocumentNo, lineCount: standard.lines.length,
        totalQty: standard.lines.reduce((sum, line) => sum + line.requestedQty, 0) } })
  }
}
