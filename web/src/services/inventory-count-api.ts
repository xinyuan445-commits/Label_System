import { apiRequest } from '@/api/client'
import type { CountCheck, CountDraft, CountSource } from './inventory-count-model'

export interface CountSummary {
  id: number
  documentNo: string
  documentDate: string | null
  warehouseCode: string
  partnerName: string
  lineCount: number
}

export async function searchInventoryCounts(documentNo: string, startDate: string, endDate: string): Promise<{
  items: CountSummary[]; hasMore: boolean
}> {
  const query = new URLSearchParams({ documentNo })
  if (startDate) query.set('startDate', startDate)
  if (endDate) query.set('endDate', endDate)
  return apiRequest(`/api/v1/inventory-counts?${query}`)
}

export async function downloadInventoryCount(id: number): Promise<CountSource> {
  return apiRequest(`/api/v1/inventory-counts/${id}`)
}

export async function validateInventoryCount(draft: CountDraft): Promise<{ items: CountCheck[]; canSubmit: false }> {
  return apiRequest(`/api/v1/inventory-counts/${draft.task.source.document.id}/validate`, {
    method: 'POST', timeoutMs: 120000,
    body: JSON.stringify({ accountId: draft.task.source.accountId, databaseName: draft.task.source.databaseName,
      sourceFingerprint: draft.task.source.fingerprint,
      observations: draft.observations.map(row => ({ code: row.code, quantity: row.quantity })) }),
  })
}
