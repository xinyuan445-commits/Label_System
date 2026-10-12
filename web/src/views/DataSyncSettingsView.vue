<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import {
  fetchDraftDevices,
  fetchDraftRetentionPolicy,
  fetchDataSyncStatus,
  saveDraftRetentionPolicy,
  syncDepartments,
  syncRdStyles,
  syncWarehousePositions,
  type DataSyncStatusItem,
  type DraftDeviceState,
  type DraftRetentionPolicy,
} from '@/services/data-sync'
import { readSession } from '@/services/auth'

interface SyncOption {
  key: 'users' | 'categories' | 'locations' | 'departments'
  label: string
  enabled: boolean
  statusKey?: string
}

const router = useRouter()
const options: SyncOption[] = [
  { key: 'users', label: '用户信息', enabled: false },
  { key: 'categories', label: '出入库类别', enabled: true, statusKey: 'rd-styles' },
  { key: 'locations', label: '仓位信息', enabled: true, statusKey: 'warehouse-positions' },
  { key: 'departments', label: '部门信息', enabled: true, statusKey: 'departments' },
]
const selected = ref<Record<SyncOption['key'], boolean>>({
  users: false,
  categories: true,
  locations: true,
  departments: true,
})
const statuses = ref<DataSyncStatusItem[]>([])
const loading = ref(false)
const syncing = ref(false)
const savingRetention = ref(false)
const retention = ref<DraftRetentionPolicy>({
  activeMode: 'PERMANENT', activeDays: null, closedMode: 'DAYS', closedDays: 90,
})
const devices = ref<DraftDeviceState[]>([])
const message = ref('')
const messageTone = ref<'info' | 'error'>('info')
let messageTimer: ReturnType<typeof setTimeout> | null = null

const hasSelection = computed(() => options.some((option) => option.enabled && selected.value[option.key]))
const isAdmin = computed(() =>
  readSession()?.user.roles.some((role) => role.toLowerCase() === 'admin') === true,
)
function statusCaption(option: SyncOption): string {
  const status = statuses.value.find((item) => item.key === option.statusKey)
  if (!status || status.status === 'NEVER') return '尚未同步'
  if (status.status === 'RUNNING') return '同步中'
  if (status.status === 'FAILED') return '上次同步失败'
  return `${status.itemCount} 条 · ${formatTime(status.lastSuccessAt)}`
}

function notify(text: string, tone: 'info' | 'error' = 'info'): void {
  message.value = text
  messageTone.value = tone
  if (messageTimer) clearTimeout(messageTimer)
  messageTimer = setTimeout(() => {
    message.value = ''
  }, 1800)
}

function formatTime(value: string | null): string {
  if (!value) return ''
  return new Intl.DateTimeFormat('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(new Date(value))
}

async function loadStatus(): Promise<void> {
  loading.value = true
  try {
    const [syncStatus, policy] = await Promise.all([
      fetchDataSyncStatus(), fetchDraftRetentionPolicy(),
    ])
    statuses.value = syncStatus
    retention.value = policy
    if (isAdmin.value) devices.value = await fetchDraftDevices()
  } catch (error) {
    notify(error instanceof ApiError ? error.message : '读取失败', 'error')
  } finally {
    loading.value = false
  }
}

async function saveRetention(): Promise<void> {
  savingRetention.value = true
  try {
    retention.value = await saveDraftRetentionPolicy(retention.value)
    notify('已保存')
  } catch (error) {
    notify(error instanceof ApiError ? error.message : '保存失败', 'error')
  } finally {
    savingRetention.value = false
  }
}

async function syncNow(): Promise<void> {
  if (!hasSelection.value) {
    notify('请选择同步内容', 'error')
    return
  }
  syncing.value = true
  try {
    const tasks: Array<() => Promise<{ itemCount: number }>> = []
    if (selected.value.categories) tasks.push(syncRdStyles)
    if (selected.value.locations) tasks.push(syncWarehousePositions)
    if (selected.value.departments) tasks.push(syncDepartments)
    let count = 0
    for (const task of tasks) count += (await task()).itemCount
    notify(`已同步 ${count} 条`)
    await loadStatus()
  } catch (error) {
    notify(error instanceof ApiError ? error.message : '同步失败', 'error')
  } finally {
    syncing.value = false
  }
}

onMounted(loadStatus)
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
        <label
          v-for="option in options"
          :key="option.key"
          class="sync-row"
          :class="{ disabled: !option.enabled }"
        >
          <span class="sync-name">
            <strong>{{ option.label }}</strong>
            <small v-if="option.enabled">{{ loading ? '读取中' : statusCaption(option) }}</small>
            <small v-else>待接入</small>
          </span>
          <span class="checkbox">
            <input v-model="selected[option.key]" type="checkbox" :disabled="!option.enabled || syncing" />
            <i aria-hidden="true">
              <svg viewBox="0 0 24 24"><path d="m6 12 4 4 8-9" /></svg>
            </i>
          </span>
        </label>
      </section>

      <section class="settings-card">
        <h2>草稿保留</h2>
        <label class="setting-row">
          <span>活跃草稿</span>
          <select v-model="retention.activeMode">
            <option value="PERMANENT">永久</option>
            <option value="DAYS">按天</option>
          </select>
          <input
            v-if="retention.activeMode === 'DAYS'"
            v-model.number="retention.activeDays"
            type="number"
            min="1"
            max="36500"
          />
        </label>
        <label class="setting-row">
          <span>已结束</span>
          <select v-model="retention.closedMode">
            <option value="DAYS">按天</option>
            <option value="PERMANENT">永久</option>
          </select>
          <input
            v-if="retention.closedMode === 'DAYS'"
            v-model.number="retention.closedDays"
            type="number"
            min="1"
            max="36500"
          />
        </label>
        <button class="save-button" type="button" :disabled="savingRetention" @click="saveRetention">
          {{ savingRetention ? '保存中' : '保存' }}
        </button>
      </section>

      <section v-if="isAdmin && devices.length" class="settings-card device-card">
        <h2>PDA 状态</h2>
        <div v-for="device in devices" :key="`${device.userId}:${device.deviceId}`" class="device-row">
          <span><strong>{{ device.userName }}</strong><small>{{ formatTime(device.lastSeenAt) }}</small></span>
          <span>{{ device.localDraftCount }} 草稿</span>
          <span v-if="device.pendingSyncCount">{{ device.pendingSyncCount }} 待同步</span>
          <span v-if="device.failedSyncCount" class="danger-text">{{ device.failedSyncCount }} 失败</span>
        </div>
      </section>
    </div>

    <footer class="action-shell">
      <button type="button" :disabled="syncing || !hasSelection" @click="syncNow">
        {{ syncing ? '同步中' : '立即同步' }}
      </button>
    </footer>

    <Transition name="toast">
      <div v-if="message" class="toast-message" :class="messageTone" role="status">{{ message }}</div>
    </Transition>
  </main>
</template>

<style scoped>
.sync-page{min-height:100dvh;padding:72px 0 86px;background:#f3f6fa;color:#21324a}.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:72px;border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}.topbar,.page-content,.action-shell{width:min(100%,620px);margin-inline:auto}.topbar{height:100%;display:flex;align-items:center;gap:12px;padding:0 18px}.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg,.checkbox svg{fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2.3}.back-button svg{width:23px;height:23px}.topbar h1{color:#17365d;font-size:22px}.page-content{padding:16px 14px}.sync-card{overflow:hidden;display:grid;grid-template-columns:repeat(2,minmax(0,1fr));border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.055)}.sync-row{min-width:0;min-height:70px;display:flex;align-items:center;justify-content:space-between;gap:10px;padding:11px 14px;border-right:1px solid #edf1f5;border-bottom:1px solid #edf1f5}.sync-row:nth-child(2n){border-right:0}.sync-row:last-child{border-bottom:0}.sync-row.disabled{background:#fafbfd}.sync-name{min-width:0;display:grid;gap:3px}.sync-name strong{color:#2b405c;font-size:14px;font-weight:650}.sync-name small{overflow:hidden;color:#8290a2;font-size:11px;font-weight:500;text-overflow:ellipsis;white-space:nowrap}.sync-row.disabled .sync-name strong{color:#8d99a9}.checkbox{position:relative;width:28px;height:28px;display:block;flex:0 0 auto}.checkbox input{position:absolute;opacity:0;pointer-events:none}.checkbox i{position:absolute;inset:0;display:grid;place-items:center;border:1.5px solid #bdc9d7;border-radius:8px;background:#fff;color:transparent;transition:.16s}.checkbox svg{width:18px;height:18px}.checkbox input:checked+i{border-color:#2368c4;background:#2368c4;color:#fff}.checkbox input:disabled+i{border-color:#dbe2ea;background:#edf1f5}.action-shell{position:fixed;z-index:18;right:0;bottom:0;left:0;padding:10px 14px max(10px,env(safe-area-inset-bottom));border-top:1px solid #e1e8f0;background:rgba(255,255,255,.97);box-shadow:0 -6px 20px rgba(23,54,93,.07)}.action-shell button{width:100%;height:48px;border:0;border-radius:11px;background:#2368c4;color:#fff;font-size:15px;font-weight:750;box-shadow:0 7px 16px rgba(35,104,196,.2)}.action-shell button:disabled{background:#a7b7ca;box-shadow:none}.toast-message{position:fixed;z-index:50;top:82px;left:50%;width:max-content;max-width:calc(100% - 36px);padding:10px 16px;border-radius:10px;background:#31445e;color:#fff;font-size:13px;font-weight:700;box-shadow:0 8px 22px rgba(23,54,93,.2);transform:translateX(-50%)}.toast-message.error{background:#b64a4a}.toast-enter-active,.toast-leave-active{transition:opacity .16s ease,transform .16s ease}.toast-enter-from,.toast-leave-to{opacity:0;transform:translate(-50%,-14px)}
.sync-page {
  padding-top: var(--app-header-height);
}

.topbar-shell {
  height: var(--app-header-height);
}

.topbar {
  padding-top: var(--app-safe-top);
}

.toast-message {
  top: calc(var(--app-header-height) + 10px);
}

.page-content{display:grid;gap:14px}.settings-card{display:grid;gap:12px;padding:15px;border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.055)}.settings-card h2{color:#2b405c;font-size:15px}.setting-row{display:grid;grid-template-columns:minmax(90px,1fr) 96px 90px;align-items:center;gap:10px}.setting-row span{color:#52657d;font-size:13px;font-weight:650}.setting-row select,.setting-row input{height:38px;min-width:0;padding:0 9px;border:1px solid #cfdae6;border-radius:9px;background:#fff;color:#31445e}.save-button{justify-self:end;min-width:88px;height:38px;border:0;border-radius:9px;background:#e9f2fd;color:#2368c4;font-weight:750}.device-row{display:flex;align-items:center;gap:10px;padding:10px 0;border-top:1px solid #edf1f5;color:#60758e;font-size:12px}.device-row>span:first-child{min-width:0;margin-right:auto;display:grid;gap:2px}.device-row strong{color:#2b405c;font-size:13px}.device-row small{color:#8b98a8}.danger-text{color:#b64a4a}@media(max-width:480px){.setting-row{grid-template-columns:minmax(80px,1fr) 88px 76px}.device-row{flex-wrap:wrap}}
</style>
