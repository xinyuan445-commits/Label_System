<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import { useRouter } from 'vue-router'

import WorkspaceIcon from '@/WorkspaceIcon.vue'
import {
  MAX_WORKSPACE_FAVORITES,
  readWorkspaceFavorites,
  saveWorkspaceFavorites,
} from '@/services/workspace-favorites'
import { useAuthStore } from '@/stores/auth'
import { workspaceGroups } from '@/workspace'

const router = useRouter()
const authStore = useAuthStore()
const username = authStore.session?.username || '用户'
const selected = ref(readWorkspaceFavorites(username))
const limitMessage = ref('')
let messageTimer: ReturnType<typeof setTimeout> | undefined

function toggle(code: string): void {
  const index = selected.value.indexOf(code)

  if (index >= 0) {
    selected.value.splice(index, 1)
    return
  }

  if (selected.value.length >= MAX_WORKSPACE_FAVORITES) {
    limitMessage.value = `最多选择 ${MAX_WORKSPACE_FAVORITES} 个`
    clearTimeout(messageTimer)
    messageTimer = setTimeout(() => {
      limitMessage.value = ''
    }, 1600)
    return
  }

  selected.value.push(code)
}

async function save(): Promise<void> {
  saveWorkspaceFavorites(username, selected.value)
  await router.replace({ name: 'home' })
}

onBeforeUnmount(() => clearTimeout(messageTimer))
</script>

<template>
  <main class="favorites-page">
    <header class="page-header-shell">
      <div class="page-header">
        <button class="back-button" type="button" aria-label="返回" @click="router.back()">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="m15 18-6-6 6-6" />
          </svg>
        </button>
        <h1>常用功能</h1>
        <span class="selected-count">{{ selected.length }}/{{ MAX_WORKSPACE_FAVORITES }}</span>
        <button class="save-button" type="button" @click="save">保存</button>
      </div>
    </header>

    <div class="selection-list">
      <section v-for="group in workspaceGroups" :key="group.key" class="selection-group">
        <h2>{{ group.title }}</h2>

        <div class="selection-grid">
          <button
            v-for="entry in group.entries"
            :key="entry.code"
            class="selection-item"
            :class="{ selected: selected.includes(entry.code) }"
            type="button"
            @click="toggle(entry.code)"
          >
            <span class="item-icon" aria-hidden="true">
              <WorkspaceIcon :icon="entry.icon" />
            </span>
            <span>{{ entry.label }}</span>
            <span class="check" aria-hidden="true">
              <svg viewBox="0 0 24 24">
                <path d="m7 12 3 3 7-7" />
              </svg>
            </span>
          </button>
        </div>
      </section>
    </div>

    <div v-if="limitMessage" class="limit-message">{{ limitMessage }}</div>
  </main>
</template>

<style scoped>
.favorites-page {
  height: 100dvh;
  overflow: hidden;
  background: #f3f6fa;
}

.page-header-shell {
  position: relative;
  z-index: 10;
  height: 72px;
  border-bottom: 1px solid #e7edf4;
  background: #fff;
  box-shadow: 0 3px 12px rgba(23, 54, 93, 0.05);
}

.page-header,
.selection-list {
  width: min(100%, 920px);
  margin-inline: auto;
}

.page-header {
  height: 100%;
  display: flex;
  align-items: center;
  gap: 11px;
  padding: 0 20px;
}

.back-button {
  width: 42px;
  height: 42px;
  flex: 0 0 auto;
  display: grid;
  place-items: center;
  border: 0;
  border-radius: 12px;
  background: #f3f6fa;
  color: #365678;
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
  font-size: 24px;
}

.selected-count {
  color: #7c8999;
  font-size: 13px;
}

.save-button {
  min-width: 62px;
  min-height: 38px;
  margin-left: auto;
  border: 0;
  border-radius: 10px;
  background: #2368c4;
  color: #fff;
  font-size: 14px;
  font-weight: 700;
}

.selection-list {
  height: calc(100dvh - 72px);
  display: grid;
  align-content: start;
  gap: 14px;
  overflow-y: auto;
  padding: 18px 20px 32px;
  overscroll-behavior: contain;
}

.selection-group {
  padding: 17px;
  border: 1px solid rgba(23, 54, 93, 0.07);
  border-radius: 17px;
  background: #fff;
}

h2 {
  margin-bottom: 13px;
  color: #34465e;
  font-size: 16px;
}

.selection-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 9px;
}

.selection-item {
  position: relative;
  min-width: 0;
  min-height: 60px;
  display: grid;
  grid-template-columns: 30px minmax(0, 1fr) 19px;
  align-items: center;
  gap: 9px;
  padding: 9px 10px;
  border: 1px solid #e3e9f0;
  border-radius: 12px;
  background: #f9fafc;
  color: #40516a;
  font-size: 13px;
  font-weight: 650;
  line-height: 1.25;
  text-align: left;
}

.selection-item.selected {
  border-color: #8ab6ec;
  background: #edf5ff;
  color: #1f5da9;
}

.item-icon {
  width: 28px;
  height: 28px;
  color: #63768d;
}

.selected .item-icon {
  color: #2368c4;
}

.check {
  width: 19px;
  height: 19px;
  display: grid;
  place-items: center;
  border: 1px solid #bdc9d7;
  border-radius: 50%;
  color: transparent;
}

.selected .check {
  border-color: #2368c4;
  background: #2368c4;
  color: #fff;
}

.check svg {
  width: 14px;
  height: 14px;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 2.4;
}

.limit-message {
  position: fixed;
  bottom: 28px;
  left: 50%;
  z-index: 20;
  padding: 10px 16px;
  border-radius: 10px;
  background: rgba(33, 45, 61, 0.92);
  color: #fff;
  font-size: 13px;
  transform: translateX(-50%);
}

@media (max-width: 480px) {
  .page-header {
    padding: 0 14px;
  }

  h1 {
    font-size: 22px;
  }

  .selection-list {
    padding: 16px 14px 28px;
  }

  .selection-group {
    padding: 15px 12px;
  }

  .selection-grid {
    grid-template-columns: 1fr;
  }
}
</style>
