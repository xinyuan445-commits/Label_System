import { apiRequest } from '@/api/client'
import type { CountCheck, CountDraft, CountSource } from './inventory-count-model'
import type { InventoryObjectLine } from './inventory-object-lookup'

export interface CountContext { accountId: string; databaseName: string; uploadSaveEnabled?: boolean }
export interface CountWarehouse extends CountContext { warehouseCode: string; warehouseName: string; positionManaged: boolean }
export async function getCountWarehouse(code: string): Promise<CountWarehouse> {
  return apiRequest('/api/v1/inventory-counts/warehouses/' + encodeURIComponent(code))
}
export interface DirectCountCheck { code: string; status: string; message: string; label?: InventoryObjectLine | null }
export async function getCountContext(): Promise<CountContext> {
  return apiRequest('/api/v1/inventory-counts/capabilities')
}
export async function validateDirectCount(context: CountContext, warehouseCode: string,
  observations: Array<{ code: string; quantity: string }>, countByLocation: boolean): Promise<{ items: DirectCountCheck[]; canSubmit: false }> {
  return apiRequest('/api/v1/inventory-counts/validate-direct', { method: 'POST', timeoutMs: 120000,
    body: JSON.stringify({ ...context, warehouseCode, observations, countByLocation }) })
}

export interface CountSummary {
  id: number
  documentNo: string
  documentDate: string | null
  warehouseCode: string
  partnerName: string
  createdBy?: string | null
  status?: string
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
  return validateInventoryCountItems(draft.task.source, draft.observations)
}

export async function validateInventoryCountItems(source: CountSource,
  observations: Array<{ code: string; quantity: string | null }>): Promise<{ items: CountCheck[]; canSubmit: false }> {
  return apiRequest(`/api/v1/inventory-counts/${source.document.id}/validate`, {
    method: 'POST', timeoutMs: 120000,
    body: JSON.stringify({ accountId: source.accountId, databaseName: source.databaseName,
      sourceFingerprint: source.fingerprint,
      observations: observations.map(row => ({ code: row.code, quantity: row.quantity })) }),
  })
}
