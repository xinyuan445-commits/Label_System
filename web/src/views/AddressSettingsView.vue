<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'

import {
  createDefaultShellConfig,
  loadShellConfig,
  saveShellConfig,
  type ShellConfig,
} from '@/services/shell-config'

const router = useRouter()
const config = ref<ShellConfig>(createDefaultShellConfig())
const loading = ref(true)
const saving = ref(false)
const errorMessage = ref('')

async function saveConfig(): Promise<void> {
  if (saving.value) return
  saving.value = true
  errorMessage.value = ''
  try {
    await saveShellConfig(config.value)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '保存失败'
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    config.value = await loadShellConfig()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '读取失败'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <main class="subpage">
    <header><button type="button" aria-label="返回" @click="router.back()">‹</button><h1>地址设置</h1></header>

    <section v-if="!loading" class="card">
      <label><span><input v-model="config.environment" type="radio" value="development" />开发</span><input v-model="config.developmentUrl" class="url" inputmode="url" /></label>
      <label><span><input v-model="config.environment" type="radio" value="test" />测试</span><input v-model="config.testUrl" class="url" inputmode="url" /></label>
      <label><span><input v-model="config.environment" type="radio" value="production" />生产</span><input v-model="config.productionUrl" class="url" inputmode="url" /></label>
      <button class="primary" type="button" :disabled="saving" @click="saveConfig">{{ saving ? '保存中' : '保存并切换' }}</button>
    </section>
    <div v-else class="loading">加载中</div>
    <p class="error">{{ errorMessage }}</p>
  </main>
</template>

<style scoped>
.subpage{min-height:100dvh;padding:20px 16px 36px;background:#f3f6fa}header,.card,.error{width:min(100%,620px);margin-inline:auto}header{display:flex;align-items:center;gap:12px;margin-bottom:20px}header button{width:44px;height:44px;border:0;border-radius:12px;background:#fff;color:#27496f;font-size:34px;line-height:1}h1{color:#17365d;font-size:28px}.card{display:grid;gap:12px;padding:20px;border:1px solid rgba(23,54,93,.08);border-radius:18px;background:#fff;box-shadow:0 10px 28px rgba(23,54,93,.07)}label{display:grid;gap:8px;padding:12px;border:1px solid #dce5f0;border-radius:13px;background:#f9fbfd}label span{display:flex;align-items:center;gap:8px;color:#33445d;font-weight:700}input[type=radio]{width:18px;height:18px;accent-color:#2368c4}.url{width:100%;height:44px;padding:0 12px;border:1px solid #d3deeb;border-radius:10px;background:#fff;color:#17233d;font:13px ui-monospace,SFMono-Regular,Consolas,monospace;outline:none}.url:focus{border-color:#3378cf;box-shadow:0 0 0 3px rgba(51,120,207,.1)}.primary{height:48px;margin-top:2px;border:0;border-radius:11px;background:#2368c4;color:#fff;font-size:15px;font-weight:700}.loading{min-height:60vh;display:grid;place-items:center;color:#7b899a}.error{min-height:22px;margin-top:12px;color:#bf3434;font-size:14px}button:disabled{opacity:.55}
</style>
