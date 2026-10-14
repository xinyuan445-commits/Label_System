import type { OutboundDocumentType } from '@/services/source-document-selection'
import { readSession } from '@/services/auth'
import {
  readDraftDocument,
  readDraftDocuments,
  removeDraftDocument,
  removeDraftDocuments,
  writeDraftDocument,
  writeDraftDocuments,
} from '@/services/draft-database'
import { getDeviceId, getDeviceName } from '@/services/device-identity'
import { createUniqueId } from '@/utils/unique-id'

export type OutboundDocumentUploadStatus =
  | 'pending'
  | 'attention'
  | 'completed'
  | 'cancelled'
  | 'uploaded'
  | 'prepared'

export type DraftSyncStatus =
  | 'local'
  | 'pending'
  | 'syncing'
  | 'synced'
  | 'failed'
  | 'deleted-pending'

export interface PreparedOperation {
  id: number
  operationNo: string
  sourceDocumentNo: string
  warehouseCode: string
  lineCount: number
  totalQty: number
  status?: string
  erpStatus?: string
  approvalStatus?: string
  erpDocumentNo?: string | null
  errorCode?: string | null
  errorMessage?: string | null
  createdAt?: string | null
  updatedAt?: string | null
  completedAt?: string | null
}

export interface OperationGroupSnapshot {
  submissionGroupId: string
  documentType?: string
  status: string
  isTerminal?: boolean
  operatorId?: string
  operatorName?: string
  sourceDocumentNo?: string
  lineCount?: number
  totalQty?: number
  lockExpiresAt: string | null
  createdAt?: string
  updatedAt?: string
  completedAt?: string | null
  errorCode?: string | null
  errorMessage?: string | null
  operations: PreparedOperation[]
  issuedLabels?: Array<{
    code: string
    materialCode: string
    materialName?: string | null
    batchNo?: string | null
    unitCode: string
    quantity: number
    warehouseCode: string
    locationCode?: string | null
    status: string
  }>
}

export interface OutboundDocumentSummary {
  documentDate: string
  sourceTypeLabel: string
  sourceDocumentNo: string
  lineCount: number
  totalQty: number
}

export interface StoredOutboundDocument {
  id: string
  documentType: OutboundDocumentType
  uploadStatus: OutboundDocumentUploadStatus
  createdAt: string
  updatedAt: string
  uploadedAt: string | null
  uploadAttempts: number
  uploadRequestId?: string
  lastUploadError: string
  serverOperationId: string
  serverStatus?: string
  lockExpiresAt?: string | null
  serverCheckedAt?: string | null
  serverErrorCode?: string | null
  serverErrorMessage?: string | null
  u8WriteEnabled?: boolean
  preparedOperations?: PreparedOperation[]
  issuedLabels?: OperationGroupSnapshot['issuedLabels']
  summary: OutboundDocumentSummary
  payload: unknown
  ownerUserId: string
  ownerName: string
  deviceId: string
  deviceName?: string
  clientRevision: number
  syncStatus: DraftSyncStatus
  lastSyncedAt: string | null
  syncError: string
  remoteOnly?: boolean
  operationOnly?: boolean
}

export interface OutboundDocumentEditRequest {
  documentType: OutboundDocumentType
  documentId: string
}

export interface OutboundDocumentWorkingState {
  documentType: OutboundDocumentType
  documentId: string | null
  currentStep: 1 | 2
  payload: unknown
  baselineSnapshot?: string
}

const legacyStorageKey = 'label-system:outbound-documents:v1'
const migrationMarkerKey = 'label-system:outbound-documents:indexeddb-v1'
const editRequestKey = 'label-system:outbound-document-edit'
const workingStateKey = 'label-system:outbound-document-working'
const changeEventName = 'label-system:draft-changed'
const hiddenDocumentRecordsKey = 'label-system:hidden-document-records:v1'
let migrationPromise: Promise<void> | null = null

function notifyChanged(): void {
  window.dispatchEvent(new Event(changeEventName))
}

function readHiddenDocumentRecordIds(): Set<string> {
  try {
    const value = JSON.parse(localStorage.getItem(hiddenDocumentRecordsKey) ?? '[]') as unknown
    return new Set(Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : [])
  } catch {
    return new Set()
  }
}

function writeHiddenDocumentRecordIds(ids: Set<string>): void {
  localStorage.setItem(hiddenDocumentRecordsKey, JSON.stringify([...ids].slice(-1000)))
}

function documentRecordIds(document: Pick<StoredOutboundDocument, 'id' | 'serverOperationId'>): string[] {
  return [document.id, document.serverOperationId].filter((id) => Boolean(id))
}

export function isLocalDocumentRecordHidden(
  document: Pick<StoredOutboundDocument, 'id' | 'serverOperationId'>,
): boolean {
  const hidden = readHiddenDocumentRecordIds()
  return documentRecordIds(document).some((id) => hidden.has(id))
}

export function isLocalOperationGroupHidden(groupId: string): boolean {
  return readHiddenDocumentRecordIds().has(groupId)
}

function storageSafeValue<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

export function subscribeDraftChanges(listener: () => void): () => void {
  window.addEventListener(changeEventName, listener)
  return () => window.removeEventListener(changeEventName, listener)
}

function isAdmin(): boolean {
  return readSession()?.user.roles.some((role) => role.toLowerCase() === 'admin') === true
}

function currentOwner(): { id: string; name: string } {
  const user = readSession()?.user
  if (!user) throw new Error('请先登录')
  return user
}

function normalizedDocument(document: Partial<StoredOutboundDocument>): StoredOutboundDocument | null {
  if (!document.id || !document.documentType || !document.summary) return null
  const owner = readSession()?.user
  return {
    id: document.id,
    documentType: document.documentType,
    uploadStatus: document.uploadStatus ?? 'pending',
    createdAt: document.createdAt ?? beijingTimestamp(),
    updatedAt: document.updatedAt ?? document.createdAt ?? beijingTimestamp(),
    uploadedAt: document.uploadedAt ?? null,
    uploadAttempts: document.uploadAttempts ?? 0,
    uploadRequestId: document.uploadRequestId ?? '',
    lastUploadError: document.lastUploadError ?? '',
    serverOperationId: document.serverOperationId ?? '',
    serverStatus: document.serverStatus,
    lockExpiresAt: document.lockExpiresAt,
    serverCheckedAt: document.serverCheckedAt,
    serverErrorCode: document.serverErrorCode,
    serverErrorMessage: document.serverErrorMessage,
    u8WriteEnabled: document.u8WriteEnabled,
    preparedOperations: document.preparedOperations,
    issuedLabels: document.issuedLabels ?? [],
    summary: document.summary,
    payload: document.payload ?? {},
    ownerUserId: document.ownerUserId ?? owner?.id ?? 'unknown',
    ownerName: document.ownerName ?? owner?.name ?? '',
    deviceId: document.deviceId ?? getDeviceId(),
    deviceName: document.deviceName ?? getDeviceName(),
    clientRevision: Math.max(1, document.clientRevision ?? 1),
    syncStatus: document.syncStatus ?? 'pending',
    lastSyncedAt: document.lastSyncedAt ?? null,
    syncError: document.syncError ?? '',
    remoteOnly: document.remoteOnly ?? false,
    operationOnly: document.operationOnly ?? false,
  }
}

async function ensureMigration(): Promise<void> {
  if (migrationPromise) return migrationPromise
  migrationPromise = (async () => {
    if (localStorage.getItem(migrationMarkerKey)) return
    const raw = localStorage.getItem(legacyStorageKey)
    if (raw) {
      try {
        const parsed = JSON.parse(raw) as Array<Partial<StoredOutboundDocument>>
        const documents = Array.isArray(parsed)
          ? parsed.map(normalizedDocument).filter((item): item is StoredOutboundDocument => item !== null)
          : []
        await writeDraftDocuments(documents)
        localStorage.removeItem(legacyStorageKey)
      } catch {
        return
      }
    }
    localStorage.setItem(migrationMarkerKey, beijingTimestamp())
  })()
  return migrationPromise
}

async function readAll(): Promise<StoredOutboundDocument[]> {
  await ensureMigration()
  return readDraftDocuments()
}

function beijingTimestamp(): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai', year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
  }).formatToParts(new Date())
  const value = Object.fromEntries(parts.map((part) => [part.type, part.value]))
  return `${value.year}-${value.month}-${value.day}T${value.hour}:${value.minute}:${value.second}+08:00`
}

function localStatusForServer(status: string, isTerminal = false): OutboundDocumentUploadStatus {
  if (status === 'CANCELLED') return 'cancelled'
  if (status === 'FAILED') return 'pending'
  return isTerminal ? 'completed' : 'attention'
}

async function updateDocument(
  id: string,
  create: (existing: StoredOutboundDocument) => StoredOutboundDocument,
): Promise<StoredOutboundDocument> {
  await ensureMigration()
  const existing = await readDraftDocument(id)
  if (!existing) throw new Error('本地单据不存在')
  const updated = create(existing)
  await writeDraftDocument(updated)
  notifyChanged()
  return updated
}

export async function listAllLocalDocuments(): Promise<StoredOutboundDocument[]> {
  return readAll()
}

export async function listOutboundDocuments(
  documentType: OutboundDocumentType,
  uploadStatus: OutboundDocumentUploadStatus,
): Promise<StoredOutboundDocument[]> {
  const session = readSession()
  return (await readAll())
    .filter((document) => document.syncStatus !== 'deleted-pending' &&
      document.documentType === documentType && document.uploadStatus === uploadStatus &&
      (isAdmin() || document.ownerUserId === session?.user.id))
    .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
}

export async function getOutboundDocument(id: string): Promise<StoredOutboundDocument | null> {
  await ensureMigration()
  return readDraftDocument(id)
}

export async function saveOutboundDocument(input: {
  id?: string
  documentType: OutboundDocumentType
  summary: OutboundDocumentSummary
  payload: unknown
}): Promise<StoredOutboundDocument> {
  await ensureMigration()
  const now = beijingTimestamp()
  const owner = currentOwner()
  const safeSummary = storageSafeValue(input.summary)
  const safePayload = storageSafeValue(input.payload)
  const existing = input.id ? await readDraftDocument(input.id) : null
  if (existing) {
    if (existing.ownerUserId !== owner.id) throw new Error('不能修改其他用户的草稿')
    const updated: StoredOutboundDocument = {
      ...existing, updatedAt: now, uploadStatus: 'pending', uploadedAt: null,
      uploadRequestId: '', lastUploadError: '', serverOperationId: '',
      serverStatus: undefined, serverErrorCode: undefined, serverErrorMessage: undefined,
      summary: safeSummary, payload: safePayload,
      clientRevision: existing.clientRevision + 1, syncStatus: 'pending', syncError: '',
      remoteOnly: false, deviceId: getDeviceId(), deviceName: getDeviceName(),
    }
    await writeDraftDocument(updated)
    notifyChanged()
    return updated
  }

  const created: StoredOutboundDocument = {
    id: createUniqueId('draft'), documentType: input.documentType,
    uploadStatus: 'pending', createdAt: now, updatedAt: now, uploadedAt: null,
    uploadAttempts: 0, uploadRequestId: '', lastUploadError: '', serverOperationId: '',
    summary: safeSummary, payload: safePayload,
    ownerUserId: owner.id, ownerName: owner.name,
    deviceId: getDeviceId(), deviceName: getDeviceName(), clientRevision: 1,
    syncStatus: 'pending', lastSyncedAt: null, syncError: '', remoteOnly: false,
  }
  await writeDraftDocument(created)
  notifyChanged()
  return created
}

export async function markOutboundDocumentUploaded(
  id: string, serverOperationId: string,
): Promise<StoredOutboundDocument> {
  return updateDocument(id, (existing) => {
    const now = beijingTimestamp()
    return { ...existing, uploadStatus: 'uploaded', uploadedAt: now, updatedAt: now,
      uploadAttempts: existing.uploadAttempts + 1, lastUploadError: '', serverOperationId,
      clientRevision: existing.clientRevision + 1, syncStatus: 'pending', syncError: '' }
  })
}

export async function markOutboundDocumentSubmitted(
  id: string, result: OperationGroupSnapshot,
): Promise<StoredOutboundDocument> {
  return updateDocument(id, (existing) => {
    const now = beijingTimestamp()
    return { ...existing, uploadStatus: localStatusForServer(result.status, result.isTerminal), uploadedAt: now,
      updatedAt: now, uploadAttempts: existing.uploadAttempts + 1, uploadRequestId: '',
      lastUploadError: result.status === 'FAILED' ? result.errorMessage || '上传失败' : '',
      serverOperationId: result.submissionGroupId, serverStatus: result.status,
      lockExpiresAt: result.lockExpiresAt, serverCheckedAt: now,
      serverErrorCode: result.errorCode, serverErrorMessage: result.errorMessage,
      u8WriteEnabled: true, preparedOperations: result.operations,
      issuedLabels: result.issuedLabels ?? existing.issuedLabels ?? [],
      clientRevision: existing.clientRevision + 1, syncStatus: 'pending', syncError: '' }
  })
}

export async function beginOutboundDocumentUpload(id: string, retryFailed = false): Promise<string> {
  let requestId = ''
  await updateDocument(id, (existing) => {
    requestId = existing.uploadRequestId || (retryFailed || existing.serverStatus === 'FAILED'
      ? createUniqueId('retry')
      : existing.id)
    return { ...existing, uploadRequestId: requestId }
  })
  return requestId
}

export async function updateOutboundDocumentGroup(
  id: string, result: OperationGroupSnapshot,
): Promise<StoredOutboundDocument> {
  return updateDocument(id, (existing) => {
    const now = beijingTimestamp()
    return { ...existing, uploadStatus: localStatusForServer(result.status, result.isTerminal), updatedAt: now,
      uploadRequestId: '',
      lastUploadError: result.status === 'FAILED' ? result.errorMessage || '上传失败' : '',
      serverOperationId: result.submissionGroupId,
      serverStatus: result.status, lockExpiresAt: result.lockExpiresAt,
      serverErrorCode: result.errorCode, serverErrorMessage: result.errorMessage,
      serverCheckedAt: now, preparedOperations: result.operations,
      issuedLabels: result.issuedLabels ?? existing.issuedLabels ?? [],
      clientRevision: existing.clientRevision + 1, syncStatus: 'pending', syncError: '' }
  })
}

export async function markOutboundDocumentUploadFailed(
  id: string, error: string,
): Promise<StoredOutboundDocument> {
  return updateDocument(id, (existing) => ({ ...existing, updatedAt: beijingTimestamp(),
    uploadAttempts: existing.uploadAttempts + 1, lastUploadError: error }))
}

export async function markDraftSyncing(id: string, revision: number): Promise<void> {
  const existing = await readDraftDocument(id)
  if (!existing || existing.clientRevision !== revision || existing.syncStatus === 'deleted-pending') return
  await writeDraftDocument({ ...existing, syncStatus: 'syncing', syncError: '' })
}

export async function markDraftSynced(id: string, revision: number, syncedAt: string): Promise<void> {
  const existing = await readDraftDocument(id)
  if (!existing || existing.clientRevision !== revision) return
  await writeDraftDocument({ ...existing, syncStatus: 'synced', lastSyncedAt: syncedAt,
    syncError: '', remoteOnly: false })
  notifyChanged()
}

export async function markDraftSyncFailed(id: string, revision: number, error: string): Promise<void> {
  const existing = await readDraftDocument(id)
  if (!existing || existing.clientRevision !== revision || existing.syncStatus === 'deleted-pending') return
  await writeDraftDocument({ ...existing, syncStatus: 'failed', syncError: error })
  notifyChanged()
}

export async function deleteOutboundDocument(id: string): Promise<void> {
  const existing = await getOutboundDocument(id)
  if (!existing) return
  await writeDraftDocument({ ...existing, clientRevision: existing.clientRevision + 1,
    syncStatus: 'deleted-pending', updatedAt: beijingTimestamp(), syncError: '' })
  notifyChanged()
}

export async function deleteLocalDocumentRecords(
  documents: Array<Pick<StoredOutboundDocument, 'id' | 'serverOperationId'>>,
): Promise<void> {
  if (!documents.length) return
  const hidden = readHiddenDocumentRecordIds()
  for (const document of documents) {
    for (const id of documentRecordIds(document)) hidden.add(id)
  }
  writeHiddenDocumentRecordIds(hidden)
  await removeDraftDocuments(documents.map((document) => document.id))
  notifyChanged()
}

export async function confirmLocalDraftDeletion(id: string): Promise<void> {
  await removeDraftDocument(id)
  notifyChanged()
}

export async function importRemoteDocument(document: StoredOutboundDocument): Promise<void> {
  const hidden = readHiddenDocumentRecordIds()
  for (const id of documentRecordIds(document)) hidden.delete(id)
  writeHiddenDocumentRecordIds(hidden)
  await writeDraftDocument({ ...document, remoteOnly: false, syncStatus: 'synced' })
  notifyChanged()
}

export function requestOutboundDocumentEdit(request: OutboundDocumentEditRequest): void {
  sessionStorage.setItem(editRequestKey, JSON.stringify(request))
}

export function clearOutboundDocumentEditRequest(): void {
  sessionStorage.removeItem(editRequestKey)
}

export function saveOutboundDocumentWorkingState(state: OutboundDocumentWorkingState): void {
  sessionStorage.setItem(workingStateKey, JSON.stringify(state))
}

export function consumeOutboundDocumentWorkingState(
  documentType: OutboundDocumentType,
): OutboundDocumentWorkingState | null {
  const state = readOutboundDocumentWorkingState(documentType)
  if (!state) return null
  sessionStorage.removeItem(workingStateKey)
  return state
}

export function readOutboundDocumentWorkingState(
  documentType: OutboundDocumentType,
): OutboundDocumentWorkingState | null {
  const raw = sessionStorage.getItem(workingStateKey)
  if (!raw) return null
  try {
    const state = JSON.parse(raw) as Partial<OutboundDocumentWorkingState>
    if (state.documentType !== documentType || !state.payload) return null
    return state as OutboundDocumentWorkingState
  } catch { return null }
}

export async function findPendingSourceLineConflicts(
  documentType: OutboundDocumentType,
  sourceLineIds: number[],
  excludeDocumentId: string | null = null,
): Promise<number[]> {
  const requested = new Set(sourceLineIds.filter((id) => Number.isInteger(id) && id > 0))
  if (!requested.size) return []
  const conflicts = new Set<number>()
  const session = readSession()
  for (const document of await readAll()) {
    if (document.ownerUserId !== session?.user.id || document.documentType !== documentType ||
      document.uploadStatus !== 'pending' ||
      document.syncStatus === 'deleted-pending' || document.id === excludeDocumentId) continue
    const payload = document.payload && typeof document.payload === 'object'
      ? document.payload as { selectedSourceLines?: Array<{ sourceLineId?: number }> } : {}
    for (const line of payload.selectedSourceLines ?? []) {
      if (typeof line.sourceLineId === 'number' && requested.has(line.sourceLineId)) conflicts.add(line.sourceLineId)
    }
  }
  return [...conflicts]
}

export function clearOutboundDocumentWorkingState(): void {
  sessionStorage.removeItem(workingStateKey)
}

export function consumeOutboundDocumentEditRequest(
  documentType: OutboundDocumentType,
): OutboundDocumentEditRequest | null {
  const raw = sessionStorage.getItem(editRequestKey)
  if (!raw) return null
  sessionStorage.removeItem(editRequestKey)
  try {
    const request = JSON.parse(raw) as Partial<OutboundDocumentEditRequest>
    if (request.documentType !== documentType || !request.documentId) return null
    return request as OutboundDocumentEditRequest
  } catch { return null }
}

export async function migrateLegacyOutboundDraft(
  documentType: OutboundDocumentType,
  legacyKey: string,
  createSummary: (payload: unknown) => OutboundDocumentSummary,
): Promise<void> {
  await ensureMigration()
  const raw = localStorage.getItem(legacyKey)
  if (!raw) return
  try {
    const payload = JSON.parse(raw) as unknown
    await saveOutboundDocument({ documentType, summary: createSummary(payload), payload })
    localStorage.removeItem(legacyKey)
  } catch {
    // 旧草稿保留在原位置，下次进入时继续尝试。
  }
}
