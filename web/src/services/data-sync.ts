import { apiRequest } from '@/api/client'

export interface DataSyncStatusItem {
  key: string
  name: string
  status: 'NEVER' | 'RUNNING' | 'SUCCESS' | 'FAILED'
  itemCount: number
  lastStartedAt: string | null
  lastCompletedAt: string | null
  lastSuccessAt: string | null
  syncedBy: string | null
  errorMessage: string | null
}

export interface DataSyncResult {
  key: string
  status: 'SUCCESS'
  itemCount: number
  completedAt: string
}

interface SettingItem {
  scope: string
  documentType: string | null
  key: string
  name: string
  value: unknown
  isEnabled: boolean
  version: number
  rowVersion: string
}

export interface DraftRetentionPolicy {
  activeMode: 'DAYS' | 'PERMANENT'
  activeDays: number | null
  closedMode: 'DAYS' | 'PERMANENT'
  closedDays: number | null
}

export interface DraftDeviceState {
  userId: string
  userName: string
  deviceId: string
  deviceName: string | null
  deviceModel: string | null
  appVersion: string | null
  localDraftCount: number
  pendingSyncCount: number
  failedSyncCount: number
  lastSeenAt: string
  lastSuccessfulSyncAt: string | null
}

let retentionRow: SettingItem | null = null

function retentionValue(value: unknown): DraftRetentionPolicy {
  const candidate = value && typeof value === 'object'
    ? value as Partial<DraftRetentionPolicy>
    : {}
  return {
    activeMode: candidate.activeMode === 'DAYS' ? 'DAYS' : 'PERMANENT',
    activeDays: candidate.activeMode === 'DAYS' && typeof candidate.activeDays === 'number'
      ? candidate.activeDays : null,
    closedMode: candidate.closedMode === 'PERMANENT' ? 'PERMANENT' : 'DAYS',
    closedDays: candidate.closedMode === 'PERMANENT' ? null
      : typeof candidate.closedDays === 'number' ? candidate.closedDays : 90,
  }
}

export async function fetchDraftRetentionPolicy(): Promise<DraftRetentionPolicy> {
  const settings = await apiRequest<SettingItem[]>('/api/v1/settings')
  retentionRow = settings.find((item) =>
    item.scope === 'GLOBAL' && item.key === 'draft_retention_policy') ?? null
  return retentionValue(retentionRow?.value)
}

export async function saveDraftRetentionPolicy(
  policy: DraftRetentionPolicy,
): Promise<DraftRetentionPolicy> {
  const activeDays = policy.activeMode === 'DAYS'
    ? Math.max(1, Math.min(36500, Math.trunc(policy.activeDays ?? 90))) : null
  const closedDays = policy.closedMode === 'DAYS'
    ? Math.max(1, Math.min(36500, Math.trunc(policy.closedDays ?? 90))) : null
  retentionRow = await apiRequest<SettingItem>('/api/v1/admin/settings/global/draft_retention_policy', {
    method: 'PUT',
    body: JSON.stringify({
      documentType: null,
      name: '草稿保留',
      value: { ...policy, activeDays, closedDays },
      isEnabled: true,
      expectedRowVersion: retentionRow?.rowVersion ?? null,
    }),
  })
  return retentionValue(retentionRow.value)
}

export async function fetchDraftDevices(): Promise<DraftDeviceState[]> {
  return apiRequest<DraftDeviceState[]>('/api/v1/admin/draft-devices')
}

export async function fetchDataSyncStatus(): Promise<DataSyncStatusItem[]> {
  return apiRequest<DataSyncStatusItem[]>('/api/v1/admin/data-sync/status')
}

export async function syncRdStyles(): Promise<DataSyncResult> {
  return apiRequest<DataSyncResult>('/api/v1/admin/data-sync/rd-styles', {
    method: 'POST',
    timeoutMs: 45_000,
  })
}

export async function syncWarehousePositions(): Promise<DataSyncResult> {
  return apiRequest<DataSyncResult>('/api/v1/admin/data-sync/warehouse-positions', {
    method: 'POST',
    timeoutMs: 45_000,
  })
}

export async function syncDepartments(): Promise<DataSyncResult> {
  return apiRequest<DataSyncResult>('/api/v1/admin/data-sync/departments', {
    method: 'POST',
    timeoutMs: 45_000,
  })
}
