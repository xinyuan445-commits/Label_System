<script setup lang="ts">
import { useRouter } from 'vue-router'

import { useAuthStore } from '@/stores/auth'
import { workspaceGroups, type WorkspaceIcon } from '@/workspace'

const router = useRouter()
const authStore = useAuthStore()

const iconPaths: Record<WorkspaceIcon, string[]> = {
  receive: [
    'M5 8.5 12 5l7 3.5v7L12 19l-7-3.5v-7Z',
    'M12 12v7M5.5 8.7l6.5 3.3 6.5-3.3M12 3v5M9.5 5.5 12 8l2.5-2.5',
  ],
  truck: [
    'M3 6h11v10H3V6ZM14 10h3l3 3v3h-6v-6Z',
    'M7 19a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM17 19a2 2 0 1 0 0-4 2 2 0 0 0 0 4Z',
  ],
  warehouse: [
    'm3 9 9-5 9 5v11H3V9ZM7 12h10v8H7v-8Z',
    'M7 15h10M10 12v8M14 12v8',
  ],
  document: [
    'M6 3h8l4 4v14H6V3ZM14 3v5h4',
    'M9 12h6M9 16h4',
  ],
  dispatch: [
    'M4 8.5 11 5l7 3.5v7L11 19l-7-3.5v-7ZM11 12v7M4.5 8.7 11 12l6.5-3.3',
    'M16 4h5v5M21 4l-6 6',
  ],
  material: [
    'M4 7h16v12H4V7ZM4 11h16M9 11v8',
    'M14 4h6v6M20 4l-7 7',
  ],
  transfer: ['M7 7h12M16 4l3 3-3 3M17 17H5M8 14l-3 3 3 3'],
  location: [
    'M12 21s6-5.1 6-11a6 6 0 1 0-12 0c0 5.9 6 11 6 11Z',
    'M12 13a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z',
  ],
  inventory: [
    'M8 4h8M9 3v3h6V3M6 5h12v16H6V5Z',
    'm9 14 2 2 4-5',
  ],
  approve: [
    'M12 3 20 6v6c0 5-3.4 8-8 9-4.6-1-8-4-8-9V6l8-3Z',
    'm8.5 12 2.2 2.2 4.8-5',
  ],
}

async function openEntry(code: string): Promise<void> {
  await router.push({ name: 'work-function', params: { code } })
}

async function logout(): Promise<void> {
  authStore.logout()
  await router.replace({ name: 'login' })
}
</script>

<template>
  <main class="workspace">
    <header class="topbar">
      <div>
        <h1>工作台</h1>
        <span class="username">{{ authStore.session?.username }}</span>
      </div>

      <button class="logout-button" type="button" @click="logout">退出</button>
    </header>

    <div class="group-list">
      <section
        v-for="group in workspaceGroups"
        :key="group.key"
        class="business-group"
        :class="`tone-${group.tone}`"
      >
        <h2>{{ group.title }}</h2>

        <div class="entry-grid">
          <button
            v-for="entry in group.entries"
            :key="entry.code"
            class="entry-button"
            type="button"
            @click="openEntry(entry.code)"
          >
            <span class="entry-icon" aria-hidden="true">
              <svg viewBox="0 0 24 24">
                <path v-for="path in iconPaths[entry.icon]" :key="path" :d="path" />
              </svg>
            </span>
            <span>{{ entry.label }}</span>
          </button>
        </div>
      </section>
    </div>
  </main>
</template>

<style scoped>
.workspace {
  min-height: 100dvh;
  padding: 24px 20px 34px;
  background: #f3f6fa;
}

.topbar,
.group-list {
  width: min(100%, 920px);
  margin-inline: auto;
}

.topbar {
  min-height: 54px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 18px;
  margin-bottom: 18px;
}

h1 {
  color: #17365d;
  font-size: 28px;
}

.username {
  display: block;
  margin-top: 3px;
  color: #7b899a;
  font-size: 13px;
}

.logout-button {
  min-width: 68px;
  min-height: 42px;
  padding: 0 16px;
  border: 1px solid #d9e2ec;
  border-radius: 11px;
  background: #fff;
  color: #40516a;
  font-size: 14px;
  font-weight: 650;
}

.group-list {
  display: grid;
  gap: 16px;
}

.business-group {
  --group-color: #2368c4;
  --group-soft: #e9f2fd;
  padding: 18px;
  border: 1px solid rgba(23, 54, 93, 0.07);
  border-radius: 18px;
  background: #fff;
  box-shadow: 0 10px 28px rgba(23, 54, 93, 0.06);
}

.tone-green {
  --group-color: #16805c;
  --group-soft: #e6f5ef;
}

.tone-orange {
  --group-color: #bb651c;
  --group-soft: #fff0e3;
}

.tone-purple {
  --group-color: #7651b5;
  --group-soft: #f1ebfb;
}

h2 {
  margin-bottom: 15px;
  color: #24344d;
  font-size: 17px;
}

.entry-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px;
}

.entry-button {
  min-width: 0;
  min-height: 94px;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 9px;
  padding: 11px 6px;
  border: 0;
  border-radius: 14px;
  background: #f7f9fc;
  color: #2b3d55;
  font-size: 14px;
  font-weight: 650;
  line-height: 1.3;
  text-align: center;
}

.entry-button:active {
  background: var(--group-soft);
}

.entry-icon {
  width: 38px;
  height: 38px;
  display: grid;
  place-items: center;
  border-radius: 11px;
  background: var(--group-soft);
  color: var(--group-color);
}

.entry-icon svg {
  width: 22px;
  height: 22px;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.7;
}

@media (max-width: 480px) {
  .workspace {
    padding: 20px 14px 28px;
  }

  .business-group {
    padding: 16px 12px;
  }

  .entry-grid {
    gap: 8px;
  }

  .entry-button {
    min-height: 90px;
    font-size: 13px;
  }
}
</style>
