import { apiRequest } from '@/api/client'

export interface RdStyleOption {
  code: string
  name: string
  grade: number
  direction: 'IN' | 'OUT'
  isTerminal: boolean
  oppositeCode: string | null
  isRetail: boolean
  isActive: boolean
  updatedAt: string
}

export interface WarehousePositionOption {
  warehouseCode: string
  warehouseName: string
  positionCode: string | null
  positionName: string | null
  updatedAt: string
}

export interface DepartmentOption {
  code: string
  name: string
  updatedAt: string
}

export interface DocumentTypeOption {
  code: string
  name: string
  rdStyleCode: string | null
  isDefault: boolean
  updatedAt: string
}

export interface CurrencyOption {
  code: string
  name: string
  isLocal: boolean
  usesExchangeRate: boolean
  decimalDigits: number
  latestRate: number | null
  rateYear: number | null
  ratePeriod: number | null
  rateDate: string | null
  updatedAt: string
}

export interface MaterialOption {
  code: string
  name: string
  specification: string
  unitCode: string
  requiresBatch: boolean
}

export async function fetchRdStyles(
  direction?: 'IN' | 'OUT',
  functionCode?: string,
): Promise<RdStyleOption[]> {
  const query = new URLSearchParams({ terminalOnly: 'true' })
  if (direction) query.set('direction', direction)
  return apiRequest<RdStyleOption[]>(`/api/v1/reference-data/rd-styles?${query}`, {
    headers: functionCode ? { 'X-Function-Code': functionCode } : undefined,
  })
}

export async function fetchWarehousePositions(): Promise<WarehousePositionOption[]> {
  return apiRequest<WarehousePositionOption[]>('/api/v1/reference-data/warehouse-positions')
}

export async function fetchDepartments(): Promise<DepartmentOption[]> {
  return apiRequest<DepartmentOption[]>('/api/v1/reference-data/departments')
}

export async function fetchPurchaseTypes(): Promise<DocumentTypeOption[]> {
  return apiRequest<DocumentTypeOption[]>('/api/v1/reference-data/purchase-types')
}

export async function fetchSalesTypes(): Promise<DocumentTypeOption[]> {
  return apiRequest<DocumentTypeOption[]>('/api/v1/reference-data/sales-types')
}

export async function fetchCurrencies(): Promise<CurrencyOption[]> {
  return apiRequest<CurrencyOption[]>('/api/v1/reference-data/currencies')
}

export async function searchMaterials(keyword: string): Promise<MaterialOption[]> {
  const query = new URLSearchParams({ keyword: keyword.trim(), limit: '50' })
  return apiRequest<MaterialOption[]>(`/api/v1/reference-data/materials?${query.toString()}`)
}
