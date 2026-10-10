<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'

interface SyncOption {
  key: 'users' | 'categories' | 'locations'
  label: string
}

const router = useRouter()
const options: SyncOption[] = [
  { key: 'users', label: '用户信息' },
  { key: 'categories', label: '出入类别' },
  { key: 'locations', label: '仓位信息' },
]
const selected = ref<Record<SyncOption['key'], boolean>>({
  users: true,
  categories: true,
  locations: true,
})
const message = ref('')
const messageTone = ref<'info' | 'error'>('info')
let messageTimer: ReturnType<typeof setTimeout> | null = null

const hasSelection = computed(() => Object.values(selected.value).some(Boolean))

function notify(text: string, tone: 'info' | 'error' = 'info'): void {
  message.value = text
  messageTone.value = tone
  if (messageTimer) clearTimeout(messageTimer)
  messageTimer = setTimeout(() => {
    message.value = ''
  }, 1800)
}

function syncNow(): void {
  if (!hasSelection.value) {
    notify('请选择同步内容', 'error')
    return
  }
  notify('接口待接入')
}
</script>

<template>
  <main class="sync-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button class="back-button" type="button" aria-label="返回" @click="router.back()">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>数据同步</h1>
      </div>
    </header>

    <div class="page-content">
      <section class="sync-card">
        <label v-for="option in options" :key="option.key" class="sync-row">
          <span>{{ option.label }}</span>
          <span class="checkbox">
            <input v-model="selected[option.key]" type="checkbox" />
            <i aria-hidden="true">
              <svg viewBox="0 0 24 24"><path d="m6 12 4 4 8-9" /></svg>
            </i>
          </span>
        </label>
      </section>
    </div>

    <footer class="action-shell">
      <button type="button" @click="syncNow">立即同步</button>
    </footer>

    <Transition name="toast">
      <div v-if="message" class="toast-message" :class="messageTone" role="status">{{ message }}</div>
    </Transition>
  </main>
</template>

<style scoped>
.sync-page{min-height:100dvh;padding:72px 0 86px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content,.action-shell{width:min(100%,620px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:0 18px}.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg,.checkbox svg{fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2.3}.back-button svg{width:23px;height:23px}.topbar h1{color:#17365d;font-size:22px}.page-content{padding:16px 14px}.sync-card{overflow:hidden;display:grid;grid-template-columns:repeat(2,minmax(0,1fr));border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.055)}.sync-row{min-width:0;min-height:64px;display:flex;align-items:center;justify-content:space-between;gap:10px;padding:11px 14px;border-right:1px solid #edf1f5;border-bottom:1px solid #edf1f5}.sync-row:nth-child(2n){border-right:0}.sync-row:last-child{border-bottom:0}.sync-row>span:first-child{color:#2b405c;font-size:14px;font-weight:650}.checkbox{position:relative;width:28px;height:28px;display:block;flex:0 0 auto}.checkbox input{position:absolute;opacity:0;pointer-events:none}.checkbox i{position:absolute;inset:0;display:grid;place-items:center;border:1.5px solid #bdc9d7;border-radius:8px;background:#fff;color:transparent;transition:.16s}.checkbox svg{width:18px;height:18px}.checkbox input:checked+i{border-color:#2368c4;background:#2368c4;color:#fff}.action-shell{position:fixed;z-index:18;right:0;bottom:0;left:0;padding:10px 14px max(10px,env(safe-area-inset-bottom));border-top:1px solid #e1e8f0;background:rgba(255,255,255,.97);box-shadow:0 -6px 20px rgba(23,54,93,.07)}.action-shell button{width:100%;height:48px;border:0;border-radius:11px;background:#2368c4;color:#fff;font-size:15px;font-weight:750;box-shadow:0 7px 16px rgba(35,104,196,.2)}.toast-message{position:fixed;z-index:50;top:82px;left:50%;width:max-content;max-width:calc(100% - 36px);padding:10px 16px;border-radius:10px;background:#31445e;color:#fff;font-size:13px;font-weight:700;box-shadow:0 8px 22px rgba(23,54,93,.2);transform:translateX(-50%)}.toast-message.error{background:#b64a4a}.toast-enter-active,.toast-leave-active{transition:opacity .16s ease,transform .16s ease}.toast-enter-from,.toast-leave-to{opacity:0;transform:translate(-50%,-14px)}
</style>
