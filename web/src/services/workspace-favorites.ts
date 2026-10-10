import { workspaceEntries } from '@/workspace'

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

export function readWorkspaceFavorites(username: string): string[] {
  try {
    const value = localStorage.getItem(storageKey(username))
    return value === null ? [...defaultFavorites] : normalize(JSON.parse(value))
  } catch {
    return [...defaultFavorites]
  }
}

export function saveWorkspaceFavorites(username: string, codes: string[]): void {
  localStorage.setItem(storageKey(username), JSON.stringify(normalize(codes)))
}
