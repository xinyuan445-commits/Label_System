import {
  getBusinessDocumentConfig,
  type BusinessDocumentType,
} from '@/services/business-document-config'

export type DocumentFieldArea = 'header' | 'body'
export type DocumentFieldValueType = 'text' | 'number' | 'date'

export interface DocumentFieldDefinition {
  key: string
  label: string
  area: DocumentFieldArea
  valueType: DocumentFieldValueType
  defaultVisible: boolean
  defaultRequired: boolean
}

export interface DocumentFieldGroup {
  key: string
  label: string
  area: DocumentFieldArea
  valueType: DocumentFieldValueType
  fields: DocumentFieldDefinition[]
}

export interface DocumentFieldState {
  visible: boolean
  required: boolean
}

export type DocumentFieldSettings = Record<string, DocumentFieldState>

function field(
  key: string,
  label: string,
  area: DocumentFieldArea,
  valueType: DocumentFieldValueType,
  defaultVisible = false,
  defaultRequired = false,
): DocumentFieldDefinition {
  return { key, label, area, valueType, defaultVisible, defaultRequired }
}

function customFields(
  area: DocumentFieldArea,
  valueType: DocumentFieldValueType,
  numbers: number[],
): DocumentFieldDefinition[] {
  return numbers.map((number) => field(
    `${area === 'header' ? 'header' : 'body'}.custom${number}`,
    `${area === 'header' ? '表头' : '表体'}自定义项${number}`,
    area,
    valueType,
  ))
}

function standardHeaderFields(documentType: BusinessDocumentType): DocumentFieldDefinition[] {
  const config = getBusinessDocumentConfig(documentType)
  const fields = [
    field('header.documentDate', '日期', 'header', 'date', true, true),
  ]
  if (config.partnerLabel) {
    fields.push(field(
      'header.partnerCode', config.partnerLabel, 'header', 'text', true, config.partnerRequired,
    ))
  }
  if (config.warehouseLabel && !['transfer-order', 'location-adjustment'].includes(documentType)) {
    fields.push(field(
      'header.warehouseCode', config.warehouseLabel, 'header', 'text', true, config.warehouseRequired,
    ))
  }
  if (config.targetWarehouseLabel && documentType !== 'transfer-order') {
    fields.push(field(
      'header.targetWarehouseCode', config.targetWarehouseLabel, 'header', 'text', true,
      config.targetWarehouseRequired,
    ))
  }
  if (config.sourceLocationLabel && documentType !== 'location-adjustment') {
    fields.push(field(
      'header.sourceLocationCode', config.sourceLocationLabel, 'header', 'text', true,
      config.sourceLocationRequired,
    ))
  }
  if (config.targetLocationLabel && !['transfer-order', 'location-adjustment'].includes(documentType)) {
    fields.push(field(
      'header.targetLocationCode', config.targetLocationLabel, 'header', 'text', true,
      config.targetLocationRequired,
    ))
  }
  fields.push(field(
    'header.departmentCode', documentType === 'transfer-order' ? '调出部门' : '部门', 'header', 'text',
    config.departmentRequired, config.departmentRequired,
  ))
  if (config.categoryLabel) {
    fields.push(field(
      'header.categoryCode', config.categoryLabel, 'header', 'text', true, config.categoryRequired,
    ))
  }
  if (config.productionBatch) {
    fields.push(field('header.productionBatch', '生产批号', 'header', 'text', true))
  }
  if (['purchase-arrival', 'outsourcing-arrival'].includes(documentType)) {
    fields.push(
      field('header.purchaseTypeCode', '采购类型', 'header', 'text', true, true),
      field('header.paymentCode', '付款条件', 'header', 'text'),
      field('header.currencyCode', '币种', 'header', 'text', true, true),
      field('header.exchangeRate', '汇率', 'header', 'number', true, true),
      field('header.taxRate', '税率', 'header', 'number'),
    )
  }
  if (documentType === 'purchase-inbound') {
    fields.push(
      field('header.purchaseTypeCode', '采购类型', 'header', 'text', true, true),
      field('header.currencyCode', '币种', 'header', 'text', true, true),
      field('header.exchangeRate', '汇率', 'header', 'number', true, true),
      field('header.taxRate', '税率', 'header', 'number'),
    )
  }
  if (documentType === 'delivery-note') {
    fields.push(
      field('header.salesTypeCode', '销售类型', 'header', 'text', true, true),
      field('header.paymentCode', '收款条件', 'header', 'text'),
      field('header.currencyCode', '币种', 'header', 'text', true, true),
      field('header.exchangeRate', '汇率', 'header', 'number', true, true),
      field('header.taxRate', '税率', 'header', 'number'),
      field('header.deliveryAddress', '送货地址', 'header', 'text'),
      field('header.contactName', '联系人', 'header', 'text'),
      field('header.contactPhone', '联系电话', 'header', 'text'),
    )
  }
  if (documentType === 'sales-outbound') {
    fields.push(
      field('header.salesTypeCode', '销售类型', 'header', 'text', true, true),
      field('header.deliveryUnit', '收货单位', 'header', 'text'),
      field('header.deliveryAddress', '收货地址', 'header', 'text'),
      field('header.contactName', '联系人', 'header', 'text'),
      field('header.contactPhone', '联系电话', 'header', 'text'),
    )
  }
  if (documentType === 'transfer-order') {
    fields.push(
      field('header.targetDepartmentCode', '调入部门', 'header', 'text', true),
      field('header.outCategoryCode', '调出类别', 'header', 'text', true, true),
      field('header.inCategoryCode', '调入类别', 'header', 'text', true, true),
    )
  }
  if (documentType === 'inventory-count') {
    fields.push(
      field('header.outCategoryCode', '盘亏类别', 'header', 'text', true, true),
      field('header.inCategoryCode', '盘盈类别', 'header', 'text', true, true),
      field('header.inventoryBatch', '盘点批号', 'header', 'text'),
      field('header.locationCode', '货位', 'header', 'text'),
      field('header.countPeriod', '盘点期间', 'header', 'text'),
      field('header.countType', '盘点类型', 'header', 'text'),
    )
  }
  fields.push(field('header.remark', '备注', 'header', 'text', true))
  return fields
}

function longCustomCount(documentType: BusinessDocumentType): number {
  if (['purchase-arrival', 'outsourcing-arrival', 'purchase-inbound', 'delivery-note']
    .includes(documentType)) return 3
  if (['material-outbound', 'sales-outbound', 'transfer-order'].includes(documentType)) return 2
  return 0
}

function headerCustomGroups(documentType: BusinessDocumentType): DocumentFieldGroup[] {
  const longFields = Array.from({ length: longCustomCount(documentType) }, (_, index) => field(
    `header.longCustom${index + 1}`,
    `表头长文本自定义项${index + 1}`,
    'header',
    'text',
  ))
  return [
    {
      key: 'header-text-custom', label: '文本字段', area: 'header', valueType: 'text',
      fields: [
        ...customFields('header', 'text', [1, 2, 3, 8, 9, 10, 11, 12, 13, 14]),
        ...longFields,
      ],
    },
    {
      key: 'header-number-custom', label: '数值字段', area: 'header', valueType: 'number',
      fields: customFields('header', 'number', [5, 7, 15, 16]),
    },
    {
      key: 'header-date-custom', label: '日期字段', area: 'header', valueType: 'date',
      fields: customFields('header', 'date', [4, 6]),
    },
  ]
}

function bodyBasicGroup(documentType: BusinessDocumentType): DocumentFieldGroup {
  const config = getBusinessDocumentConfig(documentType)
  const fields = [
    field('body.labelCode', '标签码', 'body', 'text', true, true),
    field('body.objectType', '对象类型', 'body', 'text', true, true),
    field('body.materialCode', '料号', 'body', 'text', true, true),
    field('body.materialName', '物料名称', 'body', 'text'),
    field('body.batchNo', '批号', 'body', 'text', true),
    field('body.unitCode', '计量单位', 'body', 'text', true, true),
    field('body.warehouseCode', documentType === 'transfer-order' ? '调出仓库' : '仓库', 'body', 'text', true, true),
    field(
      'body.locationCode', documentType === 'location-adjustment' ? '原货位' : '货位',
      'body', 'text', true, documentType === 'location-adjustment',
    ),
  ]
  if (config.targetWarehouseLabel) {
    fields.push(field(
      'body.targetWarehouseCode', config.targetWarehouseLabel, 'body', 'text',
      documentType === 'transfer-order', documentType === 'transfer-order',
    ))
  }
  if (config.targetLocationLabel) {
    fields.push(field(
      'body.targetLocationCode', config.targetLocationLabel, 'body', 'text',
      ['transfer-order', 'location-adjustment'].includes(documentType),
      documentType === 'location-adjustment',
    ))
  }
  fields.push(
    field('body.availableQty', '可用量', 'body', 'number', true, true),
    field('body.quantity', config.quantityLabel, 'body', 'number', true, true),
    field('body.projectClassCode', '项目大类编码', 'body', 'text'),
    field('body.projectCode', '项目编码', 'body', 'text'),
    field('body.remark', '行备注', 'body', 'text'),
  )
  return { key: 'body-basic', label: '基础字段', area: 'body', valueType: 'text', fields }
}

function bodyCustomGroups(documentType: BusinessDocumentType): DocumentFieldGroup[] {
  const textNumbers = [22, 23, 24, 25, 28, 29, 30, 31, 32, 33]
  const dateNumbers = documentType === 'delivery-note' ? [] : [36, 37]
  if (documentType === 'delivery-note') textNumbers.push(36, 37)
  const groups: DocumentFieldGroup[] = [
    {
      key: 'body-text-custom', label: '文本字段', area: 'body', valueType: 'text',
      fields: customFields('body', 'text', textNumbers),
    },
    {
      key: 'body-number-custom', label: '数值字段', area: 'body', valueType: 'number',
      fields: customFields('body', 'number', [26, 27, 34, 35]),
    },
  ]
  if (dateNumbers.length) {
    groups.push({
      key: 'body-date-custom', label: '日期字段', area: 'body', valueType: 'date',
      fields: customFields('body', 'date', dateNumbers),
    })
  }
  return groups
}

export function getBusinessDocumentFieldGroups(
  documentType: BusinessDocumentType,
): DocumentFieldGroup[] {
  const groups: DocumentFieldGroup[] = [{
    key: 'header-basic',
    label: '基础字段',
    area: 'header',
    valueType: 'text',
    fields: standardHeaderFields(documentType),
  }]
  groups.push(...headerCustomGroups(documentType))
  groups.push(bodyBasicGroup(documentType))
  groups.push(...bodyCustomGroups(documentType))
  return groups
}

export function getBusinessDocumentFields(
  documentType: BusinessDocumentType,
): DocumentFieldDefinition[] {
  return getBusinessDocumentFieldGroups(documentType).flatMap((group) => group.fields)
}

export function createDefaultDocumentFieldSettings(
  documentType: BusinessDocumentType,
): DocumentFieldSettings {
  return Object.fromEntries(
    getBusinessDocumentFieldGroups(documentType).flatMap((group) => group.fields).map((item) => [
      item.key,
      { visible: item.defaultVisible || item.defaultRequired, required: item.defaultRequired },
    ]),
  )
}

export function mergeDocumentFieldSettings(
  documentType: BusinessDocumentType,
  stored?: Partial<DocumentFieldSettings>,
): DocumentFieldSettings {
  const result = createDefaultDocumentFieldSettings(documentType)
  if (!stored || typeof stored !== 'object') return result
  for (const [key, defaults] of Object.entries(result)) {
    const value = stored[key]
    if (!value || typeof value !== 'object') continue
    const required = typeof value.required === 'boolean' ? value.required : defaults.required
    const visible = required || (typeof value.visible === 'boolean' ? value.visible : defaults.visible)
    result[key] = { visible, required }
  }
  return result
}
