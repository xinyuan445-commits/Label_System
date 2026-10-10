<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'

import { getPdaBridge, isMockPdaBridge } from '@/bridge/pda-bridge'
import type { PdaScanEvent } from '@/bridge/types'
import { getRuntimeConfig } from '@/services/runtime-config'
import { useAuthStore } from '@/stores/auth'

const router = useRouter()
const authStore = useAuthStore()
const config = getRuntimeConfig()
const bridge = getPdaBridge()

const lastScan = ref<PdaScanEvent | null>(null)
const mockBarcode = ref('TEST-CT58C-001')
const canSimulate = computed(() => isMockPdaBridge(bridge))

let stopScanListener: (() => void) | null = null

function simulateScan(): void {
  if (isMockPdaBridge(bridge)) {
    bridge.simulateScan(mockBarcode.value)
  }
}

async function logout(): Promise<void> {
  authStore.logout()
  await router.replace({ name: 'login' })
}

onMounted(() => {
  stopScanListener = bridge.subscribeScan((event) => {
    lastScan.value = event
  })
})

onBeforeUnmount(() => {
  stopScanListener?.()
})
</script>

<template>
  <main class="workspace">
    <header class="topbar">
      <div>
        <h1>工作台</h1>
        <span class="username">{{ authStore.session?.username }}</span>
      </div>

      <button class="secondary-button" type="button" @click="logout">退出</button>
    </header>

    <section class="scan-card">
      <div class="card-heading">
        <h2>扫码</h2>
        <span class="bridge-state">{{ bridge.mode === 'mock' ? '模拟' : '设备' }}</span>
      </div>

      <div class="scan-value" :class="{ empty: !lastScan }">
        {{ lastScan?.barcode || '等待扫码' }}
      </div>

      <time v-if="lastScan" :datetime="new Date(lastScan.receivedAt).toISOString()">
        {{ new Date(lastScan.receivedAt).toLocaleTimeString('zh-CN', { hour12: false }) }}
      </time>
    </section>

    <section v-if="canSimulate" class="mock-panel">
      <input v-model="mockBarcode" aria-label="模拟条码" @keyup.enter="simulateScan" />
      <button class="primary-button" type="button" @click="simulateScan">模拟扫码</button>
    </section>

    <footer>{{ config.h5Version }}</footer>
  </main>
</template>

<style scoped>
.workspace {
  min-height: 100dvh;
  padding: 28px;
  background: #f3f6fa;
}

.topbar {
  max-width: 920px;
  margin: 0 auto 28px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
}

h1 {
  color: #17365d;
  font-size: 30px;
}

.username {
  display: block;
  margin-top: 5px;
  color: #718096;
  font-size: 14px;
}

.secondary-button,
.primary-button {
  min-height: 48px;
  padding: 0 22px;
  border-radius: 12px;
  font-size: 15px;
  font-weight: 700;
  cursor: pointer;
}

.secondary-button {
  border: 1px solid #d5dfeb;
  background: #fff;
  color: #40516a;
}

.primary-button {
  border: 0;
  background: #2368c4;
  color: #fff;
}

.scan-card,
.mock-panel {
  width: min(100%, 920px);
  margin: 0 auto;
  border: 1px solid rgba(23, 54, 93, 0.07);
  border-radius: 20px;
  background: #fff;
  box-shadow: 0 12px 34px rgba(23, 54, 93, 0.08);
}

.scan-card {
  padding: 28px;
}

.card-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

h2 {
  color: #24344d;
  font-size: 18px;
}

.bridge-state {
  padding: 5px 10px;
  border-radius: 999px;
  background: #eaf2fc;
  color: #2862a9;
  font-size: 12px;
  font-weight: 700;
}

.scan-value {
  min-height: 112px;
  display: flex;
  align-items: center;
  margin-top: 22px;
  padding: 20px;
  border-radius: 15px;
  background: #f1f6fc;
  color: #17365d;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: clamp(22px, 5vw, 34px);
  font-weight: 750;
  overflow-wrap: anywhere;
}

.scan-value.empty {
  color: #8090a4;
  font-family: inherit;
  font-size: 18px;
  font-weight: 600;
}

time {
  display: block;
  margin-top: 12px;
  color: #7b899a;
  font-size: 13px;
  text-align: right;
}

.mock-panel {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 12px;
  margin-top: 18px;
  padding: 18px;
}

.mock-panel input {
  min-width: 0;
  height: 48px;
  padding: 0 14px;
  border: 2px solid #d9e2ef;
  border-radius: 12px;
  color: #17233d;
  font: 600 16px ui-monospace, SFMono-Regular, Consolas, monospace;
  outline: none;
}

.mock-panel input:focus {
  border-color: #3378cf;
  box-shadow: 0 0 0 4px rgba(51, 120, 207, 0.1);
}

footer {
  margin-top: 24px;
  color: #96a1b0;
  font-size: 12px;
  text-align: center;
}

@media (max-width: 600px) {
  .workspace {
    padding: 22px 16px;
  }

  .topbar {
    align-items: flex-start;
  }

  .scan-card {
    padding: 22px;
  }

  .mock-panel {
    grid-template-columns: 1fr;
  }
}
</style>
