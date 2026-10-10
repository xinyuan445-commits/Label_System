import type { BusinessDocumentType } from '@/services/business-document-config'

export type ApprovalDocumentStatus = 'pending' | 'audited'

export interface ApprovalDocumentLine {
  id: string
  materialCode: string
  materialName: string
  batchNo: string
  warehouseCode: string
  locationCode: string
  quantity: number
}

export interface ApprovalLabelImpact {
  labelCode: string
  quantity: number
  warehouseCode: string
  locationCode: string
}

export interface ApprovalDocument {
  id: string
  documentType: BusinessDocumentType
  documentNo: string
  documentDate: string
  partnerLabel: string
  partnerName: string
  warehouseCode: string
  createdBy: string
  status: ApprovalDocumentStatus
  auditedBy: string
  auditedAt: string
  version: string
  lines: ApprovalDocumentLine[]
  labelImpacts: ApprovalLabelImpact[]
}

interface StatusOverride {
  status: ApprovalDocumentStatus
  auditedBy: string
  auditedAt: string
}

const storageKey = 'label-system:approval-document-mock-status:v1'

const documentMetadata: Record<
  BusinessDocumentType,
  { prefix: string; partnerLabel: string; partners: [string, string, string]; warehouse: string }
> = {
  'purchase-arrival': {
    prefix: 'DH',
    partnerLabel: '供应商',
    partners: ['华东金属材料有限公司', '新源包装有限公司', '宏达工业用品有限公司'],
    warehouse: 'WH01',
  },
  'outsourcing-arrival': {
    prefix: 'WWDH',
    partnerLabel: '供应商',
    partners: ['恒鑫加工厂', '嘉盛制造有限公司', '联达表面处理厂'],
    warehouse: 'WH01',
  },
  'purchase-inbound': {
    prefix: 'CGRK',
    partnerLabel: '供应商',
    partners: ['华东金属材料有限公司', '新源包装有限公司', '宏达工业用品有限公司'],
    warehouse: 'WH01',
  },
  'delivery-note': {
    prefix: 'FH',
    partnerLabel: '客户',
    partners: ['启航设备有限公司', '东城机电有限公司', '海川自动化有限公司'],
    warehouse: 'WH02',
  },
  'sales-outbound': {
    prefix: 'XSCK',
    partnerLabel: '客户',
    partners: ['启航设备有限公司', '东城机电有限公司', '海川自动化有限公司'],
    warehouse: 'WH02',
  },
  'material-outbound': {
    prefix: 'CLCK',
    partnerLabel: '生产订单',
    partners: ['MO20261011001', 'MO20261011002', 'WO20261011003'],
    warehouse: 'WH01',
  },
  'transfer-order': {
    prefix: 'DB',
    partnerLabel: '调入仓库',
    partners: ['WH02', 'WH03', 'WH02'],
    warehouse: 'WH01',
  },
  'location-adjustment': {
    prefix: 'HWTZ',
    partnerLabel: '目标货位',
    partners: ['B-01-02', 'B-02-05', 'A-03-01'],
    warehouse: 'WH01',
  },
  'inventory-count': {
    prefix: 'PD',
    partnerLabel: '盘点范围',
    partners: ['WH01 / A区', 'WH02 / 成品区', 'WH01 / B区'],
    warehouse: 'WH01',
  },
}

function beijingDate(offsetDays: number): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(new Date())
  const value = Object.fromEntries(parts.map((part) => [part.type, part.value]))
  const date = new Date(Date.UTC(Number(value.year), Number(value.month) - 1, Number(value.day)))
  date.setUTCDate(date.getUTCDate() + offsetDays)
  return date.toISOString().slice(0, 10)
}

function beijingTimestamp(): string {
  const formatter = new Intl.DateTimeFormat('sv-SE', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })
  return `${formatter.format(new Date()).replace(' ', 'T')}+08:00`
}

function readOverrides(): Record<string, StatusOverride> {
  const raw = localStorage.getItem(storageKey)
  if (!raw) return {}
  try {
    return JSON.parse(raw) as Record<string, StatusOverride>
  } catch {
    return {}
  }
}

function createLines(seed: number, warehouseCode: string): ApprovalDocumentLine[] {
  return [
    {
      id: `${seed}-1`,
      materialCode: `MAT-${String(seed * 10 + 1).padStart(4, '0')}`,
      materialName: seed % 2 ? '铝合金支架' : '不锈钢连接件',
      batchNo: `B202610${String(seed + 10).padStart(2, '0')}`,
      warehouseCode,
      locationCode: seed % 2 ? 'A-01-03' : 'A-02-06',
      quantity: seed % 2 ? 60 : 48,
    },
    {
      id: `${seed}-2`,
      materialCode: `MAT-${String(seed * 10 + 2).padStart(4, '0')}`,
      materialName: seed % 2 ? '缓冲垫片' : '包装隔板',
      batchNo: `B202610${String(seed + 11).padStart(2, '0')}`,
      warehouseCode,
      locationCode: seed % 2 ? 'A-01-05' : 'A-03-02',
      quantity: seed % 2 ? 40 : 72,
    },
  ]
}

function createBaseDocuments(documentType: BusinessDocumentType): ApprovalDocument[] {
  const metadata = documentMetadata[documentType]
  return [0, 1, 2].map((index) => {
    const lines = createLines(index + 1, metadata.warehouse)
    const status: ApprovalDocumentStatus = index === 2 ? 'audited' : 'pending'
    const documentNo = `${metadata.prefix}${beijingDate(-index).replaceAll('-', '')}${String(index + 1).padStart(3, '0')}`
    return {
      id: `${documentType}-${index + 1}`,
      documentType,
      documentNo,
      documentDate: beijingDate(-index),
      partnerLabel: metadata.partnerLabel,
      partnerName: metadata.partners[index],
      warehouseCode: metadata.warehouse,
      createdBy: index === 1 ? '张敏' : '李伟',
      status,
      auditedBy: status === 'audited' ? 'admin' : '',
      auditedAt: status === 'audited' ? `${beijingDate(-index)}T10:26:18+08:00` : '',
      version: `mock-${index + 1}`,
      lines,
      labelImpacts:
        index === 1
          ? []
          : lines.map((line, lineIndex) => ({
              labelCode: `LBL-${documentNo}-${lineIndex + 1}`,
              quantity: line.quantity,
              warehouseCode: line.warehouseCode,
              locationCode: line.locationCode,
            })),
    }
  })
}

export function loadApprovalDocuments(documentType: BusinessDocumentType): ApprovalDocument[] {
  const overrides = readOverrides()
  return createBaseDocuments(documentType).map((document) => ({
    ...document,
    ...(overrides[document.id] ?? {}),
  }))
}

export function saveApprovalDocumentStatus(
  documentId: string,
  status: ApprovalDocumentStatus,
  operator = 'admin',
): void {
  const overrides = readOverrides()
  overrides[documentId] = {
    status,
    auditedBy: status === 'audited' ? operator : '',
    auditedAt: status === 'audited' ? beijingTimestamp() : '',
  }
  localStorage.setItem(storageKey, JSON.stringify(overrides))
}
