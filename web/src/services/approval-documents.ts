import { apiRequest } from '@/api/client'
import type { BusinessDocumentType } from '@/services/business-document-config'
import { getRuntimeConfig } from '@/services/runtime-config'
import { createUniqueId } from '@/utils/unique-id'

export type ApprovalDocumentStatus = 'pending' | 'audited'

export interface ApprovalDocumentLine {
  id: number
  lineNo: number | null
  materialCode: string
  materialName: string
  batchNo: string
  warehouseCode: string
  locationCode: string
  targetLocationCode: string
  quantity: number
}

export interface ApprovalDocument {
  id: number
  documentType: string
  documentNo: string
  documentDate: string
  partnerLabel: string
  partnerName: string
  warehouseCode: string
  createdBy: string
  status: ApprovalDocumentStatus
  auditedBy: string
  auditedAt: string | null
  version: string
  lineCount: number
  totalQuantity: number
  lines: ApprovalDocumentLine[]
  labelImpacts: never[]
}

interface ApprovalSearchResult {
  items: Omit<ApprovalDocument, 'lines' | 'labelImpacts'>[]
  limit: number
  hasMore: boolean
}

export async function fetchApprovalDocuments(
  documentType: BusinessDocumentType,
  status: ApprovalDocumentStatus,
  startDate: string,
  endDate: string,
  documentNo: string,
): Promise<ApprovalDocument[]> {
  const params = new URLSearchParams({ documentType, status, startDate, endDate, limit: '50' })
  if (documentNo) params.set('documentNo', documentNo)
  const result = await apiRequest<ApprovalSearchResult>(
    `/api/v1/approval-documents?${params.toString()}`,
  )
  return result.items.map((item) => ({ ...item, documentDate: item.documentDate?.slice(0, 10) ?? '', lines: [], labelImpacts: [] }))
}

export async function fetchApprovalDocument(
  documentType: BusinessDocumentType,
  id: number,
): Promise<ApprovalDocument> {
  const item = await apiRequest<Omit<ApprovalDocument, 'labelImpacts'>>(
    `/api/v1/approval-documents/${encodeURIComponent(documentType)}/${id}`,
  )
  return { ...item, documentDate: item.documentDate?.slice(0, 10) ?? '', labelImpacts: [] }
}

export async function submitApprovalAction(
  documentType: BusinessDocumentType,
  document: ApprovalDocument,
  action: 'audit' | 'unaudit',
  reason: string,
): Promise<void> {
  const config = getRuntimeConfig()
  await apiRequest(
    `/api/v1/approval-documents/${encodeURIComponent(documentType)}/${encodeURIComponent(document.documentNo)}/${action}`,
    {
      method: 'POST',
      body: JSON.stringify({
        clientRequestId: createUniqueId('approval'),
        expectedVersion: document.version,
        reason: reason.trim() || null,
        deviceId: null,
        deviceModel: navigator.userAgent.slice(0, 100),
        apkVersion: null,
        h5Version: config.h5Version,
        bridgeVersion: config.bridgeProtocol,
      }),
    },
  )
}
