import type { OutboundDocumentType } from '@/services/source-document-selection'

export type OutboundDocumentUploadStatus = 'pending' | 'uploaded'

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
  summary: OutboundDocumentSummary
  payload: unknown
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

const storageKey = 'label-system:outbound-documents:v1'
const editRequestKey = 'label-system:outbound-document-edit'
const workingStateKey = 'label-system:outbound-document-working'

function readAll(): StoredOutboundDocument[] {
  const raw = localStorage.getItem(storageKey)
  if (!raw) return []

  try {
    const value = JSON.parse(raw) as unknown
    return Array.isArray(value) ? (value as StoredOutboundDocument[]) : []
  } catch {
    return []
  }
}

function writeAll(documents: StoredOutboundDocument[]): void {
  localStorage.setItem(storageKey, JSON.stringify(documents))
}

function beijingTimestamp(): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hourCycle: 'h23',
  }).formatToParts(new Date())
  const value = Object.fromEntries(parts.map((part) => [part.type, part.value]))
  return `${value.year}-${value.month}-${value.day}T${value.hour}:${value.minute}:${value.second}+08:00`
}

function createDocumentId(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  return `draft-${Date.now()}-${Math.random().toString(16).slice(2)}`
}

export function listOutboundDocuments(
  documentType: OutboundDocumentType,
  uploadStatus: OutboundDocumentUploadStatus,
): StoredOutboundDocument[] {
  return readAll()
    .filter(
      (document) =>
        document.documentType === documentType && document.uploadStatus === uploadStatus,
    )
    .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
}

export function getOutboundDocument(id: string): StoredOutboundDocument | null {
  return readAll().find((document) => document.id === id) ?? null
}

export function saveOutboundDocument(input: {
  id?: string
  documentType: OutboundDocumentType
  summary: OutboundDocumentSummary
  payload: unknown
}): StoredOutboundDocument {
  const documents = readAll()
  const now = beijingTimestamp()
  const existingIndex = input.id
    ? documents.findIndex((document) => document.id === input.id)
    : -1

  if (existingIndex >= 0) {
    const existing = documents[existingIndex]
    if (!existing) throw new Error('草稿不存在')
    const updated: StoredOutboundDocument = {
      ...existing,
      updatedAt: now,
      summary: input.summary,
      payload: input.payload,
    }
    documents.splice(existingIndex, 1, updated)
    writeAll(documents)
    return updated
  }

  const created: StoredOutboundDocument = {
    id: createDocumentId(),
    documentType: input.documentType,
    uploadStatus: 'pending',
    createdAt: now,
    updatedAt: now,
    uploadedAt: null,
    summary: input.summary,
    payload: input.payload,
  }
  documents.push(created)
  writeAll(documents)
  return created
}

export function deleteOutboundDocument(id: string): void {
  writeAll(readAll().filter((document) => document.id !== id))
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
  const raw = sessionStorage.getItem(workingStateKey)
  if (!raw) return null
  sessionStorage.removeItem(workingStateKey)

  try {
    const state = JSON.parse(raw) as Partial<OutboundDocumentWorkingState>
    if (state.documentType !== documentType || !state.payload) return null
    return state as OutboundDocumentWorkingState
  } catch {
    return null
  }
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
  } catch {
    return null
  }
}

export function migrateLegacyOutboundDraft(
  documentType: OutboundDocumentType,
  legacyKey: string,
  createSummary: (payload: unknown) => OutboundDocumentSummary,
): void {
  const raw = localStorage.getItem(legacyKey)
  if (!raw) return

  localStorage.removeItem(legacyKey)
  try {
    const payload = JSON.parse(raw) as unknown
    saveOutboundDocument({ documentType, summary: createSummary(payload), payload })
  } catch {
    // 旧草稿损坏时直接忽略，避免阻止页面进入。
  }
}
