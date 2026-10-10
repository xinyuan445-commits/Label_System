<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'

import WorkspaceIcon from '@/WorkspaceIcon.vue'
import { isFeatureVisible } from '@/services/feature-visibility'
import { readWorkspaceFavorites } from '@/services/workspace-favorites'
import { useAuthStore } from '@/stores/auth'
import { findWorkspaceEntry, type WorkspaceEntry } from '@/workspace'

const router = useRouter()
const authStore = useAuthStore()
const accountMenu = ref<HTMLElement | null>(null)
const accountOpen = ref(false)
const username = computed(() => authStore.session?.username || '用户')

const favoriteEntries = computed<WorkspaceEntry[]>(() =>
  readWorkspaceFavorites(username.value)
    .map((code) => findWorkspaceEntry(code))
    .filter(
      (entry): entry is WorkspaceEntry =>
        entry !== undefined && isFeatureVisible(entry.code),
    ),
)

async function openEntry(code: string): Promise<void> {
  await router.push({
    name: 'work-function',
    params: { code },
    query: { origin: 'home' },
  })
}

async function logout(): Promise<void> {
  accountOpen.value = false
  authStore.logout()
  await router.replace({ name: 'login' })
}

function closeAccountMenu(event: PointerEvent): void {
  if (!accountMenu.value?.contains(event.target as Node)) {
    accountOpen.value = false
  }
}

onMounted(() => document.addEventListener('pointerdown', closeAccountMenu))
onBeforeUnmount(() => document.removeEventListener('pointerdown', closeAccountMenu))
</script>

<template>
  <main class="workspace">
    <header class="topbar-shell">
      <div class="topbar">
        <h1>业务中心</h1>

        <div ref="accountMenu" class="account-menu">
          <button
            class="account-button"
            type="button"
            :aria-expanded="accountOpen"
            aria-haspopup="menu"
            @click="accountOpen = !accountOpen"
          >
            <span class="avatar" aria-hidden="true">
              <svg viewBox="0 0 24 24">
                <path d="M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM5 20a7 7 0 0 1 14 0" />
              </svg>
            </span>
            <span class="account-name">{{ username }}</span>
            <svg class="account-chevron" viewBox="0 0 24 24" aria-hidden="true">
              <path d="m7 10 5 5 5-5" />
            </svg>
          </button>

          <div v-if="accountOpen" class="account-popover" role="menu">
            <button type="button" role="menuitem" @click="logout">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M10 5H5v14h5M14 8l4 4-4 4M18 12H9" />
              </svg>
              <span>退出登录</span>
            </button>
          </div>
        </div>
      </div>
    </header>

    <div class="workspace-content">
      <section class="favorites-card">
        <div class="section-heading">
          <h2>常用功能</h2>
          <button type="button" @click="router.push({ name: 'favorites' })">管理</button>
        </div>

        <div v-if="favoriteEntries.length" class="favorite-grid">
          <button
            v-for="entry in favoriteEntries"
            :key="entry.code"
            class="favorite-button"
            type="button"
            @click="openEntry(entry.code)"
          >
            <span class="favorite-icon" aria-hidden="true">
              <WorkspaceIcon :icon="entry.icon" />
            </span>
            <span>{{ entry.label }}</span>
          </button>
        </div>

        <button v-else class="empty-favorites" type="button" @click="router.push({ name: 'favorites' })">
          <span>＋</span>
          <span>添加常用功能</span>
        </button>
      </section>

      <button class="all-functions" type="button" @click="router.push({ name: 'all-functions' })">
        <span class="all-icon" aria-hidden="true">
          <svg viewBox="0 0 24 24">
            <rect x="4" y="4" width="6" height="6" rx="1" />
            <rect x="14" y="4" width="6" height="6" rx="1" />
            <rect x="4" y="14" width="6" height="6" rx="1" />
            <rect x="14" y="14" width="6" height="6" rx="1" />
          </svg>
        </span>
        <span class="all-label">全部功能</span>
        <svg class="chevron" viewBox="0 0 24 24" aria-hidden="true">
          <path d="m9 6 6 6-6 6" />
        </svg>
      </button>
    </div>
  </main>
</template>

<style scoped>
.workspace {
  height: 100dvh;
  overflow: hidden;
  background: #f3f6fa;
}

.topbar-shell {
  position: relative;
  z-index: 20;
  height: 72px;
  border-bottom: 1px solid #e7edf4;
  background: #fff;
  box-shadow: 0 3px 12px rgba(23, 54, 93, 0.05);
}

.topbar {
  width: min(100%, 920px);
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 18px;
  margin-inline: auto;
  padding: 0 20px;
}

h1 {
  color: #17365d;
  font-size: 26px;
}

.account-menu {
  position: relative;
}

.account-button {
  min-height: 44px;
  display: flex;
  align-items: center;
  gap: 9px;
  padding: 5px 10px 5px 6px;
  border: 1px solid #d9e2ec;
  border-radius: 22px;
  background: #fff;
  color: #31445e;
  font-size: 14px;
  font-weight: 650;
}

.avatar {
  width: 32px;
  height: 32px;
  display: grid;
  place-items: center;
  border-radius: 50%;
  background: #e9f2fd;
  color: #2368c4;
}

.avatar svg,
.account-popover svg {
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.8;
}

.avatar svg {
  width: 19px;
  height: 19px;
}

.account-name {
  max-width: 110px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.account-chevron {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: #8593a5;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 2;
}

.account-popover {
  position: absolute;
  top: calc(100% + 8px);
  right: 0;
  width: 144px;
  padding: 6px;
  border: 1px solid #e0e7ef;
  border-radius: 13px;
  background: #fff;
  box-shadow: 0 12px 30px rgba(23, 54, 93, 0.14);
}

.account-popover button {
  width: 100%;
  min-height: 42px;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 0 11px;
  border: 0;
  border-radius: 9px;
  background: transparent;
  color: #47586e;
  font-size: 14px;
  text-align: left;
}

.account-popover button:active {
  background: #f3f6fa;
}

.account-popover svg {
  width: 19px;
  height: 19px;
}

.workspace-content {
  width: min(100%, 920px);
  height: calc(100dvh - 72px);
  overflow-y: auto;
  margin-inline: auto;
  padding: 18px 20px 32px;
  overscroll-behavior: contain;
}

.favorites-card {
  padding: 18px;
  border: 1px solid rgba(23, 54, 93, 0.07);
  border-radius: 18px;
  background: #fff;
  box-shadow: 0 10px 28px rgba(23, 54, 93, 0.06);
}

.section-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 15px;
}

h2 {
  color: #24344d;
  font-size: 17px;
}

.section-heading button {
  min-height: 34px;
  padding: 0 10px;
  border: 0;
  border-radius: 9px;
  background: #edf4fd;
  color: #2368c4;
  font-size: 13px;
  font-weight: 650;
}

.favorite-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px;
}

.favorite-button {
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

.favorite-button:active {
  background: #e9f2fd;
}

.favorite-icon,
.all-icon {
  display: grid;
  place-items: center;
  color: #2368c4;
}

.favorite-icon {
  width: 38px;
  height: 38px;
  padding: 8px;
  border-radius: 11px;
  background: #e9f2fd;
}

.empty-favorites {
  width: 100%;
  min-height: 96px;
  display: grid;
  place-items: center;
  gap: 4px;
  border: 1px dashed #c7d5e5;
  border-radius: 14px;
  background: #f8fafc;
  color: #718096;
  font-size: 13px;
}

.empty-favorites span:first-child {
  color: #3f77bd;
  font-size: 25px;
}

.all-functions {
  width: 100%;
  min-height: 72px;
  display: grid;
  grid-template-columns: auto 1fr auto;
  align-items: center;
  gap: 14px;
  margin-top: 16px;
  padding: 12px 18px;
  border: 1px solid rgba(23, 54, 93, 0.07);
  border-radius: 18px;
  background: #fff;
  color: #24344d;
  text-align: left;
  box-shadow: 0 10px 28px rgba(23, 54, 93, 0.06);
}

.all-icon {
  width: 40px;
  height: 40px;
  padding: 9px;
  border-radius: 12px;
  background: #e9f2fd;
}

.all-icon svg,
.chevron {
  width: 100%;
  height: 100%;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.8;
}

.all-label {
  font-size: 16px;
  font-weight: 700;
}

.chevron {
  width: 20px;
  height: 20px;
  color: #9aa8b9;
  stroke-width: 2;
}

@media (max-width: 480px) {
  .topbar {
    padding: 0 14px;
  }

  h1 {
    font-size: 24px;
  }

  .workspace-content {
    padding: 16px 14px 28px;
  }

  .favorites-card {
    padding: 16px 12px;
  }

  .favorite-grid {
    gap: 8px;
  }

  .favorite-button {
    min-height: 90px;
    font-size: 13px;
  }
}
</style>
