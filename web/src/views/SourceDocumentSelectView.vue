<script setup lang="ts">
import { computed, ref } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import {
  getBusinessDocumentConfig,
  isBusinessDocumentType,
} from '@/services/business-document-config'
import {
  saveSourceDocumentSelection,
  type OutboundDocumentType,
} from '@/services/source-document-selection'

interface SourceTypeOption {
  value: string
  label: string
}

interface SearchResult {
  documentNo: string
  documentDate: string
  sourceType: string
  sourceTypeLabel: string
}

const route = useRoute()
const router = useRouter()

const outboundDocumentType = computed<OutboundDocumentType>(() => {
  const code = Array.isArray(route.params.code) ? route.params.code[0] : route.params.code
  return isBusinessDocumentType(code) ? code : 'material-outbound'
})

const sourceTypes = computed<SourceTypeOption[]>(() =>
  getBusinessDocumentConfig(outboundDocumentType.value).sourceTypes,
)

const sourceType = ref(sourceTypes.value[0]?.value ?? '')
const startDate = ref(offsetBeijingDate(-30))
const endDate = ref(offsetBeijingDate(0))
const documentNo = ref('')
const searched = ref(false)
const errorMessage = ref('')
const results = ref<SearchResult[]>([])
let allowDocumentReturn = false

function offsetBeijingDate(offsetDays: number): string {
  const now = new Date()
  const beijingParts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(now)
  const value = Object.fromEntries(beijingParts.map((part) => [part.type, part.value]))
  const utcDate = new Date(Date.UTC(Number(value.year), Number(value.month) - 1, Number(value.day)))
  utcDate.setUTCDate(utcDate.getUTCDate() + offsetDays)
  return utcDate.toISOString().slice(0, 10)
}

function search(): void {
  searched.value = true
  errorMessage.value = ''
  results.value = []

  if (!sourceType.value) {
    errorMessage.value = '请选择选单类型'
    return
  }

  const inputNo = documentNo.value.trim()
  if (!inputNo) {
    if (!startDate.value || !endDate.value) {
      errorMessage.value = '请选择日期范围或输入源单号'
      return
    }
    if (startDate.value > endDate.value) {
      errorMessage.value = '开始日期不能晚于结束日期'
      return
    }
    return
  }

  const option = sourceTypes.value.find((item) => item.value === sourceType.value)
  if (!option) return

  results.value = [
    {
      documentNo: inputNo,
      documentDate: endDate.value || offsetBeijingDate(0),
      sourceType: option.value,
      sourceTypeLabel: option.label,
    },
  ]
}

async function selectDocument(result: SearchResult): Promise<void> {
  saveSourceDocumentSelection({
    outboundDocumentType: outboundDocumentType.value,
    sourceType: result.sourceType,
    sourceTypeLabel: result.sourceTypeLabel,
    documentNo: result.documentNo,
    documentDate: result.documentDate,
  })
  await returnToDocument()
}

async function returnToDocument(): Promise<void> {
  allowDocumentReturn = true
  await router.replace({
    name: 'work-function',
    params: { code: outboundDocumentType.value },
    query: route.query,
  })
}

onBeforeRouteLeave((to) => {
  if (allowDocumentReturn) {
    allowDocumentReturn = false
    return true
  }
  if (to.name === 'work-function' && to.params.code === outboundDocumentType.value) return true
  return {
    name: 'work-function',
    params: { code: outboundDocumentType.value },
    query: route.query,
    replace: true,
  }
})
</script>

<template>
  <main class="source-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button type="button" aria-label="返回" @click="returnToDocument">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>选择源单</h1>
      </div>
    </header>

    <div class="page-content">
      <section class="search-card">
        <label class="field full-width">
          <span><i>*</i>选单类型</span>
          <select v-model="sourceType">
            <option v-for="option in sourceTypes" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
        </label>

        <div class="date-row">
          <label class="field">
            <span>开始日期</span>
            <input v-model="startDate" type="date" />
          </label>
          <label class="field">
            <span>结束日期</span>
            <input v-model="endDate" type="date" />
          </label>
        </div>

        <label class="field full-width">
          <span>源单号</span>
          <div class="search-row">
            <input v-model="documentNo" autocomplete="off" placeholder="输入单号" @keyup.enter="search" />
            <button type="button" @click="search">查询</button>
          </div>
        </label>
      </section>

      <p v-if="errorMessage" class="error-message">{{ errorMessage }}</p>

      <section class="result-section">
        <div class="section-heading">
          <h2>查询结果</h2>
          <span>{{ results.length }}项</span>
        </div>

        <article v-for="result in results" :key="result.documentNo" class="result-card">
          <div>
            <strong>{{ result.documentNo }}</strong>
            <span>{{ result.sourceTypeLabel }} · {{ result.documentDate }}</span>
          </div>
          <button type="button" @click="selectDocument(result)">选择</button>
        </article>

        <div v-if="!results.length" class="empty-result">
          {{ searched && !documentNo.trim() ? '接口待接入' : '暂无数据' }}
        </div>
      </section>
    </div>
  </main>
</template>

<style scoped>
.source-page{min-height:100dvh;padding-top:72px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content{width:min(100%,760px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:0 18px}.topbar button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.topbar svg{width:23px;height:23px;fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}h1{color:#17365d;font-size:22px}.page-content{display:grid;gap:14px;padding:16px}.search-card,.result-card{border:1px solid rgba(23,54,93,.08);background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.055)}.search-card{display:grid;gap:14px;padding:16px;border-radius:17px}.date-row{display:grid;grid-template-columns:1fr 1fr;gap:12px}.field{display:grid;gap:7px}.field>span{color:#5b6c82;font-size:13px;font-weight:650}.field i{margin-right:3px;color:#d64040;font-style:normal}.field input,.field select{width:100%;height:44px;padding:0 11px;border:1px solid #d4deea;border-radius:10px;background:#fff;color:#17233d;font:inherit;outline:none}.field input:focus,.field select:focus{border-color:#3378cf;box-shadow:0 0 0 3px rgba(51,120,207,.1)}.search-row{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:9px}.search-row button{min-width:72px;border:0;border-radius:10px;background:#2368c4;color:#fff;font-size:14px;font-weight:700}.error-message{padding:0 4px;color:#bd3b3b;font-size:13px}.result-section{display:grid;gap:10px}.section-heading{display:flex;align-items:center;justify-content:space-between;padding:2px}.section-heading h2{color:#24344d;font-size:16px}.section-heading span{color:#7d8a9b;font-size:13px}.result-card{display:flex;align-items:center;justify-content:space-between;gap:14px;padding:15px;border-radius:15px}.result-card div{min-width:0;display:grid;gap:5px}.result-card strong{overflow-wrap:anywhere;color:#203552;font:700 15px ui-monospace,SFMono-Regular,Consolas,monospace}.result-card span{color:#728096;font-size:12px}.result-card button{min-width:66px;height:40px;border:0;border-radius:10px;background:#e9f2fd;color:#2368c4;font-size:13px;font-weight:750}.empty-result{min-height:126px;display:grid;place-items:center;border:1px dashed #cbd7e4;border-radius:15px;background:rgba(255,255,255,.6);color:#8b98a8;font-size:14px}@media(max-width:560px){.topbar{padding:0 14px}.page-content{padding:14px 12px}.search-card{padding:14px}.date-row{gap:9px}}
</style>
