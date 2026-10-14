<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import { getPdaBridge } from '@/bridge/pda-bridge'
import type { PdaScanEvent } from '@/bridge/types'
import DateField from '@/components/DateField.vue'
import { getBusinessDocumentConfig, type BusinessDocumentType } from '@/services/business-document-config'
import {
  fetchApprovalDocument,
  fetchApprovalDocuments,
  submitApprovalAction,
  type ApprovalDocument,
  type ApprovalDocumentStatus,
} from '@/services/approval-documents'

interface Props {
  documentType: BusinessDocumentType
}

const props = defineProps<Props>()
const route = useRoute()
const router = useRouter()
const bridge = getPdaBridge()
const activeStatus = ref<ApprovalDocumentStatus>('pending')
const startDate = ref(beijingDate(-7))
const endDate = ref(beijingDate(0))
const documentNo = ref('')
const documents = ref<ApprovalDocument[]>([])
const selectedDocument = ref<ApprovalDocument | null>(null)
const loading = ref(false)
const submitting = ref(false)
const confirmAction = ref<'audit' | 'unaudit' | null>(null)
const errorMessage = ref('')
const message = ref('')
let messageTimer: ReturnType<typeof setTimeout> | null = null
let stopScanListener: (() => void) | null = null

const pageTitle = computed(() => `${getBusinessDocumentConfig(props.documentType).title}审核`)
const returnRouteName = computed(() =>
  route.query.origin === 'all-functions' ? 'all-functions' : 'home',
)
const filteredDocuments = computed(() => documents.value)
const selectedTotalQuantity = computed(() =>
  selectedDocument.value?.lines.reduce((total, line) => total + line.quantity, 0) ?? 0,
)

function beijingDate(offsetDays: number): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(new Date())
  const value = Object.fromEntries(parts.map((part) => [part.type, part.value]))
  const date = new Date(Date.UTC(Number(value.year), Number(value.month) - 1, Number(value.day)))
  date.setUTCDate(date.getUTCDate() + offsetDays)
  return date.toISOString().slice(0, 10)
}

function totalQuantity(document: ApprovalDocument): number {
  return document.totalQuantity
}

function notify(text: string): void {
  message.value = text
  if (messageTimer) clearTimeout(messageTimer)
  messageTimer = setTimeout(() => {
    message.value = ''
  }, 1800)
}

async function selectStatus(status: ApprovalDocumentStatus): Promise<void> {
  activeStatus.value = status
  selectedDocument.value = null
  await search()
}

async function search(): Promise<void> {
  errorMessage.value = ''
  if (startDate.value && endDate.value && startDate.value > endDate.value) {
    errorMessage.value = '开始日期不能晚于结束日期'
    return
  }
  loading.value = true
  try {
    documents.value = await fetchApprovalDocuments(
      props.documentType,
      activeStatus.value,
      startDate.value,
      endDate.value,
      documentNo.value.trim(),
    )
  } catch (reason) {
    documents.value = []
    errorMessage.value = reason instanceof Error ? reason.message : '查询失败'
  } finally {
    loading.value = false
  }
}

function handleScan(event: PdaScanEvent): void {
  if (selectedDocument.value || confirmAction.value) return
  documentNo.value = event.barcode.trim()
  search()
}

async function openDocument(document: ApprovalDocument): Promise<void> {
  errorMessage.value = ''
  loading.value = true
  try {
    selectedDocument.value = await fetchApprovalDocument(props.documentType, document.id)
    window.scrollTo({ top: 0, behavior: 'smooth' })
  } catch (reason) {
    errorMessage.value = reason instanceof Error ? reason.message : '读取失败'
  } finally {
    loading.value = false
  }
}

function openConfirm(action: 'audit' | 'unaudit'): void {
  confirmAction.value = action
  errorMessage.value = ''
}

function closeConfirm(): void {
  confirmAction.value = null
  errorMessage.value = ''
}

async function submitAction(): Promise<void> {
  const document = selectedDocument.value
  const action = confirmAction.value
  if (!document || !action) return

  submitting.value = true
  try {
    await submitApprovalAction(props.documentType, document, action, '')
    closeConfirm()
    selectedDocument.value = null
    await search()
    notify(action === 'audit' ? '审核成功' : '弃审成功')
  } catch (failure) {
    errorMessage.value = failure instanceof Error ? failure.message : '操作失败'
  } finally {
    submitting.value = false
  }
}

async function leavePage(): Promise<void> {
  if (confirmAction.value) {
    closeConfirm()
    return
  }
  if (selectedDocument.value) {
    selectedDocument.value = null
    return
  }
  await router.replace({ name: returnRouteName.value })
}

function formatAuditTime(value: string | null): string {
  if (!value) return ''
  return new Intl.DateTimeFormat('zh-CN', {
    timeZone: 'Asia/Shanghai',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(new Date(value))
}

onMounted(() => {
  void search()
  stopScanListener = bridge.subscribeScan(handleScan)
})

onBeforeUnmount(() => {
  stopScanListener?.()
  if (messageTimer) clearTimeout(messageTimer)
})

onBeforeRouteLeave((to) => {
  if (confirmAction.value) {
    closeConfirm()
    return false
  }
  if (selectedDocument.value) {
    selectedDocument.value = null
    return false
  }
  if (to.name !== returnRouteName.value) {
    return { name: returnRouteName.value, replace: true }
  }
  return true
})
</script>

<template>
  <main class="approval-page" :class="{ 'showing-detail': selectedDocument }">
    <header class="topbar-shell">
      <div class="topbar">
        <button class="back-button" type="button" aria-label="返回" @click="leavePage">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>{{ pageTitle }}</h1>
      </div>
    </header>

    <div v-if="!selectedDocument" class="page-content list-content">
      <nav class="status-tabs" aria-label="审核状态">
        <button type="button" :class="{ active: activeStatus === 'pending' }" @click="selectStatus('pending')">待审核</button>
        <button type="button" :class="{ active: activeStatus === 'audited' }" @click="selectStatus('audited')">已审核</button>
      </nav>

      <section class="search-card">
        <div class="date-row">
          <label><span>开始日期</span><DateField v-model="startDate" aria-label="开始日期" /></label>
          <label><span>结束日期</span><DateField v-model="endDate" aria-label="结束日期" /></label>
        </div>
        <div class="number-row">
          <input v-model="documentNo" autocomplete="off" placeholder="单据号" @keyup.enter="search" />
          <button type="button" @click="search">查询</button>
        </div>
      </section>

      <p v-if="errorMessage" class="error-message">{{ errorMessage }}</p>

      <div v-if="loading" class="empty-state">读取中</div>
      <section v-else-if="filteredDocuments.length" class="document-list">
        <article
          v-for="document in filteredDocuments"
          :key="document.id"
          class="document-card"
          role="button"
          tabindex="0"
          @click="openDocument(document)"
          @keydown.enter="openDocument(document)"
        >
          <div class="document-heading">
            <div><strong>{{ document.documentNo }}</strong><span>{{ document.documentDate }}</span></div>
            <span class="status-badge" :class="document.status">{{ document.status === 'pending' ? '待审核' : '已审核' }}</span>
          </div>
          <div class="partner-row"><span>{{ document.partnerLabel }}</span><strong>{{ document.partnerName }}</strong></div>
          <dl>
            <div><dt>明细</dt><dd>{{ document.lineCount }}项</dd></div>
            <div><dt>数量</dt><dd>{{ totalQuantity(document) }}</dd></div>
            <div><dt>标签</dt><dd>{{ document.labelImpacts.length }}个</dd></div>
          </dl>
          <div class="card-footer"><span>制单人 {{ document.createdBy }}</span><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9 6 6 6-6 6" /></svg></div>
        </article>
      </section>

      <div v-else class="empty-state">暂无单据</div>
    </div>

    <div v-else class="page-content detail-content">
      <section class="summary-card">
        <div class="detail-title"><strong>{{ selectedDocument.documentNo }}</strong><span class="status-badge" :class="selectedDocument.status">{{ selectedDocument.status === 'pending' ? '待审核' : '已审核' }}</span></div>
        <dl class="summary-grid">
          <div><dt>单据日期</dt><dd>{{ selectedDocument.documentDate }}</dd></div>
          <div><dt>制单人</dt><dd>{{ selectedDocument.createdBy }}</dd></div>
          <div><dt>{{ selectedDocument.partnerLabel }}</dt><dd>{{ selectedDocument.partnerName }}</dd></div>
          <div><dt>仓库</dt><dd>{{ selectedDocument.warehouseCode }}</dd></div>
          <div v-if="selectedDocument.status === 'audited'"><dt>审核人</dt><dd>{{ selectedDocument.auditedBy }}</dd></div>
          <div v-if="selectedDocument.status === 'audited'"><dt>审核时间</dt><dd>{{ formatAuditTime(selectedDocument.auditedAt) }}</dd></div>
        </dl>
      </section>

      <section class="detail-card">
        <div class="section-heading"><h2>单据明细</h2><span>{{ selectedDocument.lines.length }}项 · {{ selectedTotalQuantity }}</span></div>
        <article v-for="line in selectedDocument.lines" :key="line.id" class="line-item">
          <div class="line-heading"><strong>{{ line.materialCode }}</strong><b>{{ line.quantity }}</b></div>
          <span>{{ line.materialName }}</span>
          <small>{{ line.batchNo }} · {{ line.warehouseCode }} / {{ line.locationCode }}</small>
        </article>
      </section>

      <section class="detail-card label-card">
        <div class="section-heading"><h2>标签影响</h2><span>{{ selectedDocument.labelImpacts.length }}个</span></div>
        <div v-if="!selectedDocument.labelImpacts.length" class="no-impact">无标签操作</div>
        <article v-for="label in selectedDocument.labelImpacts" :key="label.labelCode" class="label-item">
          <div><strong>{{ label.labelCode }}</strong><span>{{ label.warehouseCode }} / {{ label.locationCode }}</span></div>
          <b>{{ label.quantity }}</b>
        </article>
      </section>
    </div>

    <footer v-if="selectedDocument" class="action-shell">
      <button class="secondary" type="button" @click="selectedDocument = null">返回</button>
      <button v-if="selectedDocument.status === 'pending'" type="button" @click="openConfirm('audit')">审核</button>
      <button v-else class="danger" type="button" @click="openConfirm('unaudit')">弃审</button>
    </footer>

    <div v-if="confirmAction && selectedDocument" class="confirm-mask" @click.self="closeConfirm">
      <section class="confirm-sheet">
        <h2>{{ confirmAction === 'audit' ? '确认审核' : '确认弃审' }}</h2>
        <div class="confirm-document"><strong>{{ selectedDocument.documentNo }}</strong><span>明细 {{ selectedDocument.lines.length }}项 · 标签 {{ selectedDocument.labelImpacts.length }}个</span></div>
        <p v-if="errorMessage" class="error-message">{{ errorMessage }}</p>
        <div class="confirm-actions"><button type="button" :disabled="submitting" @click="closeConfirm">取消</button><button type="button" :disabled="submitting" @click="submitAction">{{ submitting ? '处理中' : (confirmAction === 'audit' ? '确认审核' : '确认弃审') }}</button></div>
      </section>
    </div>

    <Transition name="toast"><div v-if="message" class="toast-message" role="status">{{ message }}</div></Transition>
  </main>
</template>

<style scoped>
.approval-page{min-height:100dvh;padding:72px 0 20px;background:#f3f6fa;color:#21324a}.approval-page.showing-detail{padding-bottom:88px}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content,.action-shell{width:min(100%,760px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:0 18px}.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg,.card-footer svg{fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}.back-button svg{width:23px;height:23px}.topbar h1{overflow:hidden;color:#17365d;font-size:21px;text-overflow:ellipsis;white-space:nowrap}.page-content{display:grid;gap:14px;padding:15px 14px}.status-tabs{display:grid;grid-template-columns:1fr 1fr;padding:4px;border:1px solid #dce5ef;border-radius:13px;background:#e9eff6}.status-tabs button{height:40px;border:0;border-radius:9px;background:transparent;color:#6c7b8e;font-size:14px;font-weight:750}.status-tabs button.active{background:#fff;color:#2368c4;box-shadow:0 2px 8px rgba(23,54,93,.1)}.search-card,.document-card,.summary-card,.detail-card{border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.055)}.search-card{display:grid;gap:12px;padding:14px}.date-row{display:grid;grid-template-columns:1fr 1fr;gap:10px}.date-row label{display:grid;gap:6px}.date-row span{color:#65758a;font-size:12px;font-weight:650}.date-row input,.number-row input,.confirm-sheet textarea{width:100%;border:1px solid #d4deea;border-radius:10px;background:#fff;color:#17233d;font:inherit;outline:none}.date-row input,.number-row input{height:43px;padding:0 10px}.date-row input:focus,.number-row input:focus,.confirm-sheet textarea:focus{border-color:#3378cf;box-shadow:0 0 0 3px rgba(51,120,207,.1)}.number-row{display:grid;grid-template-columns:minmax(0,1fr) 72px;gap:9px}.number-row button{border:0;border-radius:10px;background:#2368c4;color:#fff;font-size:14px;font-weight:750}.error-message{margin:0;padding:0 3px;color:#bd3b3b;font-size:13px}.document-list{display:grid;gap:11px}.document-card{padding:15px;cursor:pointer}.document-heading,.detail-title,.line-heading,.section-heading,.label-item,.card-footer{display:flex;align-items:center;justify-content:space-between;gap:12px}.document-heading>div{min-width:0;display:grid;gap:4px}.document-heading strong,.detail-title>strong{overflow-wrap:anywhere;color:#203552;font:750 15px ui-monospace,SFMono-Regular,Consolas,monospace}.document-heading>div>span{color:#7b899b;font-size:12px}.status-badge{flex:0 0 auto;padding:5px 9px;border-radius:999px;font-size:12px;font-weight:750}.status-badge.pending{background:#fff2dc;color:#a56417}.status-badge.audited{background:#e7f6ee;color:#23724d}.partner-row{display:grid;grid-template-columns:68px minmax(0,1fr);gap:8px;margin-top:13px}.partner-row span{color:#8491a2;font-size:12px}.partner-row strong{overflow:hidden;color:#33465e;font-size:13px;text-overflow:ellipsis;white-space:nowrap}.document-card dl{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:9px;margin:13px 0;padding:11px;border-radius:11px;background:#f7f9fc}.document-card dl div{display:grid;gap:3px}.document-card dt,.document-card dd{margin:0;font-size:12px}.document-card dt{color:#8491a2}.document-card dd{color:#33465e;font-weight:750}.card-footer{padding-top:1px;color:#7b899b;font-size:12px}.card-footer svg{width:18px;height:18px}.empty-state{min-height:170px;display:grid;place-items:center;border:1px dashed #cbd7e4;border-radius:16px;background:rgba(255,255,255,.62);color:#8b98a8;font-size:14px}.summary-card,.detail-card{overflow:hidden}.summary-card{padding:16px}.detail-title{padding-bottom:14px;border-bottom:1px solid #edf1f5}.summary-grid{display:grid;grid-template-columns:1fr 1fr;gap:14px 12px;margin:15px 0 0}.summary-grid div{min-width:0;display:grid;gap:4px}.summary-grid dt,.summary-grid dd{margin:0}.summary-grid dt{color:#8491a2;font-size:12px}.summary-grid dd{overflow:hidden;color:#33465e;font-size:13px;font-weight:700;text-overflow:ellipsis;white-space:nowrap}.section-heading{min-height:48px;padding:0 15px;border-bottom:1px solid #edf1f5}.section-heading h2{color:#29405d;font-size:15px}.section-heading span{color:#7d8a9b;font-size:12px}.line-item{display:grid;gap:5px;padding:13px 15px;border-bottom:1px solid #edf1f5}.line-item:last-child{border-bottom:0}.line-heading strong{color:#24456b;font:700 13px ui-monospace,SFMono-Regular,Consolas,monospace}.line-heading b,.label-item>b{color:#2368c4;font-size:15px}.line-item>span{color:#364b65;font-size:13px}.line-item small{color:#8390a1;font-size:11px}.no-impact{min-height:70px;display:grid;place-items:center;color:#8491a2;font-size:13px}.label-item{padding:13px 15px;border-bottom:1px solid #edf1f5}.label-item:last-child{border-bottom:0}.label-item>div{min-width:0;display:grid;gap:4px}.label-item strong{overflow-wrap:anywhere;color:#24456b;font:700 12px ui-monospace,SFMono-Regular,Consolas,monospace}.label-item span{color:#8491a2;font-size:11px}.action-shell{position:fixed;z-index:18;right:0;bottom:0;left:0;display:grid;grid-template-columns:1fr 1.5fr;gap:10px;padding:10px 14px max(10px,env(safe-area-inset-bottom));border-top:1px solid #e1e8f0;background:rgba(255,255,255,.97);box-shadow:0 -6px 20px rgba(23,54,93,.07)}.action-shell button{height:48px;border:0;border-radius:11px;background:#2368c4;color:#fff;font-size:15px;font-weight:750}.action-shell button.secondary{border:1px solid #cdd9e7;background:#fff;color:#48627f}.action-shell button.danger{background:#b94b4b}.confirm-mask{position:fixed;z-index:40;inset:0;display:flex;align-items:flex-end;justify-content:center;padding:16px;background:rgba(20,32,48,.4)}.confirm-sheet{width:min(100%,560px);padding:18px;border-radius:18px;background:#fff;box-shadow:0 18px 44px rgba(15,29,48,.24)}.confirm-sheet h2{color:#203552;font-size:18px}.confirm-document{display:grid;gap:5px;margin:15px 0;padding:13px;border-radius:11px;background:#f3f7fb}.confirm-document strong{overflow-wrap:anywhere;color:#24456b;font:750 14px ui-monospace,SFMono-Regular,Consolas,monospace}.confirm-document span{color:#718096;font-size:12px}.confirm-sheet label{display:grid;gap:7px;margin-bottom:12px}.confirm-sheet label>span{color:#52657d;font-size:13px;font-weight:700}.confirm-sheet i{margin-right:3px;color:#d64040;font-style:normal}.confirm-sheet textarea{resize:none;padding:10px}.confirm-actions{display:grid;grid-template-columns:1fr 1.4fr;gap:9px;margin-top:16px}.confirm-actions button{height:44px;border:1px solid #ccd8e6;border-radius:10px;background:#fff;color:#50657e;font-size:14px;font-weight:750}.confirm-actions button:last-child{border-color:#2368c4;background:#2368c4;color:#fff}.toast-message{position:fixed;z-index:60;top:82px;left:50%;width:max-content;max-width:calc(100% - 36px);padding:10px 16px;border-radius:10px;background:#197052;color:#fff;font-size:13px;font-weight:700;box-shadow:0 8px 22px rgba(23,54,93,.2);transform:translateX(-50%)}.toast-enter-active,.toast-leave-active{transition:opacity .16s ease,transform .16s ease}.toast-enter-from,.toast-leave-to{opacity:0;transform:translate(-50%,-14px)}@media(max-width:480px){.topbar{padding:0 14px}.page-content{padding:13px 12px}.date-row{gap:8px}.summary-card{padding:14px}}
.approval-page {
  padding-top: var(--app-header-height);
}

.topbar-shell {
  height: var(--app-header-height);
}

.topbar {
  padding-top: var(--app-safe-top);
}

.toast-message {
  top: calc(var(--app-header-height) + 10px);
}
</style>
