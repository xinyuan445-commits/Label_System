<script setup lang="ts">
import { computed, ref } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import {
  getBusinessDocumentConfig,
  isBusinessDocumentType,
  type BusinessDocumentType,
} from '@/services/business-document-config'
import {
  ensureDocumentPolicy,
  loadBusinessDocumentSettings,
  saveBusinessDocumentSettings,
  syncPolicySettingToGlobal,
  type DocumentPolicySettings,
} from '@/services/business-document-settings'

type GlobalSection = 'operation' | 'pallet'

const route = useRoute()
const router = useRouter()
const settings = ref(loadBusinessDocumentSettings())
const expandedSections = ref<Record<GlobalSection, boolean>>({ operation: true, pallet: false })
const message = ref('')
let messageTimer: ReturnType<typeof setTimeout> | null = null
let allowDocumentReturn = false

const documentType = computed<BusinessDocumentType>(() => {
  const code = Array.isArray(route.params.code) ? route.params.code[0] : route.params.code
  return isBusinessDocumentType(code) ? code : 'material-outbound'
})
const documentTitle = computed(() => getBusinessDocumentConfig(documentType.value).title)
const documentPolicy = computed(() =>
  ensureDocumentPolicy(settings.value, documentType.value),
)

function toggleSection(section: GlobalSection): void {
  expandedSections.value[section] = !expandedSections.value[section]
}

function notifyGlobalSync(): void {
  message.value = '已全局同步'
  if (messageTimer) clearTimeout(messageTimer)
  messageTimer = setTimeout(() => {
    message.value = ''
  }, 1200)
}

function syncToGlobal(key: keyof DocumentPolicySettings): void {
  syncPolicySettingToGlobal(settings.value, key, documentPolicy.value[key])
  saveBusinessDocumentSettings(settings.value)
  notifyGlobalSync()
}

async function saveAndReturn(): Promise<void> {
  saveBusinessDocumentSettings(settings.value)
  await returnToDocument()
}

async function returnToDocument(): Promise<void> {
  allowDocumentReturn = true
  await router.replace({
    name: 'work-function',
    params: { code: documentType.value },
    query: route.query,
  })
}

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
  <main class="settings-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button class="back-button" type="button" aria-label="返回" @click="returnToDocument">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <div><h1>开关设置</h1><span>{{ documentTitle }}</span></div>
      </div>
    </header>

    <div class="page-content">
      <section class="settings-card document-settings-card">
        <h2><span>单据设置</span><small>{{ documentTitle }}</small></h2>
        <div class="setting-row">
          <span>扫托包含托盘</span>
          <div class="setting-actions">
            <label class="toggle"><input v-model="documentPolicy.includePalletByDefault" type="checkbox" /><i aria-hidden="true"></i></label>
            <button type="button" @click="syncToGlobal('includePalletByDefault')">全局</button>
          </div>
        </div>
        <div class="setting-row">
          <span>完成自动上传</span>
          <div class="setting-actions">
            <label class="toggle"><input v-model="documentPolicy.autoUploadOnComplete" type="checkbox" /><i aria-hidden="true"></i></label>
            <button type="button" @click="syncToGlobal('autoUploadOnComplete')">全局</button>
          </div>
        </div>
        <div class="setting-row">
          <span>上传自动审核</span>
          <div class="setting-actions">
            <label class="toggle"><input v-model="documentPolicy.autoApproveAfterUpload" type="checkbox" /><i aria-hidden="true"></i></label>
            <button type="button" @click="syncToGlobal('autoApproveAfterUpload')">全局</button>
          </div>
        </div>
      </section>

      <div class="group-title">全局设置</div>

      <section class="settings-card collapsible-card operation-card">
        <button class="collapse-heading" type="button" @click="toggleSection('operation')">
          <span class="collapse-title"><b>操</b><span>操作设置</span></span>
          <svg :class="{ expanded: expandedSections.operation }" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 6 6 6-6 6" /></svg>
        </button>
        <div v-if="expandedSections.operation" class="collapse-content">
          <label class="setting-row"><span>原因必填</span><span class="toggle"><input v-model="settings.globalOperation.reasonRequired" type="checkbox" /><i aria-hidden="true"></i></span></label>
        </div>
      </section>

      <section class="settings-card collapsible-card pallet-card">
        <button class="collapse-heading" type="button" @click="toggleSection('pallet')">
          <span class="collapse-title"><b>托</b><span>托盘设置</span></span>
          <svg :class="{ expanded: expandedSections.pallet }" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 6 6 6-6 6" /></svg>
        </button>
        <div v-if="expandedSections.pallet" class="collapse-content">
          <label class="setting-row"><span>不同物料混托</span><span class="toggle"><input v-model="settings.globalPallet.allowMixedMaterials" type="checkbox" /><i aria-hidden="true"></i></span></label>
          <label class="setting-row"><span>同料不同批次混托</span><span class="toggle"><input v-model="settings.globalPallet.allowMixedBatches" type="checkbox" /><i aria-hidden="true"></i></span></label>
          <label class="setting-row"><span>不同仓库混托</span><span class="toggle"><input v-model="settings.globalPallet.allowMixedWarehouses" type="checkbox" /><i aria-hidden="true"></i></span></label>
          <label class="setting-row"><span>不同货位混托</span><span class="toggle"><input v-model="settings.globalPallet.allowMixedLocations" type="checkbox" /><i aria-hidden="true"></i></span></label>
          <label class="setting-row"><span>空托盘允许复用</span><span class="toggle"><input v-model="settings.globalPallet.allowPalletReuse" type="checkbox" /><i aria-hidden="true"></i></span></label>
          <label class="setting-row"><span>托内标签单独使用</span><span class="toggle"><input v-model="settings.globalPallet.allowPalletizedLabelDirectUse" type="checkbox" /><i aria-hidden="true"></i></span></label>
        </div>
      </section>
    </div>

    <footer class="action-shell"><button type="button" @click="saveAndReturn">保存</button></footer>

    <Transition name="drop-toast">
      <div v-if="message" class="toast-message" role="status">{{ message }}</div>
    </Transition>
  </main>
</template>

<style scoped>
.settings-page{min-height:100dvh;padding:72px 0 86px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content,.action-shell{width:min(100%,760px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:0 18px}.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg,.collapse-heading svg{fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}.back-button svg{width:23px;height:23px}.topbar>div{display:grid;gap:2px}.topbar h1{color:#17365d;font-size:21px}.topbar span{color:#7b899b;font-size:12px}.page-content{display:grid;gap:12px;padding:15px 14px 28px}.group-title{padding:5px 3px 0;color:#65758a;font-size:13px;font-weight:750}.settings-card{overflow:hidden;border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.055)}.settings-card>h2{padding:15px 15px 8px;color:#65758a;font-size:13px}.setting-row{min-height:56px;display:flex;align-items:center;justify-content:space-between;gap:14px;padding:10px 15px;border-top:1px solid #edf1f5}.setting-row>span:first-child{color:#2b405c;font-size:14px;font-weight:650}.setting-actions{display:flex;align-items:center;gap:9px}.setting-actions>button{height:32px;padding:0 10px;border:1px solid #bad0ea;border-radius:8px;background:#f2f7fd;color:#2867ad;font-size:12px;font-weight:750}.toggle{position:relative;width:46px;height:26px;display:block;flex:0 0 auto}.toggle input{position:absolute;opacity:0;pointer-events:none}.toggle i{position:absolute;inset:0;border-radius:999px;background:#c8d1dc;transition:.18s}.toggle i::after{content:"";position:absolute;top:3px;left:3px;width:20px;height:20px;border-radius:50%;background:#fff;box-shadow:0 2px 6px rgba(21,39,63,.22);transition:.18s}.toggle input:checked+i{background:#2368c4}.toggle input:checked+i::after{transform:translateX(20px)}.collapse-heading{width:100%;height:54px;display:flex;align-items:center;justify-content:space-between;padding:0 15px;border:0;background:#fff;color:#2b405c;font-size:14px;font-weight:750}.collapse-heading svg{width:19px;height:19px;color:#7d8a9b;transition:transform .18s}.collapse-heading svg.expanded{transform:rotate(90deg)}.collapse-content{border-top:1px solid #edf1f5}.collapse-content .setting-row:first-child{border-top:0}.action-shell{position:fixed;z-index:18;right:0;bottom:0;left:0;padding:10px 14px max(10px,env(safe-area-inset-bottom));border-top:1px solid #e1e8f0;background:rgba(255,255,255,.97);box-shadow:0 -6px 20px rgba(23,54,93,.07)}.action-shell button{width:100%;height:48px;border:0;border-radius:11px;background:#2368c4;color:#fff;font-size:15px;font-weight:750}.toast-message{position:fixed;z-index:50;top:82px;left:50%;width:max-content;max-width:calc(100% - 36px);padding:10px 16px;border-radius:10px;background:#197052;color:#fff;font-size:13px;font-weight:700;box-shadow:0 8px 22px rgba(23,54,93,.2);transform:translateX(-50%)}.drop-toast-enter-active,.drop-toast-leave-active{transition:opacity .16s ease,transform .16s ease}.drop-toast-enter-from,.drop-toast-leave-to{opacity:0;transform:translate(-50%,-14px)}

.page-content {
  gap: 15px;
  padding-top: 18px;
}

.settings-card {
  border-color: #dfe7f0;
  box-shadow: 0 9px 26px rgba(23, 54, 93, 0.075);
}

.document-settings-card {
  border-color: #cddff3;
}

.document-settings-card > h2 {
  min-height: 50px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 0 16px;
  background: #eef5fd;
  color: #245f9f;
  font-size: 15px;
}

.document-settings-card > h2 small {
  color: #7188a3;
  font-size: 12px;
  font-weight: 650;
}

.setting-row {
  min-height: 62px;
  padding-inline: 16px;
}

.setting-row > span:first-child {
  color: #263b56;
  font-size: 14px;
}

.setting-actions {
  gap: 12px;
}

.setting-actions > button {
  min-width: 48px;
  height: 34px;
  padding: 0 11px;
  border-color: #a9c7e9;
  border-radius: 9px;
  background: #eaf3fd;
  color: #1f63ad;
}

.setting-actions > button:active {
  background: #d7e9fb;
}

.toggle {
  width: 50px;
  height: 28px;
}

.toggle i::after {
  width: 22px;
  height: 22px;
}

.toggle input:checked + i::after {
  transform: translateX(22px);
}

.group-title {
  padding: 4px 4px 0;
  color: #536a85;
  font-size: 14px;
}

.collapse-heading {
  height: 62px;
  padding-inline: 16px;
}

.collapse-title {
  display: flex;
  align-items: center;
  gap: 11px;
}

.collapse-title b {
  width: 34px;
  height: 34px;
  display: grid;
  place-items: center;
  border-radius: 10px;
  background: #e9f2fd;
  color: #2368c4;
  font-size: 13px;
}

.pallet-card .collapse-title b {
  background: #fff0e3;
  color: #b56520;
}

.operation-card .collapse-title b {
  background: #eee9fb;
  color: #7450ad;
}

.collapse-heading svg {
  width: 21px;
  height: 21px;
}

.collapse-content {
  background: #fbfcfe;
}

.action-shell button {
  box-shadow: 0 7px 16px rgba(35, 104, 196, 0.2);
}
</style>
