export const businessDocumentTypes = [
  'purchase-arrival',
  'outsourcing-arrival',
  'purchase-inbound',
  'delivery-note',
  'sales-outbound',
  'material-outbound',
  'transfer-order',
  'location-adjustment',
  'inventory-count',
] as const

export type BusinessDocumentType = (typeof businessDocumentTypes)[number]

export interface SourceTypeOption {
  value: string
  label: string
}

export interface BusinessDocumentConfig {
  title: string
  draftKey: string
  sourceTypes: SourceTypeOption[]
  partnerLabel?: string
  partnerRequired?: boolean
  warehouseLabel?: string
  warehouseRequired?: boolean
  targetWarehouseLabel?: string
  targetWarehouseRequired?: boolean
  sourceLocationLabel?: string
  sourceLocationRequired?: boolean
  targetLocationLabel?: string
  targetLocationRequired?: boolean
  departmentRequired: boolean
  categoryLabel?: string
  categoryRequired?: boolean
  categoryDirection?: 'IN' | 'OUT'
  productionBatch: boolean
  detailTitle: string
  quantityLabel: string
  supportsRedBlue?: boolean
}

const configs: Record<BusinessDocumentType, BusinessDocumentConfig> = {
  'purchase-arrival': {
    title: '采购到货',
    draftKey: 'label-system:draft:purchase-arrival',
    sourceTypes: [{ value: 'purchase-order', label: '采购订单' }],
    partnerLabel: '供应商',
    partnerRequired: true,
    departmentRequired: true,
    productionBatch: false,
    detailTitle: '到货明细',
    quantityLabel: '到货数量',
    supportsRedBlue: true,
  },
  'outsourcing-arrival': {
    title: '委外到货',
    draftKey: 'label-system:draft:outsourcing-arrival',
    sourceTypes: [{ value: 'outsourcing-order', label: '委外订单' }],
    partnerLabel: '供应商',
    partnerRequired: true,
    departmentRequired: true,
    productionBatch: false,
    detailTitle: '到货明细',
    quantityLabel: '到货数量',
    supportsRedBlue: true,
  },
  'purchase-inbound': {
    title: '采购入库',
    draftKey: 'label-system:draft:purchase-inbound',
    sourceTypes: [
      { value: 'purchase-arrival', label: '采购到货单' },
      { value: 'purchase-order', label: '采购订单' },
    ],
    partnerLabel: '供应商',
    partnerRequired: true,
    warehouseLabel: '仓库',
    warehouseRequired: true,
    departmentRequired: true,
    categoryLabel: '入库类别',
    categoryRequired: true,
    categoryDirection: 'IN',
    productionBatch: false,
    detailTitle: '入库明细',
    quantityLabel: '入库数量',
    supportsRedBlue: true,
  },
  'delivery-note': {
    title: '发货单',
    draftKey: 'label-system:draft:delivery-note',
    sourceTypes: [{ value: 'sales-order', label: '销售订单' }],
    partnerLabel: '客户',
    partnerRequired: true,
    warehouseLabel: '仓库',
    warehouseRequired: true,
    departmentRequired: true,
    productionBatch: false,
    detailTitle: '发货明细',
    quantityLabel: '发货数量',
    supportsRedBlue: true,
  },
  'sales-outbound': {
    title: '销售出库单',
    draftKey: 'label-system:draft:sales-outbound',
    sourceTypes: [
      { value: 'sales-delivery', label: '发货单' },
      { value: 'sales-order', label: '销售订单' },
    ],
    partnerLabel: '客户',
    partnerRequired: true,
    warehouseLabel: '仓库',
    warehouseRequired: true,
    departmentRequired: true,
    categoryLabel: '出库类别',
    categoryRequired: true,
    categoryDirection: 'OUT',
    productionBatch: false,
    detailTitle: '出库明细',
    quantityLabel: '出库数量',
    supportsRedBlue: true,
  },
  'material-outbound': {
    title: '材料出库单',
    draftKey: 'label-system:draft:material-outbound',
    sourceTypes: [
      { value: 'production-order', label: '生产订单' },
    ],
    departmentRequired: true,
    categoryLabel: '出库类别',
    categoryRequired: true,
    categoryDirection: 'OUT',
    productionBatch: true,
    detailTitle: '出库明细',
    quantityLabel: '出库数量',
    supportsRedBlue: true,
  },
  'transfer-order': {
    title: '调拨单',
    draftKey: 'label-system:draft:transfer-order',
    sourceTypes: [],
    warehouseLabel: '调出仓库',
    warehouseRequired: true,
    targetWarehouseLabel: '调入仓库',
    targetWarehouseRequired: true,
    departmentRequired: true,
    productionBatch: false,
    detailTitle: '调拨明细',
    quantityLabel: '调拨数量',
  },
  'location-adjustment': {
    title: '货位调整',
    draftKey: 'label-system:draft:location-adjustment',
    sourceTypes: [],
    warehouseLabel: '仓库',
    warehouseRequired: true,
    sourceLocationLabel: '原货位',
    targetLocationLabel: '目标货位',
    targetLocationRequired: true,
    departmentRequired: false,
    productionBatch: false,
    detailTitle: '调整明细',
    quantityLabel: '调整数量',
  },
  'inventory-count': {
    title: '盘点单',
    draftKey: 'label-system:draft:inventory-count',
    sourceTypes: [],
    warehouseLabel: '仓库',
    warehouseRequired: true,
    departmentRequired: true,
    productionBatch: false,
    detailTitle: '盘点明细',
    quantityLabel: '盘点数量',
  },
}

export function isBusinessDocumentType(value: unknown): value is BusinessDocumentType {
  return businessDocumentTypes.includes(value as BusinessDocumentType)
}

export function getBusinessDocumentConfig(
  documentType: BusinessDocumentType,
): BusinessDocumentConfig {
  return configs[documentType]
}
