import type { SourceDocumentLine } from './source-document-selection'

export const isSalesDocument = (type: string) => type === 'delivery-note' || type === 'sales-return'

export function requireSalesSourceSelection(type: string, lines: SourceDocumentLine[]): void {
  if (!isSalesDocument(type)) return
  const identities = new Set(lines.map(line => JSON.stringify([
    line.partnerCode, line.salesTypeCode, line.currencyName, line.exchangeRate,
  ])))
  if (!lines.length || lines.some(line => !line.partnerCode || !line.currencyName || !line.exchangeRate)) {
    throw new Error('原单资料不完整，请重新查询')
  }
  if (identities.size !== 1) throw new Error('请选择同客户、销售类型和币种的原单')
}

export function salesSourceProgress(sources: SourceDocumentLine[], scans: Array<{ materialCode?: string; requestedQty?: number }>) {
  const quantities = new Map<string, number>()
  for (const line of scans) {
    const code = (line.materialCode ?? '').trim().toUpperCase()
    quantities.set(code, (quantities.get(code) ?? 0) + Math.max(Number(line.requestedQty) || 0, 0))
  }
  return new Map(sources.map(source => {
    const code = source.productCode.trim().toUpperCase()
    const available = Math.max(source.remainingQuantity ?? source.orderQuantity, 0)
    const picked = Math.min(quantities.get(code) ?? 0, available)
    quantities.set(code, Math.max((quantities.get(code) ?? 0) - picked, 0))
    return [source.sourceLineId, { picked, pending: Math.max(available - picked, 0), complete: picked >= available }]
  }))
}
