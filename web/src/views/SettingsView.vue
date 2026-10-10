<script setup lang="ts">
import { useRouter } from 'vue-router'

const router = useRouter()

const entries = [
  { name: 'settings-address', label: '地址设置', mark: '址' },
  { name: 'settings-scanner', label: '扫码测试', mark: '扫' },
  { name: 'settings-sound', label: '扫码提示音测试', mark: '音' },
]
</script>

<template>
  <main class="settings-page">
    <header class="page-header">
      <button type="button" aria-label="返回" @click="router.back()">‹</button>
      <h1>设置</h1>
    </header>

    <nav class="entry-list" aria-label="设置功能">
      <button
        v-for="entry in entries"
        :key="entry.name"
        type="button"
        @click="router.push({ name: entry.name })"
      >
        <span class="entry-mark" aria-hidden="true">{{ entry.mark }}</span>
        <span>{{ entry.label }}</span>
        <span class="arrow" aria-hidden="true">›</span>
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
.entry-list {
  width: min(100%, 620px);
  margin-inline: auto;
}

.page-header {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 20px;
}

.page-header button {
  width: 44px;
  height: 44px;
  border: 0;
  border-radius: 12px;
  background: #fff;
  color: #27496f;
  font-size: 34px;
  line-height: 1;
}

h1 {
  color: #17365d;
  font-size: 28px;
}

.entry-list {
  display: grid;
  gap: 14px;
}

.entry-list button {
  min-height: 78px;
  display: grid;
  grid-template-columns: auto 1fr auto;
  align-items: center;
  gap: 14px;
  padding: 14px 18px;
  border: 1px solid rgba(23, 54, 93, 0.08);
  border-radius: 17px;
  background: #fff;
  color: #24344d;
  font-size: 17px;
  font-weight: 700;
  text-align: left;
  box-shadow: 0 10px 28px rgba(23, 54, 93, 0.07);
}

.entry-mark {
  width: 44px;
  height: 44px;
  display: grid;
  place-items: center;
  border-radius: 12px;
  background: #e8f1fc;
  color: #2368c4;
}

.arrow {
  color: #8a9aaf;
  font-size: 28px;
  font-weight: 400;
}
</style>
