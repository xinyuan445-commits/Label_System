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
