import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import test from 'node:test'
import vm from 'node:vm'
import * as Vue from 'vue'
import * as SSR from 'vue/server-renderer'
import { compileScript, compileTemplate, parse } from '@vue/compiler-sfc'
import { IDBFactory } from 'fake-indexeddb'
import ts from 'typescript'

const root = fileURLToPath(new URL('../src/', import.meta.url))
const source = () => ({ accountId: '888', databaseName: 'UFDATA_888_2021',
  document: { id: 12, documentNo: 'COUNT12', documentDate: '2026-11-04', warehouseCode: 'W1',
    status: 'pending', version: '0x01', lineCount: 1 },
  lines: [{ id: 41, lineNo: 1, materialCode: 'M1', materialName: '物料', batchNo: 'B1',
    warehouseCode: 'W1', locationCode: 'L1', unitCode: 'U1', bookQuantity: 10,
    actualQuantity: null, freeItems: Array(10).fill(''), requiresAuxiliaryUnit: false }], fingerprint: 'A'.repeat(64) })
const code = '008|M1|B1|65.00|2609270095'
const label = () => ({ labelCode: code, objectType: 'PRODUCT_LABEL', materialCode: 'M1', materialName: '物料',
  batchNo: 'B1', unitCode: 'U1', warehouseCode: 'W1', locationCode: 'L1', availableQty: 10, status: 'ACTIVE', rowVersion: 'v1' })
const scope = { apiBaseUrl: 'http://count.example:9891', environment: 'development', userId: 'counter' }
function deferred() { let resolve; const promise = new Promise(r => { resolve = r }); return { promise, resolve } }

function fixture(t, type = 'inventory-count') {
  const mounted = [], unmounted = [], guards = [], feedback = [], routes = []
  const storage = () => { const values = new Map(); return { getItem: key => values.get(key) ?? null,
    setItem: (key, value) => values.set(key, String(value)), removeItem: key => values.delete(key) } }
  const window = { addEventListener() {}, removeEventListener() {}, dispatchEvent() {}, scrollTo() {},
    confirm: () => true, setTimeout: () => 1, clearTimeout() {}, parent: null }
  window.parent = window
  const globals = { window, navigator: { onLine: true, userAgent: 'test-pda' },
    document: { activeElement: null, visibilityState: 'visible' }, HTMLElement: class {},
    localStorage: storage(), sessionStorage: storage(), indexedDB: new IDBFactory(),
    URL, URLSearchParams, Error, Event, console, setTimeout: () => 1, clearTimeout() {} }
  const route = { params: { code: type }, query: {}, name: 'work-function' }
  let nativeScan, nativeBack, identity = 0
  const api = { lookups: 0, validations: 0,
    lookup: async () => ({ pallet: null, lines: [label()] }),
    validate: async (src, rows) => ({ canSubmit: false, items: rows.map(row => ({
      code: row.code, status: 'MATCHED', message: '', sourceLineId: 41, materialName: '物料', balance: 10,
    })) }) }
  const overrides = {
    vue: { ...Vue, onMounted: fn => mounted.push(fn), onBeforeUnmount: fn => unmounted.push(fn) },
    'vue/server-renderer': SSR,
    'vue-router': { useRoute: () => route, useRouter: () => ({
      push: async to => routes.push(to), replace: async to => routes.push(to),
    }), onBeforeRouteLeave: fn => guards.push(fn) },
    '@/services/auth': { readSession: () => ({ user: { id: 'counter', name: '盘点员', roles: ['user'] } }) },
    '@/services/auth-session': { readStoredAuthSession: () => { throw new Error('Unexpected upload auth') } },
    '@/services/runtime-config': { getRuntimeConfig: () => ({ ...scope, enableMockSourceDocuments: false }) },
    '@/services/shell-config': { playScanFeedback: value => feedback.push(value), setOfflineDocumentMode() {} },
    '@/utils/unique-id': { createUniqueId: prefix => prefix + '-test-' + (++identity) },
    '@/bridge/pda-bridge': { isMockPdaBridge: () => false, getPdaBridge: () => ({
      subscribeScan: fn => { nativeScan = fn; return () => { nativeScan = null } },
      subscribeBack: fn => { nativeBack = fn; return () => { nativeBack = null } },
    }) },
    '@/services/inventory-object-lookup': { lookupInventoryObject: async (...args) => { api.lookups++; return api.lookup(...args) } },
    '@/services/inventory-count-api': {
      downloadInventoryCount: async () => source(),
      searchInventoryCounts: async () => ({ items: [{ ...source().document }], hasMore: false }),
      validateInventoryCountItems: async (...args) => { api.validations++; return api.validate(...args) },
    },
    '@/services/reference-data': Object.fromEntries(['fetchRdStyles','fetchWarehousePositions','fetchDepartments',
      'fetchCurrencies','fetchPurchaseTypes','fetchSalesTypes','searchMaterials'].map(name => [name, async () => []])),
    '@/api/client': { ApiError: Error, apiRequest: async () => [] },
    '@/components/DateField.vue': { default: { props: ['modelValue','disabled'], render() { return Vue.h('button', { disabled: this.disabled }, this.modelValue) } } },
    '@/components/SearchSelect.vue': { default: { props: ['modelValue','disabled'], render() { return Vue.h('button', { disabled: this.disabled }, this.modelValue) } } },
  }
  const modules = new Map()
  function evaluate(text, filename) {
    const module = { exports: {} }
    const js = ts.transpileModule(text, { compilerOptions: { target: ts.ScriptTarget.ES2022,
      module: ts.ModuleKind.CommonJS } }).outputText
    vm.runInNewContext(js, { ...globals, exports: module.exports, module,
      require: request => load(request, filename) }, { filename })
    return module.exports
  }
  function load(request, parent = path.join(root, 'test.ts')) {
    if (request in overrides) return overrides[request]
    const filename = request.startsWith('@/') ? path.join(root, request.slice(2))
      : path.resolve(path.dirname(parent), request)
    if (modules.has(filename)) return modules.get(filename)
    if (filename.endsWith('.vue')) {
      const { descriptor } = parse(readFileSync(filename, 'utf8'))
      const compiled = compileScript(descriptor, { id: 'test-page' })
      const component = evaluate(compiled.content, filename)
      const template = compileTemplate({ source: descriptor.template.content, filename, id: 'test-page',
        ssr: true, ssrCssVars: descriptor.cssVars, compilerOptions: { bindingMetadata: compiled.bindings } })
      assert.equal(template.errors.length, 0)
      component.default.ssrRender = evaluate(template.code, filename).ssrRender
      modules.set(filename, component)
      return component
    }
    const result = evaluate(readFileSync(filename + '.ts', 'utf8'), filename + '.ts')
    modules.set(filename, result)
    return result
  }
  async function mount(typeName = type) {
    const index = mounted.length
    route.params.code = typeName
    const component = load('@/views/OutboundDocumentView.vue').default
    const view = component.setup(Vue.reactive({ documentType: typeName }), { expose() {} })
    for (const hook of mounted.slice(index)) await hook()
    return view
  }
  async function select() {
    const helper = load('@/services/inventory-count-documents')
    load('@/services/source-document-selection').saveSourceDocumentSelection({
      outboundDocumentType: 'inventory-count', sourceType: 'inventory-count', sourceTypeLabel: '盘点单',
      documentNo: 'COUNT12', documentDate: '2026-11-04', selectedLines: helper.countSourceLines(source()),
      requirements: [], inventoryCountSource: source(),
    })
    return mount()
  }
  t.after(async () => {
    for (const hook of unmounted) hook()
    await load('@/services/inventory-count-store').inventoryCountStore.close()
  })
  return { load, mount, select, api, globals, feedback, routes, route,
    nativeScan: value => nativeScan?.({ barcode: value }), nativeBack: () => nativeBack?.(),
    docs: () => load('@/services/outbound-document-drafts').listAllLocalDocuments() }
}

async function scanAndConfirm(f, v, quantity = '0') {
  v.goToScanStep()
  await v.addBarcode(code)
  assert.notEqual(v.editingLine.value, null)
  assert.equal(v.editorFieldValue(v.editingLine.value, 'body.quantity'), '')
  v.updateEditorField('body.quantity', quantity)
  await v.confirmEditingLine()
}

test('盘点和材料出库共用同一制单组件；盘点选单渲染没有合并/明细或选单类型', async t => {
  const f = fixture(t)
  const business = readFileSync(path.join(root, 'views/BusinessView.vue'), 'utf8')
  assert.equal(business.includes('InventoryCountView'), false)
  const component = f.load('@/views/SourceDocumentSelectView.vue').default
  const openedPicker = { ...component, setup(props, context) {
    const state = component.setup(props, context); state.queryOpen.value = true
    state.results.value = [{ sourceDocumentId: 12, sourceDocumentNo: 'COUNT12', documentDate: '2026-11-04',
      productCode: 'W1', warehouseCode: 'W1', materialCount: 9 }]
    return state
  } }
  const html = await SSR.renderToString(Vue.createSSRApp(openedPicker))
  assert.match(html, /盘点单/)
  assert.doesNotMatch(html, /合并|显示方式|选单类型|单号或产品/)
  assert.match(html, /COUNT12/); assert.match(html, /9条明细/)
  const form = f.load('@/views/OutboundDocumentView.vue').default
  const countHtml = await SSR.renderToString(Vue.createSSRApp(form, { documentType: 'inventory-count' }))
  assert.match(countHtml, /单据管理/); assert.match(countHtml, /下一步/)
  assert.doesNotMatch(countHtml, /采集记录|联网核验|采集模式/)
})

test('标准选单整单确认，带回完整原单；明细仍用共用选单路径', async t => {
  const f = fixture(t)
  const component = f.load('@/views/SourceDocumentSelectView.vue').default
  const picker = component.setup({}, { expose() {} })
  await picker.search()
  assert.equal(picker.results.value.length, 1)
  picker.toggleDocument(12)
  picker.changeDisplayMode('detail')
  assert.equal(picker.displayMode.value, 'merged')
  await picker.confirmSelection()
  const v = await f.mount()
  assert.equal(v.inventoryCountSource.value.document.id, 12)
  assert.equal(v.selectedSourceLines.value[0].sourceLineId, 41)
  assert.equal(v.documentDate.value, '2026-11-04'); assert.equal(v.warehouseCode.value, 'W1')
  await v.openSourceDetails()
  assert.equal(f.routes.at(-1).name, 'source-document-details')
})

test('扫码一次实时查标签，确认数量0后才入明细和标准IndexedDB；没有独立采集流程', async t => {
  const f = fixture(t), v = await f.select()
  await scanAndConfirm(f, v)
  assert.equal(f.api.lookups, 1); assert.equal(v.lines.value.length, 1)
  assert.equal(v.lines.value[0].availableQty, 10); assert.equal(v.lines.value[0].requestedQty, 0)
  assert.equal(f.feedback.at(-1), 'success')
  const [doc] = await f.docs()
  assert.equal(doc.documentType, 'inventory-count'); assert.equal(doc.payload.lines[0].requestedQty, 0)
  assert.equal(doc.payload.lines[0].countQuantityConfirmed, true); assert.equal(doc.uploadStatus, 'pending')
  await v.finishDocument()
  assert.equal(f.api.validations, 1); assert.equal(f.routes.at(-1).name, 'outbound-document-manager')
  assert.equal((await f.docs()).length, 1)
})

test('重复码及查询并发不误删首个查询锁，失败不误报成功', async t => {
  const f = fixture(t), v = await f.select()
  v.goToScanStep()
  const wait = deferred()
  f.api.lookup = async () => { await wait.promise; return { pallet: null, lines: [label()] } }
  const first = v.addBarcode(code)
  await v.addBarcode(code.toLowerCase())
  assert.equal(v.countLookupBusy.value, true); assert.equal(f.api.lookups, 1)
  wait.resolve(); await first
  assert.notEqual(v.editingLine.value, null); assert.equal(v.countLookupBusy.value, false)
  v.updateEditorField('body.quantity', '1'); await v.confirmEditingLine()
  await v.addBarcode(code.toLowerCase())
  assert.equal(v.lines.value.length, 1); assert.equal(f.feedback.at(-1), 'error')
})

test('保存失败保留编辑输入，提交中暂停消费，后台校验失败不冒充完成', async t => {
  const f = fixture(t), v = await f.select()
  v.goToScanStep(); await v.addBarcode(code); v.updateEditorField('body.quantity', '2')
  const repo = f.load('@/services/outbound-document-drafts'), original = repo.saveOutboundDocument
  repo.saveOutboundDocument = async () => { throw new Error('本机保存失败') }
  await v.confirmEditingLine()
  assert.equal(v.lines.value.length, 0); assert.equal(v.editingLine.value.requestedQty, 2)
  repo.saveOutboundDocument = original
  await v.confirmEditingLine()
  const wait = deferred(), entered = deferred()
  f.api.validate = async () => { entered.resolve(); await wait.promise; return { items: [{ code, status: 'OUTSIDE_SOURCE', message: '标签位置已变化' }], canSubmit: false } }
  const finishing = v.finishDocument(); await entered.promise
  f.nativeScan('OTHER'); assert.equal(v.finishing.value, true)
  wait.resolve(); await finishing
  assert.equal(f.routes.length, 0); assert.equal(v.completionError.value, '标签位置已变化')
  assert.equal((await f.docs())[0].uploadStatus, 'pending')
})

test('离线扫描和确认使用标准草稿，联网后校验自动执行，无额外用户步骤', async t => {
  const f = fixture(t), v = await f.select()
  f.globals.navigator.onLine = false
  await scanAndConfirm(f, v, '3.5')
  assert.equal(f.api.lookups, 0); assert.equal(v.lines.value[0].countPendingIdentity, true)
  await v.finishDocument()
  assert.equal(f.api.validations, 0)
  const [doc] = await f.docs()
  f.load('@/services/outbound-document-drafts').requestOutboundDocumentEdit({ documentType: 'inventory-count', documentId: doc.id })
  f.globals.navigator.onLine = true
  const restored = await f.mount()
  assert.equal(restored.lines.value[0].requestedQty, 3.5)
  await restored.finishDocument()
  assert.equal(f.api.validations, 1); assert.equal(restored.lines.value[0].materialCode, 'M1')
  assert.equal(restored.lines.value[0].countPendingIdentity, false)
})

test('旧记录导入普通单据草稿，幂等且删除不复活，旧原始数据保留', async t => {
  const f = fixture(t)
  const legacy = f.load('@/services/inventory-count-store').inventoryCountStore
  await legacy.create(scope, source(), 'legacy-count')
  await legacy.scan(scope, 'legacy-count', code)
  const helper = f.load('@/services/inventory-count-documents')
  await helper.preserveLegacyCountDocuments(scope); await helper.preserveLegacyCountDocuments(scope)
  const [doc] = await f.docs()
  assert.equal(doc.id, 'legacy-count'); assert.equal(doc.clientRevision, 1)
  assert.equal(doc.payload.lines[0].countQuantityConfirmed, false)
  assert.equal(doc.payload.lines[0].labelCode, code)
  await f.load('@/services/outbound-document-drafts').deleteLocalDocumentRecords([doc])
  await helper.preserveLegacyCountDocuments(scope)
  assert.equal((await f.docs()).length, 0); assert.equal((await legacy.read(scope, 'legacy-count')).observations.length, 1)
})

test('材料出库原有选项、数量校验和表单编辑不受盘点影响', async t => {
  const f = fixture(t, 'material-outbound'), v = await f.mount('material-outbound')
  v.warehouseCode.value = 'W1'; v.outboundCategoryCode.value = '01'
  v.currentStep.value = 2
  await v.addBarcode(code)
  assert.equal(v.lines.value.length, 1); assert.equal(v.lines.value[0].requestedQty, 10)
  assert.equal(v.lines.value[0].countQuantityConfirmed, undefined)
  assert.equal(v.fieldVisible('header.departmentCode'), true)
  assert.equal(v.fieldRequired('header.departmentCode'), true)
})

test('盘点只读表头和业务身份检查，不接受跨仓、重复行、辅单位或默认0', async t => {
  const f = fixture(t), helper = f.load('@/services/inventory-count-documents')
  assert.throws(() => helper.countFormLine(source(), code, 'x', { ...label(), warehouseCode: 'W2' }))
  const duplicate = source(); duplicate.lines.push({ ...duplicate.lines[0], id: 42 }); duplicate.document.lineCount = 2
  assert.throws(() => helper.countFormLine(duplicate, code, 'x', label()))
  const aux = source(); aux.lines[0].requiresAuxiliaryUnit = true
  assert.throws(() => helper.countFormLine(aux, code, 'x', label()))
  const row = helper.countFormLine(source(), code, 'x', label())
  assert.throws(() => helper.requireCountForm(source(), [row]))
  row.countQuantityConfirmed = true; helper.requireCountForm(source(), [row])
})

test('后台同步旧回执与新明细保存并发时，不能回写覆盖较新草稿', async t => {
  const f = fixture(t), v = await f.select()
  await scanAndConfirm(f, v, '1')
  const repo = f.load('@/services/outbound-document-drafts')
  for (let number = 2; number <= 8; number++) {
    const old = (await f.docs())[0]
    v.openLineEditor(v.lines.value[0]); v.updateEditorField('body.quantity', String(number))
    await Promise.all([repo.markDraftSynced(old.id, old.clientRevision, '2026-11-04T12:00:00+08:00'),
      v.confirmEditingLine()])
    const saved = (await f.docs())[0]
    assert.equal(saved.payload.lines[0].requestedQty, number)
    assert.equal(saved.clientRevision, old.clientRevision + 1)
    await repo.markDraftSyncFailed(saved.id, old.clientRevision, 'old error')
    assert.equal((await f.docs())[0].syncError, '')
  }
})
