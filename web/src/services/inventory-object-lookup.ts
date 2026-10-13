import { apiRequest } from '@/api/client'

export type InventoryObjectType = 'PRODUCT_LABEL' | 'PALLET'

export interface InventoryObjectLine {
  labelCode: string
  objectType: InventoryObjectType
  materialCode: string
  materialName: string
  batchNo: string
  unitCode: string
  unitName?: string
  warehouseCode: string
  warehouseName?: string
  locationCode: string
  locationName?: string
  availableQty: number
  palletCode?: string
  status: string
  rowVersion: string
}

export interface InventoryLookupResult {
  lines: InventoryObjectLine[]
  pallet: InventoryObjectLine | null
}

export async function lookupInventoryObject(
  barcode: string,
  functionCode: string,
): Promise<InventoryLookupResult> {
  const params = new URLSearchParams({ code: barcode })
  return apiRequest<InventoryLookupResult>(
    `/api/v1/inventory-objects/lookup?${params.toString()}`,
    { headers: { 'X-Function-Code': functionCode } },
  )
}
