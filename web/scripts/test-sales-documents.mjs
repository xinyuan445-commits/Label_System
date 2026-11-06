import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'

async function module(file) {
  const code = ts.transpileModule(await readFile(new URL(file, import.meta.url), 'utf8'), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
  }).outputText
  return import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'))
}
const sales = await module('../src/services/sales-documents.ts')
const config = await module('../src/services/business-document-config.ts')
const source = (id = 1) => ({ sourceLineId: id, productCode: 'MAT', orderQuantity: 5, remainingQuantity: 5,
  partnerCode: 'C1', salesTypeCode: '01', currencyName: '人民币', exchangeRate: 1 })

test('发货与退货是独立固定方向入口，没有红蓝字选择', () => {
  const blue = config.getBusinessDocumentConfig('delivery-note'), red = config.getBusinessDocumentConfig('sales-return')
  assert.equal(blue.fixedDirection, 'BLUE'); assert.equal(red.fixedDirection, 'RED')
  assert.ok(!blue.supportsRedBlue && !red.supportsRedBlue)
  assert.deepEqual(blue.sourceTypes.map(s => s.value), ['sales-order'])
  assert.deepEqual(red.sourceTypes.map(s => s.value), ['sales-delivery-return', 'sales-order-return'])
})
test('原单客户或币种不一致、旧快照缺资料时不静默合并', () => {
  sales.requireSalesSourceSelection('delivery-note', [source(), source(2)])
  assert.throws(() => sales.requireSalesSourceSelection('sales-return', [source(), { ...source(2), partnerCode: 'C2' }]))
  assert.throws(() => sales.requireSalesSourceSelection('sales-return', [source(), { ...source(2), currencyName: '美元' }]))
  assert.throws(() => sales.requireSalesSourceSelection('delivery-note', [{ ...source(), exchangeRate: null }]))
})
test('同料多原单行逐行分配本单数量，已拣足与ERP已发数量分开', () => {
  const progress = sales.salesSourceProgress([source(), source(2)], [{ materialCode: 'MAT', requestedQty: 7 }])
  assert.deepEqual(progress.get(1), { picked: 5, pending: 0, complete: true })
  assert.deepEqual(progress.get(2), { picked: 2, pending: 3, complete: false })
})
