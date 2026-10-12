import { workspaceEntries, workspaceGroups, type WorkspaceGroup } from '@/workspace'
import { readStoredAuthSession } from '@/services/auth-session'
import { getRuntimeConfig } from '@/services/runtime-config'

export type FeatureVisibility = Record<string, boolean>

const storageKey = 'label-system:feature-visibility:global:v1'
const userAccessKeyPrefix = 'label-system:function-access:v1'

function userAccessKey(userId: string): string {
  return `${userAccessKeyPrefix}:${getRuntimeConfig().environment}:${userId}`
}

function loadUserFunctionAccess(userId: string): Set<string> | null {
  try {
    const raw = localStorage.getItem(userAccessKey(userId))
    if (!raw) return null
    const values = JSON.parse(raw) as unknown
    return Array.isArray(values) && values.every((value) => typeof value === 'string')
      ? new Set(values)
      : null
  } catch {
    return null
  }
}

export function saveUserFunctionAccess(userId: string, functionCodes: string[]): void {
  try {
    localStorage.setItem(userAccessKey(userId), JSON.stringify([...new Set(functionCodes)]))
  } catch {
    // 存储受限时由管理员默认权限兜底，普通用户保持最小权限。
  }
}

export function clearUserFunctionAccess(userId: string): void {
  try { localStorage.removeItem(userAccessKey(userId)) } catch { /* 无需处理 */ }
}

export function createDefaultFeatureVisibility(): FeatureVisibility {
  return Object.fromEntries(workspaceEntries.map((entry) => [entry.code, true]))
}

export function loadFeatureVisibility(): FeatureVisibility {
  const defaults = createDefaultFeatureVisibility()
  const raw = localStorage.getItem(storageKey)
  if (!raw) return defaults

  try {
    const stored = JSON.parse(raw) as Record<string, unknown>
    for (const code of Object.keys(defaults)) {
      if (typeof stored[code] === 'boolean') defaults[code] = stored[code]
    }
    return defaults
  } catch {
    return defaults
  }
}

export function saveFeatureVisibility(visibility: FeatureVisibility): void {
  const normalized = createDefaultFeatureVisibility()
  for (const code of Object.keys(normalized)) {
    normalized[code] = visibility[code] !== false
  }
  localStorage.setItem(storageKey, JSON.stringify(normalized))
}

export function isFeatureVisible(code: string): boolean {
  if (loadFeatureVisibility()[code] === false) return false
  const session = readStoredAuthSession()
  if (!session) return false
  const access = loadUserFunctionAccess(session.user.id)
  if (access) return access.has(code)
  return session.user.roles.some((role) => role.toLowerCase() === 'admin')
}

export function getVisibleWorkspaceGroups(): WorkspaceGroup[] {
  return workspaceGroups
    .map((group) => ({
      ...group,
      entries: group.entries.filter((entry) => isFeatureVisible(entry.code)),
    }))
    .filter((group) => group.entries.length > 0)
}
