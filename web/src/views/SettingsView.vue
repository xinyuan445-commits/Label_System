<script setup lang="ts">
import { useRouter } from 'vue-router'

const router = useRouter()

const entries = [
  { name: 'settings-address', label: '地址设置', icon: 'link' },
  { name: 'settings-scanner', label: '扫码测试', icon: 'scan' },
  { name: 'settings-sound', label: '扫码提示音测试', icon: 'sound' },
] as const
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
  </main>
</template>

<style scoped>
.settings-page {
  min-height: 100dvh;
  padding: 20px 16px 36px;
  background: #f3f6fa;
}

.page-header,
.settings-list {
  width: min(100%, 560px);
  margin-inline: auto;
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
