import { apiRequest } from '@/api/client'
import { readStoredAuthSession } from '@/services/auth-session'
import {
  beginOutboundDocumentUpload,
  markOutboundDocumentSubmitted,
  markOutboundDocumentUploadFailed,
  updateOutboundDocumentGroup,
  type OperationGroupSnapshot,
  type StoredOutboundDocument,
} from '@/services/outbound-document-drafts'

interface SubmissionLine {
  labelCode: string
  objectType: 'PRODUCT_LABEL' | 'PALLET'
  sourcePalletCode?: string
  materialCode: string
  unitCode: string
  requirementId: number
  sourceLineId: number
  sourceLineNo: number | null
  productCode: string
  normalQty: number
  overQty: number
  fieldValues?: Record<string, string | number>
}

interface SubmissionGroup {
  sourceDocumentId: number
  sourceDocumentNo: string
  lines: SubmissionLine[]
}

interface MaterialOutboundPayload {
  documentDate: string
  documentDirection: 'BLUE' | 'RED'
  sourceDocumentType: string
  departmentCode: string
  outboundCategoryCode: string
  productionBatch: string
  remark: string
  headerFieldValues?: Record<string, string | number>
  submissionGroups: SubmissionGroup[]
}

interface InventoryDocumentLine {
  labelCode: string
  objectType: 'PRODUCT_LABEL' | 'PALLET' | 'UNLABELED_MATERIAL'
  sourcePalletCode?: string
  materialCode: string
  materialName?: string
  batchNo?: string
  unitCode: string
  warehouseCode?: string
  locationCode?: string
  requiresBatch?: boolean
  requestedQty: number
  fieldValues?: Record<string, string | number>
}

interface InventoryDocumentPayload {
  documentDate: string
  documentDirection: 'BLUE' | 'RED'
  sourceDocumentType: string
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
  headerFieldValues?: Record<string, string | number>
  selectedSourceLines?: Array<{
    sourceType: string
    sourceDocumentId: number
    sourceDocumentNo: string
    sourceLineId: number
    sourceLineNo: number | null
    productCode: string
    orderQuantity: number
  }>
  lines: InventoryDocumentLine[]
}

interface OperationGroupListResult {
  skip: number
  take: number
  total: number
  items: OperationGroupSnapshot[]
}

function materialOutboundPayload(document: StoredOutboundDocument): MaterialOutboundPayload {
  if (document.documentType !== 'material-outbound') {
    throw new Error('当前单据尚未接入')
  }
  const payload = document.payload as Partial<MaterialOutboundPayload> | null
  if (!payload || !Array.isArray(payload.submissionGroups) || !payload.submissionGroups.length) {
    throw new Error('材料出库明细不完整，请重新保存')
  }
  return payload as MaterialOutboundPayload
}

function createPrepareLines(payload: MaterialOutboundPayload) {
  return payload.submissionGroups.flatMap((group) => group.lines.map((line) => ({
    objectType: line.objectType,
    labelCode: line.labelCode,
    sourcePalletCode: line.sourcePalletCode || null,
    sourceType: payload.sourceDocumentType,
    sourceDocumentId: group.sourceDocumentId,
    sourceDocumentNo: group.sourceDocumentNo,
    sourceLineId: line.sourceLineId,
    sourceLineNo: line.sourceLineNo,
    requirementId: line.requirementId,
    productCode: line.productCode,
    materialCode: line.materialCode,
    unitCode: line.unitCode,
    normalQty: line.normalQty,
    overQty: line.overQty,
    remark: payload.remark || null,
    additionalFields: line.fieldValues ?? {},
  })))
}

function inventoryDocumentPayload(document: StoredOutboundDocument): InventoryDocumentPayload {
  const payload = document.payload as Partial<InventoryDocumentPayload> | null
  if (!payload || !payload.documentDate || !Array.isArray(payload.lines) || !payload.lines.length) {
    throw new Error('单据明细不完整，请重新保存')
  }
  return payload as InventoryDocumentPayload
}

function createInventoryPrepareLines(payload: InventoryDocumentPayload) {
  const baseLines = payload.lines.map((line) => ({
    objectType: line.objectType,
    labelCode: line.labelCode,
    sourcePalletCode: line.sourcePalletCode || null,
    materialCode: line.materialCode,
    unitCode: line.unitCode,
    requestedQty: Number(line.requestedQty),
    remark: payload.remark || null,
    additionalFields: line.fieldValues ?? {},
    materialName: line.materialName || null,
    batchNo: line.batchNo || null,
    warehouseCode: line.warehouseCode || null,
    locationCode: line.locationCode || null,
    requiresBatch: line.requiresBatch === true,
  }))
  const sources = (payload.selectedSourceLines ?? []).map((line) => ({
    ...line,
    remainingQty: Number(line.orderQuantity),
  }))
  if (!sources.length) return baseLines

  const result: Array<(typeof baseLines)[number] & {
    sourceType: string
    sourceDocumentId: number
    sourceDocumentNo: string
    sourceLineId: number
    sourceLineNo: number | null
    sourceProductCode: string
  }> = []
  for (const line of baseLines) {
    let pending = line.requestedQty
    const candidates = sources.filter(
      (source) => source.productCode.localeCompare(line.materialCode, undefined, { sensitivity: 'accent' }) === 0 &&
        source.remainingQty > 0,
    )
    if (line.objectType === 'PALLET') {
      const source = candidates.find((item) => item.remainingQty >= pending)
      if (!source) throw new Error(`托盘料号不在所选源单可用范围：${line.labelCode}`)
      source.remainingQty -= pending
      result.push({
        ...line,
        sourceType: source.sourceType,
        sourceDocumentId: source.sourceDocumentId,
        sourceDocumentNo: source.sourceDocumentNo,
        sourceLineId: source.sourceLineId,
        sourceLineNo: source.sourceLineNo,
        sourceProductCode: source.productCode,
      })
      continue
    }
    for (const source of candidates) {
      if (pending <= 0) break
      const quantity = Math.min(pending, source.remainingQty)
      if (quantity <= 0) continue
      result.push({
        ...line,
        requestedQty: quantity,
        sourceType: source.sourceType,
        sourceDocumentId: source.sourceDocumentId,
        sourceDocumentNo: source.sourceDocumentNo,
        sourceLineId: source.sourceLineId,
        sourceLineNo: source.sourceLineNo,
        sourceProductCode: source.productCode,
      })
      source.remainingQty -= quantity
      pending -= quantity
    }
    if (pending > 0.000001) throw new Error(`源单可用数量不足：${line.materialCode}`)
  }
  return result
}

async function submitInventoryDocument(
  document: StoredOutboundDocument,
  session: NonNullable<ReturnType<typeof readStoredAuthSession>>,
  autoApprove: boolean,
  clientRequestId: string,
): Promise<OperationGroupSnapshot> {
  const payload = inventoryDocumentPayload(document)
  if (payload.lines.some((line) => !line.unitCode)) {
    throw new Error('单据明细不完整，请重新扫描')
  }
  const lines = createInventoryPrepareLines(payload)
  return apiRequest<OperationGroupSnapshot>(
    `/api/v1/operations/documents/${encodeURIComponent(document.documentType)}/submit?autoApprove=${autoApprove}`,
    {
      method: 'POST',
      timeoutMs: 120_000,
      headers: { 'X-Client-Request-Id': clientRequestId },
      body: JSON.stringify({
        clientRequestId,
        operatorId: session.user.id,
        operatorName: session.user.name,
        deviceId: null,
        deviceCode: null,
        deviceModel: navigator.userAgent.slice(0, 100),
        reason: payload.remark || null,
        header: {
          documentDate: payload.documentDate,
          documentDirection: payload.documentDirection || 'BLUE',
          partnerCode: payload.customerCode || null,
          warehouseCode: payload.warehouseCode || null,
          targetWarehouseCode: payload.targetWarehouseCode || null,
          sourceLocationCode: payload.sourceLocationCode || null,
          targetLocationCode: payload.targetLocationCode || null,
          departmentCode: payload.departmentCode || null,
          categoryCode: payload.outboundCategoryCode || null,
          productionBatch: payload.productionBatch || null,
          sourceType: payload.sourceDocumentType || null,
          sourceDocumentNo: payload.sourceDocumentNo || null,
          additionalFields: payload.headerFieldValues ?? {},
        },
        lines,
      }),
    },
  )
}

export async function getOutboundOperationGroup(
  submissionGroupId: string,
): Promise<OperationGroupSnapshot> {
  return apiRequest<OperationGroupSnapshot>(
    `/api/v1/operations/groups/${encodeURIComponent(submissionGroupId)}`,
  )
}

export async function listOutboundOperationGroups(
  documentType: string,
  allUsers: boolean,
): Promise<OperationGroupSnapshot[]> {
  const items: OperationGroupSnapshot[] = []
  let total = 0
  do {
    const query = new URLSearchParams({ documentType, skip: String(items.length), take: '200' })
    if (allUsers) query.set('allUsers', 'true')
    const page = await apiRequest<OperationGroupListResult>(
      `/api/v1/operations/groups?${query.toString()}`,
    )
    items.push(...page.items)
    total = page.total
    if (!page.items.length) break
  } while (items.length < total)
  return items
}

export async function heartbeatOutboundOperationGroup(
  submissionGroupId: string,
): Promise<OperationGroupSnapshot> {
  return apiRequest<OperationGroupSnapshot>(
    `/api/v1/operations/groups/${encodeURIComponent(submissionGroupId)}/heartbeat`,
    { method: 'POST' },
  )
}

export async function cancelOutboundOperationGroup(
  submissionGroupId: string,
): Promise<OperationGroupSnapshot> {
  return apiRequest<OperationGroupSnapshot>(
    `/api/v1/operations/groups/${encodeURIComponent(submissionGroupId)}/cancel`,
    { method: 'POST' },
  )
}

export async function confirmIssuedLabel(
  submissionGroupId: string,
  labelCode: string,
): Promise<OperationGroupSnapshot> {
  return apiRequest<OperationGroupSnapshot>(
    `/api/v1/operations/groups/${encodeURIComponent(submissionGroupId)}/issued-labels/${encodeURIComponent(labelCode)}/confirm`,
    { method: 'POST' },
  )
}

export async function executeOutboundOperationGroup(
  submissionGroupId: string,
  autoApprove: boolean,
): Promise<OperationGroupSnapshot> {
  return apiRequest<OperationGroupSnapshot>(
    `/api/v1/operations/groups/${encodeURIComponent(submissionGroupId)}/execute`,
    {
      method: 'POST',
      timeoutMs: 180_000,
      body: JSON.stringify({ autoApprove }),
    },
  )
}

export async function refreshOutboundDocumentGroup(
  document: StoredOutboundDocument,
): Promise<StoredOutboundDocument> {
  if (!document.serverOperationId) throw new Error('业务组编号缺失')
  const group = await getOutboundOperationGroup(document.serverOperationId)
  return updateOutboundDocumentGroup(document.id, group)
}

export async function heartbeatOutboundDocument(
  document: StoredOutboundDocument,
): Promise<StoredOutboundDocument> {
  if (!document.serverOperationId) throw new Error('业务组编号缺失')
  const group = await heartbeatOutboundOperationGroup(document.serverOperationId)
  return updateOutboundDocumentGroup(document.id, group)
}

export async function cancelOutboundDocument(
  document: StoredOutboundDocument,
): Promise<StoredOutboundDocument> {
  if (!document.serverOperationId) throw new Error('业务组编号缺失')
  const group = await cancelOutboundOperationGroup(document.serverOperationId)
  return updateOutboundDocumentGroup(document.id, group)
}

export async function executeOutboundDocument(
  document: StoredOutboundDocument,
  autoApprove: boolean,
): Promise<StoredOutboundDocument> {
  if (!document.serverOperationId) throw new Error('业务组编号缺失')
  const group = await executeOutboundOperationGroup(document.serverOperationId, autoApprove)
  return updateOutboundDocumentGroup(document.id, group)
}

export async function uploadOutboundDocument(
  document: StoredOutboundDocument,
  _autoApprove: boolean,
): Promise<StoredOutboundDocument> {
  try {
    const session = readStoredAuthSession()
    if (!session) throw new Error('请先登录')
    const clientRequestId = await beginOutboundDocumentUpload(
      document.id,
      document.serverStatus === 'FAILED',
    )
    let result: OperationGroupSnapshot
    const candidate = document.payload as Partial<InventoryDocumentPayload> | null
    const hasSelectedSource = Array.isArray(candidate?.selectedSourceLines) &&
      candidate.selectedSourceLines.length > 0
    if (document.documentType === 'material-outbound' && hasSelectedSource) {
      const payload = materialOutboundPayload(document)
      const lines = createPrepareLines(payload)
      if (!lines.length || lines.some((line) => !line.unitCode)) {
        throw new Error('材料出库明细不完整，请重新扫描')
      }
      result = await apiRequest<OperationGroupSnapshot>(
        `/api/v1/operations/material-outbound/submit?autoApprove=${_autoApprove}`,
        {
          method: 'POST',
          timeoutMs: 120_000,
          headers: { 'X-Client-Request-Id': clientRequestId },
          body: JSON.stringify({
            clientRequestId,
            operatorId: session.user.id,
            operatorName: session.user.name,
            deviceId: null,
            deviceCode: null,
            deviceModel: navigator.userAgent.slice(0, 100),
            reason: payload.remark || null,
            documentDate: payload.documentDate,
            documentDirection: payload.documentDirection || 'BLUE',
            departmentCode: payload.departmentCode || null,
            categoryCode: payload.outboundCategoryCode || null,
            productionBatch: payload.productionBatch || null,
            additionalFields: payload.headerFieldValues ?? {},
            lines,
          }),
        },
      )
    } else {
      result = await submitInventoryDocument(document, session, _autoApprove, clientRequestId)
    }
    return markOutboundDocumentSubmitted(document.id, result)
  } catch (error) {
    const message = error instanceof Error ? error.message : '上传失败'
    await markOutboundDocumentUploadFailed(document.id, message)
    throw new Error(message)
  }
}
