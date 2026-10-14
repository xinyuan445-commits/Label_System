<script setup lang="ts">
import { computed } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import {
  getBusinessDocumentConfig,
  isBusinessDocumentType,
} from '@/services/business-document-config'
import type { SourceQuantityAllocation } from '@/services/material-outbound-allocation'
import {
  returnToParentDocument,
  useDocumentSystemBack,
} from '@/services/document-subpage-navigation'
import { readOutboundDocumentWorkingState } from '@/services/outbound-document-drafts'
import type {
  MaterialRequirementLine,
  OutboundDocumentType,
  SourceDocumentLine,
} from '@/services/source-document-selection'
import { saveReturnRequirementSelection } from '@/services/source-document-selection'

const route = useRoute()
const router = useRouter()
let allowDocumentReturn = false

interface WorkingDetailLine {
  sourceAllocations?: SourceQuantityAllocation[]
}

const documentType = computed<OutboundDocumentType>(() => {
  const code = Array.isArray(route.params.code) ? route.params.code[0] : route.params.code
  return isBusinessDocumentType(code) ? code : 'material-outbound'
})
const title = computed(() =>
  documentType.value === 'material-outbound'
    ? isRedMaterialReturn.value ? '退料明细' : '领料明细'
    : '源单明细',
)
const workingPayload = computed(() => {
  const state = readOutboundDocumentWorkingState(documentType.value)
  return state?.payload && typeof state.payload === 'object'
    ? state.payload as {
        selectedSourceLines?: SourceDocumentLine[]
        materialRequirements?: MaterialRequirementLine[]
        lines?: WorkingDetailLine[]
        documentDirection?: 'BLUE' | 'RED'
      }
    : {}
})
const isRedMaterialReturn = computed(() =>
  documentType.value === 'material-outbound' && workingPayload.value.documentDirection === 'RED',
)
const sourceLines = computed(() =>
  Array.isArray(workingPayload.value.selectedSourceLines)
    ? workingPayload.value.selectedSourceLines
    : [],
)
const requirements = computed(() =>
  Array.isArray(workingPayload.value.materialRequirements)
    ? workingPayload.value.materialRequirements
    : [],
)
const displayedRequirements = computed(() => isRedMaterialReturn.value
  ? requirements.value.filter((line) => Number(line.issuedQuantity) > 0.000001)
  : requirements.value,
)
const pickedByRequirement = computed(() => {
  const result = new Map<number, number>()
  const lines = Array.isArray(workingPayload.value.lines) ? workingPayload.value.lines : []
  for (const line of lines) {
    for (const allocation of line.sourceAllocations ?? []) {
      const picked = Number(allocation.normalQty) + Number(allocation.overQty)
      result.set(allocation.requirementId, (result.get(allocation.requirementId) ?? 0) + picked)
    }
  }
  return result
})
const overPickedByRequirement = computed(() => {
  const result = new Map<number, number>()
  const lines = Array.isArray(workingPayload.value.lines) ? workingPayload.value.lines : []
  for (const line of lines) {
    for (const allocation of line.sourceAllocations ?? []) {
      const overPicked = Number(allocation.overQty)
      result.set(
        allocation.requirementId,
        (result.get(allocation.requirementId) ?? 0) + overPicked,
      )
    }
  }
  return result
})

function pickedQuantity(line: MaterialRequirementLine): number {
  return pickedByRequirement.value.get(line.requirementId) ?? 0
}

function isIssueComplete(line: MaterialRequirementLine): boolean {
  return Number(line.remainingQuantity) <= 0.000001
}

function overIssueQuantity(line: MaterialRequirementLine): number {
  const issuedOverPlan = Math.max(
    Number(line.issuedQuantity) - Number(line.plannedQuantity),
    0,
  )
  return issuedOverPlan + (overPickedByRequirement.value.get(line.requirementId) ?? 0)
}

function returnableQuantity(line: MaterialRequirementLine): number {
  return Math.max(Number(line.issuedQuantity) - pickedQuantity(line), 0)
}

function formatQty(value: number): string {
  return Number(value).toFixed(6).replace(/\.?0+$/, '')
}

async function returnToDocument(): Promise<void> {
  allowDocumentReturn = true
  await returnToParentDocument(router, route, documentType.value)
}

useDocumentSystemBack(returnToDocument)

async function selectReturnRequirement(line: MaterialRequirementLine): Promise<void> {
  if (returnableQuantity(line) <= 0.000001) return
  saveReturnRequirementSelection({
    outboundDocumentType: documentType.value,
    requirement: line,
  })
  await returnToDocument()
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
  <main class="detail-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button type="button" aria-label="返回" @click="returnToDocument">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>{{ title }}</h1>
        <span>{{ displayedRequirements.length || sourceLines.length }}项</span>
      </div>
    </header>

    <div class="page-content">
      <section v-if="displayedRequirements.length" class="detail-list">
        <article v-for="line in displayedRequirements" :key="line.requirementId" class="detail-card">
          <div class="detail-heading">
            <div><strong>{{ line.materialCode }}</strong><span>{{ line.materialName }}</span></div>
            <aside>
              <b>{{ isRedMaterialReturn ? `可退 ${formatQty(returnableQuantity(line))}` : line.remainingQuantity }}</b>
              <span v-if="!isRedMaterialReturn && isIssueComplete(line)" class="complete-stamp">领料完成</span>
              <span v-if="!isRedMaterialReturn && overIssueQuantity(line) > 0.000001" class="over-issue-stamp">
                超领 {{ formatQty(overIssueQuantity(line)) }}
              </span>
            </aside>
          </div>
          <p>{{ line.sourceDocumentNo }} · 行{{ line.sourceLineNo ?? '-' }} · {{ line.productCode }}</p>
          <dl :class="{ 'return-quantities': isRedMaterialReturn }">
            <div><dt>计划</dt><dd>{{ line.plannedQuantity }}</dd></div>
            <div><dt>已领</dt><dd>{{ line.issuedQuantity }}</dd></div>
            <template v-if="isRedMaterialReturn">
              <div><dt>本次退料</dt><dd>{{ formatQty(pickedQuantity(line)) }}</dd></div>
            </template>
            <template v-else>
              <div><dt>待领</dt><dd>{{ line.remainingQuantity }}</dd></div>
              <div><dt>已拣</dt><dd>{{ pickedQuantity(line) }}</dd></div>
            </template>
          </dl>
          <button
            v-if="isRedMaterialReturn"
            class="return-select"
            type="button"
            :disabled="returnableQuantity(line) <= 0.000001"
            @click="selectReturnRequirement(line)"
          >{{ returnableQuantity(line) > 0.000001 ? '退料' : '已选完' }}</button>
        </article>
      </section>

      <section v-else-if="sourceLines.length" class="detail-list">
        <article v-for="line in sourceLines" :key="line.sourceLineId" class="detail-card">
          <div class="detail-heading">
            <div><strong>{{ line.productCode }}</strong><span>{{ line.productName }}</span></div>
            <b>{{ line.orderQuantity }}</b>
          </div>
          <p>{{ line.sourceDocumentNo }} · 行{{ line.sourceLineNo ?? '-' }}</p>
        </article>
      </section>

      <div v-else class="empty-state">{{ isRedMaterialReturn ? '暂无可退明细' : '暂无明细' }}</div>
    </div>
  </main>
</template>

<style scoped>
.detail-page{min-height:100dvh;padding-top:72px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content{width:min(100%,760px);margin-inline:auto}.topbar{height:100%;display:grid;grid-template-columns:44px minmax(0,1fr) auto;align-items:center;gap:12px;padding:0 18px}.topbar button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.topbar svg{width:23px;height:23px;fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}.topbar h1{overflow:hidden;color:#17365d;font-size:21px;text-overflow:ellipsis;white-space:nowrap}.topbar>span{color:#78879a;font-size:13px}.page-content{padding:16px}.detail-list{display:grid;gap:11px}.detail-card{padding:15px;border:1px solid rgba(23,54,93,.08);border-radius:15px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.05)}.detail-heading{display:flex;align-items:flex-start;justify-content:space-between;gap:12px}.detail-heading>div{min-width:0;display:grid;gap:3px}.detail-heading>aside{display:grid;flex:0 0 auto;justify-items:end;gap:5px}.detail-heading strong{overflow-wrap:anywhere;color:#203552;font:700 14px ui-monospace,SFMono-Regular,Consolas,monospace}.detail-heading span{color:#53657c;font-size:13px}.detail-heading b{padding:5px 8px;border-radius:8px;background:#eaf3fd;color:#2368c4;font-size:12px}.detail-heading .complete-stamp{padding:3px 6px;transform:rotate(-5deg);border:2px solid rgba(38,152,92,.58);border-radius:5px;color:#278b55;font-size:11px;font-weight:800;letter-spacing:1px}.detail-card p{margin:9px 0 0;color:#7a8798;font-size:12px}.detail-card dl{display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin:12px 0 0;padding:10px;border-radius:10px;background:#f6f8fb}.detail-card dl div{display:grid;gap:3px}.detail-card dt,.detail-card dd{margin:0;font-size:12px}.detail-card dt{color:#8491a2}.detail-card dd{color:#33465e;font-weight:700}.empty-state{min-height:180px;display:grid;place-items:center;border:1px dashed #cbd7e4;border-radius:16px;background:rgba(255,255,255,.62);color:#8b98a8;font-size:14px}@media(max-width:560px){.topbar{padding:0 14px}.page-content{padding:14px 12px}}
.detail-page {
  padding-top: var(--app-header-height);
}

.topbar-shell {
  height: var(--app-header-height);
}

.topbar {
  padding-top: var(--app-safe-top);
}

.detail-heading .over-issue-stamp {
  padding: 3px 6px;
  transform: rotate(4deg);
  border: 2px solid rgba(210, 126, 18, .62);
  border-radius: 5px;
  color: #b56c0d;
  font-size: 11px;
  font-weight: 800;
  letter-spacing: .5px;
}

.detail-card dl.return-quantities {
  grid-template-columns: repeat(3, 1fr);
}

.return-select {
  width: 100%;
  height: 42px;
  margin-top: 11px;
  border: 0;
  border-radius: 10px;
  background: #2368c4;
  color: #fff;
  font-weight: 750;
}

.return-select:disabled {
  background: #d7dee7;
  color: #8794a4;
}
</style>
