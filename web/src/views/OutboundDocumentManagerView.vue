<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import {
  getBusinessDocumentConfig,
  isBusinessDocumentType,
} from '@/services/business-document-config'
import { returnToParentDocument } from '@/services/document-subpage-navigation'
import {
  clearOutboundDocumentWorkingState,
  deleteLocalDocumentRecords,
  isLocalDocumentRecordHidden,
  isLocalOperationGroupHidden,
  listAllLocalDocuments,
  requestOutboundDocumentEdit,
  subscribeDraftChanges,
  type OperationGroupSnapshot,
  type StoredOutboundDocument,
} from '@/services/outbound-document-drafts'
import {
  downloadServerDocumentDraft,
  listServerDocumentDrafts,
} from '@/services/document-draft-sync'
import { readSession } from '@/services/auth'
import {
  getOutboundOperationGroup,
  listOutboundOperationGroups,
  refreshOutboundDocumentGroup,
  uploadOutboundDocument,
} from '@/services/outbound-document-upload'
import {
  ensureDocumentPolicy,
  loadBusinessDocumentSettings,
} from '@/services/business-document-settings'
import type { OutboundDocumentType } from '@/services/source-document-selection'

const route = useRoute()
const router = useRouter()
type ManagerStatus = 'pending' | 'attention' | 'completed'

const activeStatus = ref<ManagerStatus>('pending')
const documents = ref<StoredOutboundDocument[]>([])
const selectedDocument = ref<StoredOutboundDocument | null>(null)
const message = ref('')
const uploadingId = ref('')
const processingId = ref('')
const selectionMode = ref(false)
const selectedIds = ref<Set<string>>(new Set())
let allowDocumentReturn = false
let unsubscribeDraftChanges: (() => void) | null = null

const documentType = computed<OutboundDocumentType>(() => {
  const code = Array.isArray(route.params.code) ? route.params.code[0] : route.params.code
  return isBusinessDocumentType(code) ? code : 'material-outbound'
})
const title = computed(() => {
  const label = getBusinessDocumentConfig(documentType.value).title
  return label.endsWith('单') ? `${label}据` : `${label}单据`
})

const currentUserId = computed(() => readSession()?.user.id ?? '')
const isAdmin = computed(() =>
  readSession()?.user.roles.some((role) => role.toLowerCase() === 'admin') === true,
)

async function refresh(): Promise<void> {
  const local = (await listAllLocalDocuments()).filter((document) =>
    document.syncStatus !== 'deleted-pending' && document.documentType === documentType.value &&
    (isAdmin.value || document.ownerUserId === currentUserId.value),
  )
  let remote: StoredOutboundDocument[] = []
  let groups: OperationGroupSnapshot[] = []
  try {
    [remote, groups] = await Promise.all([
      listServerDocumentDrafts(documentType.value, null, isAdmin.value),
      listOutboundOperationGroups(documentType.value, isAdmin.value),
    ])
  } catch {
    // 离线时继续显示本地草稿。
  }
  const merged = new Map(remote.map((document) => [document.id, document]))
  for (const document of local) merged.set(document.id, document)
  for (const group of groups) {
    if (isLocalOperationGroupHidden(group.submissionGroupId)) continue
    const existing = [...merged.values()].find((document) =>
      document.serverOperationId === group.submissionGroupId,
    ) ?? [...merged.values()].find((document) =>
      !document.serverOperationId && document.id === group.submissionGroupId,
    )
    const retryableFailure = group.status === 'FAILED' && existing &&
      existing.ownerUserId === currentUserId.value && canUpload(existing)
    const uploadStatus = group.status === 'CANCELLED'
      ? 'cancelled'
      : retryableFailure ? 'pending'
      : group.isTerminal ? 'completed' : 'attention'
    if (existing) {
      merged.set(existing.id, {
        ...existing,
        uploadStatus,
        serverOperationId: group.submissionGroupId,
        serverStatus: group.status,
        lockExpiresAt: group.lockExpiresAt,
        serverCheckedAt: new Date().toISOString(),
        serverErrorCode: group.errorCode,
        serverErrorMessage: group.errorMessage,
        preparedOperations: group.operations,
        updatedAt: group.updatedAt ?? existing.updatedAt,
      })
      continue
    }
    const now = group.updatedAt ?? new Date().toISOString()
    merged.set(group.submissionGroupId, {
      id: group.submissionGroupId,
      documentType: documentType.value,
      uploadStatus,
      createdAt: group.createdAt ?? now,
      updatedAt: now,
      uploadedAt: group.createdAt ?? now,
      uploadAttempts: 0,
      lastUploadError: '',
      serverOperationId: group.submissionGroupId,
      serverStatus: group.status,
      lockExpiresAt: group.lockExpiresAt,
      serverCheckedAt: new Date().toISOString(),
      serverErrorCode: group.errorCode,
      serverErrorMessage: group.errorMessage,
      preparedOperations: group.operations,
      summary: {
        documentDate: (group.createdAt ?? now).slice(0, 10),
        sourceTypeLabel: title.value,
        sourceDocumentNo: group.sourceDocumentNo ?? '',
        lineCount: group.lineCount ?? group.operations.reduce((sum, item) => sum + item.lineCount, 0),
        totalQty: group.totalQty ?? group.operations.reduce((sum, item) => sum + item.totalQty, 0),
      },
      payload: {},
      ownerUserId: group.operatorId ?? '',
      ownerName: group.operatorName ?? '',
      deviceId: 'server',
      clientRevision: 1,
      syncStatus: 'synced',
      lastSyncedAt: now,
      syncError: '',
      remoteOnly: true,
      operationOnly: true,
    })
  }
  documents.value = [...merged.values()].filter((document) => {
    if (isLocalDocumentRecordHidden(document)) return false
    if (activeStatus.value === 'pending') return document.uploadStatus === 'pending'
    if (activeStatus.value === 'attention') return document.uploadStatus === 'attention'
    return ['completed', 'cancelled', 'uploaded', 'prepared'].includes(document.uploadStatus)
  }).sort((left, right) =>
    right.updatedAt.localeCompare(left.updatedAt),
  )
  selectedDocument.value = null
  selectedIds.value = new Set([...selectedIds.value].filter((id) => documents.value.some((item) => item.id === id)))
}

function changeStatus(status: ManagerStatus): void {
  activeStatus.value = status
  selectedIds.value = new Set()
  void refresh()
}

const allSelected = computed(() =>
  documents.value.length > 0 && documents.value.every((document) => selectedIds.value.has(document.id)),
)

function toggleSelectionMode(): void {
  selectionMode.value = !selectionMode.value
  selectedIds.value = new Set()
  selectedDocument.value = null
}

function toggleDocumentSelection(document: StoredOutboundDocument): void {
  const next = new Set(selectedIds.value)
  if (next.has(document.id)) next.delete(document.id)
  else next.add(document.id)
  selectedIds.value = next
}

function toggleSelectAll(): void {
  selectedIds.value = allSelected.value
    ? new Set()
    : new Set(documents.value.map((document) => document.id))
}

function canUpload(document: StoredOutboundDocument): boolean {
  if (!document.payload || typeof document.payload !== 'object') return false
  const payload = document.payload as { lines?: unknown[] }
  return Array.isArray(payload.lines) && payload.lines.length > 0
}

function uploadError(document: StoredOutboundDocument): string {
  return document.lastUploadError?.trim() || document.serverErrorMessage?.trim() ||
    document.preparedOperations?.find((operation) => operation.errorMessage?.trim())?.errorMessage?.trim() || ''
}

function isRetryableFailure(document: StoredOutboundDocument): boolean {
  return document.serverStatus === 'FAILED' || Boolean(uploadError(document))
}

async function continueDocument(document: StoredOutboundDocument): Promise<void> {
  if (document.ownerUserId !== currentUserId.value) {
    selectedDocument.value = document
    return
  }
  if (document.remoteOnly) await downloadServerDocumentDraft(document.id)
  clearOutboundDocumentWorkingState()
  requestOutboundDocumentEdit({
    documentType: documentType.value,
    documentId: document.id,
  })
  await returnToDocument()
}

async function returnToDocument(): Promise<void> {
  allowDocumentReturn = true
  await returnToParentDocument(router, route, documentType.value)
}

async function uploadDocument(document: StoredOutboundDocument): Promise<void> {
  if (!canUpload(document)) {
    message.value = '请先扫码'
    return
  }
  if (uploadingId.value) return
  uploadingId.value = document.id
  try {
    const settings = loadBusinessDocumentSettings()
    const policy = ensureDocumentPolicy(settings, documentType.value)
    const uploaded = await uploadOutboundDocument(document, policy.autoApproveAfterUpload)
    message.value = uploaded.serverStatus === 'SUCCESS' ? '上传完成'
      : uploaded.serverStatus === 'NEEDS_RECONCILIATION' ? '需要核对'
        : uploaded.serverErrorMessage || '上传失败'
    activeStatus.value = uploaded.serverStatus === 'FAILED' ? 'pending'
      : uploaded.serverStatus === 'NEEDS_RECONCILIATION' ? 'attention' : 'completed'
    await refresh()
  } catch (error) {
    message.value = error instanceof Error ? error.message : '上传失败'
    await refresh()
  } finally {
    uploadingId.value = ''
    setTimeout(() => {
      message.value = ''
    }, 2200)
  }
}

function openDocument(document: StoredOutboundDocument): void {
  if (selectionMode.value) {
    toggleDocumentSelection(document)
    return
  }
  if (document.uploadStatus === 'pending') {
    void continueDocument(document)
    return
  }
  selectedDocument.value = document
}

async function refreshServerDocument(document: StoredOutboundDocument): Promise<void> {
  if (processingId.value) return
  processingId.value = document.id
  try {
    if (document.operationOnly) {
      await getOutboundOperationGroup(document.serverOperationId)
    } else {
      await refreshOutboundDocumentGroup(document)
    }
    await refresh()
  } catch (error) {
    message.value = error instanceof Error ? error.message : '状态更新失败'
  } finally {
    processingId.value = ''
  }
}

async function removeSelectedDocuments(): Promise<void> {
  const selected = documents.value.filter((document) => selectedIds.value.has(document.id))
  if (!selected.length || !window.confirm(`删除选中的 ${selected.length} 条本机记录？`)) return
  await deleteLocalDocumentRecords(selected)
  selectionMode.value = false
  selectedIds.value = new Set()
  message.value = '已删除本机记录'
  await refresh()
  setTimeout(() => { message.value = '' }, 1800)
}

function formatTime(value: string): string {
  return new Intl.DateTimeFormat('zh-CN', {
    timeZone: 'Asia/Shanghai',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(new Date(value))
}

function serverStatusLabel(status: string | undefined): string {
  const labels: Record<string, string> = {
    PENDING: '未完成', WRITING_ERP: '写入中', PENDING_APPROVAL: '待审核',
    SUCCESS: '已完成', FAILED: '失败', CANCELLED: '已取消',
    NEEDS_RECONCILIATION: '待核对', REVERSED: '已红冲',
    PARTIAL: '部分完成',
  }
  return labels[status ?? ''] ?? '--'
}

function statusLabel(document: StoredOutboundDocument): string {
  const serverLabel = serverStatusLabel(document.serverStatus)
  return serverLabel !== '--' ? serverLabel :
    (document.uploadStatus === 'pending' ? '草稿' : document.uploadStatus === 'uploaded' ? '历史已上传' : '--')
}

onMounted(() => {
  void refresh()
  unsubscribeDraftChanges = subscribeDraftChanges(() => void refresh())
})

onBeforeUnmount(() => {
  unsubscribeDraftChanges?.()
})

onBeforeRouteLeave((to) => {
  if (allowDocumentReturn) {
    allowDocumentReturn = false
    return true
  }
  if (to.name === 'work-function' && to.params.code === documentType.value) return true
  return {
    name: 'work-function',
    params: { code: documentType.value },
    query: route.query,
    replace: true,
  }
})
</script>

<template>
  <main class="manager-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button class="back-button" type="button" aria-label="返回" @click="returnToDocument">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>{{ title }}</h1>
        <button class="header-delete" type="button" @click="toggleSelectionMode">
          {{ selectionMode ? '取消' : '删除' }}
        </button>
      </div>
    </header>

    <div class="page-content">
      <nav class="status-tabs" aria-label="单据状态">
        <button
          type="button"
          :class="{ active: activeStatus === 'pending' }"
          @click="changeStatus('pending')"
        >
          未提交
        </button>
        <button
          type="button"
          :class="{ active: activeStatus === 'attention' }"
          @click="changeStatus('attention')"
        >
          待核对
        </button>
        <button
          type="button"
          :class="{ active: activeStatus === 'completed' }"
          @click="changeStatus('completed')"
        >
          已结束
        </button>
      </nav>

      <div v-if="selectionMode && documents.length" class="selection-bar">
        <button type="button" @click="toggleSelectAll">{{ allSelected ? '取消全选' : '全选' }}</button>
        <span>已选 {{ selectedIds.size }} 条</span>
        <button class="confirm-delete" type="button" :disabled="!selectedIds.size" @click="removeSelectedDocuments">
          删除
        </button>
      </div>

      <section v-if="documents.length" class="document-list">
        <article
          v-for="document in documents"
          :key="document.id"
          class="document-card"
          :class="{ selected: selectedIds.has(document.id) }"
          role="button"
          tabindex="0"
          @click="openDocument(document)"
          @keydown.enter="openDocument(document)"
        >
          <span v-if="selectionMode" class="selection-check" aria-hidden="true">
            {{ selectedIds.has(document.id) ? '✓' : '' }}
          </span>
          <div class="document-heading">
            <div>
              <strong>{{ document.summary.sourceDocumentNo || '无源单' }}</strong>
              <span>{{ document.ownerName }} · {{ document.summary.sourceTypeLabel || title }}</span>
            </div>
            <time>{{ formatTime(document.updatedAt) }}</time>
          </div>

          <dl>
            <div><dt>日期</dt><dd>{{ document.summary.documentDate }}</dd></div>
            <div><dt>明细</dt><dd>{{ document.summary.lineCount }}项</dd></div>
            <div><dt>数量</dt><dd>{{ document.summary.totalQty }}</dd></div>
          </dl>

          <p v-if="uploadError(document)" class="upload-error">
            <strong>上传失败</strong>
            <span v-if="document.serverErrorCode">{{ document.serverErrorCode }}</span>
            <em>{{ uploadError(document) }}</em>
          </p>
          <p v-else-if="document.syncStatus === 'failed'" class="upload-error">
            {{ document.syncError || '草稿同步失败' }}
          </p>
          <p v-else-if="activeStatus === 'pending'" class="draft-sync-status">
            {{ document.syncStatus === 'synced' ? '已同步' : document.syncStatus === 'syncing' ? '同步中' : '待同步' }}
            <span v-if="document.lastSyncedAt">{{ formatTime(document.lastSyncedAt) }}</span>
          </p>
          <p v-if="activeStatus !== 'pending'" class="server-status">
            {{ statusLabel(document) }}
            <span v-if="document.lockExpiresAt && document.serverStatus === 'PENDING'">至 {{ formatTime(document.lockExpiresAt) }}</span>
          </p>

          <div v-if="!selectionMode && activeStatus === 'pending' && document.ownerUserId === currentUserId" class="card-actions pending-actions">
            <button
              type="button"
              :disabled="uploadingId === document.id || !canUpload(document)"
              @click.stop="uploadDocument(document)"
            >
              {{ uploadingId === document.id ? '上传中' : isRetryableFailure(document) ? '重新上传' : '上传 ERP' }}
            </button>
          </div>
          <div v-else-if="!selectionMode && activeStatus === 'pending'" class="card-actions">
            <button type="button" @click.stop="selectedDocument = document">查看</button>
          </div>
          <div v-else-if="!selectionMode" class="card-actions">
            <button
              v-if="document.serverStatus === 'WRITING_ERP' || document.serverStatus === 'NEEDS_RECONCILIATION'"
              type="button"
              :disabled="processingId === document.id"
              @click.stop="refreshServerDocument(document)"
            >刷新</button>
            <button type="button" @click.stop="selectedDocument = document">查看</button>
          </div>
        </article>
      </section>

      <div v-else class="empty-state">暂无单据</div>
    </div>

    <div v-if="selectedDocument" class="detail-mask" @click.self="selectedDocument = null">
      <section class="detail-sheet">
        <header><h2>单据详情</h2><button type="button" @click="selectedDocument = null">×</button></header>
        <dl>
          <div><dt>源单</dt><dd>{{ selectedDocument.summary.sourceDocumentNo || '--' }}</dd></div>
          <div><dt>类型</dt><dd>{{ selectedDocument.summary.sourceTypeLabel || '--' }}</dd></div>
          <div><dt>日期</dt><dd>{{ selectedDocument.summary.documentDate }}</dd></div>
          <div><dt>明细</dt><dd>{{ selectedDocument.summary.lineCount }}项</dd></div>
          <div><dt>数量</dt><dd>{{ selectedDocument.summary.totalQty }}</dd></div>
          <div><dt>状态</dt><dd>{{ statusLabel(selectedDocument) }}</dd></div>
          <div><dt>制单人</dt><dd>{{ selectedDocument.ownerName }}</dd></div>
          <div><dt>同步</dt><dd>{{ selectedDocument.lastSyncedAt ? formatTime(selectedDocument.lastSyncedAt) : '未同步' }}</dd></div>
          <div><dt>业务组</dt><dd>{{ selectedDocument.serverOperationId || '--' }}</dd></div>
          <div v-if="uploadError(selectedDocument)"><dt>异常</dt><dd>{{ uploadError(selectedDocument) }}</dd></div>
        </dl>
        <div v-if="selectedDocument.preparedOperations?.length" class="operation-list">
          <div v-for="operation in selectedDocument.preparedOperations" :key="operation.id">
            <strong>{{ operation.operationNo }}</strong>
            <span>{{ operation.sourceDocumentNo || '无源单' }} · {{ operation.warehouseCode || '--' }} · {{ serverStatusLabel(operation.status) }}</span>
            <span v-if="operation.errorMessage" class="operation-error">{{ operation.errorMessage }}</span>
          </div>
        </div>
      </section>
    </div>

    <div v-if="message" class="toast-message" role="status">{{ message }}</div>
  </main>
</template>

<style scoped>
.manager-page{min-height:100dvh;padding-top:72px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content{width:min(100%,760px);margin-inline:auto}.topbar{height:100%;display:grid;grid-template-columns:44px minmax(0,1fr) auto;align-items:center;gap:12px;padding:0 18px}.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg{width:23px;height:23px;fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}.header-delete{height:38px;padding:0 12px;border:0;border-radius:10px;background:#fbecec;color:#b64141;font-size:13px;font-weight:750}h1{overflow:hidden;color:#17365d;font-size:21px;text-overflow:ellipsis;white-space:nowrap}.page-content{display:grid;gap:14px;padding:16px}.status-tabs{display:grid;grid-template-columns:repeat(3,1fr);padding:4px;border:1px solid #dce5ef;border-radius:13px;background:#e9eff6}.status-tabs button{height:40px;border:0;border-radius:9px;background:transparent;color:#6c7b8e;font-size:14px;font-weight:750}.status-tabs button.active{background:#fff;color:#2368c4;box-shadow:0 2px 8px rgba(23,54,93,.1)}.selection-bar{display:grid;grid-template-columns:auto 1fr auto;align-items:center;gap:10px;padding:10px 12px;border-radius:12px;background:#fff}.selection-bar span{text-align:center;color:#60758e;font-size:13px}.selection-bar button{height:36px;padding:0 12px;border:0;border-radius:9px;background:#e9f2fd;color:#2368c4;font-weight:750}.selection-bar .confirm-delete{background:#c94a4a;color:#fff}.selection-bar button:disabled{opacity:.45}.document-list{display:grid;gap:11px}.document-card{position:relative;padding:15px;border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.055);cursor:pointer}.document-card.selected{border-color:#4a89d8;background:#f7fbff}.selection-check{position:absolute;top:12px;right:12px;width:24px;height:24px;display:grid;place-items:center;border:2px solid #9bb2ca;border-radius:50%;background:#fff;color:#fff;font-size:14px;font-weight:800}.document-card.selected .selection-check{border-color:#2368c4;background:#2368c4}.document-heading{display:flex;align-items:flex-start;justify-content:space-between;gap:12px}.document-card.selected .document-heading{padding-right:34px}.document-heading>div{min-width:0;display:grid;gap:4px}.document-heading strong{overflow-wrap:anywhere;color:#203552;font:700 15px ui-monospace,SFMono-Regular,Consolas,monospace}.document-heading span,.document-heading time{color:#7b899b;font-size:12px}.document-card dl,.detail-sheet dl{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;margin:14px 0;padding:12px;border-radius:11px;background:#f7f9fc}.document-card dl div,.detail-sheet dl div{min-width:0;display:grid;gap:4px}.document-card dt,.document-card dd,.detail-sheet dt,.detail-sheet dd{margin:0;font-size:12px}.document-card dt,.detail-sheet dt{color:#8491a2}.document-card dd,.detail-sheet dd{overflow:hidden;color:#33465e;font-weight:700;text-overflow:ellipsis;white-space:nowrap}.card-actions{display:flex;justify-content:flex-end;gap:8px}.card-actions button{min-width:72px;height:38px;border:0;border-radius:9px;background:#e9f2fd;color:#2368c4;font-size:13px;font-weight:750}.card-actions button:nth-child(2){background:#2368c4;color:#fff}.empty-state{min-height:180px;display:grid;place-items:center;border:1px dashed #cbd7e4;border-radius:16px;background:rgba(255,255,255,.62);color:#8b98a8;font-size:14px}.toast-message{position:fixed;z-index:50;right:18px;bottom:28px;left:18px;width:fit-content;margin-inline:auto;padding:10px 15px;border-radius:10px;background:#31445e;color:#fff;font-size:13px;font-weight:650;box-shadow:0 8px 22px rgba(23,54,93,.2)}.detail-mask{position:fixed;z-index:40;inset:0;display:flex;align-items:flex-end;justify-content:center;padding:18px;background:rgba(20,32,48,.38)}.detail-sheet{width:min(100%,560px);padding:18px;border-radius:18px;background:#fff;box-shadow:0 16px 40px rgba(23,54,93,.2)}.detail-sheet header{display:flex;align-items:center;justify-content:space-between}.detail-sheet h2{color:#24344d;font-size:17px}.detail-sheet header button{width:36px;height:36px;border:0;border-radius:9px;background:#f1f4f8;color:#718096;font-size:22px}.detail-sheet dl{grid-template-columns:1fr 1fr;margin-bottom:0}@media(max-width:560px){.topbar{padding:0 14px}.page-content{padding:14px 12px}.document-card dl{grid-template-columns:repeat(3,minmax(0,1fr))}.pending-actions{display:grid;grid-template-columns:1fr}.card-actions button{min-width:0}}
.upload-error{display:grid;grid-template-columns:auto minmax(0,1fr);gap:3px 8px;margin:-4px 0 12px;padding:9px 10px;border-radius:9px;background:#fff2f2;color:#ae4141;font-size:12px}.upload-error strong{font-size:12px}.upload-error span{overflow:hidden;color:#c15a5a;font:600 11px ui-monospace,SFMono-Regular,Consolas,monospace;text-align:right;text-overflow:ellipsis;white-space:nowrap}.upload-error em{grid-column:1/-1;overflow-wrap:anywhere;color:#8e3636;font-style:normal;line-height:1.45}.card-actions button:disabled{opacity:.55}
.draft-sync-status{display:flex;justify-content:space-between;gap:10px;margin:-4px 0 12px;color:#60758e;font-size:12px}.draft-sync-status span{color:#8b98a8}
.server-status{display:flex;justify-content:space-between;gap:10px;margin:-4px 0 12px;color:#2867ad;font-size:12px;font-weight:700}.server-status span{color:#8491a2;font-weight:500}.operation-list{display:grid;gap:8px;margin-top:12px}.operation-list>div{display:grid;gap:3px;padding:10px;border-radius:9px;background:#f7f9fc}.operation-list strong{color:#33465e;font-size:12px}.operation-list span{color:#7b899b;font-size:11px}.operation-list .operation-error{color:#ae4141}
.manager-page {
  padding-top: var(--app-header-height);
}

.topbar-shell {
  height: var(--app-header-height);
}

.topbar {
  padding-top: var(--app-safe-top);
}
</style>
