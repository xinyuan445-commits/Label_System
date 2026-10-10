<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'

import { getPdaBridge, isMockPdaBridge } from '@/bridge/pda-bridge'
import type { PdaScanEvent } from '@/bridge/types'

const router = useRouter()
const bridge = getPdaBridge()
const lastScan = ref<PdaScanEvent | null>(null)
const mockBarcode = ref('TEST-CT58C-001')
let stopScanListener: (() => void) | null = null

function simulateScan(): void {
  if (isMockPdaBridge(bridge)) bridge.simulateScan(mockBarcode.value)
}

onMounted(() => {
  stopScanListener = bridge.subscribeScan((event) => {
    lastScan.value = event
  })
})
onBeforeUnmount(() => stopScanListener?.())
</script>

<template>
  <main class="subpage">
    <header><button type="button" aria-label="返回" @click="router.back()">‹</button><h1>扫码测试</h1></header>
    <section class="card">
      <div class="result" :class="{ empty: !lastScan }">{{ lastScan?.barcode || '等待扫码' }}</div>
      <time v-if="lastScan">{{ new Date(lastScan.receivedAt).toLocaleTimeString('zh-CN', { hour12: false }) }}</time>
      <div v-if="isMockPdaBridge(bridge)" class="mock"><input v-model="mockBarcode" @keyup.enter="simulateScan" /><button type="button" @click="simulateScan">测试</button></div>
    </section>
  </main>
</template>

<style scoped>
.subpage{min-height:100dvh;padding:20px 16px 36px;background:#f3f6fa}header,.card{width:min(100%,620px);margin-inline:auto}header{display:flex;align-items:center;gap:12px;margin-bottom:20px}header button{width:44px;height:44px;border:0;border-radius:12px;background:#fff;color:#27496f;font-size:34px;line-height:1}h1{color:#17365d;font-size:28px}.card{padding:20px;border:1px solid rgba(23,54,93,.08);border-radius:18px;background:#fff;box-shadow:0 10px 28px rgba(23,54,93,.07)}.result{min-height:130px;display:flex;align-items:center;padding:18px;border-radius:14px;background:#f1f6fc;color:#17365d;font:700 24px ui-monospace,SFMono-Regular,Consolas,monospace;overflow-wrap:anywhere}.result.empty{color:#8290a2;font-family:inherit;font-size:17px}time{display:block;margin-top:12px;color:#7b899a;font-size:13px;text-align:right}.mock{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:10px;margin-top:14px}.mock input{min-width:0;height:44px;padding:0 12px;border:1px solid #d3deeb;border-radius:10px}.mock button{padding:0 18px;border:0;border-radius:10px;background:#e7f0fb;color:#285d9d;font-weight:700}
</style>
