<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import {
  getBusinessDocumentConfig,
  isBusinessDocumentType,
  type BusinessDocumentType,
} from '@/services/business-document-config'
import {
  getBusinessDocumentFieldGroups,
  type DocumentFieldArea,
} from '@/services/business-document-fields'
import {
  ensureDocumentFieldSettings,
  fetchBusinessDocumentSettings,
  loadBusinessDocumentSettings,
  persistBusinessDocumentFieldSettings,
} from '@/services/business-document-settings'

const route = useRoute()
const router = useRouter()
const settings = ref(loadBusinessDocumentSettings())
const expanded = ref<Record<string, boolean>>({
  'header:header-basic': true,
  'body:body-basic': true,
})
const loading = ref(false)
const saving = ref(false)
const message = ref('')
let allowReturn = false

const documentType = computed<BusinessDocumentType>(() => {
  const code = Array.isArray(route.params.code) ? route.params.code[0] : route.params.code
  return isBusinessDocumentType(code) ? code : 'material-outbound'
})
const documentTitle = computed(() => getBusinessDocumentConfig(documentType.value).title)
const fieldSettings = computed(() =>
  ensureDocumentFieldSettings(settings.value, documentType.value),
)
const groups = computed(() => getBusinessDocumentFieldGroups(documentType.value))
const headerGroups = computed(() => groups.value.filter((group) => group.area === 'header'))
const bodyGroups = computed(() => groups.value.filter((group) => group.area === 'body'))

function groupKey(area: DocumentFieldArea, key: string): string {
  return `${area}:${key}`
}

function toggleGroup(area: DocumentFieldArea, key: string): void {
  const value = groupKey(area, key)
  expanded.value[value] = !expanded.value[value]
}

function updateVisible(key: string, event: Event): void {
  const state = fieldSettings.value[key]
  if (!state) return
  state.visible = (event.target as HTMLInputElement).checked
  if (!state.visible) state.required = false
}

function updateRequired(key: string, event: Event): void {
  const state = fieldSettings.value[key]
  if (!state) return
  state.required = (event.target as HTMLInputElement).checked
  if (state.required) state.visible = true
}

async function returnToDocument(): Promise<void> {
  allowReturn = true
  await router.replace({
    name: 'work-function',
    params: { code: documentType.value },
    query: route.query,
  })
}

async function saveAndReturn(): Promise<void> {
  if (saving.value) return
  saving.value = true
  try {
    await persistBusinessDocumentFieldSettings(documentType.value, settings.value)
    await returnToDocument()
  } catch (error) {
    message.value = error instanceof Error ? error.message : '保存失败'
  } finally {
    saving.value = false
  }
}

onBeforeRouteLeave((to) => {
  if (allowReturn) {
    allowReturn = false
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

onMounted(async () => {
  loading.value = true
  try {
    settings.value = await fetchBusinessDocumentSettings(documentType.value)
  } catch (error) {
    message.value = error instanceof Error ? error.message : '设置加载失败'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <main class="field-settings-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button class="back-button" type="button" aria-label="返回" @click="returnToDocument">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <div><h1>单据设置</h1><span>{{ documentTitle }}</span></div>
      </div>
    </header>

    <div v-if="!loading" class="page-content">
      <section class="area-card">
        <div class="area-heading">
          <strong>表头字段</strong>
          <span><i>显示</i><i>必填</i></span>
        </div>
        <div v-for="group in headerGroups" :key="group.key" class="field-group">
          <button type="button" @click="toggleGroup(group.area, group.key)">
            <span>{{ group.label }}</span>
            <svg :class="{ expanded: expanded[groupKey(group.area, group.key)] }" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 6 6 6-6 6" /></svg>
          </button>
          <div v-if="expanded[groupKey(group.area, group.key)]">
            <div v-for="item in group.fields" :key="item.key" class="field-row">
              <span>{{ item.label }}</span>
              <label class="toggle">
                <input :checked="fieldSettings[item.key]?.visible" type="checkbox" :aria-label="`${item.label}显示`" @change="updateVisible(item.key, $event)" />
                <i aria-hidden="true"></i>
              </label>
              <label class="toggle">
                <input :checked="fieldSettings[item.key]?.required" type="checkbox" :aria-label="`${item.label}必填`" @change="updateRequired(item.key, $event)" />
                <i aria-hidden="true"></i>
              </label>
            </div>
          </div>
        </div>
      </section>

      <section class="area-card">
        <div class="area-heading">
          <strong>表体字段</strong>
          <span><i>显示</i><i>必填</i></span>
        </div>
        <div v-for="group in bodyGroups" :key="group.key" class="field-group">
          <button type="button" @click="toggleGroup(group.area, group.key)">
            <span>{{ group.label }}</span>
            <svg :class="{ expanded: expanded[groupKey(group.area, group.key)] }" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 6 6 6-6 6" /></svg>
          </button>
          <div v-if="expanded[groupKey(group.area, group.key)]">
            <div v-for="item in group.fields" :key="item.key" class="field-row">
              <span>{{ item.label }}</span>
              <label class="toggle">
                <input :checked="fieldSettings[item.key]?.visible" type="checkbox" :aria-label="`${item.label}显示`" @change="updateVisible(item.key, $event)" />
                <i aria-hidden="true"></i>
              </label>
              <label class="toggle">
                <input :checked="fieldSettings[item.key]?.required" type="checkbox" :aria-label="`${item.label}必填`" @change="updateRequired(item.key, $event)" />
                <i aria-hidden="true"></i>
              </label>
            </div>
          </div>
        </div>
      </section>
    </div>

    <footer class="action-shell">
      <button type="button" :disabled="loading || saving" @click="saveAndReturn">
        {{ saving ? '保存中' : '保存' }}
      </button>
    </footer>

    <div v-if="message" class="toast-message" role="status">{{ message }}</div>
  </main>
</template>

<style scoped>
.field-settings-page{min-height:100dvh;padding:var(--app-header-height) 0 86px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:var(--app-header-height);border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content,.action-shell{width:min(100%,760px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:var(--app-safe-top) 18px 0}.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg,.field-group svg{fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}.back-button svg{width:23px;height:23px}.topbar>div{display:grid;gap:2px}.topbar h1{color:#17365d;font-size:21px}.topbar span{color:#7b899b;font-size:12px}.page-content{display:grid;gap:14px;padding:16px 14px 28px}.area-card{overflow:hidden;border:1px solid #dfe7f0;border-radius:16px;background:#fff;box-shadow:0 9px 26px rgba(23,54,93,.075)}.area-heading{min-height:52px;display:flex;align-items:center;justify-content:space-between;padding:0 16px;background:#eef5fd;color:#245f9f;font-size:15px}.area-heading>span{display:grid;grid-template-columns:repeat(2,48px);gap:8px;color:#71849a;font-size:11px;text-align:center}.area-heading i{font-style:normal}.field-group{border-top:1px solid #e8edf3}.field-group>button{width:100%;height:48px;display:flex;align-items:center;justify-content:space-between;padding:0 16px;border:0;background:#fff;color:#52677f;font-size:13px;font-weight:750}.field-group>button svg{width:18px;height:18px;transition:transform .18s}.field-group>button svg.expanded{transform:rotate(90deg)}.field-row{min-height:54px;display:grid;grid-template-columns:minmax(0,1fr) repeat(2,48px);align-items:center;gap:8px;padding:7px 16px;border-top:1px solid #edf1f5;background:#fbfcfe}.field-row>span{overflow:hidden;color:#2b405c;font-size:13px;font-weight:650;text-overflow:ellipsis;white-space:nowrap}.toggle{position:relative;width:42px;height:24px;display:block;margin-inline:auto}.toggle input{position:absolute;opacity:0;pointer-events:none}.toggle i{position:absolute;inset:0;border-radius:999px;background:#c8d1dc;transition:.18s}.toggle i::after{content:"";position:absolute;top:3px;left:3px;width:18px;height:18px;border-radius:50%;background:#fff;box-shadow:0 2px 6px rgba(21,39,63,.22);transition:.18s}.toggle input:checked+i{background:#2368c4}.toggle input:checked+i::after{transform:translateX(18px)}.action-shell{position:fixed;z-index:18;right:0;bottom:0;left:0;padding:10px 14px max(10px,env(safe-area-inset-bottom));border-top:1px solid #e1e8f0;background:rgba(255,255,255,.97);box-shadow:0 -6px 20px rgba(23,54,93,.07)}.action-shell button{width:100%;height:48px;border:0;border-radius:11px;background:#2368c4;color:#fff;font-size:15px;font-weight:750}.toast-message{position:fixed;z-index:50;top:calc(var(--app-header-height) + 10px);left:50%;width:max-content;max-width:calc(100% - 36px);padding:10px 16px;border-radius:10px;background:#ad3d3d;color:#fff;font-size:13px;font-weight:700;box-shadow:0 8px 22px rgba(23,54,93,.2);transform:translateX(-50%)}
</style>
