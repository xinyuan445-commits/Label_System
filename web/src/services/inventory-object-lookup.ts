import { getRuntimeConfig } from '@/services/runtime-config'

export type InventoryObjectType = 'PRODUCT_LABEL' | 'PALLET'

export interface InventoryObjectLine {
  labelCode: string
  objectType: InventoryObjectType
  materialCode: string
  materialName: string
  batchNo: string
  warehouseCode: string
  locationCode: string
  availableQty: number
  palletCode?: string
}

interface ApiEnvelope<T> {
  success: boolean
  message?: string
  data?: T
}

interface InventoryLookupResult {
  lines: InventoryObjectLine[]
}

const mockLabels: Record<string, InventoryObjectLine[]> = {
  'LBL-M1001-20-A': [mockLine('LBL-M1001-20-A', 'M1001', '材料一', 20, 'B01')],
  'LBL-M1001-30-B': [mockLine('LBL-M1001-30-B', 'M1001', '材料一', 30, 'B01')],
  'LBL-M1002-15-A': [mockLine('LBL-M1002-15-A', 'M1002', '材料二', 15, '')],
  'LBL-M1002-25-B': [mockLine('LBL-M1002-25-B', 'M1002', '材料二', 25, '')],
  'LBL-M1003-10-A': [mockLine('LBL-M1003-10-A', 'M1003', '材料三', 10, '')],
  'LBL-X999-10-A': [mockLine('LBL-X999-10-A', 'X999', '计划外材料', 10, '')],
  'PALLET-001': [
    { ...mockLine('LBL-M1002-P01', 'M1002', '材料二', 20, ''), objectType: 'PRODUCT_LABEL', palletCode: 'PALLET-001' },
    { ...mockLine('LBL-M1003-P01', 'M1003', '材料三', 10, ''), objectType: 'PRODUCT_LABEL', palletCode: 'PALLET-001' },
  ],
}

function mockLine(
  labelCode: string,
  materialCode: string,
  materialName: string,
  availableQty: number,
  batchNo: string,
): InventoryObjectLine {
  return {
    labelCode, objectType: 'PRODUCT_LABEL', materialCode, materialName,
    batchNo, warehouseCode: 'WH1', locationCode: 'L1', availableQty,
  }
}

function parseInlineMock(code: string): InventoryObjectLine[] | null {
  const parts = code.split('|').map((part) => part.trim())
  if (parts.length < 3 || parts[0]?.toUpperCase() !== 'MOCK') return null
  const quantity = Number(parts[2])
  if (!parts[1] || !Number.isFinite(quantity) || quantity <= 0) return null
  return [{
    labelCode: code,
    objectType: 'PRODUCT_LABEL',
    materialCode: parts[1],
    materialName: parts[1],
    availableQty: quantity,
    warehouseCode: parts[3] || 'WH1',
    locationCode: parts[4] || 'L1',
    batchNo: parts[5] || '',
  }]
}

export async function lookupInventoryObject(barcode: string): Promise<InventoryObjectLine[]> {
  const config = getRuntimeConfig()
  if (config.environment === 'development' && config.enableMockScanner) {
    const mock = mockLabels[barcode.toUpperCase()] ?? parseInlineMock(barcode)
    if (!mock) throw new Error('未找到标签')
    return mock.map((line) => ({ ...line }))
  }

  const params = new URLSearchParams({ code: barcode })
  const response = await fetch(
    `${config.apiBaseUrl}/api/v1/inventory-objects/lookup?${params.toString()}`,
    { cache: 'no-store' },
  )
  const envelope = (await response.json()) as ApiEnvelope<InventoryLookupResult>
  if (!response.ok || !envelope.success || !envelope.data?.lines?.length) {
    throw new Error(envelope.message || '未找到标签')
  }
  return envelope.data.lines
}
