import { getOutboundDocument, isLocalDocumentRecordHidden, saveOutboundDocument } from './outbound-document-drafts'
import { inventoryCountStore } from './inventory-count-store'
import { assertCountSource, countCodeKey, countQuantity, type CountDraft, type CountScope, type CountSource } from './inventory-count-model'
import type { InventoryObjectLine } from './inventory-object-lookup'
import type { SourceDocumentLine } from './source-document-selection'
import type { CountCheck } from './inventory-count-model'

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

export function countFormLine(source: CountSource, code: string, id: string, label?: InventoryObjectLine): CountFormLine {
  countCodeKey(code)
  assertCountSource(source)
  const result: CountFormLine = { id, labelCode: code.trim(), objectType: 'PRODUCT_LABEL',
    materialCode: '', materialName: '', batchNo: '', unitCode: '', warehouseCode: source.document.warehouseCode,
    locationCode: '', availableQty: null, requestedQty: 0, fieldValues: {},
    countQuantityConfirmed: false, countPendingIdentity: true }
  if (!label) return result
  if (label.objectType !== 'PRODUCT_LABEL' || !same(code, label.labelCode) ||
      !['ACTIVE', 'USED'].includes(label.status) || !Number.isFinite(label.availableQty) || label.availableQty < 0) {
    throw new Error('标签当前不可用')
  }
  const candidates = source.lines.filter(line => same(line.materialCode, label.materialCode) &&
    same(line.batchNo, label.batchNo) && same(line.warehouseCode, label.warehouseCode) &&
    same(line.locationCode, label.locationCode))
  if (candidates.length !== 1) throw new Error(candidates.length ? '原单明细重复，请核对' : '标签不属于所选源单')
  const line = candidates[0]!
  if (line.requiresAuxiliaryUnit || !Array.isArray(line.freeItems) ||
      line.freeItems.some(value => typeof value !== 'string' || value.trim())) {
    throw new Error('此明细的单位或自由项需核对')
  }
  if (!line.unitCode || !same(line.unitCode, label.unitCode)) throw new Error('计量单位不一致')
  return { ...result, materialCode: label.materialCode, materialName: label.materialName || line.materialName,
    batchNo: label.batchNo, unitCode: label.unitCode, warehouseCode: label.warehouseCode,
    locationCode: label.locationCode, unitName: label.unitName, warehouseName: label.warehouseName,
    locationName: label.locationName, availableQty: label.availableQty, countPendingIdentity: false }
}

export function requireCountForm(source: CountSource | null | undefined,
  lines: Array<{ labelCode: string; requestedQty: number; countQuantityConfirmed?: boolean }>): void {
  if (!source) throw new Error('请选择源单')
  assertCountSource(source)
  if (!lines.length) throw new Error('请添加明细')
  if (lines.length > 1000 || new Set(lines.map(row => countCodeKey(row.labelCode))).size !== lines.length) {
    throw new Error('标签重复或明细过多')
  }
  for (const row of lines) {
    if (row.countQuantityConfirmed === false) throw new Error('请填写盘点数量')
    countQuantity(String(row.requestedQty))
  }
}

export function applyCountChecks<T extends { labelCode: string; requestedQty: number }>(
  source: CountSource, lines: T[], checks: CountCheck[]): Array<T & Partial<CountFormLine>> {
  const map = new Map(checks.map(check => [countCodeKey(check.code), check]))
  if (map.size !== lines.length || checks.length !== lines.length) throw new Error('标签校验结果不完整')
  return lines.map(row => {
    const check = map.get(countCodeKey(row.labelCode))
    if (!check || check.status !== 'MATCHED') throw new Error(check?.message || '标签资料需核对')
    const line = source.lines.find(item => item.id === check.sourceLineId)
    if (!line) throw new Error('原单明细不一致')
    return { ...row, materialCode: line.materialCode, materialName: check.materialName || line.materialName,
      batchNo: line.batchNo, warehouseCode: line.warehouseCode, locationCode: line.locationCode,
      unitCode: line.unitCode, availableQty: check.balance ?? null, countPendingIdentity: false }
  })
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
