import { workspaceEntries, workspaceGroups, type WorkspaceGroup } from '@/workspace'

export type FeatureVisibility = Record<string, boolean>

const storageKey = 'label-system:feature-visibility:global:v1'

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
  return loadFeatureVisibility()[code] !== false
}

export function getVisibleWorkspaceGroups(): WorkspaceGroup[] {
  const visibility = loadFeatureVisibility()
  return workspaceGroups
    .map((group) => ({
      ...group,
      entries: group.entries.filter((entry) => visibility[entry.code] !== false),
    }))
    .filter((group) => group.entries.length > 0)
}
