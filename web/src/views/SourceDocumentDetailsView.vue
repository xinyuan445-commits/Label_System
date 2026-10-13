<script setup lang="ts">
import { computed } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import {
  getBusinessDocumentConfig,
  isBusinessDocumentType,
} from '@/services/business-document-config'
import { returnToParentDocument } from '@/services/document-subpage-navigation'
import { readOutboundDocumentWorkingState } from '@/services/outbound-document-drafts'
import type {
  MaterialRequirementLine,
  OutboundDocumentType,
  SourceDocumentLine,
} from '@/services/source-document-selection'

const route = useRoute()
const router = useRouter()
let allowDocumentReturn = false

const documentType = computed<OutboundDocumentType>(() => {
  const code = Array.isArray(route.params.code) ? route.params.code[0] : route.params.code
  return isBusinessDocumentType(code) ? code : 'material-outbound'
})
const title = computed(() =>
  documentType.value === 'material-outbound' ? '领料明细' : '源单明细',
)
const workingPayload = computed(() => {
  const state = readOutboundDocumentWorkingState(documentType.value)
  return state?.payload && typeof state.payload === 'object'
    ? state.payload as {
        selectedSourceLines?: SourceDocumentLine[]
        materialRequirements?: MaterialRequirementLine[]
      }
    : {}
})
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

async function returnToDocument(): Promise<void> {
  allowDocumentReturn = true
  await returnToParentDocument(router, route, documentType.value)
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
        <span>{{ requirements.length || sourceLines.length }}项</span>
      </div>
    </header>

    <div class="page-content">
      <section v-if="requirements.length" class="detail-list">
        <article v-for="line in requirements" :key="line.requirementId" class="detail-card">
          <div class="detail-heading">
            <div><strong>{{ line.materialCode }}</strong><span>{{ line.materialName }}</span></div>
            <b>{{ line.remainingQuantity }}</b>
          </div>
          <p>{{ line.sourceDocumentNo }} · 行{{ line.sourceLineNo ?? '-' }} · {{ line.productCode }}</p>
          <dl>
            <div><dt>计划</dt><dd>{{ line.plannedQuantity }}</dd></div>
            <div><dt>已领</dt><dd>{{ line.issuedQuantity }}</dd></div>
            <div><dt>待领</dt><dd>{{ line.remainingQuantity }}</dd></div>
          </dl>
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

      <div v-else class="empty-state">暂无明细</div>
    </div>
  </main>
</template>

<style scoped>
.detail-page{min-height:100dvh;padding-top:72px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content{width:min(100%,760px);margin-inline:auto}.topbar{height:100%;display:grid;grid-template-columns:44px minmax(0,1fr) auto;align-items:center;gap:12px;padding:0 18px}.topbar button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.topbar svg{width:23px;height:23px;fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}.topbar h1{overflow:hidden;color:#17365d;font-size:21px;text-overflow:ellipsis;white-space:nowrap}.topbar>span{color:#78879a;font-size:13px}.page-content{padding:16px}.detail-list{display:grid;gap:11px}.detail-card{padding:15px;border:1px solid rgba(23,54,93,.08);border-radius:15px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.05)}.detail-heading{display:flex;align-items:flex-start;justify-content:space-between;gap:12px}.detail-heading>div{min-width:0;display:grid;gap:3px}.detail-heading strong{overflow-wrap:anywhere;color:#203552;font:700 14px ui-monospace,SFMono-Regular,Consolas,monospace}.detail-heading span{color:#53657c;font-size:13px}.detail-heading b{padding:5px 8px;border-radius:8px;background:#eaf3fd;color:#2368c4;font-size:12px}.detail-card p{margin:9px 0 0;color:#7a8798;font-size:12px}.detail-card dl{display:grid;grid-template-columns:repeat(3,1fr);gap:8px;margin:12px 0 0;padding:10px;border-radius:10px;background:#f6f8fb}.detail-card dl div{display:grid;gap:3px}.detail-card dt,.detail-card dd{margin:0;font-size:12px}.detail-card dt{color:#8491a2}.detail-card dd{color:#33465e;font-weight:700}.empty-state{min-height:180px;display:grid;place-items:center;border:1px dashed #cbd7e4;border-radius:16px;background:rgba(255,255,255,.62);color:#8b98a8;font-size:14px}@media(max-width:560px){.topbar{padding:0 14px}.page-content{padding:14px 12px}}
.detail-page {
  padding-top: var(--app-header-height);
}

.topbar-shell {
  height: var(--app-header-height);
}

.topbar {
  padding-top: var(--app-safe-top);
}
</style>
