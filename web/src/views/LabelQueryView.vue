<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'

import { getPdaBridge } from '@/bridge/pda-bridge'
import type { PdaScanEvent } from '@/bridge/types'
import {
  lookupInventoryObject,
  type InventoryLookupResult,
} from '@/services/inventory-object-lookup'

const route = useRoute()
const router = useRouter()
const code = ref('')
const result = ref<InventoryLookupResult | null>(null)
const loading = ref(false)
const error = ref('')
const returnRouteName = computed(() => route.query.origin === 'all-functions' ? 'all-functions' : 'home')
const totalQuantity = computed(() => result.value?.lines.reduce((sum, item) => sum + item.availableQty, 0) ?? 0)

async function search(): Promise<void> {
  const value = code.value.trim()
  error.value = ''
  result.value = null
  if (!value) {
    error.value = '请输入条码'
    return
  }
  loading.value = true
  try {
    result.value = await lookupInventoryObject(value, 'label-query')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : '查询失败'
  } finally {
    loading.value = false
  }
}

function handleScan(event: PdaScanEvent): void {
  code.value = event.barcode.trim()
  void search()
}

const stopScan = getPdaBridge().subscribeScan(handleScan)
onBeforeUnmount(stopScan)

onBeforeRouteLeave((to) => {
  if (to.name !== returnRouteName.value) return { name: returnRouteName.value, replace: true }
  return true
})
</script>

<template>
  <main class="query-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button class="back-button" type="button" aria-label="返回" @click="router.replace({ name: returnRouteName })">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>标签查询</h1>
      </div>
    </header>

    <div class="page-content">
      <section class="search-card">
        <input v-model="code" autocomplete="off" enterkeyhint="search" placeholder="扫描或输入标签/托盘码" @keyup.enter="search" />
        <button type="button" :disabled="loading" @click="search">{{ loading ? '查询中' : '查询' }}</button>
      </section>

      <p v-if="error" class="error-message">{{ error }}</p>

      <template v-if="result">
        <section v-if="result.pallet" class="summary-card">
          <div><span>托盘</span><strong>{{ result.pallet.labelCode }}</strong></div>
          <dl>
            <div><dt>仓库</dt><dd>{{ result.pallet.warehouseCode || '-' }}</dd></div>
            <div><dt>货位</dt><dd>{{ result.pallet.locationCode || '-' }}</dd></div>
            <div><dt>标签</dt><dd>{{ result.lines.length }}</dd></div>
            <div><dt>数量</dt><dd>{{ totalQuantity }}</dd></div>
          </dl>
        </section>

        <section v-if="result.lines.length" class="result-list">
          <article v-for="item in result.lines" :key="item.labelCode" class="result-card">
            <div class="result-title"><strong>{{ item.labelCode }}</strong><b>{{ item.availableQty }} {{ item.unitCode }}</b></div>
            <span>{{ item.materialCode }}<template v-if="item.materialName && item.materialName !== item.materialCode"> · {{ item.materialName }}</template></span>
            <dl>
              <div><dt>批次</dt><dd>{{ item.batchNo || '-' }}</dd></div>
              <div><dt>仓库/货位</dt><dd>{{ item.warehouseCode || '-' }} / {{ item.locationCode || '-' }}</dd></div>
              <div><dt>状态</dt><dd>{{ item.status }}</dd></div>
              <div v-if="item.palletCode"><dt>托盘</dt><dd>{{ item.palletCode }}</dd></div>
            </dl>
          </article>
        </section>
        <section v-else class="empty-state">托盘内暂无标签</section>
      </template>
    </div>
  </main>
</template>

<style scoped>
.query-page{min-height:100dvh;padding-top:var(--app-header-height);background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:var(--app-header-height);border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content{width:min(100%,680px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:var(--app-safe-top) 18px 0}.topbar h1{color:#17365d;font-size:21px}.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg{width:23px;height:23px;fill:none;stroke:currentColor;stroke-width:2}.page-content{display:grid;gap:13px;padding:15px 14px}.search-card{display:grid;grid-template-columns:minmax(0,1fr) 74px;gap:9px;padding:14px;border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff}.search-card input{min-width:0;height:45px;padding:0 12px;border:1px solid #d4deea;border-radius:10px;font:inherit;outline:none}.search-card input:focus{border-color:#3378cf;box-shadow:0 0 0 3px rgba(51,120,207,.1)}.search-card button{border:0;border-radius:10px;background:#2368c4;color:#fff;font-weight:750}.search-card button:disabled{opacity:.6}.error-message{margin:0;padding:0 4px;color:#b53d3d;font-size:13px}.summary-card,.result-card,.empty-state{border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.05)}.summary-card{padding:15px}.summary-card>div,.result-title{display:flex;align-items:center;justify-content:space-between;gap:10px}.summary-card span{color:#7b8999;font-size:12px}.summary-card strong,.result-title strong{overflow-wrap:anywhere;color:#24456b;font:750 14px ui-monospace,SFMono-Regular,Consolas,monospace}.summary-card dl,.result-card dl{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin:13px 0 0;padding-top:12px;border-top:1px solid #edf1f5}.summary-card dl div,.result-card dl div{display:grid;gap:3px}.summary-card dt,.summary-card dd,.result-card dt,.result-card dd{margin:0;font-size:12px}.summary-card dt,.result-card dt{color:#8592a2}.summary-card dd,.result-card dd{color:#344960;font-weight:700}.result-list{display:grid;gap:10px}.result-card{display:grid;gap:7px;padding:15px}.result-title b{color:#2368c4;font-size:15px}.result-card>span{color:#435970;font-size:13px}.empty-state{min-height:130px;display:grid;place-items:center;color:#8794a4;font-size:14px}@media(max-width:420px){.topbar{padding-inline:14px}.page-content{padding-inline:12px}}
</style>
