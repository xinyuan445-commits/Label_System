import { apiRequest } from '@/api/client'
import { getRuntimeConfig } from '@/services/runtime-config'

export type LabelObjectType = 'PRODUCT_LABEL' | 'PALLET' | 'INVENTORY_OBJECT' | 'EXTERNAL'
export type LabelRuleVersionStatus = 'DRAFT' | 'ACTIVE' | 'RETIRED' | 'BLOCKED'
export type LabelSegmentKind =
  | 'LITERAL'
  | 'SEPARATOR'
  | 'FIELD'
  | 'DATE'
  | 'SEQUENCE'
  | 'CHECKSUM'

export interface LabelCodeSegmentSpec {
  kind: LabelSegmentKind
  value?: string
  field?: string
  length?: number
  maxLength?: number
  encoding?: 'NONE' | 'PERCENT'
  expectedValue?: string
  decimalScale?: number
  validateCurrentAccount?: boolean
  dateFormat?: 'yyyyMMdd' | 'yyMMdd' | 'yyyyMMddHHmmss'
  algorithm?: 'SHA256_8' | 'EAN_MOD10'
}

export interface LabelCodeRuleSpec {
  schemaVersion: 1
  symbologies: string[]
  caseMode: 'PRESERVE' | 'UPPER' | 'LOWER'
  segments: LabelCodeSegmentSpec[]
}

export interface LabelCodeRuleVersion {
  id: number
  version: number
  status: LabelRuleVersionStatus
  spec: LabelCodeRuleSpec
  specSha256: string
  createdBy: string
  createdAt: string
  updatedBy: string
  updatedAt: string
  activatedBy: string | null
  activatedAt: string | null
  retiredBy: string | null
  retiredAt: string | null
  rowVersion: string
}

export interface LabelCodeRule {
  id: number
  code: string
  name: string
  objectType: LabelObjectType
  priority: number
  isEnabled: boolean
  updatedBy: string
  updatedAt: string
  rowVersion: string
  versions: LabelCodeRuleVersion[]
}

export interface LabelCodeRuleCatalog {
  catalogVersion: string
  generatedAt: string
  rules: LabelCodeRule[]
}

export interface LabelCodePreview {
  code: string
  length: number
  parsed: Record<string, string>
  symbologies: string[]
  specSha256: string
}

export interface LabelCodeCapabilities {
  schemaVersion: number
  symbologies: string[]
  segmentKinds: LabelSegmentKind[]
  fields: string[]
  encodings: string[]
  checksumAlgorithms: string[]
}

function catalogStorageKey(): string {
  const config = getRuntimeConfig()
  return `label-system.label-code-rule-catalog.v1.${config.environment}.${encodeURIComponent(config.apiBaseUrl)}`
}

export function createDefaultLabelCodeRuleSpec(): LabelCodeRuleSpec {
  return {
    schemaVersion: 1,
    symbologies: ['CODE128', 'QR'],
    caseMode: 'UPPER',
    segments: [
      { kind: 'LITERAL', value: 'LS' },
      { kind: 'SEPARATOR', value: '|' },
      {
        kind: 'FIELD',
        field: 'ERP_ACCOUNT_ID',
        maxLength: 20,
        encoding: 'NONE',
        validateCurrentAccount: true,
      },
      { kind: 'SEPARATOR', value: '|' },
      { kind: 'SEQUENCE', length: 8 },
      { kind: 'CHECKSUM', algorithm: 'SHA256_8' },
    ],
  }
}

export async function listLabelCodeRules(): Promise<LabelCodeRule[]> {
  return apiRequest<LabelCodeRule[]>('/api/v1/admin/label-code-rules')
}

export async function loadLabelCodeCapabilities(): Promise<LabelCodeCapabilities> {
  return apiRequest<LabelCodeCapabilities>('/api/v1/label-code-rules/capabilities')
}

export async function createLabelCodeRule(payload: {
  code: string
  name: string
  objectType: LabelObjectType
  priority: number
  spec: LabelCodeRuleSpec
}): Promise<LabelCodeRule> {
  return apiRequest<LabelCodeRule>('/api/v1/admin/label-code-rules', {
    method: 'POST',
    body: JSON.stringify(payload),
  })
}

export async function saveLabelCodeRuleDraft(
  rule: LabelCodeRule,
  payload: {
    name: string
    priority: number
    isEnabled: boolean
    spec: LabelCodeRuleSpec
    expectedVersionRowVersion: string | null
  },
): Promise<LabelCodeRule> {
  return apiRequest<LabelCodeRule>(`/api/v1/admin/label-code-rules/${rule.id}/draft`, {
    method: 'PUT',
    body: JSON.stringify({
      ...payload,
      expectedRuleRowVersion: rule.rowVersion,
    }),
  })
}

export async function activateLabelCodeRuleVersion(
  rule: LabelCodeRule,
  version: LabelCodeRuleVersion,
): Promise<LabelCodeRule> {
  return apiRequest<LabelCodeRule>(
    `/api/v1/admin/label-code-rules/${rule.id}/versions/${version.id}/activate`,
    {
      method: 'POST',
      body: JSON.stringify({
        expectedRuleRowVersion: rule.rowVersion,
        expectedVersionRowVersion: version.rowVersion,
      }),
    },
  )
}

export async function retireLabelCodeRuleVersion(
  rule: LabelCodeRule,
  version: LabelCodeRuleVersion,
): Promise<LabelCodeRule> {
  return apiRequest<LabelCodeRule>(
    `/api/v1/admin/label-code-rules/${rule.id}/versions/${version.id}/retire`,
    {
      method: 'POST',
      body: JSON.stringify({
        expectedRuleRowVersion: rule.rowVersion,
        expectedVersionRowVersion: version.rowVersion,
      }),
    },
  )
}

export async function previewLabelCodeRule(
  spec: LabelCodeRuleSpec,
  values: Record<string, string>,
  symbology?: string,
): Promise<LabelCodePreview> {
  return apiRequest<LabelCodePreview>('/api/v1/admin/label-code-rules/preview', {
    method: 'POST',
    body: JSON.stringify({ spec, values, symbology: symbology || null }),
  })
}

export async function refreshLabelCodeRuleCatalog(): Promise<LabelCodeRuleCatalog> {
  const catalog = await apiRequest<LabelCodeRuleCatalog>('/api/v1/label-code-rules/catalog')
  try {
    localStorage.setItem(catalogStorageKey(), JSON.stringify(catalog))
  } catch {
    // 离线目录缓存失败不影响在线规则管理。
  }
  return catalog
}

export function readCachedLabelCodeRuleCatalog(): LabelCodeRuleCatalog | null {
  try {
    const value = localStorage.getItem(catalogStorageKey())
    return value ? JSON.parse(value) as LabelCodeRuleCatalog : null
  } catch {
    return null
  }
}
