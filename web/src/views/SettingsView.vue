<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'

import { createDefaultShellConfig, loadShellConfig } from '@/services/shell-config'

const router = useRouter()
const h5Version = __APP_VERSION__
const shellConfig = ref(createDefaultShellConfig())

const entries = [
  { name: 'settings-address', label: '地址设置', icon: 'link' },
  { name: 'settings-features', label: '功能开关', icon: 'features' },
  { name: 'settings-data-sync', label: '数据同步', icon: 'sync' },
  { name: 'settings-users', label: '用户管理', icon: 'users' },
  { name: 'settings-scanner', label: '扫码测试', icon: 'scan' },
  { name: 'settings-sound', label: '扫码提示音测试', icon: 'sound' },
] as const

onMounted(async () => {
  try {
    shellConfig.value = await loadShellConfig()
  } catch {
    // 保留构建时兼容版本，设置页仍可正常使用。
  }
})
</script>

<template>
  <main class="settings-page">
    <header class="page-header">
      <button class="back-button" type="button" aria-label="返回" @click="router.back()">
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="m15 18-6-6 6-6" />
        </svg>
      </button>
      <h1>设置</h1>
    </header>

    <nav class="settings-list" aria-label="设置功能">
      <button
        v-for="entry in entries"
        :key="entry.name"
        class="settings-row"
        type="button"
        @click="router.push({ name: entry.name })"
      >
        <span class="entry-icon" aria-hidden="true">
          <svg v-if="entry.icon === 'link'" viewBox="0 0 24 24">
            <path d="M10.5 13.5a4 4 0 0 0 5.7.1l2.4-2.4a4 4 0 0 0-5.7-5.7l-1.4 1.4" />
            <path d="M13.5 10.5a4 4 0 0 0-5.7-.1l-2.4 2.4a4 4 0 0 0 5.7 5.7l1.4-1.4" />
          </svg>
          <svg v-else-if="entry.icon === 'scan'" viewBox="0 0 24 24">
            <path d="M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3M7 12h10" />
          </svg>
          <svg v-else-if="entry.icon === 'features'" viewBox="0 0 24 24">
            <rect x="4" y="4" width="6" height="6" rx="1" />
            <rect x="14" y="4" width="6" height="6" rx="1" />
            <rect x="4" y="14" width="6" height="6" rx="1" />
            <path d="M15 17h4M17 15v4" />
          </svg>
          <svg v-else-if="entry.icon === 'sync'" viewBox="0 0 24 24">
            <path d="M20 7h-5V2" />
            <path d="M19 7a8 8 0 0 0-13.7-1.7L4 7" />
            <path d="M4 17h5v5" />
            <path d="M5 17a8 8 0 0 0 13.7 1.7L20 17" />
          </svg>
          <svg v-else-if="entry.icon === 'users'" viewBox="0 0 24 24">
            <path d="M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z" />
            <path d="M3 21a6 6 0 0 1 12 0" />
            <path d="M17 8a3 3 0 0 1 0 6M16 16a5 5 0 0 1 5 5" />
          </svg>
          <svg v-else viewBox="0 0 24 24">
            <path d="M11 5 7 9H4v6h3l4 4V5Z" />
            <path d="M15 9a4 4 0 0 1 0 6M18 6a8 8 0 0 1 0 12" />
          </svg>
        </span>
        <span class="entry-label">{{ entry.label }}</span>
        <svg class="chevron" viewBox="0 0 24 24" aria-hidden="true">
          <path d="m9 6 6 6-6 6" />
        </svg>
      </button>
    </nav>

    <div class="version-info">
      <span>H5 {{ h5Version }}</span>
      <i>·</i>
      <span>APK {{ shellConfig.apkVersion }}</span>
    </div>
  </main>
</template>

<style scoped>
.settings-page {
  min-height: 100dvh;
  padding: 20px 16px 36px;
  background: #f3f6fa;
}

.page-header,
.settings-list,
.version-info {
  width: min(100%, 560px);
  margin-inline: auto;
}

.version-info {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 7px;
  margin-top: 18px;
  color: #94a0af;
  font-size: 12px;
}

.version-info i {
  font-style: normal;
}

.page-header {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 52px;
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
  font-size: 28px;
}

.settings-list {
  overflow: hidden;
  border: 1px solid rgba(23, 54, 93, 0.08);
  border-radius: 18px;
  background: #fff;
  box-shadow: 0 12px 32px rgba(23, 54, 93, 0.08);
}

.settings-row {
  position: relative;
  width: 100%;
  min-height: 72px;
  display: grid;
  grid-template-columns: auto 1fr auto;
  align-items: center;
  gap: 15px;
  padding: 12px 18px;
  border: 0;
  background: #fff;
  color: #24344d;
  text-align: left;
}

.settings-row + .settings-row::before {
  position: absolute;
  top: 0;
  right: 18px;
  left: 68px;
  height: 1px;
  background: #e7edf4;
  content: '';
}

.settings-row:active {
  background: #f6f9fc;
}

.entry-icon {
  width: 38px;
  height: 38px;
  display: grid;
  place-items: center;
  border-radius: 11px;
  background: #e8f1fc;
  color: #2368c4;
}

.entry-icon svg {
  width: 21px;
  height: 21px;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.8;
}

.entry-label {
  font-size: 16px;
  font-weight: 680;
}

.chevron {
  width: 20px;
  height: 20px;
  fill: none;
  stroke: #9aa8b9;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 2;
}
</style>
