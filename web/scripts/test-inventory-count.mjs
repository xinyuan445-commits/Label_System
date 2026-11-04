import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import { IDBFactory, IDBObjectStore } from 'fake-indexeddb'
import ts from 'typescript'

async function compiled(file) {
  return ts.transpileModule(await readFile(new URL(file, import.meta.url), 'utf8'), {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
  }).outputText
}
const url = text => `data:text/javascript;base64,${Buffer.from(text).toString('base64')}`
const modelUrl = url(await compiled('../src/services/inventory-count-model.ts'))
const model = await import(modelUrl)
const storeCode = (await compiled('../src/services/inventory-count-store.ts'))
  .replace("from './inventory-count-model'", `from '${modelUrl}'`)
const { InventoryCountStore } = await import(url(storeCode))
const scope = { apiBaseUrl: 'http://count.example:9891', environment: 'development', userId: 'counter' }
const source = () => ({ accountId: '888', databaseName: 'UFDATA_888_2021',
  document: { id: 12, documentNo: 'COUNT12', documentDate: '2026-11-04', warehouseCode: 'W1',
    status: 'pending', version: '0x01', lineCount: 1 },
  lines: [{ id: 41, lineNo: 1, materialCode: 'M1', materialName: '物料', batchNo: 'B1',
    warehouseCode: 'W1', locationCode: 'L1', unitCode: 'U1', bookQuantity: 10,
    actualQuantity: null, freeItems: Array(10).fill(''), requiresAuxiliaryUnit: false }], fingerprint: 'A'.repeat(64) })

async function fixture(t) {
  const factory = new IDBFactory()
  const store = new InventoryCountStore(factory)
  await store.create(scope, source(), 'task1')
  t.after(() => store.close())
  return { store, factory }
}

test('数量明确非负且限6位小数；小数合计不发生浮点误差', () => {
  for (const value of ['', '-1', 'NaN', 'Infinity', '1e3', '1,000', '01', '0.0000001', '1000000000000']) {
    assert.throws(() => model.countQuantity(value))
  }
  assert.equal(model.countQuantity(' 0 '), '0')
  assert.equal(model.sumCountQuantities(['0.1', '0.2', null, '0.000001']), '0.300001')
  assert.equal(model.sumCountQuantities(['999999999999.999999', '0.000001']), '1000000000000')
})

test('原单缺行、重复行或未审核状态不符时不能落为可用任务', async t => {
  const { store } = await fixture(t)
  for (const mutate of [s => { s.lines = [] }, s => { s.lines.push(s.lines[0]); s.document.lineCount++ },
    s => { s.document.status = 'audited' }, s => { s.fingerprint = '' }]) {
    const input = source(); mutate(input)
    await assert.rejects(store.create(scope, input, 'bad-task'))
  }
  assert.equal((await store.list(scope)).length, 1)
})

test('历史盘点上传和执行在本机草稿或网络操作之前拒绝', async () => {
  const forbidden = url(`export const apiRequest = () => { throw new Error('unexpected API call') }`)
  const auth = url(`export const readStoredAuthSession = () => { throw new Error('unexpected session read') }`)
  const drafts = url(['beginOutboundDocumentUpload', 'markOutboundDocumentSubmitted',
    'markOutboundDocumentUploadFailed', 'updateOutboundDocumentGroup']
    .map(name => `export const ${name} = () => { throw new Error('unexpected draft mutation') }`).join(';'))
  const uploader = await compiled('../src/services/outbound-document-upload.ts')
  const { uploadOutboundDocument, executeOutboundDocument } = await import(url(uploader
    .replace("from '@/api/client'", `from '${forbidden}'`)
    .replace("from '@/services/auth-session'", `from '${auth}'`)
    .replace("from '@/services/outbound-document-drafts'", `from '${drafts}'`)
    .replace("from '@/services/inventory-count-model'", `from '${modelUrl}'`)))
  for (const action of [uploadOutboundDocument, executeOutboundDocument]) {
    await assert.rejects(action({ documentType: 'inventory-count', id: 'legacy' }, false), /上传ERP尚未开放/)
  }
  assert.doesNotThrow(() => model.rejectLegacyCountUpload('material-outbound'))
})

test('扫描只保存完整Code，不从打印数量带入实盘；零数需显式保存', async t => {
  const { store } = await fixture(t)
  await store.scan(scope, 'task1', '888|M1|B1|999.00|001')
  let draft = await store.read(scope, 'task1')
  assert.equal(draft.observations[0].quantity, null)
  await store.quantity(scope, 'task1', draft.observations[0].code, '0', 1)
  draft = await store.read(scope, 'task1')
  assert.equal(draft.observations[0].quantity, '0')
})

test('并发重复扫码拒绝，重启后仍拒绝且原数量不变', async t => {
  const { store, factory } = await fixture(t)
  const outcomes = await Promise.allSettled([store.scan(scope, 'task1', 'tag1'), store.scan(scope, 'task1', ' TAG1 ')])
  assert.equal(outcomes.filter(r => r.status === 'fulfilled').length, 1)
  assert.equal(outcomes.find(r => r.status === 'rejected').reason.code, 'DUPLICATE_SCAN')
  await store.quantity(scope, 'task1', 'tag1', '3.5', 1)
  await store.close()
  const reopened = new InventoryCountStore(factory)
  t.after(() => reopened.close())
  await assert.rejects(reopened.scan(scope, 'task1', 'tag1'), { code: 'DUPLICATE_SCAN' })
  const draft = await reopened.read(scope, 'task1')
  assert.equal(draft.task.observationCount, 1)
  assert.equal(draft.observations[0].quantity, '3.5')
})

test('保存失败事务回滚，不误占条码；恢复后原码可再扫', async t => {
  const { store } = await fixture(t)
  const original = IDBObjectStore.prototype.add
  IDBObjectStore.prototype.add = function (...args) {
    if (this.name === 'observations') throw new DOMException('Storage full', 'QuotaExceededError')
    return original.apply(this, args)
  }
  try { await assert.rejects(store.scan(scope, 'task1', 'tag1'), { name: 'QuotaExceededError' }) }
  finally { IDBObjectStore.prototype.add = original }
  const failed = await store.read(scope, 'task1')
  assert.equal(failed.observations.length, 0)
  assert.equal(failed.task.revision, 0)
  assert.equal(failed.task.observationCount, 0)
  await store.scan(scope, 'task1', 'tag1')
  assert.equal((await store.read(scope, 'task1')).observations.length, 1)
})

test('账号、API来源和账套隔离；不覆盖原单变化前的本机记录', async t => {
  const { store } = await fixture(t)
  await store.scan(scope, 'task1', 'tag1')
  assert.deepEqual(await store.list({ ...scope, userId: 'other' }), [])
  assert.deepEqual(await store.list({ ...scope, apiBaseUrl: 'http://other.example' }), [])
  await assert.rejects(store.read({ ...scope, userId: 'other' }, 'task1'))
  const prod = source(); prod.accountId = '008'; prod.databaseName = 'UFDATA_008_2021'
  await store.create(scope, prod, 'prod-task')
  const changed = source(); changed.fingerprint = 'B'.repeat(64)
  await assert.rejects(store.create(scope, changed, 'replacement'), { code: 'SOURCE_CHANGED' })
  assert.equal((await store.read(scope, 'task1')).observations.length, 1)
  assert.equal((await store.read(scope, 'prod-task')).observations.length, 0)
})

test('旧数量修订及核验期间发生的编辑不会覆盖较新数据', async t => {
  const { store } = await fixture(t)
  await store.scan(scope, 'task1', 'tag1')
  await store.quantity(scope, 'task1', 'tag1', '4', 1)
  const frozen = await store.beginValidation(scope, 'task1', 'check1')
  await store.quantity(scope, 'task1', 'tag1', '5', 2)
  await assert.rejects(store.quantity(scope, 'task1', 'tag1', '6', 2), { code: 'REVISION_CHANGED' })
  await assert.rejects(store.applyValidation(scope, frozen, 'check1', [
    { code: 'tag1', status: 'MATCHED', message: '', sourceLineId: 41, balance: 4, latestFlowId: 1 },
  ]), { code: 'REVISION_CHANGED' })
  const current = await store.read(scope, 'task1')
  assert.equal(current.observations[0].quantity, '5')
  assert.equal(current.task.validatedAt, null)
})

test('丢失/重复核验结果原子拒绝，过时网络响应不能替代新一轮核验', async t => {
  const { store } = await fixture(t)
  await store.scan(scope, 'task1', 'tag1')
  const older = await store.beginValidation(scope, 'task1', 'old')
  const fresh = await store.beginValidation(scope, 'task1', 'new')
  const item = { code: 'tag1', status: 'QUANTITY_REQUIRED', message: '请输入实盘数量' }
  await assert.rejects(store.applyValidation(scope, older, 'old', [item]), { code: 'REVISION_CHANGED' })
  await assert.rejects(store.applyValidation(scope, fresh, 'new', []))
  await assert.rejects(store.applyValidation(scope, fresh, 'new', [item, item]))
  await store.applyValidation(scope, fresh, 'new', [item])
  assert.equal((await store.read(scope, 'task1')).observations[0].check.status, 'QUANTITY_REQUIRED')
})
