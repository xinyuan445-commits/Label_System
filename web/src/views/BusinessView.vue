<script setup lang="ts">
import { computed } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import ApprovalDocumentView from '@/views/ApprovalDocumentView.vue'
import LabelQueryView from '@/views/LabelQueryView.vue'
import OutboundDocumentView from '@/views/OutboundDocumentView.vue'
import {
  isBusinessDocumentType,
  type BusinessDocumentType,
} from '@/services/business-document-config'
import { findWorkspaceEntry } from '@/workspace'

const route = useRoute()
const router = useRouter()

const entry = computed(() => {
  const code = Array.isArray(route.params.code) ? route.params.code[0] : route.params.code
  return findWorkspaceEntry(code ?? '')
})

const businessDocumentType = computed(() => {
  const code = entry.value?.code
  return isBusinessDocumentType(code) ? code : null
})

const approvalDocumentType = computed<BusinessDocumentType | null>(() => {
  const code = entry.value?.code
  const documentCode = code?.startsWith('approval-') ? code.slice('approval-'.length) : ''
  return isBusinessDocumentType(documentCode) ? documentCode : null
})

const returnRouteName = computed(() =>
  route.query.origin === 'all-functions' ? 'all-functions' : 'home',
)

async function leaveFunction(): Promise<void> {
  await router.replace({ name: returnRouteName.value })
}

onBeforeRouteLeave((to) => {
  if (businessDocumentType.value || approvalDocumentType.value || to.name === returnRouteName.value) return true
  return { name: returnRouteName.value, replace: true }
})
</script>

<template>
  <OutboundDocumentView
    v-if="businessDocumentType"
    :key="businessDocumentType"
    :document-type="businessDocumentType"
  />

  <ApprovalDocumentView
    v-else-if="approvalDocumentType"
    :key="`approval-${approvalDocumentType}`"
    :document-type="approvalDocumentType"
  />

  <LabelQueryView v-else-if="entry?.code === 'label-query'" />

  <main v-else class="function-page">
    <header class="page-header">
      <button class="back-button" type="button" aria-label="返回" @click="leaveFunction">
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="m15 18-6-6 6-6" />
        </svg>
      </button>
      <h1>{{ entry?.label ?? '功能' }}</h1>
    </header>

    <section class="empty-state">待开发</section>
  </main>
</template>

<style scoped>
.function-page {
  min-height: 100dvh;
  padding: calc(24px + var(--app-safe-top)) 20px 24px;
  background: #f3f6fa;
}

.page-header,
.empty-state {
  width: min(100%, 920px);
  margin-inline: auto;
}

.page-header {
  min-height: 52px;
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 18px;
}

.back-button {
  width: 44px;
  height: 44px;
  display: grid;
  place-items: center;
  border: 0;
  border-radius: 12px;
  background: #fff;
  color: #365678;
  box-shadow: 0 5px 16px rgba(23, 54, 93, 0.08);
}

.back-button svg {
  width: 23px;
  height: 23px;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 2;
}

h1 {
  color: #17365d;
  font-size: 25px;
}

.empty-state {
  min-height: 180px;
  display: grid;
  place-items: center;
  border: 1px solid rgba(23, 54, 93, 0.07);
  border-radius: 18px;
  background: #fff;
  color: #8a97a8;
  font-size: 14px;
  box-shadow: 0 10px 28px rgba(23, 54, 93, 0.06);
}

@media (max-width: 480px) {
  .function-page {
    padding: calc(20px + var(--app-safe-top)) 14px 20px;
  }
}
</style>
