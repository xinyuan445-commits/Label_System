import { apiRequest } from '@/api/client'
import {
  businessDocumentTypes,
  type BusinessDocumentType,
} from '@/services/business-document-config'
import {
  mergeDocumentFieldSettings,
  type DocumentFieldSettings,
} from '@/services/business-document-fields'

export interface DocumentPolicySettings {
  includePalletByDefault: boolean
  autoAddScannedDetail: boolean
  autoUploadOnComplete: boolean
  autoApproveAfterUpload: boolean
  allowOverIssue: boolean
}

export interface GlobalPalletSettings {
  allowMixedMaterials: boolean
  allowMixedBatches: boolean
  allowMixedWarehouses: boolean
  allowMixedLocations: boolean
  allowPalletReuse: boolean
  allowPalletizedLabelDirectUse: boolean
}

export interface BusinessDocumentSettings {
  globalPolicy: DocumentPolicySettings
  documentPolicies: Partial<Record<BusinessDocumentType, DocumentPolicySettings>>
  documentFields: Partial<Record<BusinessDocumentType, DocumentFieldSettings>>
  globalPallet: GlobalPalletSettings
}

interface SettingItem {
  scope: 'GLOBAL' | 'DOCUMENT'
  documentType: string | null
  key: string
  name: string
  value: unknown
  isEnabled: boolean
  version: number
  rowVersion: string
}

interface SettingDefinition {
  key: string
  name: string
}

const storageKey = 'label-system:business-document-settings:v1'
const rows = new Map<string, SettingItem>()

const documentDefinitions: Record<keyof DocumentPolicySettings, SettingDefinition> = {
  includePalletByDefault: { key: 'include_pallet_in_document_by_default', name: '扫托包含托盘' },
  autoAddScannedDetail: { key: 'auto_add_scanned_detail', name: '扫码后自动添加到明细' },
  autoUploadOnComplete: { key: 'auto_upload_on_complete', name: '完成自动上传' },
  autoApproveAfterUpload: { key: 'auto_approve_after_upload', name: '上传自动审核' },
  allowOverIssue: { key: 'allow_over_issue', name: '允许超领' },
}

const documentFieldDefinition: SettingDefinition = {
  key: 'field_input_rules',
  name: '填写字段设置',
}

const globalDefinitions: Array<{
  property: keyof GlobalPalletSettings
  definition: SettingDefinition
}> = [
  { property: 'allowMixedMaterials', definition: { key: 'allow_mixed_materials', name: '不同物料混托' } },
  { property: 'allowMixedBatches', definition: { key: 'allow_mixed_batches', name: '同料不同批次混托' } },
  { property: 'allowMixedWarehouses', definition: { key: 'allow_mixed_warehouses', name: '不同仓库混托' } },
  { property: 'allowMixedLocations', definition: { key: 'allow_mixed_locations', name: '不同货位混托' } },
  { property: 'allowPalletReuse', definition: { key: 'allow_pallet_reuse', name: '空托盘允许复用' } },
  { property: 'allowPalletizedLabelDirectUse', definition: { key: 'allow_direct_use_of_palletized_label', name: '托内标签单独使用' } },
]

const defaultPolicy: DocumentPolicySettings = {
  includePalletByDefault: true,
  autoAddScannedDetail: true,
  autoUploadOnComplete: false,
  autoApproveAfterUpload: false,
  allowOverIssue: false,
}

const defaultGlobalPallet: GlobalPalletSettings = {
  allowMixedMaterials: true,
  allowMixedBatches: true,
  allowMixedWarehouses: false,
  allowMixedLocations: false,
  allowPalletReuse: true,
  allowPalletizedLabelDirectUse: false,
}

function copyPolicy(value?: Partial<DocumentPolicySettings>): DocumentPolicySettings {
  return {
    includePalletByDefault: value?.includePalletByDefault ?? defaultPolicy.includePalletByDefault,
    autoAddScannedDetail: value?.autoAddScannedDetail ?? defaultPolicy.autoAddScannedDetail,
    autoUploadOnComplete: value?.autoUploadOnComplete ?? defaultPolicy.autoUploadOnComplete,
    autoApproveAfterUpload: value?.autoApproveAfterUpload ?? defaultPolicy.autoApproveAfterUpload,
    allowOverIssue: value?.allowOverIssue ?? defaultPolicy.allowOverIssue,
  }
}

function rowKey(scope: string, documentType: string | null, key: string): string {
  return `${scope}:${documentType ?? ''}:${key}`
}

function remember(items: SettingItem[]): void {
  for (const item of items) rows.set(rowKey(item.scope, item.documentType, item.key), item)
}

function booleanValue(items: SettingItem[], scope: string, key: string, fallback: boolean): boolean {
  const item = items.find((candidate) => candidate.scope === scope && candidate.key === key)
  return item?.isEnabled && typeof item.value === 'boolean' ? item.value : fallback
}

function fieldValue(
  items: SettingItem[],
  documentType: BusinessDocumentType,
  fallback: DocumentFieldSettings,
): DocumentFieldSettings {
  const item = items.find((candidate) =>
    candidate.scope === 'DOCUMENT' && candidate.documentType === documentType &&
    candidate.key === documentFieldDefinition.key,
  )
  return item?.isEnabled && item.value && typeof item.value === 'object' && !Array.isArray(item.value)
    ? mergeDocumentFieldSettings(documentType, item.value as Partial<DocumentFieldSettings>)
    : fallback
}

export function createDefaultBusinessDocumentSettings(): BusinessDocumentSettings {
  return {
    globalPolicy: copyPolicy(),
    documentPolicies: {},
    documentFields: {},
    globalPallet: { ...defaultGlobalPallet },
  }
}

export function loadBusinessDocumentSettings(): BusinessDocumentSettings {
  const defaults = createDefaultBusinessDocumentSettings()
  const raw = localStorage.getItem(storageKey)
  if (!raw) return defaults
  try {
    const stored = JSON.parse(raw) as Partial<BusinessDocumentSettings>
    const documentPolicies: BusinessDocumentSettings['documentPolicies'] = {}
    const documentFields: BusinessDocumentSettings['documentFields'] = {}
    for (const documentType of businessDocumentTypes) {
      const policy = stored.documentPolicies?.[documentType]
      if (policy) documentPolicies[documentType] = copyPolicy(policy)
      const fields = stored.documentFields?.[documentType]
      if (fields) documentFields[documentType] = mergeDocumentFieldSettings(documentType, fields)
    }
    return {
      globalPolicy: copyPolicy(stored.globalPolicy),
      documentPolicies,
      documentFields,
      globalPallet: { ...defaultGlobalPallet, ...(stored.globalPallet ?? {}) },
    }
  } catch {
    return defaults
  }
}

export function saveBusinessDocumentSettings(settings: BusinessDocumentSettings): void {
  localStorage.setItem(storageKey, JSON.stringify(settings))
}

export function ensureDocumentPolicy(
  settings: BusinessDocumentSettings,
  documentType: BusinessDocumentType,
): DocumentPolicySettings {
  const existing = settings.documentPolicies[documentType]
  if (existing) return existing
  const created = copyPolicy(settings.globalPolicy)
  settings.documentPolicies[documentType] = created
  return created
}

export function ensureDocumentFieldSettings(
  settings: BusinessDocumentSettings,
  documentType: BusinessDocumentType,
): DocumentFieldSettings {
  const existing = settings.documentFields[documentType]
  if (existing) return existing
  const created = mergeDocumentFieldSettings(documentType)
  settings.documentFields[documentType] = created
  return created
}

export function syncPolicySettingToGlobal(
  settings: BusinessDocumentSettings,
  key: keyof DocumentPolicySettings,
  value: boolean,
): void {
  settings.globalPolicy[key] = value
  for (const documentType of businessDocumentTypes) {
    ensureDocumentPolicy(settings, documentType)[key] = value
  }
}

export async function fetchBusinessDocumentSettings(
  documentType: BusinessDocumentType,
): Promise<BusinessDocumentSettings> {
  const settings = loadBusinessDocumentSettings()
  const items = await apiRequest<SettingItem[]>(
    `/api/v1/settings?${new URLSearchParams({ documentType }).toString()}`,
  )
  remember(items)
  const policy = ensureDocumentPolicy(settings, documentType)
  settings.documentFields[documentType] = fieldValue(
    items,
    documentType,
    ensureDocumentFieldSettings(settings, documentType),
  )
  for (const [property, definition] of Object.entries(documentDefinitions) as Array<
    [keyof DocumentPolicySettings, SettingDefinition]
  >) {
    policy[property] = booleanValue(items, 'DOCUMENT', definition.key, policy[property])
  }
  for (const item of globalDefinitions) {
    settings.globalPallet[item.property] = booleanValue(
      items, 'GLOBAL', item.definition.key, settings.globalPallet[item.property],
    )
  }
  saveBusinessDocumentSettings(settings)
  return settings
}

async function updateSetting(
  scope: 'GLOBAL' | 'DOCUMENT',
  documentType: BusinessDocumentType | null,
  definition: SettingDefinition,
  value: unknown,
): Promise<void> {
  const key = rowKey(scope, documentType, definition.key)
  const current = rows.get(key)
  const updated = await apiRequest<SettingItem>(
    `/api/v1/admin/settings/${scope.toLowerCase()}/${encodeURIComponent(definition.key)}`,
    {
      method: 'PUT',
      body: JSON.stringify({
        documentType,
        name: definition.name,
        value,
        isEnabled: true,
        expectedRowVersion: current?.rowVersion ?? null,
      }),
    },
  )
  rows.set(key, updated)
}

export async function persistBusinessDocumentSettings(
  documentType: BusinessDocumentType,
  settings: BusinessDocumentSettings,
  globallySynced: ReadonlySet<keyof DocumentPolicySettings> = new Set(),
): Promise<void> {
  const policy = ensureDocumentPolicy(settings, documentType)
  for (const [property, definition] of Object.entries(documentDefinitions) as Array<
    [keyof DocumentPolicySettings, SettingDefinition]
  >) {
    if (property === 'allowOverIssue' && documentType !== 'material-outbound') continue
    await updateSetting('DOCUMENT', documentType, definition, policy[property])
  }
  for (const item of globalDefinitions) {
    await updateSetting('GLOBAL', null, item.definition, settings.globalPallet[item.property])
  }
  for (const property of globallySynced) {
    const definition = documentDefinitions[property]
    for (const targetType of businessDocumentTypes) {
      if (targetType === documentType) continue
      if (property === 'allowOverIssue' && targetType !== 'material-outbound') continue
      const targetItems = await apiRequest<SettingItem[]>(
        `/api/v1/settings?${new URLSearchParams({ documentType: targetType }).toString()}`,
      )
      remember(targetItems)
      await updateSetting(
        'DOCUMENT', targetType, definition, ensureDocumentPolicy(settings, targetType)[property],
      )
    }
  }
  saveBusinessDocumentSettings(settings)
}

export async function persistBusinessDocumentFieldSettings(
  documentType: BusinessDocumentType,
  settings: BusinessDocumentSettings,
): Promise<void> {
  await updateSetting(
    'DOCUMENT',
    documentType,
    documentFieldDefinition,
    ensureDocumentFieldSettings(settings, documentType),
  )
  saveBusinessDocumentSettings(settings)
}
