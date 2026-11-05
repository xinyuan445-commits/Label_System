export interface CountSourceLine {
  id: number
  lineNo: number
  materialCode: string
  materialName: string
  batchNo: string
  warehouseCode: string
  locationCode: string
  unitCode: string
  unitName?: string
  bookQuantity: number
  actualQuantity: number | null
  freeItems: string[]
  requiresAuxiliaryUnit: boolean
}

export interface CountSource {
  accountId: string
  databaseName: string
  document: {
    id: number
    documentNo: string
    documentDate: string | null
    warehouseCode: string
    inCategoryCode?: string | null
    inCategoryName?: string | null
    outCategoryCode?: string | null
    outCategoryName?: string | null
    countByLocation?: boolean | null
    warehousePositionManaged?: boolean | null
    status: string
    version: string
    lineCount: number
  }
  lines: CountSourceLine[]
  fingerprint: string
}

export interface CountScope { apiBaseUrl: string; environment: string; userId: string }
export interface CountTask {
  id: string
  scopeKey: string
  binding: string
  source: CountSource
  revision: number
  observationCount: number
  updatedAt: string
  validatedAt: string | null
  validationToken: string | null
}
export interface CountCheck {
  originalWarehouseCode?: string | null
  originalLocationCode?: string | null
  locationCode?: string | null
  code: string
  status: string
  message: string
  sourceLineId?: number | null
  materialName?: string | null
  balance?: number | null
  latestFlowId?: number | null
}
export interface CountObservation {
  taskId: string
  code: string
  codeKey: string
  quantity: string | null
  revision: number
  sequence: number
  check: CountCheck | null
}
export interface CountDraft { task: CountTask; observations: CountObservation[] }

export function rejectLegacyCountUpload(documentType: string, context?: { accountId: string; databaseName: string }): void {
  if (documentType.trim().toLowerCase() === 'inventory-count' &&
      (context?.accountId !== '888' || context?.databaseName !== 'UFDATA_888_2021')) {
    throw new Error('盘点上传仅开放888测试账套，请重新保存单据')
  }
}

export function countScopeKey(scope: CountScope): string {
  const api = new URL(scope.apiBaseUrl)
  if (!['http:', 'https:'].includes(api.protocol) || !scope.userId || !scope.environment) {
    throw new Error('盘点账号或地址无效')
  }
  return JSON.stringify([api.href.replace(/\/$/, ''), scope.environment, scope.userId])
}

export function countCodeKey(value: string): string {
  const code = value.trim()
  if (!code || code.length > 100 || /[\u0000-\u001f\u007f]/.test(code)) throw new Error('标签码无效')
  return code.toUpperCase()
}

export function countQuantity(value: string): string {
  const normalized = value.trim()
  if (!/^(?:0|[1-9][0-9]{0,11})(?:\.[0-9]{1,6})?$/.test(normalized)) {
    throw new Error('实盘数量须为非负数，最多6位小数')
  }
  return normalized
}

// Scaled decimal text avoids both floating-point rounding and BigInt requirements
// on older PDA WebViews. Each value is still limited to decimal(18,6).
export function sumCountQuantities(values: Array<string | null>): string {
  let units = '0'
  for (const value of values) {
    if (value === null) continue
    const [integer = '0', fraction = ''] = countQuantity(value).split('.')
    const addend = integer + fraction.padEnd(6, '0')
    let sum = '', carry = 0, left = units.length - 1, right = addend.length - 1
    while (left >= 0 || right >= 0 || carry) {
      const digit = Number(units[left--] ?? 0) + Number(addend[right--] ?? 0) + carry
      sum = String(digit % 10) + sum; carry = Math.floor(digit / 10)
    }
    units = sum.replace(/^0+(?=\d)/, '')
  }
  const fixed = units.padStart(7, '0'), remainder = fixed.slice(-6).replace(/0+$/, '')
  return `${fixed.slice(0, -6)}${remainder ? `.${remainder}` : ''}`
}

export function assertCountSource(source: CountSource): void {
  if (!source?.accountId || !source.databaseName || !source.document ||
      !Number.isSafeInteger(source.document.id) || source.document.id <= 0 ||
      source.document.status !== 'pending' || !source.document.version ||
      !/^[A-F0-9]{64}$/i.test(source.fingerprint) || !Array.isArray(source.lines) ||
      !source.lines.length || source.lines.length > 5000 || source.lines.length !== source.document.lineCount ||
      source.lines.some(line => !line || !Number.isSafeInteger(line.id) || line.id <= 0 ||
        !line.materialCode || !line.warehouseCode || !Number.isFinite(line.bookQuantity)) ||
      new Set(source.lines.map(line => line.id)).size !== source.lines.length) {
    throw new Error('原盘点单不完整，请重新下载')
  }
}
