import { apiRequest, ApiError } from '@/api/client'

export type WorkspaceHomeMode = 'compact' | 'detailed'

const preferenceKey = 'workspace.homeMode'
const storagePrefix = 'label-system.workspace.home-mode'

interface PreferenceItem {
  value: unknown
}

function storageKey(username: string): string {
  return `${storagePrefix}.${username || 'default'}`
}

function normalize(value: unknown): WorkspaceHomeMode {
  if (value && typeof value === 'object' && 'mode' in value) {
    return (value as { mode?: unknown }).mode === 'compact' ? 'compact' : 'detailed'
  }
  return value === 'compact' ? 'compact' : 'detailed'
}

export function readWorkspaceHomeMode(username: string): WorkspaceHomeMode {
  try {
    return normalize(localStorage.getItem(storageKey(username)))
  } catch {
    return 'detailed'
  }
}

export async function loadWorkspaceHomeMode(username: string): Promise<WorkspaceHomeMode> {
  try {
    const item = await apiRequest<PreferenceItem>(
      `/api/v1/users/me/preferences/${preferenceKey}`,
    )
    const mode = normalize(item.value)
    localStorage.setItem(storageKey(username), mode)
    return mode
  } catch (error) {
    if (error instanceof ApiError && error.code === 'PREFERENCE_NOT_FOUND') {
      return readWorkspaceHomeMode(username)
    }
    return readWorkspaceHomeMode(username)
  }
}

export async function saveWorkspaceHomeMode(
  username: string,
  mode: WorkspaceHomeMode,
): Promise<void> {
  const normalized = normalize(mode)
  localStorage.setItem(storageKey(username), normalized)
  const save = () =>
    apiRequest(`/api/v1/users/me/preferences/${preferenceKey}`, {
      method: 'PUT',
      body: JSON.stringify({ value: { mode: normalized }, deviceId: null, expectedRowVersion: null }),
    })

  try {
    await save()
  } catch (error) {
    if (!(error instanceof ApiError) || error.code !== 'DATA_CHANGED') throw error

    const current = await apiRequest<PreferenceItem>(
      `/api/v1/users/me/preferences/${preferenceKey}`,
    )
    if (normalize(current.value) !== normalized) await save()
  }
}
