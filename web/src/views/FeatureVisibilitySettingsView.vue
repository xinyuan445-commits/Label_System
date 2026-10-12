<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'

import {
  loadFeatureVisibility,
  saveFeatureVisibility,
  type FeatureVisibility,
} from '@/services/feature-visibility'
import { workspaceGroups } from '@/workspace'

const router = useRouter()
const visibility = ref<FeatureVisibility>(loadFeatureVisibility())
const expandedGroups = ref<Record<string, boolean>>(
  Object.fromEntries(workspaceGroups.map((group, index) => [group.key, index === 0])),
)

function toggleGroup(groupKey: string): void {
  expandedGroups.value[groupKey] = !expandedGroups.value[groupKey]
}

function enabledCount(groupKey: string): number {
  const group = workspaceGroups.find((item) => item.key === groupKey)
  return group?.entries.filter((entry) => visibility.value[entry.code] !== false).length ?? 0
}

function setGroup(groupKey: string, enabled: boolean): void {
  const group = workspaceGroups.find((item) => item.key === groupKey)
  for (const entry of group?.entries ?? []) visibility.value[entry.code] = enabled
  persist()
}

function persist(): void {
  saveFeatureVisibility(visibility.value)
}
</script>

<template>
  <main class="feature-settings-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button class="back-button" type="button" aria-label="返回" @click="router.back()">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>功能开关</h1>
      </div>
    </header>

    <div class="page-content">
      <section v-for="group in workspaceGroups" :key="group.key" class="group-card">
        <button class="group-heading" type="button" @click="toggleGroup(group.key)">
          <span><strong>{{ group.title }}</strong><small>{{ enabledCount(group.key) }}/{{ group.entries.length }}</small></span>
          <svg :class="{ expanded: expandedGroups[group.key] }" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 6 6 6-6 6" /></svg>
        </button>

        <div v-if="expandedGroups[group.key]" class="group-content">
          <div class="group-actions">
            <button type="button" @click="setGroup(group.key, true)">全部开启</button>
            <button type="button" @click="setGroup(group.key, false)">全部关闭</button>
          </div>
          <label v-for="entry in group.entries" :key="entry.code" class="setting-row">
            <span>{{ entry.label }}</span>
            <span class="toggle">
              <input v-model="visibility[entry.code]" type="checkbox" @change="persist" />
              <i aria-hidden="true"></i>
            </span>
          </label>
        </div>
      </section>
    </div>

  </main>
</template>

<style scoped>
.feature-settings-page{min-height:100dvh;padding:72px 0 20px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content{width:min(100%,620px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:0 18px}.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg,.group-heading svg{fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}.back-button svg{width:23px;height:23px}.topbar h1{color:#17365d;font-size:22px}.page-content{display:grid;gap:12px;padding:16px 14px 28px}.group-card{overflow:hidden;border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.055)}.group-heading{width:100%;height:60px;display:flex;align-items:center;justify-content:space-between;padding:0 16px;border:0;background:#fff;color:#2b405c}.group-heading>span{display:flex;align-items:baseline;gap:9px}.group-heading strong{font-size:15px}.group-heading small{color:#8794a5;font-size:12px;font-weight:650}.group-heading svg{width:20px;height:20px;color:#8290a2;transition:transform .18s}.group-heading svg.expanded{transform:rotate(90deg)}.group-content{border-top:1px solid #edf1f5;background:#fbfcfe}.group-actions{display:grid;grid-template-columns:1fr 1fr;gap:8px;padding:10px 15px;border-bottom:1px solid #edf1f5}.group-actions button{height:34px;border:1px solid #cddbea;border-radius:9px;background:#fff;color:#38628f;font-size:12px;font-weight:700}.setting-row{min-height:58px;display:flex;align-items:center;justify-content:space-between;gap:14px;padding:10px 16px;border-bottom:1px solid #edf1f5}.setting-row:last-child{border-bottom:0}.setting-row>span:first-child{color:#2b405c;font-size:14px;font-weight:650}.toggle{position:relative;width:50px;height:28px;display:block;flex:0 0 auto}.toggle input{position:absolute;opacity:0;pointer-events:none}.toggle i{position:absolute;inset:0;border-radius:999px;background:#c8d1dc;transition:.18s}.toggle i::after{position:absolute;top:3px;left:3px;width:22px;height:22px;border-radius:50%;background:#fff;box-shadow:0 2px 6px rgba(21,39,63,.22);content:"";transition:.18s}.toggle input:checked+i{background:#2368c4}.toggle input:checked+i::after{transform:translateX(22px)}
.feature-settings-page {
  padding-top: var(--app-header-height);
}

.topbar-shell {
  height: var(--app-header-height);
}

.topbar {
  padding-top: var(--app-safe-top);
}
</style>
