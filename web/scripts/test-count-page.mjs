import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import vm from 'node:vm'
import * as Vue from 'vue'
import { compileScript, parse } from '@vue/compiler-sfc'
import { IDBFactory } from 'fake-indexeddb'
import ts from 'typescript'

const source = id => ({ accountId: '888', databaseName: 'UFDATA_888_2021',
  document: { id, documentNo: 'COUNT' + id, documentDate: '2026-11-04', warehouseCode: 'W1',
    status: 'pending', version: '0x01', lineCount: 1 },
  lines: [{ id: id * 10, lineNo: 1, materialCode: 'M1', materialName: '物料', batchNo: 'B1',
    warehouseCode: 'W1', locationCode: 'L1', unitCode: 'U1', bookQuantity: 10,
    actualQuantity: null, freeItems: Array(10).fill(''), requiresAuxiliaryUnit: false }], fingerprint: 'A'.repeat(64) })
const code = '008|M1|B1|65.00|2609270095'
const scope = { apiBaseUrl: 'http://count.example:9891', environment: 'development', userId: 'counter' }
const componentScript = compileScript(parse(await readFile(new URL('../src/views/InventoryCountView.vue', import.meta.url), 'utf8')).descriptor,
  { id: 'count-page-test' }).content
const modelScript = await readFile(new URL('../src/services/inventory-count-model.ts', import.meta.url), 'utf8')
const storeScript = await readFile(new URL('../src/services/inventory-count-store.ts', import.meta.url), 'utf8')

function evaluate(script, dependencies, globals) {
  const module = { exports: {} }
  const compiled = ts.transpileModule(script, { compilerOptions: {
    target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS,
  } }).outputText
  vm.runInNewContext(compiled, { exports: module.exports, module, ...globals,
    require: name => { if (!(name in dependencies)) throw new Error('Unexpected dependency: ' + name); return dependencies[name] },
  })
  return module.exports
}
function deferred() { let resolve; const promise = new Promise(r => { resolve = r }); return { promise, resolve } }
async function fixture(t) {
  const mounted = [], unmounted = [], guards = [], feedback = [], navigations = []
  let scanListener, backListener, confirms = true, identity = 0
  const window = { addEventListener() {}, removeEventListener() {}, scrollTo() {},
    confirm: () => confirms, clearTimeout() {}, setTimeout: () => 1 }
  window.parent = window
  const globals = { window, navigator: { onLine: true }, document: { visibilityState: 'visible' },
    indexedDB: new IDBFactory(), URL, Error, console }
  const model = evaluate(modelScript, {}, globals)
  const { inventoryCountStore: store } = evaluate(storeScript, { './inventory-count-model': model }, globals)
  const api = { downloadInventoryCount: async id => source(id),
    searchInventoryCounts: async () => ({ items: [source(1).document, source(2).document], hasMore: false }),
    validateInventoryCount: async draft => ({ canSubmit: false, items: draft.observations.map(row => ({
      code: row.code, status: 'MATCHED', message: '', sourceLineId: draft.task.source.lines[0].id, balance: 10,
    })) }),
  }
  const dependencies = {
    vue: { ...Vue, onMounted: callback => mounted.push(callback), onBeforeUnmount: callback => unmounted.push(callback) },
    'vue-router': { useRoute: () => ({ query: {}, params: { code: 'inventory-count' } }),
      useRouter: () => ({ replace: async value => navigations.push(value) }),
      onBeforeRouteLeave: callback => guards.push(callback), onBeforeRouteUpdate: callback => guards.push(callback) },
    '@/components/DateField.vue': { default: {} },
    '@/bridge/pda-bridge': { getPdaBridge: () => ({
      subscribeScan: callback => { scanListener = callback; return () => { scanListener = null } },
      subscribeBack: callback => { backListener = callback; return () => { backListener = null } },
    }) },
    '@/services/auth': { readSession: () => ({ user: { id: 'counter' } }) },
    '@/services/runtime-config': { getRuntimeConfig: () => scope },
    '@/services/shell-config': { playScanFeedback: value => feedback.push(value) },
    '@/utils/unique-id': { createUniqueId: () => 'count-test-' + (++identity) },
    '@/services/inventory-count-api': api,
    '@/services/inventory-count-store': { inventoryCountStore: store },
    '@/services/inventory-count-model': model,
  }
  const component = evaluate(componentScript, dependencies, globals).default
  const view = component.setup({}, { expose() {} })
  for (const mount of mounted) await mount()
  t.after(async () => { for (const unmount of unmounted) unmount(); await store.close() })
  async function select(id = 1) {
    view.openSources(); await view.search(); view.selectedSourceId.value = id; await view.choose()
  }
  return { view, store, api, feedback, guards, navigations, select, globals,
    nativeScan: value => scanListener?.({ barcode: value }), nativeBack: () => backListener?.(),
    confirm: value => { confirms = value } }
}

test('表头、选单、扫码分层；日期和原单明细返回不退出功能', async t => {
  const f = await fixture(t), v = f.view
  assert.equal(v.page.value, 'header')
  await v.scan(code); assert.equal(v.draft.value, null)
  v.openSources(); v.startDatePicker.value = { close: () => true }
  await v.back(); assert.equal(v.page.value, 'source')
  v.startDatePicker.value = null
  await f.select(); assert.equal(v.page.value, 'header'); assert.equal(v.draft.value.task.source.document.id, 1)
  f.nativeScan(code); assert.equal(v.pendingWrites.value, 0)
  v.startScanning(); await v.scan(code)
  v.navigate('lines'); f.nativeScan('OTHER'); assert.equal(v.pendingWrites.value, 0)
  await v.back(); assert.equal(v.page.value, 'scan')
  await v.back(); assert.equal(v.page.value, 'header'); assert.equal(v.draft.value.observations.length, 1)
  await v.back(); assert.equal(f.navigations.length, 1)
})

test('扫码记录保持事务去重；编辑期间暂停扫码，明确0保存后恢复', async t => {
  const f = await fixture(t), v = f.view
  await f.select(); v.startScanning()
  await Promise.all([v.scan(code), v.scan(code)])
  assert.equal(v.draft.value.observations.length, 1)
  assert.equal(f.feedback.filter(x => x === 'success').length, 1)
  assert.equal(f.feedback.filter(x => x === 'error').length, 1)
  assert.equal(v.draft.value.observations[0].quantity, null)
  v.editRow(v.draft.value.observations[0]); f.nativeScan('OTHER')
  assert.equal(v.pendingWrites.value, 0); assert.equal(v.canScan.value, false)
  v.editing.value.quantity = '0'; await v.saveQuantity()
  assert.equal(v.editing.value, null); assert.equal(v.draft.value.observations[0].quantity, '0')
  assert.equal(v.canScan.value, true)
})

test('未保存输入返回先确认，保存失败保留输入；保存中阻止切换任务', async t => {
  const f = await fixture(t), v = f.view
  await f.select(); v.startScanning(); await v.scan(code); v.editRow(v.draft.value.observations[0])
  v.editing.value.quantity = '2.5'; f.confirm(false); await v.back()
  assert.equal(v.editing.value.quantity, '2.5'); assert.equal(f.guards[0](), false)
  const original = f.store.quantity.bind(f.store)
  f.store.quantity = async () => { throw new Error('本机保存失败') }
  await v.saveQuantity(); assert.equal(v.editing.value.quantity, '2.5'); assert.equal(v.error.value, true)
  const wait = deferred()
  f.store.quantity = async (...args) => { await wait.promise; return original(...args) }
  const saving = v.saveQuantity(); await v.openRecords(); await v.back()
  assert.equal(v.page.value, 'scan'); assert.notEqual(v.editing.value, null)
  wait.resolve(); await saving
  assert.equal(v.editing.value, null); assert.equal(v.draft.value.observations[0].quantity, '2.5')
})

test('采集记录独立恢复；选单失败不清除原本机任务，切换不串数据', async t => {
  const f = await fixture(t), v = f.view
  await f.select(); v.startScanning(); await v.scan(code)
  const first = v.draft.value.task.id
  await v.openRecords(); assert.equal(v.page.value, 'records'); assert.equal(v.canScan.value, false)
  await v.back(); assert.equal(v.page.value, 'scan')
  await v.back(); v.openSources(); v.selectedSourceId.value = 2
  f.api.downloadInventoryCount = async () => { throw new Error('断网') }
  await v.choose(); assert.equal(v.page.value, 'source'); assert.equal(v.draft.value.task.id, first)
  f.api.downloadInventoryCount = async id => source(id)
  await v.choose(); v.startScanning(); assert.equal(v.draft.value.observations.length, 0)
  await v.openRecords(); await v.openTask(first)
  assert.equal(v.page.value, 'scan'); assert.equal(v.draft.value.observations[0].code, code)
})

test('核验期间不消费扫码；外部较新修订使旧核验作废，保留新数量', async t => {
  const f = await fixture(t), v = f.view
  await f.select(); v.startScanning(); await v.scan(code)
  const wait = deferred(), entered = deferred(), original = f.api.validateInventoryCount
  f.api.validateInventoryCount = async draft => { entered.resolve(); await wait.promise; return original(draft) }
  const checking = v.validate(); await entered.promise
  f.nativeScan('OTHER'); await v.openRecords(); assert.equal(v.page.value, 'scan')
  await f.store.quantity(scope, v.draft.value.task.id, code, '7', 1)
  wait.resolve(); await checking
  assert.equal(v.draft.value.task.validatedAt, null)
  assert.equal(v.draft.value.observations[0].quantity, '7')
  assert.equal(v.error.value, true); assert.equal(v.canScan.value, true)
})
