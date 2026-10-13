<script setup lang="ts">
import { computed, ref } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import DateField from '@/components/DateField.vue'
import { returnToParentDocument } from '@/services/document-subpage-navigation'
import {
  getBusinessDocumentConfig,
  isBusinessDocumentType,
} from '@/services/business-document-config'
import {
  findPendingSourceLineConflicts,
  readOutboundDocumentWorkingState,
} from '@/services/outbound-document-drafts'
import {
  loadMaterialRequirements,
  loadSourceDocumentLines,
  saveSourceDocumentSelection,
  searchSourceDocuments,
  type OutboundDocumentType,
  type SourceDocumentLine,
} from '@/services/source-document-selection'

const route = useRoute()
const router = useRouter()

const outboundDocumentType = computed<OutboundDocumentType>(() => {
  const code = Array.isArray(route.params.code) ? route.params.code[0] : route.params.code
  return isBusinessDocumentType(code) ? code : 'material-outbound'
})
const sourceTypes = computed(() =>
  getBusinessDocumentConfig(outboundDocumentType.value).sourceTypes,
)

const sourceType = ref(sourceTypes.value[0]?.value ?? '')
const displayMode = ref<'merged' | 'detail'>('merged')
const queryOpen = ref(false)
const startDate = ref(offsetBeijingDate(-30))
const endDate = ref(offsetBeijingDate(0))
const keyword = ref('')
const results = ref<SourceDocumentLine[]>([])
const selectedLineIds = ref<number[]>([])
const selectedDocumentId = ref<number | null>(null)
const searched = ref(false)
const loading = ref(false)
const errorMessage = ref('')
let allowDocumentReturn = false

const selectedLines = computed(() => {
  const selected = new Set(selectedLineIds.value)
  return results.value.filter((line) => selected.has(line.sourceLineId))
})

const mergedResults = computed(() => {
  const groups = new Map<number, {
    sourceDocumentId: number
    sourceDocumentNo: string
    documentDate: string
    lineCount: number
    productCount: number
    totalQuantity: number
  }>()
  const productCodes = new Map<number, Set<string>>()
  results.value.forEach((line) => {
    const current = groups.get(line.sourceDocumentId)
    if (!current) {
      groups.set(line.sourceDocumentId, {
        sourceDocumentId: line.sourceDocumentId,
        sourceDocumentNo: line.sourceDocumentNo,
        documentDate: line.documentDate,
        lineCount: 1,
        productCount: 1,
        totalQuantity: line.orderQuantity,
      })
      productCodes.set(line.sourceDocumentId, new Set([line.productCode]))
      return
    }
    current.lineCount += 1
    current.totalQuantity += line.orderQuantity
    const codes = productCodes.get(line.sourceDocumentId)!
    codes.add(line.productCode)
    current.productCount = codes.size
  })
  return [...groups.values()]
})

const selectedCount = computed(() =>
  displayMode.value === 'merged'
    ? selectedDocumentId.value === null ? 0 : 1
    : selectedLineIds.value.length,
)

const sourceTypeLabel = computed(() =>
  sourceTypes.value.find((option) => option.value === sourceType.value)?.label ?? '未选择',
)

const displayModeLabel = computed(() => displayMode.value === 'merged' ? '合并' : '明细')

const querySummary = computed(() => {
  const normalizedKeyword = keyword.value.trim()
  if (normalizedKeyword) return normalizedKeyword
  const start = startDate.value.slice(5).replace('-', '/')
  const end = endDate.value.slice(5).replace('-', '/')
  return start && end ? `${start}—${end}` : '日期或关键字'
})

function offsetBeijingDate(offsetDays: number): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai', year: 'numeric', month: '2-digit', day: '2-digit',
  }).formatToParts(new Date())
  const value = Object.fromEntries(parts.map((part) => [part.type, part.value]))
  const utcDate = new Date(Date.UTC(Number(value.year), Number(value.month) - 1, Number(value.day)))
  utcDate.setUTCDate(utcDate.getUTCDate() + offsetDays)
  return utcDate.toISOString().slice(0, 10)
}

async function search(): Promise<void> {
  errorMessage.value = ''
  searched.value = true
  if (!sourceType.value) {
    errorMessage.value = '请选择选单类型'
    return
  }
  if (!keyword.value.trim() && (!startDate.value || !endDate.value)) {
    errorMessage.value = '请选择日期范围或输入单号'
    return
  }
  if (startDate.value && endDate.value && startDate.value > endDate.value) {
    errorMessage.value = '开始日期不能晚于结束日期'
    return
  }

  loading.value = true
  try {
    const normalizedKeyword = keyword.value.trim()
    results.value = await searchSourceDocuments({
      documentType: outboundDocumentType.value,
      sourceType: sourceType.value,
      keyword: normalizedKeyword,
      startDate: normalizedKeyword ? '' : startDate.value,
      endDate: normalizedKeyword ? '' : endDate.value,
    })
    selectedLineIds.value = []
    selectedDocumentId.value = null
    queryOpen.value = false
  } catch (error) {
    results.value = []
    errorMessage.value = error instanceof Error ? error.message : '查询失败'
  } finally {
    loading.value = false
  }
}

function changeDisplayMode(mode: 'merged' | 'detail'): void {
  if (displayMode.value === mode) return
  displayMode.value = mode
  selectedLineIds.value = []
  selectedDocumentId.value = null
  errorMessage.value = ''
}

function toggleDocument(sourceDocumentId: number): void {
  selectedDocumentId.value = selectedDocumentId.value === sourceDocumentId
    ? null
    : sourceDocumentId
  errorMessage.value = ''
}

function toggleLine(line: SourceDocumentLine): void {
  const selected = new Set(selectedLineIds.value)
  if (selected.has(line.sourceLineId)) selected.delete(line.sourceLineId)
  else selected.add(line.sourceLineId)
  selectedLineIds.value = [...selected]
  errorMessage.value = ''
}

async function confirmSelection(): Promise<void> {
  if (!selectedCount.value) {
    errorMessage.value = displayMode.value === 'merged' ? '请选择源单' : '请选择源单明细'
    return
  }
  loading.value = true
  errorMessage.value = ''
  try {
    const lines = displayMode.value === 'merged'
      ? await loadSourceDocumentLines(
          outboundDocumentType.value, sourceType.value, [selectedDocumentId.value!],
        )
      : selectedLines.value
    const workingState = readOutboundDocumentWorkingState(outboundDocumentType.value)
    const conflicts = await findPendingSourceLineConflicts(
      outboundDocumentType.value,
      lines.map((line) => line.sourceLineId),
      workingState?.documentId ?? null,
    )
    if (conflicts.length) {
      const conflict = lines.find((line) => conflicts.includes(line.sourceLineId))
      errorMessage.value = conflict
        ? `${conflict.sourceDocumentNo} 行${conflict.sourceLineNo ?? '-'}已在未上传单据中`
        : '所选明细已在未上传单据中'
      return
    }
    const requirements = outboundDocumentType.value === 'material-outbound'
      ? await loadMaterialRequirements(
          outboundDocumentType.value,
          sourceType.value,
          lines.map((line) => line.sourceLineId),
        )
      : []
    const option = sourceTypes.value.find((item) => item.value === sourceType.value)
    if (!option) return
    const documentNumbers = [...new Set(lines.map((line) => line.sourceDocumentNo))]
    saveSourceDocumentSelection({
      outboundDocumentType: outboundDocumentType.value,
      sourceType: sourceType.value,
      sourceTypeLabel: option.label,
      documentNo: documentNumbers.join('、'),
      documentDate: lines[0]?.documentDate ?? endDate.value,
      selectedLines: lines,
      requirements,
    })
    await returnToDocument()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '明细加载失败'
  } finally {
    loading.value = false
  }
}

async function returnToDocument(): Promise<void> {
  allowDocumentReturn = true
  await returnToParentDocument(router, route, outboundDocumentType.value)
}

onBeforeRouteLeave((to) => {
  if (allowDocumentReturn) {
    allowDocumentReturn = false
    return true
  }
  if (to.name === 'work-function' && to.params.code === outboundDocumentType.value) return true
  return {
    name: 'work-function', params: { code: outboundDocumentType.value },
    query: route.query, replace: true,
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
        <button
          class="parameter-summary"
          type="button"
          :aria-expanded="queryOpen"
          @click="queryOpen = !queryOpen"
        >
          <span>
            <strong>{{ sourceTypeLabel }} · {{ displayModeLabel }}</strong>
            <small>{{ querySummary }}</small>
          </span>
          <svg :class="{ expanded: queryOpen }" viewBox="0 0 24 24" aria-hidden="true">
            <path d="m7 10 5 5 5-5" />
          </svg>
        </button>

        <div v-if="queryOpen" class="query-fields">
          <div class="source-options-row">
            <label class="field">
              <span><i>*</i>选单类型</span>
              <select v-model="sourceType" @change="results = []; selectedLineIds = []; selectedDocumentId = null">
                <option v-for="option in sourceTypes" :key="option.value" :value="option.value">
                  {{ option.label }}
                </option>
              </select>
            </label>
            <div class="display-switch" role="group" aria-label="显示方式">
              <button
                type="button"
                :class="{ active: displayMode === 'merged' }"
                @click="changeDisplayMode('merged')"
              >
                合并
              </button>
              <button
                type="button"
                :class="{ active: displayMode === 'detail' }"
                @click="changeDisplayMode('detail')"
              >
                明细
              </button>
            </div>
          </div>
          <div class="date-row">
            <label class="field"><span>开始日期</span><DateField v-model="startDate" aria-label="开始日期" /></label>
            <label class="field"><span>结束日期</span><DateField v-model="endDate" aria-label="结束日期" /></label>
          </div>
          <label class="field full-width">
            <span>单号或产品</span>
            <div class="search-row">
              <input v-model="keyword" autocomplete="off" placeholder="输入关键字" @keyup.enter="search" />
              <button type="button" :disabled="loading" @click="search">查询</button>
            </div>
          </label>
        </div>
      </section>

      <p v-if="errorMessage" class="error-message">{{ errorMessage }}</p>

      <section class="result-section">
        <div class="section-heading">
          <h2>查询结果</h2>
          <span>{{ displayMode === 'merged' ? mergedResults.length : results.length }}项</span>
        </div>

        <template v-if="displayMode === 'merged'">
          <button
            v-for="result in mergedResults"
            :key="result.sourceDocumentId"
            class="result-card"
            :class="{ selected: selectedDocumentId === result.sourceDocumentId }"
            type="button"
            @click="toggleDocument(result.sourceDocumentId)"
          >
            <span class="check" aria-hidden="true">{{ selectedDocumentId === result.sourceDocumentId ? '✓' : '' }}</span>
            <span class="result-main">
              <strong>{{ result.sourceDocumentNo }}</strong>
              <span>{{ result.lineCount }}条明细 · {{ result.productCount }}个产品</span>
              <small>{{ result.documentDate }} · {{ result.totalQuantity }}</small>
            </span>
            <span class="material-count">{{ result.lineCount }}</span>
          </button>
        </template>

        <template v-else>
          <button
            v-for="result in results"
            :key="result.sourceLineId"
            class="result-card"
            :class="{ selected: selectedLineIds.includes(result.sourceLineId) }"
            type="button"
            @click="toggleLine(result)"
          >
            <span class="check" aria-hidden="true">{{ selectedLineIds.includes(result.sourceLineId) ? '✓' : '' }}</span>
            <span class="result-main">
              <strong>{{ result.sourceDocumentNo }}</strong>
              <span>{{ result.productCode }} {{ result.productName }}</span>
              <small>行{{ result.sourceLineNo ?? '-' }} · {{ result.orderQuantity }} · {{ result.documentDate }}</small>
            </span>
            <span class="material-count">{{ result.materialCount }}</span>
          </button>
        </template>

        <div v-if="!results.length" class="empty-result">
          {{ loading ? '查询中' : searched ? '暂无数据' : '请选择条件查询' }}
        </div>
      </section>
    </div>

    <footer class="action-shell">
      <span>已选{{ selectedCount }}项</span>
      <button type="button" :disabled="loading || !selectedCount" @click="confirmSelection">
        确定
      </button>
    </footer>
  </main>
</template>

<style scoped>
.source-page{min-height:100dvh;padding:72px 0 84px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content,.action-shell{width:min(100%,760px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:0 18px}.topbar button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.topbar svg{width:23px;height:23px;fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}h1{color:#17365d;font-size:22px}.page-content{display:grid;gap:14px;padding:16px 14px 28px}.search-card{display:grid;gap:14px;padding:16px;border:1px solid rgba(23,54,93,.08);border-radius:17px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.055)}.display-switch{display:grid;grid-template-columns:1fr 1fr;padding:3px;border-radius:11px;background:#edf2f7}.display-switch button{height:38px;border:0;border-radius:8px;background:transparent;color:#66768a;font:inherit;font-size:14px;font-weight:700}.display-switch button.active{background:#fff;color:#2368c4;box-shadow:0 2px 8px rgba(23,54,93,.12)}.date-row{display:grid;grid-template-columns:1fr 1fr;gap:12px}.field{display:grid;gap:7px}.field>span{color:#5b6c82;font-size:13px;font-weight:650}.field i{margin-right:3px;color:#d64040;font-style:normal}.field input,.field select{width:100%;height:44px;padding:0 11px;border:1px solid #d4deea;border-radius:10px;background:#fff;color:#17233d;font:inherit;outline:none}.search-row{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:9px}.search-row button{min-width:72px;border:0;border-radius:10px;background:#2368c4;color:#fff;font-size:14px;font-weight:700}.search-row button:disabled,.action-shell button:disabled{opacity:.48}.error-message{padding:0 4px;color:#bd3b3b;font-size:13px}.result-section{display:grid;gap:10px}.section-heading{display:flex;align-items:center;justify-content:space-between;padding:2px}.section-heading h2{color:#24344d;font-size:16px}.section-heading span{color:#7d8a9b;font-size:13px}.result-card{width:100%;display:grid;grid-template-columns:25px minmax(0,1fr) auto;align-items:center;gap:11px;padding:14px;border:1px solid rgba(23,54,93,.09);border-radius:15px;background:#fff;color:inherit;text-align:left;box-shadow:0 8px 24px rgba(23,54,93,.05)}.result-card.selected{border-color:#79a9df;background:#f5f9fe}.check{width:22px;height:22px;display:grid;place-items:center;border:1px solid #b8c6d6;border-radius:7px;color:#fff;font-size:13px}.selected .check{border-color:#2368c4;background:#2368c4}.result-main{min-width:0;display:grid;gap:4px}.result-main strong{overflow-wrap:anywhere;color:#203552;font:700 14px ui-monospace,SFMono-Regular,Consolas,monospace}.result-main>span{color:#2c405c;font-size:13px;font-weight:650}.result-main small{color:#7a8798;font-size:12px}.material-count{min-width:28px;height:28px;display:grid;place-items:center;border-radius:9px;background:#eaf3fd;color:#2368c4;font-size:12px;font-weight:750}.empty-result{min-height:126px;display:grid;place-items:center;border:1px dashed #cbd7e4;border-radius:15px;background:rgba(255,255,255,.6);color:#8b98a8;font-size:14px}.action-shell{position:fixed;z-index:18;right:0;bottom:0;left:0;display:grid;grid-template-columns:1fr minmax(130px,45%);align-items:center;gap:12px;padding:10px 14px max(10px,env(safe-area-inset-bottom));border-top:1px solid #e1e8f0;background:rgba(255,255,255,.97)}.action-shell span{color:#65758a;font-size:13px}.action-shell button{height:48px;border:0;border-radius:11px;background:#2368c4;color:#fff;font-size:15px;font-weight:750}@media(max-width:560px){.topbar{padding:0 14px}.page-content{padding-inline:12px}.search-card{padding:14px}.date-row{gap:9px}}
.source-page {
  padding-top: var(--app-header-height);
}

.topbar-shell {
  height: var(--app-header-height);
}

.topbar {
  padding-top: var(--app-safe-top);
}

.source-options-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  align-items: end;
  gap: 9px;
}

.source-options-row .field {
  min-width: 0;
}

.display-switch {
  height: 44px;
  align-items: stretch;
}

.display-switch button {
  height: 38px;
}

.parameter-summary {
  width: 100%;
  min-height: 52px;
  display: grid;
  grid-template-columns: minmax(0, 1fr) 18px;
  align-items: center;
  gap: 12px;
  padding: 8px 12px;
  border: 0;
  border-radius: 11px;
  background: #f3f6fa;
  color: #40536d;
  text-align: left;
}

.parameter-summary > span {
  min-width: 0;
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 12px;
}

.parameter-summary strong {
  overflow: hidden;
  font-size: 13px;
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.parameter-summary small {
  overflow: hidden;
  color: #8592a3;
  font-size: 12px;
  flex: 0 1 auto;
  text-align: right;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.parameter-summary svg {
  width: 18px;
  height: 18px;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 2;
  transition: transform 160ms ease;
}

.parameter-summary svg.expanded {
  transform: rotate(180deg);
}

.query-fields {
  display: grid;
  gap: 12px;
  padding-top: 1px;
}
</style>
