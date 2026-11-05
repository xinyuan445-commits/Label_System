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
    status: 'pending', version: '0x01', lineCount: 1, countByLocation: true, warehousePositionManaged: true,
    inCategoryCode: '107', inCategoryName: '盘盈入库', outCategoryCode: '207', outCategoryName: '盘亏出库' },
  lines: [{ id: 41, lineNo: 1, materialCode: 'M1', materialName: '物料', batchNo: 'B1',
    warehouseCode: 'W1', locationCode: 'L1', unitCode: 'U1', unitName: '张', bookQuantity: 10,
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
  const runtime = { ...scope }
  const globals = { window, navigator: { onLine: true, userAgent: 'test-pda' },
    document: { activeElement: null, visibilityState: 'visible' }, HTMLElement: class {},
    localStorage: storage(), sessionStorage: storage(), indexedDB: new IDBFactory(),
    URL, URLSearchParams, Error, Event, console, setTimeout: () => 1, clearTimeout() {} }
  window.localStorage=globals.localStorage
  const route = { params: { code: type }, query: {}, name: 'work-function' }
  let nativeScan, nativeBack, identity = 0
  const api = { lookups: 0, validations: 0, directValidations: 0, searches: 0, lastSearch: null,
    uploads: [], settingsWrites: [], upload: async document => document,
    context: { accountId: '888', databaseName: 'UFDATA_888_2021', uploadSaveEnabled: true },
    search: async () => ({ items: [{ ...source().document, partnerName: '原料仓', createdBy: '测试制单员' }], hasMore: false }),
    warehouse: async warehouseCode => ({ accountId:'888', databaseName:'UFDATA_888_2021', warehouseCode,
      warehouseName:warehouseCode, positionManaged: warehouseCode==='W1' }),
    lookup: async () => ({ pallet: null, lines: [label()] }),
    validate: async (src, rows) => ({ canSubmit: false, items: rows.map(row => ({
      code: row.code, status: 'MATCHED', message: '', sourceLineId: 41, materialName: '物料', balance: 10,
    })) }),
    directValidate: async (context, warehouse, rows) => ({ canSubmit: false,
      items: rows.map(row => ({ code: row.code, status: 'MATCHED', message: '', label: label() })) }) }
  const reference = { styles: [
    { code: '107', name: '盘盈入库', direction: 'IN', isTerminal: true, isActive: true },
    { code: '207', name: '盘亏出库', direction: 'OUT', isTerminal: true, isActive: true },
    { code: '108', name: '其他入库', direction: 'IN', isTerminal: true, isActive: true },
    { code: '208', name: '其他出库', direction: 'OUT', isTerminal: true, isActive: true },
  ] }
  const overrides = {
    vue: { ...Vue, onMounted: fn => mounted.push(fn), onBeforeUnmount: fn => unmounted.push(fn) },
    'vue/server-renderer': SSR,
    'vue-router': { useRoute: () => route, useRouter: () => ({
      push: async to => routes.push(to), replace: async to => routes.push(to),
    }), onBeforeRouteLeave: fn => guards.push(fn) },
    '@/services/auth': { readSession: () => ({ user: { id: 'counter', name: '盘点员', roles: ['user'] } }) },
    '@/services/auth-session': { readStoredAuthSession: () => { throw new Error('Unexpected upload auth') } },
    '@/services/runtime-config': { getRuntimeConfig: () => ({ ...runtime, enableMockSourceDocuments: false }) },
    '@/services/shell-config': { playScanFeedback: value => feedback.push(value), setOfflineDocumentMode() {} },
    '@/utils/unique-id': { createUniqueId: prefix => prefix + '-test-' + (++identity) },
    '@/bridge/pda-bridge': { isMockPdaBridge: () => false, getPdaBridge: () => ({
      subscribeScan: fn => { nativeScan = fn; return () => { nativeScan = null } },
      subscribeBack: fn => { nativeBack = fn; return () => { nativeBack = null } },
    }) },
    '@/services/inventory-object-lookup': { lookupInventoryObject: async (...args) => { api.lookups++; return api.lookup(...args) } },
    '@/services/inventory-count-api': {
      downloadInventoryCount: async () => source(),
      searchInventoryCounts: async (...args) => { api.searches++; api.lastSearch=args; return api.search(...args) },
      validateInventoryCountItems: async (...args) => { api.validations++; return api.validate(...args) },
      getCountContext: async () => api.context,
      getCountWarehouse: async code => api.warehouse(code),
      validateDirectCount: async (...args) => { api.directValidations++; return api.directValidate(...args) },
    },
    '@/services/outbound-document-upload': { uploadOutboundDocument: async (document, autoApprove) => {
      api.uploads.push({ document, autoApprove }); return api.upload(document, autoApprove)
    } },
    '@/services/reference-data': Object.fromEntries(['fetchRdStyles','fetchWarehousePositions','fetchDepartments',
      'fetchCurrencies','fetchPurchaseTypes','fetchSalesTypes','searchMaterials'].map(name => [name, async () => []]).concat([
        ['fetchRdStyles', async () => reference.styles],
      ])),
    '@/api/client': { ApiError: Error, apiRequest: async (path, options) => {
      if (options?.method === 'PUT' && path.startsWith('/api/v1/admin/settings/')) {
        const body = JSON.parse(options.body)
        api.settingsWrites.push({ path, body })
        return { scope: path.includes('/global/') ? 'GLOBAL' : 'DOCUMENT', documentType: body.documentType,
          key: path.split('/').at(-1), value: body.value, isEnabled: true, rowVersion: 'v1', version: 1 }
      }
      return []
    } },
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
  async function mountPicker() {
    const index=mounted.length
    const component=load('@/views/SourceDocumentSelectView.vue').default
    const state=component.setup({}, {expose(){}})
    for (const hook of mounted.slice(index)) await hook()
    return state
  }
  t.after(async () => {
    for (const hook of unmounted) hook()
    await load('@/services/inventory-count-store').inventoryCountStore.close()
  })
  return { load, mount, mountPicker, select, api, reference, globals, feedback, routes, route, runtime,
    realAuthSession: () => evaluate(readFileSync(path.join(root,'services/auth-session.ts'),'utf8'),path.join(root,'services/auth-session.ts')),
    nativeScan: value => nativeScan?.({ barcode: value }), nativeBack: () => nativeBack?.(),
    docs: () => load('@/services/outbound-document-drafts').listAllLocalDocuments() }
}

async function scanAndConfirm(f, v, quantity = '0') {
  v.goToScanStep()
  await v.addBarcode(code)
  assert.notEqual(v.editingLine.value, null)
  assert.equal(v.editorFieldValue(v.editingLine.value, 'body.quantity'), f.globals.navigator.onLine ? 10 : '')
  v.updateEditorField('body.quantity', quantity)
  await v.confirmEditingLine()
}

test('盘点完成自动上传遵循开关，只提交一次且不自动审核', async t => {
  const f = fixture(t), v = await f.select()
  await scanAndConfirm(f, v, '5')
  v.documentPolicy.value.autoUploadOnComplete = true
  v.documentPolicy.value.autoApproveAfterUpload = true
  const pending = deferred(); f.api.upload = async () => pending.promise
  const first = v.finishDocument()
  while (f.api.uploads.length === 0) await new Promise(resolve => setImmediate(resolve))
  await v.finishDocument()
  assert.equal(f.api.uploads.length, 1)
  assert.equal(f.api.uploads[0].autoApprove, false)
  assert.equal(f.api.uploads[0].document.payload.lines[0].requestedQty, 5)
  pending.resolve(f.api.uploads[0].document); await first
  assert.equal(v.finishing.value, false)
  assert.equal(f.routes.at(-1).name, 'outbound-document-manager')
})

test('盘点关闭自动上传只保存；返回时完成也遵循自动上传', async t => {
  const f = fixture(t), v = await f.select()
  await scanAndConfirm(f, v, '5')
  v.documentPolicy.value.autoUploadOnComplete = false
  await v.finishDocument(); assert.equal(f.api.uploads.length, 0)
  assert.equal((await f.docs()).length, 1)
  v.documentPolicy.value.autoUploadOnComplete = true
  v.showLeaveConfirm.value = true
  await v.finishAndLeave()
  assert.equal(f.api.uploads.length, 1)
  assert.equal(v.finishing.value, false)
})

test('盘点自动上传断网、能力关闭或账套变化时保留草稿且不发送上传', async t => {
  for (const mode of ['offline', 'disabled', 'account-changed']) {
    const f = fixture(t), v = await f.select()
    await scanAndConfirm(f, v, '5')
    v.documentPolicy.value.autoUploadOnComplete = true
    if (mode === 'offline') f.globals.navigator.onLine = false
    if (mode === 'disabled') f.api.context.uploadSaveEnabled = false
    if (mode === 'account-changed') f.api.context = { accountId: '008', databaseName: 'UFDATA_008_2021', uploadSaveEnabled: true }
    await v.finishDocument()
    assert.equal(f.api.uploads.length, 0)
    assert.equal((await f.docs())[0].payload.lines[0].requestedQty, 5)
  }
})

test('盘点自动上传失败保留实盘和草稿，不在失败后重新上传', async t => {
  const f = fixture(t), v = await f.select()
  await scanAndConfirm(f, v, '5')
  v.documentPolicy.value.autoUploadOnComplete = true
  f.api.upload = async () => { throw new Error('上传失败') }
  await v.finishDocument()
  assert.equal(f.api.uploads.length, 1)
  assert.equal((await f.docs())[0].payload.lines[0].requestedQty, 5)
  assert.equal(v.finishing.value, false)
})

test('盘点开关仅展示可用自动上传，其他单据仍保留原开关', async t => {
  const f = fixture(t)
  const component = f.load('@/views/BusinessDocumentSettingsView.vue').default
  const countHtml = await SSR.renderToString(Vue.createSSRApp(component))
  assert.match(countHtml, /完成自动上传/)
  assert.doesNotMatch(countHtml, /扫码后自动添加到明细|上传自动审核|扫托包含托盘|托盘设置/)
  f.route.params.code = 'material-outbound'
  const materialHtml = await SSR.renderToString(Vue.createSSRApp(component))
  assert.match(materialHtml, /扫码后自动添加到明细/)
  assert.match(materialHtml, /上传自动审核/)
  assert.match(materialHtml, /托盘设置/)
})

test('盘点保存开关只更新自动上传，不覆盖隐藏的审核/托盘和其他单据设置', async t => {
  const f = fixture(t), settings = f.load('@/services/business-document-settings')
  const state = settings.loadBusinessDocumentSettings()
  settings.ensureDocumentPolicy(state, 'inventory-count').autoUploadOnComplete = true
  settings.ensureDocumentPolicy(state, 'material-outbound').autoUploadOnComplete = false
  await settings.persistBusinessDocumentSettings('inventory-count', state)
  assert.equal(f.api.settingsWrites.length, 1)
  assert.match(f.api.settingsWrites[0].path, /auto_upload_on_complete$/)
  assert.equal(f.api.settingsWrites[0].body.value, true)
  const loaded = settings.loadBusinessDocumentSettings()
  assert.equal(settings.ensureDocumentPolicy(loaded, 'inventory-count').autoUploadOnComplete, true)
  assert.equal(settings.ensureDocumentPolicy(loaded, 'material-outbound').autoUploadOnComplete, false)
})

test('同一环境名切换008/888接口时，会话、草稿和选单隔离且旧数据保留', async t => {
  const f=fixture(t), repo=f.load('@/services/outbound-document-drafts'), selections=f.load('@/services/source-document-selection')
  const auth=f.realAuthSession()
  f.runtime.apiBaseUrl='http://172.16.1.159:9890'
  const session={token:'test-prod-token',user:{id:'counter',name:'测试',roles:[]},signedInAt:Date.now(),expiresAt:Date.now()+60000}
  auth.writeStoredAuthSession(session)
  const production=await repo.saveOutboundDocument({documentType:'inventory-count',summary:{documentDate:'2026-11-05',sourceTypeLabel:'盘点单',sourceDocumentNo:'PROD',lineCount:0,totalQty:0},payload:{remark:'008'}})
  repo.saveOutboundDocumentWorkingState({documentType:'inventory-count',documentId:production.id,currentStep:1,payload:{remark:'008'}})
  repo.requestOutboundDocumentEdit({documentType:'inventory-count',documentId:production.id})
  selections.saveSourceDocumentSelection({outboundDocumentType:'inventory-count',sourceType:'inventory-count',sourceTypeLabel:'盘点单',documentNo:'PROD',documentDate:'2026-11-05',selectedLines:[],requirements:[]})
  f.globals.localStorage.setItem('label-system:draft:inventory-count','{"remark":"old 008"}')
  f.runtime.apiBaseUrl='http://172.16.1.159:9891'
  assert.equal(auth.readStoredAuthSession(),null)
  assert.equal((await f.docs()).length,0)
  assert.equal(repo.readOutboundDocumentWorkingState('inventory-count'),null)
  assert.equal(repo.consumeOutboundDocumentEditRequest('inventory-count'),null)
  assert.equal(selections.consumeSourceDocumentSelection('inventory-count'),null)
  await repo.migrateLegacyOutboundDraft('inventory-count','label-system:draft:inventory-count',()=>({documentDate:'',sourceTypeLabel:'',sourceDocumentNo:'',lineCount:0,totalQty:0}))
  assert.equal((await f.docs()).length,0)
  auth.writeStoredAuthSession({...session,token:'test-dev-token'})
  await repo.saveOutboundDocument({id:production.id,documentType:'inventory-count',summary:production.summary,payload:{remark:'888'}})
  assert.equal((await f.docs())[0].payload.remark,'888')
  f.runtime.apiBaseUrl='http://172.16.1.159:9890'
  assert.equal(auth.readStoredAuthSession().token,'test-prod-token')
  assert.equal((await f.docs())[0].payload.remark,'008')
  assert.equal(repo.readOutboundDocumentWorkingState('inventory-count').payload.remark,'008')
  assert.equal(repo.consumeOutboundDocumentEditRequest('inventory-count').documentId,production.id)
  assert.equal(selections.consumeSourceDocumentSelection('inventory-count').documentNo,'PROD')
  assert.equal(f.globals.localStorage.getItem('label-system:draft:inventory-count'),'{"remark":"old 008"}')
})

test('盘点和材料出库共用同一制单组件；盘点选单渲染没有合并/明细或选单类型', async t => {
  const f = fixture(t)
  const business = readFileSync(path.join(root, 'views/BusinessView.vue'), 'utf8')
  assert.equal(business.includes('InventoryCountView'), false)
  const component = f.load('@/views/SourceDocumentSelectView.vue').default
  const openedPicker = { ...component, setup(props, context) {
    const state = component.setup(props, context); state.queryOpen.value = true
    state.results.value = [{ sourceDocumentId: 12, sourceDocumentNo: 'COUNT12', documentDate: '2026-11-04',
      productCode: 'W1', warehouseCode: 'W1', warehouseName: '原料仓', materialCount: 9,
      sourceCreatedBy: '测试制单员', sourceStatus: 'pending' }]
    return state
  } }
  const html = await SSR.renderToString(Vue.createSSRApp(openedPicker))
  assert.match(html, /盘点单/)
  assert.doesNotMatch(html, /合并|显示方式|选单类型|单号或产品/)
  assert.match(html, /COUNT12/); assert.match(html, /9条明细/)
  assert.match(html, /原料仓/); assert.match(html, /W1/)
  assert.match(html, /测试制单员/); assert.match(html, /未审核/)
  const form = f.load('@/views/OutboundDocumentView.vue').default
  const countHtml = await SSR.renderToString(Vue.createSSRApp(form, { documentType: 'inventory-count' }))
  assert.match(countHtml, /单据管理/); assert.match(countHtml, /下一步/)
  assert.match(countHtml, /盘盈类别/); assert.match(countHtml, /盘亏类别/)
  assert.doesNotMatch(countHtml, /采集记录|联网核验|采集模式/)
  for (const hasSource of [false, true]) {
    const preparedForm = { ...form, setup(props, context) {
      const state=form.setup(props, context)
      state.inventoryCountSource.value=hasSource ? source() : null
      state.inventoryCountContext.value={accountId:'888',databaseName:'UFDATA_888_2021'}
      state.warehouseCode.value='W1'
      state.headerFieldValues.value={ 'header.inCategoryCode':'107', 'header.outCategoryCode':'207' }
      return state
    } }
    const rendered=await SSR.renderToString(Vue.createSSRApp(preparedForm,{documentType:'inventory-count'}))
    const controls=new Map([...rendered.matchAll(/<button([^>]*)>(107|207|W1)<\/button>/g)].map(match=>[match[2],match[1]]))
    for (const category of ['107','207']) {
      assert.equal(controls.has(category),true)
      assert.doesNotMatch(controls.get(category), /(?:^|\s)disabled(?:\s|$)/)
    }
    assert.equal(/(?:^|\s)disabled(?:\s|$)/.test(controls.get('W1')),hasSource)
  }
})

test('盘点选单进入自动按默认日期查询一次，补齐卡片元数据，其他业务不自动查询', async t => {
  const f=fixture(t), picker=await f.mountPicker()
  assert.equal(f.api.searches,1)
  assert.equal(f.api.lastSearch[0],'')
  assert.equal(f.api.lastSearch[1],picker.startDate.value)
  assert.equal(f.api.lastSearch[2],picker.endDate.value)
  assert.equal(picker.results.value[0].warehouseName,'原料仓')
  assert.equal(picker.results.value[0].sourceCreatedBy,'测试制单员')
  assert.equal(picker.results.value[0].sourceStatus,'pending')
  assert.equal(picker.queryOpen.value,false)
  assert.equal(picker.selectedDocumentId.value,null)
  const other=fixture(t,'material-outbound'), materialPicker=await other.mountPicker()
  assert.equal(other.api.searches,0);assert.equal(materialPicker.searched.value,false)
})

test('盘点自动查询空结果/失败时展开条件，查询中不重复请求且旧选择不残留', async t => {
  const f=fixture(t)
  f.api.search=async()=>({items:[],hasMore:false})
  const picker=await f.mountPicker()
  assert.equal(picker.queryOpen.value,true);assert.equal(picker.searched.value,true)
  picker.selectedDocumentId.value=12
  const wait=deferred()
  f.api.search=async()=>{await wait.promise;throw new Error('查询失败')}
  const first=picker.search();await picker.search()
  assert.equal(f.api.searches,2);assert.equal(picker.selectedDocumentId.value,null)
  wait.resolve();await first
  assert.equal(picker.errorMessage.value,'查询失败');assert.equal(picker.queryOpen.value,true)
  f.api.search=async()=>({items:[],hasMore:true})
  await picker.search()
  assert.match(picker.errorMessage.value,/缩小查询范围/)
  assert.equal(picker.queryOpen.value,true)
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
  const [downloaded] = await f.docs()
  assert.equal(downloaded.payload.inventoryCountSource.lines.length, 1)
  assert.equal(downloaded.payload.inventoryCountSource.fingerprint, source().fingerprint)
  assert.equal(downloaded.summary.lineCount, 0)
  const v = await f.mount()
  assert.equal(v.draftId.value, downloaded.id)
  assert.equal(v.inventoryCountSource.value.document.id, 12)
  assert.equal(v.selectedSourceLines.value[0].sourceLineId, 41)
  assert.equal(v.documentDate.value, '2026-11-04'); assert.equal(v.warehouseCode.value, 'W1')
  v.warehouseCode.value='W2'; await v.handleWarehouseChange()
  assert.equal(v.warehouseCode.value,'W1')
  f.load('@/services/outbound-document-drafts').requestOutboundDocumentEdit({ documentType:'inventory-count', documentId:downloaded.id })
  const restored=await f.mount()
  assert.equal(restored.inventoryCountSource.value.document.id,12)
  assert.equal(restored.lines.value.length,0)
  assert.equal(restored.inventoryCountSource.value.lines[0].id,41)
  await v.openSourceDetails()
  assert.equal(f.routes.at(-1).name, 'source-document-details')
})

test('盘盈盘亏带原单值且允许改选；保存恢复保持用户选择，清除后恢复默认', async t => {
  const f = fixture(t), v = await f.select()
  assert.equal(v.fieldVisible('header.inCategoryCode'), true)
  assert.equal(v.fieldVisible('header.outCategoryCode'), true)
  assert.equal(v.headerFieldValues.value['header.inCategoryCode'], '107')
  assert.equal(v.headerFieldValues.value['header.outCategoryCode'], '207')
  assert.equal(v.inRdStyleSelectOptions.value[0].label, '盘盈入库')
  assert.equal(v.outRdStyleSelectOptions.value[0].label, '盘亏出库')
  v.setHeaderField('header.inCategoryCode', '108')
  assert.equal(v.headerFieldValues.value['header.inCategoryCode'], '108')
  await scanAndConfirm(f, v, '1')
  const [doc] = await f.docs()
  assert.equal(doc.payload.headerFieldValues['header.outCategoryCode'], '207')
  f.load('@/services/outbound-document-drafts').requestOutboundDocumentEdit({ documentType: 'inventory-count', documentId: doc.id })
  const restored = await f.mount()
  assert.equal(restored.headerFieldValues.value['header.inCategoryCode'], '108')
  assert.equal(restored.outRdStyleSelectOptions.value[0].label, '盘亏出库')
  await restored.removeLine(restored.lines.value[0].id)
  restored.clearSourceDocument()
  await Promise.resolve(); await Promise.resolve()
  assert.equal(restored.headerFieldValues.value['header.inCategoryCode'], '107')
  assert.equal(restored.headerFieldValues.value['header.outCategoryCode'], '207')
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

test('无原单可选择仓库直接制单，默认类别正确，完成前重新批量复核并保存', async t => {
  const f = fixture(t), v = await f.mount()
  await v.loadReferenceData()
  assert.equal(v.inventoryCountSource.value, null)
  assert.equal(v.headerFieldValues.value['header.inCategoryCode'], '107')
  assert.equal(v.headerFieldValues.value['header.outCategoryCode'], '207')
  v.warehouseCode.value = 'W1'; await v.handleWarehouseChange()
  v.setHeaderField('header.outCategoryCode', '208')
  await scanAndConfirm(f, v, '2')
  assert.equal(f.api.lookups, 1)
  await v.finishDocument()
  assert.equal(f.api.directValidations, 1); assert.equal(f.api.validations, 0)
  const [doc] = await f.docs()
  assert.equal(doc.payload.inventoryCountSource, undefined)
  assert.equal(doc.payload.inventoryCountContext.accountId, '888')
  assert.equal(doc.payload.headerFieldValues['header.outCategoryCode'], '208')
  assert.equal(doc.payload.lines[0].requestedQty, 2)
  assert.equal(f.routes.at(-1).name, 'outbound-document-manager')
})

test('无原单离线可保存，联网校验失败保留输入；仓库锁定直到重置', async t => {
  const f = fixture(t), v = await f.mount(); await v.loadReferenceData()
  v.warehouseCode.value = 'W1'; await v.handleWarehouseChange(); f.globals.navigator.onLine = false
  await scanAndConfirm(f, v, '0')
  v.warehouseCode.value = 'W2'; await v.handleWarehouseChange()
  assert.equal(v.warehouseCode.value, 'W1')
  await v.finishDocument(); assert.equal(f.api.directValidations, 0)
  const [doc] = await f.docs()
  f.load('@/services/outbound-document-drafts').requestOutboundDocumentEdit({ documentType: 'inventory-count', documentId: doc.id })
  f.globals.navigator.onLine = true
  const restored = await f.mount(); await restored.loadReferenceData()
  f.api.directValidate = async () => ({ canSubmit: false, items: [{code, status: 'OUTSIDE_WAREHOUSE', message: '标签不属于所选仓库'}] })
  const routeCount=f.routes.length
  await restored.finishDocument()
  assert.equal(f.routes.length, routeCount); assert.equal(restored.lines.value[0].requestedQty, 0)
  assert.equal(restored.completionError.value, '标签不属于所选仓库')
  restored.resetDocument()
  restored.warehouseCode.value = 'W2'; await restored.handleWarehouseChange()
  assert.equal(restored.warehouseCode.value, 'W2'); assert.equal(restored.lines.value.length, 0)
  assert.equal(restored.headerFieldValues.value['header.inCategoryCode'], '107')
})

test('仓库货位标志独立于货位列表；原单是否盘货位不照抄仓库标志', async t => {
  const f=fixture(t),v=await f.mount();await v.loadReferenceData()
  assert.equal(v.warehousePositionOptions.value.length,0)
  v.warehouseCode.value='W1';await v.handleWarehouseChange()
  assert.equal(v.countByLocation.value,true);assert.equal(v.headerValue('header.countByLocation'),'1')
  v.warehouseCode.value='W2';await v.handleWarehouseChange()
  assert.equal(v.countByLocation.value,false);assert.equal(v.headerValue('header.countByLocation'),'0')
  const original=source();original.document.countByLocation=false;original.document.warehousePositionManaged=true
  v.inventoryCountSource.value=original
  assert.equal(v.countByLocation.value,false)
})

test('快速切仓只接受最后响应，查询失败及未知状态不伪装成否', async t => {
  const f=fixture(t),v=await f.mount();await v.loadReferenceData()
  const pending=deferred(),warehouse=f.api.warehouse
  f.api.warehouse=async code=>{if(code==='W1')await pending.promise;return warehouse(code)}
  v.warehouseCode.value='W1';const first=v.handleWarehouseChange()
  assert.equal(v.countByLocation.value,null);assert.equal(v.countWarehouseLoading.value,true)
  v.goToScanStep();assert.equal(v.currentStep.value,1)
  v.warehouseCode.value='W2';await v.handleWarehouseChange()
  pending.resolve();await first
  assert.equal(v.countByLocation.value,false);assert.equal(v.inventoryCountWarehouse.value.warehouseCode,'W2')
  f.api.warehouse=async()=>{throw new Error('仓库查询失败')}
  v.warehouseCode.value='W1';await v.handleWarehouseChange()
  assert.equal(v.countByLocation.value,null);v.goToScanStep();assert.equal(v.currentStep.value,1)
})

test('盘货位时缺货位拒绝；不盘货位和离线恢复保留明确的否', async t => {
  const f=fixture(t),v=await f.mount();await v.loadReferenceData()
  v.warehouseCode.value='W1';await v.handleWarehouseChange();v.goToScanStep()
  f.api.lookup=async()=>({pallet:null,lines:[{...label(),locationCode:''}]})
  await v.addBarcode(code);assert.equal(v.editingLine.value,null);assert.equal(f.feedback.at(-1),'error')
  v.returnToHeaderStep();v.warehouseCode.value='W2';await v.handleWarehouseChange()
  f.api.lookup=async()=>({pallet:null,lines:[{...label(),warehouseCode:'W2',locationCode:''}]})
  await scanAndConfirm(f,v,'1')
  const [doc]=await f.docs()
  assert.equal(doc.payload.headerFieldValues['header.countByLocation'],'0')
  assert.equal(doc.payload.inventoryCountWarehouse.positionManaged,false)
  f.load('@/services/outbound-document-drafts').requestOutboundDocumentEdit({documentType:'inventory-count',documentId:doc.id})
  f.globals.navigator.onLine=false
  const restored=await f.mount()
  assert.equal(restored.countByLocation.value,false)
})

test('原单确认保存失败不得返回，已有明细不得被另一原单覆盖', async t => {
  const f=fixture(t), picker=f.load('@/views/SourceDocumentSelectView.vue').default.setup({}, {expose(){}})
  await picker.search(); picker.toggleDocument(12)
  const repo=f.load('@/services/outbound-document-drafts'), save=repo.saveOutboundDocument
  repo.saveOutboundDocument=async()=>{throw new Error('本机保存失败')}
  await picker.confirmSelection()
  assert.equal(f.routes.length,0); assert.equal(picker.errorMessage.value,'本机保存失败')
  repo.saveOutboundDocument=save
  const helper=f.load('@/services/inventory-count-documents')
  await assert.rejects(helper.saveCountSourceDraft(source(), {documentType:'inventory-count',documentId:null,currentStep:1,
    payload:{lines:[{labelCode:code,requestedQty:1}]} }), /重置/)
  assert.equal((await f.docs()).length,0)
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

test('盘点身份检查不接受跨仓、重复行或辅单位，当前余额可作盘点默认值', async t => {
  const f = fixture(t), helper = f.load('@/services/inventory-count-documents')
  assert.throws(() => helper.countFormLine(source(), code, 'x', { ...label(), warehouseCode: 'W2' }))
  const duplicate = source(); duplicate.lines.push({ ...duplicate.lines[0], id: 42 }); duplicate.document.lineCount = 2
  assert.throws(() => helper.countFormLine(duplicate, code, 'x', label()))
  const aux = source(); aux.lines[0].requiresAuxiliaryUnit = true
  assert.throws(() => helper.countFormLine(aux, code, 'x', label()))
  const row = helper.countFormLine(source(), code, 'x', label())
  assert.equal(row.requestedQty, 10); helper.requireCountForm(source(), [row])
  row.countQuantityConfirmed = false
  assert.throws(() => helper.requireCountForm(source(), [row]))
})

test('盘点单位跟随原单物料，不比较旧标签的单位文字或编码', async t => {
  const f = fixture(t), helper = f.load('@/services/inventory-count-documents')
  const src = source(); src.lines[0].unitCode = '0101'
  for (const unitCode of ['张', 'U2', '']) {
    const row = helper.countFormLine(src, code, 'x', { ...label(), unitCode })
    assert.equal(row.unitCode, '0101')
    assert.equal(row.unitName, '张')
    assert.throws(() => helper.countFormLine(src, code, 'x', { ...label(), unitCode, materialCode: 'M2' }))
  }
})

test('盘点默认实时余额并显示单位名称，修改和联网复核不覆盖盘点量', async t => {
  const f = fixture(t), v = await f.select()
  v.goToScanStep(); await v.addBarcode(code)
  assert.equal(v.editorFieldValue(v.editingLine.value, 'body.quantity'), 10)
  assert.equal(v.bodyDisplayValue(v.editingLine.value, 'body.unitCode'), '张')
  assert.equal(v.bodyDisplayValue({ ...v.editingLine.value, unitName: undefined }, 'body.unitCode'), '张')
  assert.equal(v.fieldVisible('body.availableQty'), false)
  assert.equal(v.readonlyEditorFields.value.some(field => field.key === 'body.availableQty'), false)
  await v.confirmEditingLine()
  assert.equal(v.lines.value[0].requestedQty, 10)
  v.openLineEditor(v.lines.value[0]); v.updateEditorField('body.quantity', '3')
  await v.confirmEditingLine(); await v.finishDocument()
  assert.equal(v.lines.value[0].requestedQty, 3)
  assert.equal(v.lines.value[0].unitName, '张')
  assert.equal((await f.docs())[0].payload.lines[0].requestedQty, 3)
  const form = f.load('@/views/OutboundDocumentView.vue').default
  const rendered = await SSR.renderToString(Vue.createSSRApp({ ...form, setup(props, context) {
    const state = form.setup(props, context)
    state.currentStep.value = 2; state.lines.value = v.lines.value
    return state
  } }, { documentType: 'inventory-count' }))
  assert.doesNotMatch(rendered, /标签数量|可用量/)
  assert.match(rendered, /<dt>单位<\/dt><dd>张<\/dd>/)
})

test('合法零余额默认盘点0，断网无余额不默认，复核保留手改数量', async t => {
  const f = fixture(t), helper = f.load('@/services/inventory-count-documents')
  const zero = helper.countFormLine(source(), code, 'x', { ...label(), availableQty: 0, status: 'USED' })
  assert.equal(zero.requestedQty, 0); assert.equal(zero.countQuantityConfirmed, true)
  const offline = helper.countFormLine(source(), code, 'x')
  assert.equal(offline.countQuantityConfirmed, false); assert.equal(offline.availableQty, null)
  const direct = helper.countFormLine(null, code, 'x', { ...label(), unitCode: '张' }, 'W1')
  assert.equal(direct.unitName, '张'); assert.equal(direct.requestedQty, 10)
  const result = helper.applyDirectCountChecks('W1', [{ ...direct, requestedQty: 2 }],
    [{ code, status: 'MATCHED', label: { ...label(), availableQty: 8 } }])
  assert.equal(result[0].requestedQty, 2)
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
