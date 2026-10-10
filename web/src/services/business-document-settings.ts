import {
  businessDocumentTypes,
  type BusinessDocumentType,
} from '@/services/business-document-config'

export interface DocumentPolicySettings {
  includePalletByDefault: boolean
  autoUploadOnComplete: boolean
  autoApproveAfterUpload: boolean
}

export interface GlobalOperationSettings {
  reasonRequired: boolean
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
  globalOperation: GlobalOperationSettings
  globalPallet: GlobalPalletSettings
}

const storageKey = 'label-system:business-document-settings:v1'

const defaultPolicy: DocumentPolicySettings = {
  includePalletByDefault: true,
  autoUploadOnComplete: false,
  autoApproveAfterUpload: false,
}

const defaultGlobalOperation: GlobalOperationSettings = {
  reasonRequired: false,
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
    includePalletByDefault:
      value?.includePalletByDefault ?? defaultPolicy.includePalletByDefault,
    autoUploadOnComplete:
      value?.autoUploadOnComplete ?? defaultPolicy.autoUploadOnComplete,
    autoApproveAfterUpload:
      value?.autoApproveAfterUpload ?? defaultPolicy.autoApproveAfterUpload,
  }
}

export function createDefaultBusinessDocumentSettings(): BusinessDocumentSettings {
  return {
    globalPolicy: copyPolicy(),
    documentPolicies: {},
    globalOperation: { ...defaultGlobalOperation },
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
    for (const documentType of businessDocumentTypes) {
      const policy = stored.documentPolicies?.[documentType]
      if (policy) documentPolicies[documentType] = copyPolicy(policy)
    }
    return {
      globalPolicy: copyPolicy(stored.globalPolicy),
      documentPolicies,
      globalOperation: {
        ...defaultGlobalOperation,
        ...(stored.globalOperation ?? {}),
        reasonRequired:
          stored.globalOperation?.reasonRequired ??
          (stored.globalPolicy as { reasonRequired?: boolean } | undefined)?.reasonRequired ??
          false,
      },
      globalPallet: {
        ...defaultGlobalPallet,
        ...(stored.globalPallet ?? {}),
      },
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

export function syncPolicySettingToGlobal(
  settings: BusinessDocumentSettings,
  key: keyof DocumentPolicySettings,
  value: boolean,
): void {
  settings.globalPolicy[key] = value
  for (const documentType of businessDocumentTypes) {
    const policy = ensureDocumentPolicy(settings, documentType)
    policy[key] = value
  }
}
