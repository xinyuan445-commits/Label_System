import type { BusinessDocumentType } from '@/services/business-document-config'
import { apiRequest } from '@/api/client'
import { getRuntimeConfig } from '@/services/runtime-config'
import { searchInventoryCounts } from '@/services/inventory-count-api'
import type { CountSource } from '@/services/inventory-count-model'
import { scopedStorageKey } from './storage-scope'

export type OutboundDocumentType = BusinessDocumentType

export interface SourceDocumentLine {
  sourceType: string
  sourceDocumentId: number
  sourceDocumentNo: string
  sourceLineId: number
  sourceLineNo: number | null
  documentDate: string
  productCode: string
  productName: string
  orderQuantity: number
  departmentCode: string
  materialCount: number
  batchNo?: string
  warehouseCode?: string
  warehouseName?: string
  sourceCreatedBy?: string | null
  sourceStatus?: string
  locationCode?: string
  plannedQuantity?: number | null
  processedQuantity?: number | null
  remainingQuantity?: number | null
  partnerCode?: string
  partnerName?: string
  salesTypeCode?: string
  currencyName?: string
  exchangeRate?: number | null
  taxRate?: number | null
  unitPrice?: number | null
  taxUnitPrice?: number | null
  salesOrderLineId?: number | null
  salesOrderNo?: string
}

export interface MaterialRequirementLine {
  sourceType: string
  sourceDocumentId: number
  sourceDocumentNo: string
  sourceLineId: number
  sourceLineNo: number | null
  requirementId: number
  productCode: string
  productName: string
  materialCode: string
  materialName: string
  departmentCode: string
  operationSequence: string
  requiresBatch: boolean
  plannedQuantity: number
  issuedQuantity: number
  remainingQuantity: number
}

export interface SourceDocumentSelection {
  outboundDocumentType: OutboundDocumentType
  sourceType: string
  sourceTypeLabel: string
  documentNo: string
  documentDate: string
  selectedLines: SourceDocumentLine[]
  requirements: MaterialRequirementLine[]
  inventoryCountSource?: CountSource
  localDraftId?: string
}

export interface ReturnRequirementSelection {
  outboundDocumentType: OutboundDocumentType
  requirement: MaterialRequirementLine
}

export interface SourceDocumentQuery {
  documentType: OutboundDocumentType
  sourceType: string
  keyword: string
  startDate: string
  endDate: string
}

interface SourceDocumentSearchResult {
  items: SourceDocumentLine[]
}

const selectionKey = 'label-system:source-document-selection'
const returnRequirementSelectionKey = 'label-system:return-requirement-selection'

const mockSourceLines: SourceDocumentLine[] = [
  {
    sourceType: 'production-order', sourceDocumentId: 10001,
    sourceDocumentNo: 'MO-20261011-001', sourceLineId: 11001, sourceLineNo: 10,
    documentDate: '2026-10-11', productCode: 'P1001', productName: '产品A',
    orderQuantity: 100, departmentCode: 'D01', materialCount: 2,
  },
  {
    sourceType: 'production-order', sourceDocumentId: 10001,
    sourceDocumentNo: 'MO-20261011-001', sourceLineId: 11002, sourceLineNo: 20,
    documentDate: '2026-10-11', productCode: 'P1002', productName: '产品B',
    orderQuantity: 80, departmentCode: 'D01', materialCount: 2,
  },
  {
    sourceType: 'production-order', sourceDocumentId: 10002,
    sourceDocumentNo: 'MO-20261010-002', sourceLineId: 12001, sourceLineNo: 10,
    documentDate: '2026-10-10', productCode: 'P2001', productName: '产品C',
    orderQuantity: 50, departmentCode: 'D02', materialCount: 1,
  },
]

const mockRequirements: MaterialRequirementLine[] = [
  {
    sourceType: 'production-order', sourceDocumentId: 10001,
    sourceDocumentNo: 'MO-20261011-001', sourceLineId: 11001, sourceLineNo: 10,
    requirementId: 21001, productCode: 'P1001', productName: '产品A',
    materialCode: 'M1001', materialName: '材料一', departmentCode: 'D01',
    operationSequence: '10', requiresBatch: true, plannedQuantity: 60,
    issuedQuantity: 60, remainingQuantity: 0,
  },
  {
    sourceType: 'production-order', sourceDocumentId: 10001,
    sourceDocumentNo: 'MO-20261011-001', sourceLineId: 11002, sourceLineNo: 20,
    requirementId: 21002, productCode: 'P1002', productName: '产品B',
    materialCode: 'M1001', materialName: '材料一', departmentCode: 'D01',
    operationSequence: '10', requiresBatch: true, plannedQuantity: 40,
    issuedQuantity: 40, remainingQuantity: 0,
  },
  {
    sourceType: 'production-order', sourceDocumentId: 10001,
    sourceDocumentNo: 'MO-20261011-001', sourceLineId: 11001, sourceLineNo: 10,
    requirementId: 21003, productCode: 'P1001', productName: '产品A',
    materialCode: 'M1002', materialName: '材料二', departmentCode: 'D01',
    operationSequence: '20', requiresBatch: false, plannedQuantity: 50,
    issuedQuantity: 20, remainingQuantity: 30,
  },
  {
    sourceType: 'production-order', sourceDocumentId: 10001,
    sourceDocumentNo: 'MO-20261011-001', sourceLineId: 11002, sourceLineNo: 20,
    requirementId: 21004, productCode: 'P1002', productName: '产品B',
    materialCode: 'M1003', materialName: '材料三', departmentCode: 'D01',
    operationSequence: '20', requiresBatch: false, plannedQuantity: 25,
    issuedQuantity: 5, remainingQuantity: 20,
  },
  {
    sourceType: 'production-order', sourceDocumentId: 10002,
    sourceDocumentNo: 'MO-20261010-002', sourceLineId: 12001, sourceLineNo: 10,
    requirementId: 22001, productCode: 'P2001', productName: '产品C',
    materialCode: 'M2001', materialName: '材料四', departmentCode: 'D02',
    operationSequence: '10', requiresBatch: false, plannedQuantity: 100,
    issuedQuantity: 40, remainingQuantity: 60,
  },
]

function shouldUseMockData(): boolean {
  const config = getRuntimeConfig()
  return config.environment === 'development' && config.enableMockSourceDocuments
}

function toQueryString(query: SourceDocumentQuery): string {
  const params = new URLSearchParams({ sourceType: query.sourceType, limit: '100' })
  if (query.keyword.trim()) params.set('keyword', query.keyword.trim())
  if (query.startDate) params.set('startDate', query.startDate)
  if (query.endDate) params.set('endDate', query.endDate)
  return params.toString()
}

function normalizeSourceLine(line: SourceDocumentLine): SourceDocumentLine {
  return {
    ...line,
    documentDate: String(line.documentDate || '').slice(0, 10),
    orderQuantity: Number(line.orderQuantity) || 0,
    materialCount: Number(line.materialCount) || 0,
  }
}

export async function searchSourceDocuments(
  query: SourceDocumentQuery,
): Promise<SourceDocumentLine[]> {
  if (query.documentType === 'inventory-count') {
    const result = await searchInventoryCounts(query.keyword, query.startDate, query.endDate)
    if (result.hasMore) throw new Error('结果超过100条，请缩小查询范围')
    return result.items.map(item => ({ sourceType: 'inventory-count', sourceDocumentId: item.id,
      sourceDocumentNo: item.documentNo, sourceLineId: item.id, sourceLineNo: null,
      documentDate: item.documentDate?.slice(0, 10) ?? '', productCode: item.warehouseCode,
      productName: '', orderQuantity: 0, departmentCode: '', materialCount: item.lineCount,
      warehouseCode: item.warehouseCode, warehouseName: item.partnerName,
      sourceCreatedBy: item.createdBy, sourceStatus: item.status }))
  }
  if (shouldUseMockData()) {
    const keyword = query.keyword.trim().toLocaleLowerCase()
    return mockSourceLines.filter((line) => {
      if (line.sourceType !== query.sourceType) return false
      if (query.startDate && line.documentDate < query.startDate) return false
      if (query.endDate && line.documentDate > query.endDate) return false
      if (!keyword) return true
      return [line.sourceDocumentNo, line.productCode, line.productName]
        .some((value) => value.toLocaleLowerCase().includes(keyword))
    })
  }

  const result = await apiRequest<SourceDocumentSearchResult>(
    `/api/v1/source-documents?${toQueryString(query)}`,
    { headers: { 'X-Function-Code': query.documentType } },
  )
  return result.items.map(normalizeSourceLine)
}

export async function loadMaterialRequirements(
  documentType: OutboundDocumentType,
  sourceType: string,
  sourceLineIds: number[],
): Promise<MaterialRequirementLine[]> {
  if (shouldUseMockData()) {
    const selected = new Set(sourceLineIds)
    return mockRequirements.filter(
      (line) => line.sourceType === sourceType && selected.has(line.sourceLineId),
    )
  }

  return apiRequest<MaterialRequirementLine[]>('/api/v1/source-documents/material-requirements', {
    method: 'POST',
    headers: { 'X-Function-Code': documentType },
    body: JSON.stringify({ sourceType, sourceLineIds }),
  })
}

export async function loadSourceDocumentLines(
  documentType: OutboundDocumentType,
  sourceType: string,
  sourceDocumentIds: number[],
): Promise<SourceDocumentLine[]> {
  if (shouldUseMockData()) {
    const selected = new Set(sourceDocumentIds)
    return mockSourceLines.filter(
      (line) => line.sourceType === sourceType && selected.has(line.sourceDocumentId),
    )
  }

  const result = await apiRequest<SourceDocumentLine[]>('/api/v1/source-documents/lines', {
    method: 'POST',
    headers: { 'X-Function-Code': documentType },
    body: JSON.stringify({ sourceType, sourceDocumentIds }),
  })
  return result.map(normalizeSourceLine)
}

export function saveSourceDocumentSelection(selection: SourceDocumentSelection): void {
  sessionStorage.setItem(scopedStorageKey(selectionKey), JSON.stringify(selection))
}

export function consumeSourceDocumentSelection(
  outboundDocumentType: OutboundDocumentType,
): SourceDocumentSelection | null {
  const raw = sessionStorage.getItem(scopedStorageKey(selectionKey))
  if (!raw) return null
  sessionStorage.removeItem(scopedStorageKey(selectionKey))

  try {
    const selection = JSON.parse(raw) as Partial<SourceDocumentSelection>
    if (
      selection.outboundDocumentType !== outboundDocumentType ||
      !selection.sourceType || !selection.sourceTypeLabel || !selection.documentNo ||
      !Array.isArray(selection.selectedLines) || !Array.isArray(selection.requirements)
    ) return null
    return selection as SourceDocumentSelection
  } catch {
    return null
  }
}

export function saveReturnRequirementSelection(selection: ReturnRequirementSelection): void {
  sessionStorage.setItem(scopedStorageKey(returnRequirementSelectionKey), JSON.stringify(selection))
}

export function consumeReturnRequirementSelection(
  outboundDocumentType: OutboundDocumentType,
): MaterialRequirementLine | null {
  const raw = sessionStorage.getItem(scopedStorageKey(returnRequirementSelectionKey))
  if (!raw) return null
  sessionStorage.removeItem(scopedStorageKey(returnRequirementSelectionKey))

  try {
    const selection = JSON.parse(raw) as Partial<ReturnRequirementSelection>
    const requirement = selection.requirement
    if (
      selection.outboundDocumentType !== outboundDocumentType ||
      !requirement || !Number.isInteger(requirement.requirementId) || requirement.requirementId <= 0
    ) return null
    return requirement
  } catch {
    return null
  }
}
