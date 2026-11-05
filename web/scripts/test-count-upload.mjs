import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'

const url = text => 'data:text/javascript;base64,' + Buffer.from(text).toString('base64')
const compiled = async file => ts.transpileModule(await readFile(new URL(file, import.meta.url), 'utf8'), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
}).outputText
const modelUrl = url(await compiled('../src/services/inventory-count-model.ts'))

async function fixture() {
  const id = 'count-upload-test-' + Math.random()
  globalThis[id] = { requests: [], mutations: [] }
  const state = globalThis[id]
  const api = url(`export const apiRequest = async (path, options) => {
    globalThis[${JSON.stringify(id)}].requests.push({path,options});
    return {submissionGroupId:'group1',status:'SUCCESS',operations:[]}; }`)
  const auth = url('export const readStoredAuthSession = () => ({user:{id:"counter",name:"User"}})')
  const drafts = url(`const state=globalThis[${JSON.stringify(id)}];
    export const beginOutboundDocumentUpload=async()=>{state.mutations.push('begin');return 'stable1'};
    export const markOutboundDocumentSubmitted=async(id,group)=>{state.mutations.push('submitted');return {id,group}};
    export const markOutboundDocumentUploadFailed=async()=>state.mutations.push('failed');
    export const updateOutboundDocumentGroup=async()=>{};`)
  const code = (await compiled('../src/services/outbound-document-upload.ts'))
    .replace("from '@/api/client'", `from '${api}'`)
    .replace("from '@/services/auth-session'", `from '${auth}'`)
    .replace("from '@/services/outbound-document-drafts'", `from '${drafts}'`)
    .replace("from '@/services/inventory-count-model'", `from '${modelUrl}'`)
  const module = await import(url(code))
  return { state, upload: module.uploadOutboundDocument, close: () => delete globalThis[id] }
}

function document(account = '888', source = true) {
  return { id: 'draft1', documentType: 'inventory-count', payload: {
    documentDate: '2026-11-05', warehouseCode: 'W1', documentDirection: 'BLUE',
    inventoryCountContext: { accountId: account, databaseName: `UFDATA_${account}_2021` },
    inventoryCountSource: source ? { accountId: account, document: { id: 12 }, fingerprint: 'source1', lines: [] } : undefined,
    selectedSourceLines: source ? [{ productCode: 'M1', orderQuantity: 1, sourceLineId: 41 }] : [],
    headerFieldValues: { 'header.countByLocation': '0', 'header.inCategoryCode': '107', 'header.outCategoryCode': '207' },
    lines: [{ labelCode: 'TAG1', objectType: 'PRODUCT_LABEL', materialCode: 'M1', unitCode: '0101', requestedQty: 0 },
      { labelCode: 'TAG2', objectType: 'PRODUCT_LABEL', materialCode: 'M1', unitCode: '0101', requestedQty: 5 }],
  } }
}

test('888盘点上传保留零数量和多标签，不按原单数量拆分，强制只保存未审核', async t => {
  const f = await fixture(); t.after(f.close)
  const doc = document()
  await f.upload(doc, true)
  assert.equal(f.state.requests.length, 1)
  const call = f.state.requests[0], body = JSON.parse(call.options.body)
  assert.match(call.path, /inventory-count\/submit\?autoApprove=false$/)
  assert.deepEqual(body.lines.map(l => l.requestedQty), [0, 5])
  assert.equal(body.inventoryCount.source.document.id, 12)
  assert.equal(body.inventoryCount.countByLocation, false)
  assert.equal(body.inventoryCount.accountId, '888')
  assert.equal(body.clientRequestId, 'stable1')
  assert.deepEqual(f.state.mutations, ['begin', 'submitted'])
})

test('无原单上传保持真实仓库绑定，不伪造来源；008和旧草稿在修改状态前拒绝', async t => {
  const f = await fixture(); t.after(f.close)
  await assert.rejects(f.upload(document('008'), false), /仅开放888/)
  await assert.rejects(f.upload({ id: 'legacy', documentType: 'inventory-count' }, false), /仅开放888/)
  assert.equal(f.state.requests.length, 0); assert.equal(f.state.mutations.length, 0)
  await f.upload(document('888', false), false)
  const body = JSON.parse(f.state.requests[0].options.body)
  assert.equal(body.inventoryCount.source, null)
  assert.equal(body.header.warehouseCode, 'W1')
})
