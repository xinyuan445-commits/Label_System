<script setup lang="ts">
import { useRouter } from 'vue-router'

import WorkspaceIcon from '@/WorkspaceIcon.vue'
import { workspaceGroups } from '@/workspace'

const router = useRouter()
</script>

<template>
  <main class="functions-page">
    <header class="page-header-shell">
      <div class="page-header">
        <button class="back-button" type="button" aria-label="返回" @click="router.back()">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="m15 18-6-6 6-6" />
          </svg>
        </button>
        <h1>全部功能</h1>
      </div>
    </header>

    <div class="function-list">
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
            @click="router.push({ name: 'work-function', params: { code: entry.code } })"
          >
            <span class="entry-icon" aria-hidden="true">
              <WorkspaceIcon :icon="entry.icon" />
            </span>
            <span>{{ entry.label }}</span>
          </button>
        </div>
      </section>
    </div>
  </main>
</template>

<style scoped>
.functions-page {
  height: 100dvh;
  overflow: hidden;
  background: #f3f6fa;
}

.page-header-shell {
  height: 72px;
  border-bottom: 1px solid #e7edf4;
  background: #fff;
  box-shadow: 0 3px 12px rgba(23, 54, 93, 0.05);
}

.page-header,
.function-list {
  width: min(100%, 920px);
  margin-inline: auto;
}

.page-header {
  height: 100%;
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 0 20px;
}

.back-button {
  width: 42px;
  height: 42px;
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

.function-list {
  height: calc(100dvh - 72px);
  display: grid;
  align-content: start;
  gap: 16px;
  overflow-y: auto;
  padding: 18px 20px 32px;
  overscroll-behavior: contain;
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

.tone-cyan {
  --group-color: #147a8c;
  --group-soft: #e5f5f7;
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
  padding: 8px;
  border-radius: 11px;
  background: var(--group-soft);
  color: var(--group-color);
}

@media (max-width: 480px) {
  .page-header {
    padding: 0 14px;
  }

  .function-list {
    padding: 16px 14px 28px;
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
