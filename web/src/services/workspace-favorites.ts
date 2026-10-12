import { workspaceEntries } from '@/workspace'
import { apiRequest, ApiError } from '@/api/client'

export const MAX_WORKSPACE_FAVORITES = 6

const storagePrefix = 'label-system.workspace.favorites'
const defaultFavorites = [
  'purchase-arrival',
  'purchase-inbound',
  'sales-outbound',
  'label-query',
]
const validCodes = new Set(workspaceEntries.map((entry) => entry.code))

function storageKey(username: string): string {
  return `${storagePrefix}.${username || 'default'}`
}

function normalize(codes: unknown): string[] {
  if (!Array.isArray(codes)) {
    return [...defaultFavorites]
  }

  return [...new Set(codes)]
    .filter((code): code is string => typeof code === 'string' && validCodes.has(code))
    .slice(0, MAX_WORKSPACE_FAVORITES)
}

interface PreferenceItem {
  value: unknown
}

export function readWorkspaceFavorites(username: string): string[] {
  try {
    const value = localStorage.getItem(storageKey(username))
    return value === null ? [...defaultFavorites] : normalize(JSON.parse(value))
  } catch {
    return [...defaultFavorites]
  }
}

export async function loadWorkspaceFavorites(username: string): Promise<string[]> {
  try {
    const item = await apiRequest<PreferenceItem>('/api/v1/users/me/preferences/workspace.favorites')
    const favorites = normalize(item.value)
    localStorage.setItem(storageKey(username), JSON.stringify(favorites))
    return favorites
  } catch (error) {
    if (error instanceof ApiError && error.code === 'PREFERENCE_NOT_FOUND') {
      return readWorkspaceFavorites(username)
    }
    return readWorkspaceFavorites(username)
  }
}

export async function saveWorkspaceFavorites(username: string, codes: string[]): Promise<void> {
  const favorites = normalize(codes)
  localStorage.setItem(storageKey(username), JSON.stringify(favorites))
  await apiRequest('/api/v1/users/me/preferences/workspace.favorites', {
    method: 'PUT',
    body: JSON.stringify({ value: favorites, deviceId: null, expectedRowVersion: null }),
  })
}
