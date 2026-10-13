import { ApiError, apiRequest } from '@/api/client'
import { readSession } from '@/services/auth'
import { getDeviceAppVersion, getDeviceId, getDeviceName } from '@/services/device-identity'
import {
  confirmLocalDraftDeletion,
  importRemoteDocument,
  listAllLocalDocuments,
  markDraftSynced,
  markDraftSyncFailed,
  markDraftSyncing,
  subscribeDraftChanges,
  type OutboundDocumentUploadStatus,
  type StoredOutboundDocument,
} from '@/services/outbound-document-drafts'
import { isBusinessDocumentType } from '@/services/business-document-config'

interface ServerDraftItem {
  id: string
  ownerUserId: string
  ownerName: string
  deviceId: string
  deviceName: string | null
  documentType: string
  businessStatus: 'DRAFT' | 'PREPARED' | 'COMPLETED' | 'CANCELLED' | 'DELETED'
  schemaVersion: number
  clientRevision: number
  documentDate: string | null
  sourceTypeLabel: string | null
  sourceDocumentNo: string | null
  warehouseCode: string | null
  departmentCode: string | null
  lineCount: number
  totalQuantity: number
  operationGroupId: string | null
  createdAt: string
  updatedAt: string
  lastSyncedAt: string
  deletedAt: string | null
  rowVersion: string
  payload: unknown | null
}

let started = false
let running = false
let delayTimer: ReturnType<typeof setTimeout> | null = null

function businessStatus(document: StoredOutboundDocument): ServerDraftItem['businessStatus'] {
  if (document.uploadStatus === 'prepared') return 'PREPARED'
  if (document.uploadStatus === 'cancelled') return 'CANCELLED'
  if (document.uploadStatus === 'completed' || document.uploadStatus === 'uploaded') return 'COMPLETED'
  return 'DRAFT'
}

function headerValue(document: StoredOutboundDocument, key: string): string | null {
  if (!document.payload || typeof document.payload !== 'object') return null
  const value = (document.payload as Record<string, unknown>)[key]
  return typeof value === 'string' && value.trim() ? value.trim() : null
}

async function syncOne(document: StoredOutboundDocument): Promise<boolean> {
  const revision = document.clientRevision
  if (document.syncStatus === 'deleted-pending') {
    try {
      await apiRequest<ServerDraftItem>(`/api/v1/drafts/${encodeURIComponent(document.id)}`, {
        method: 'DELETE',
        body: JSON.stringify({ deviceId: getDeviceId(), clientRevision: revision }),
      })
      await confirmLocalDraftDeletion(document.id)
      return true
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) {
        await confirmLocalDraftDeletion(document.id)
        return true
      }
      throw error
    }
  }

  await markDraftSyncing(document.id, revision)
  const result = await apiRequest<ServerDraftItem>(
    `/api/v1/drafts/${encodeURIComponent(document.id)}`,
    {
      method: 'PUT',
      timeoutMs: 45_000,
      body: JSON.stringify({
        deviceId: getDeviceId(),
        deviceName: getDeviceName(),
        documentType: document.documentType,
        businessStatus: businessStatus(document),
        schemaVersion: 1,
        clientRevision: revision,
        createdAt: document.createdAt,
        updatedAt: document.updatedAt,
        operationGroupId: document.serverOperationId || null,
        summary: {
          documentDate: document.summary.documentDate || null,
          sourceTypeLabel: document.summary.sourceTypeLabel || null,
          sourceDocumentNo: document.summary.sourceDocumentNo || null,
          warehouseCode: headerValue(document, 'warehouseCode'),
          departmentCode: headerValue(document, 'departmentCode'),
          lineCount: document.summary.lineCount,
          totalQuantity: document.summary.totalQty,
        },
        payload: document.payload,
      }),
    },
  )
  await markDraftSynced(document.id, revision, result.lastSyncedAt)
  return true
}

async function sendHeartbeat(documents: StoredOutboundDocument[], synced: boolean): Promise<void> {
  const userId = readSession()?.user.id
  if (!userId) return
  const owned = documents.filter((document) => document.ownerUserId === userId)
  await apiRequest<{ updated: boolean }>('/api/v1/drafts/device-heartbeat', {
    method: 'POST',
    body: JSON.stringify({
      deviceId: getDeviceId(),
      deviceName: getDeviceName(),
      deviceModel: navigator.userAgent.slice(0, 180),
      appVersion: getDeviceAppVersion(),
      localDraftCount: owned.filter((item) => item.syncStatus !== 'deleted-pending').length,
      pendingSyncCount: owned.filter((item) => ['pending', 'syncing', 'deleted-pending'].includes(item.syncStatus)).length,
      failedSyncCount: owned.filter((item) => item.syncStatus === 'failed').length,
      lastSuccessfulSyncAt: synced ? new Date().toISOString() : null,
    }),
  })
}

async function pullDeletedDrafts(): Promise<number> {
  const session = readSession()
  if (!session) return 0
  const deleted = await apiRequest<ServerDraftItem[]>('/api/v1/drafts?status=DELETED&take=200')
  const localIds = new Set((await listAllLocalDocuments()).map((item) => item.id))
  let removed = 0
  for (const item of deleted) {
    if (item.ownerUserId !== session.user.id || !localIds.has(item.id)) continue
    await confirmLocalDraftDeletion(item.id)
    removed++
  }
  return removed
}

export async function syncPendingDocumentDrafts(includeFailed = false): Promise<void> {
  const session = readSession()
  if (!session || !navigator.onLine || running) return
  running = true
  let synced = false
  try {
    try { synced = await pullDeletedDrafts() > 0 } catch { /* 下次同步重试。 */ }
    const documents = await listAllLocalDocuments()
    const pending = documents.filter((document) =>
      document.ownerUserId === session.user.id &&
      (['pending', 'deleted-pending'].includes(document.syncStatus) ||
        (includeFailed && document.syncStatus === 'failed')))
    for (const document of pending) {
      try {
        synced = await syncOne(document) || synced
      } catch (error) {
        if (document.syncStatus !== 'deleted-pending') {
          await markDraftSyncFailed(
            document.id,
            document.clientRevision,
            error instanceof Error ? error.message : '同步失败',
          )
        }
        if (error instanceof ApiError && error.status === 401) break
      }
    }
    try { await sendHeartbeat(await listAllLocalDocuments(), synced) } catch { /* 下次同步补报。 */ }
  } finally {
    running = false
  }
}

function scheduleSync(): void {
  if (delayTimer) clearTimeout(delayTimer)
  delayTimer = setTimeout(() => void syncPendingDocumentDrafts(false), 3_000)
}

export function startDocumentDraftSync(): void {
  if (started) return
  started = true
  subscribeDraftChanges(scheduleSync)
  window.addEventListener('label-system:auth-changed', scheduleSync)
  window.addEventListener('online', () => void syncPendingDocumentDrafts(true))
  window.setInterval(() => void syncPendingDocumentDrafts(true), 30_000)
  scheduleSync()
}

function uploadStatus(status: ServerDraftItem['businessStatus']): OutboundDocumentUploadStatus {
  if (status === 'PREPARED') return 'prepared'
  if (status === 'CANCELLED' || status === 'DELETED') return 'cancelled'
  if (status === 'COMPLETED') return 'completed'
  return 'pending'
}

function toStored(item: ServerDraftItem): StoredOutboundDocument {
  if (!isBusinessDocumentType(item.documentType)) throw new Error('单据类型无效')
  return {
    id: item.id,
    documentType: item.documentType,
    uploadStatus: uploadStatus(item.businessStatus),
    createdAt: item.createdAt,
    updatedAt: item.updatedAt,
    uploadedAt: item.businessStatus === 'DRAFT' ? null : item.updatedAt,
    uploadAttempts: 0,
    lastUploadError: '',
    serverOperationId: item.operationGroupId ?? '',
    serverStatus: item.businessStatus === 'PREPARED' ? 'PENDING'
      : item.businessStatus === 'CANCELLED' || item.businessStatus === 'DELETED' ? 'CANCELLED'
        : item.businessStatus === 'COMPLETED' ? 'SUCCESS' : undefined,
    summary: {
      documentDate: item.documentDate ?? '',
      sourceTypeLabel: item.sourceTypeLabel ?? '',
      sourceDocumentNo: item.sourceDocumentNo ?? '',
      lineCount: item.lineCount,
      totalQty: item.totalQuantity,
    },
    payload: item.payload ?? {},
    ownerUserId: item.ownerUserId,
    ownerName: item.ownerName,
    deviceId: item.deviceId,
    deviceName: item.deviceName ?? undefined,
    clientRevision: item.clientRevision,
    syncStatus: 'synced',
    lastSyncedAt: item.lastSyncedAt,
    syncError: '',
    remoteOnly: item.payload === null,
  }
}

export async function listServerDocumentDrafts(
  documentType: string,
  status: ServerDraftItem['businessStatus'] | null,
  allUsers: boolean,
): Promise<StoredOutboundDocument[]> {
  const query = new URLSearchParams({ documentType, take: '200' })
  if (status) query.set('status', status)
  if (allUsers) query.set('allUsers', 'true')
  const items = await apiRequest<ServerDraftItem[]>(`/api/v1/drafts?${query.toString()}`)
  const localIds = new Set((await listAllLocalDocuments()).map((item) => item.id))
  for (const item of items) {
    if (item.businessStatus === 'DELETED' && item.ownerUserId === readSession()?.user.id &&
      localIds.has(item.id)) {
      await confirmLocalDraftDeletion(item.id)
    }
  }
  return items.filter((item) => item.businessStatus !== 'DELETED').map(toStored)
}

export async function downloadServerDocumentDraft(id: string): Promise<StoredOutboundDocument> {
  const result = toStored(await apiRequest<ServerDraftItem>(`/api/v1/drafts/${encodeURIComponent(id)}`))
  await importRemoteDocument(result)
  return result
}
