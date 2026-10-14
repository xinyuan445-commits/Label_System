<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'

import {
  createDefaultShellConfig,
  loadShellConfig,
  saveShellConfig,
  testScanFeedback,
  type ScanFeedbackMode,
  type ScanFeedbackResult,
  type ShellConfig,
} from '@/services/shell-config'

const router = useRouter()
const config = ref<ShellConfig>(createDefaultShellConfig())
const loading = ref(true)
const busy = ref(false)
const errorMessage = ref('')
const options: Array<{ value: ScanFeedbackMode; label: string }> = [
  { value: 'tone', label: '提示音' },
  { value: 'voice', label: '语音播报' },
  { value: 'off', label: '关闭' },
]

async function selectFeedback(mode: ScanFeedbackMode): Promise<void> {
  if (busy.value) return
  config.value.scanFeedbackMode = mode
  if (mode === 'tone' && config.value.scanSound === 'off') config.value.scanSound = 'short'
  busy.value = true
  errorMessage.value = ''
  try {
    await saveShellConfig(config.value, { applyAddress: false })
    if (mode !== 'off') await testScanFeedback(mode, 'success')
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '设置失败'
  } finally {
    busy.value = false
  }
}

async function preview(result: ScanFeedbackResult): Promise<void> {
  if (busy.value || config.value.scanFeedbackMode === 'off') return
  busy.value = true
  errorMessage.value = ''
  try {
    await testScanFeedback(config.value.scanFeedbackMode, result)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '播放失败'
  } finally {
    busy.value = false
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
    <header><button type="button" aria-label="返回" @click="router.back()">‹</button><h1>扫码反馈</h1></header>
    <section v-if="!loading" class="card">
      <div class="options">
        <button v-for="item in options" :key="item.value" type="button" :class="{ selected: config.scanFeedbackMode === item.value }" :disabled="busy" @click="selectFeedback(item.value)">{{ item.label }}</button>
      </div>
      <div class="previews">
        <button type="button" :disabled="busy || config.scanFeedbackMode === 'off'" @click="preview('success')">试听成功</button>
        <button type="button" :disabled="busy || config.scanFeedbackMode === 'off'" @click="preview('error')">试听错误</button>
      </div>
    </section>
    <div v-else class="loading">加载中</div>
    <p class="error">{{ errorMessage }}</p>
  </main>
</template>

<style scoped>
.subpage{min-height:100dvh;padding:20px 16px 36px;background:#f3f6fa}header,.card,.error{width:min(100%,620px);margin-inline:auto}header{display:flex;align-items:center;gap:12px;margin-bottom:20px}header button{width:44px;height:44px;border:0;border-radius:12px;background:#fff;color:#27496f;font-size:34px;line-height:1}h1{color:#17365d;font-size:26px}.card{padding:20px;border:1px solid rgba(23,54,93,.08);border-radius:18px;background:#fff;box-shadow:0 10px 28px rgba(23,54,93,.07)}.options{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}.options button,.previews button{min-height:52px;border:1px solid #d3deeb;border-radius:12px;background:#f8fafc;color:#42526a;font-size:15px;font-weight:700}.options button.selected{border-color:#2368c4;background:#e8f1fc;color:#1f5fae}.previews{display:grid;grid-template-columns:repeat(2,1fr);gap:10px;margin-top:14px}.previews button{min-height:46px}.loading{min-height:60vh;display:grid;place-items:center;color:#7b899a}.error{min-height:22px;margin-top:12px;color:#bf3434;font-size:14px}button:disabled{opacity:.55}
.subpage{padding-top:calc(20px + var(--app-safe-top))}
</style>
