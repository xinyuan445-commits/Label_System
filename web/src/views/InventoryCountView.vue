<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'
import { getPdaBridge } from '@/bridge/pda-bridge'
import { readSession } from '@/services/auth'
import { getRuntimeConfig } from '@/services/runtime-config'
import { playScanFeedback } from '@/services/shell-config'
import { createUniqueId } from '@/utils/unique-id'
import { downloadInventoryCount, searchInventoryCounts, validateInventoryCount, type CountSummary } from '@/services/inventory-count-api'
import { inventoryCountStore } from '@/services/inventory-count-store'
import { sumCountQuantities, type CountDraft, type CountObservation, type CountScope, type CountTask } from '@/services/inventory-count-model'

const router = useRouter(), route = useRoute()
const draft = ref<CountDraft | null>(null), tasks = ref<CountTask[]>([]), sources = ref<CountSummary[]>([])
const documentNo = ref(''), startDate = ref(''), endDate = ref(''), barcode = ref('')
const message = ref(''), error = ref(false), searching = ref(false), validating = ref(false)
const selecting = ref(false), hasMore = ref(false), online = ref(navigator.onLine)
const shellSupportsCollection = ref(window.parent === window)
const visibleCount = ref(30), visibleLines = ref(30), pendingWrites = ref(0)
const quantities = reactive<Record<string, string>>({})
const editRevisions = new Map<string, number>()
let stopScan: (() => void) | undefined, stopBack: (() => void) | undefined, refreshSequence = 0

function scope(): CountScope {
  const user = readSession()?.user
  if (!user) throw new Error('请联网重新登录，采集记录会保留')
  const config = getRuntimeConfig()
  return { apiBaseUrl: config.apiBaseUrl, environment: config.environment, userId: user.id }
}
function notify(text: string, failed = false): void { message.value = text; error.value = failed }
function shellCollection(enabled: boolean): void {
  if (window.parent !== window) window.parent.postMessage(
    { source: 'label-system-h5', type: 'offline-collection', payload: { enabled } }, 'http://127.0.0.1:18765')
}
function reason(value: unknown): string { return value instanceof Error ? value.message : '操作失败，请重试' }
async function listTasks(): Promise<void> { tasks.value = await inventoryCountStore.list(scope()) }

async function refresh(taskId: string): Promise<void> {
  const sequence = ++refreshSequence
  const next = await inventoryCountStore.read(scope(), taskId)
  if (sequence !== refreshSequence || (draft.value && draft.value.task.id !== taskId)) return
  draft.value = next
  for (const row of next.observations) {
    if (!editRevisions.has(row.codeKey)) quantities[row.codeKey] = row.quantity ?? ''
  }
}
const dirty = computed(() => draft.value?.observations.some(row =>
  quantities[row.codeKey] !== (row.quantity ?? '')) ?? false)
function mayLeave(): boolean {
  if (pendingWrites.value || validating.value || selecting.value) { notify('正在保存或核验，请稍候', true); return false }
  return !dirty.value || window.confirm('有数量尚未保存，确定离开？')
}
async function activateTask(id: string): Promise<void> {
  editRevisions.clear(); for (const key of Object.keys(quantities)) delete quantities[key]
  draft.value = null; visibleCount.value = 30; visibleLines.value = 30
  await refresh(id); shellCollection(true); notify('已恢复本机记录')
}
async function openTask(id: string): Promise<void> {
  if (!mayLeave()) return
  selecting.value = true
  try { await activateTask(id) }
  catch (failure) { notify(reason(failure), true) }
  finally { selecting.value = false }
}
async function choose(id: number): Promise<void> {
  if (!mayLeave()) return
  selecting.value = true
  try {
    const source = await downloadInventoryCount(id)
    const task = await inventoryCountStore.create(scope(), source, createUniqueId('count'))
    await activateTask(task.id); await listTasks()
  } catch (failure) { notify(reason(failure), true) }
  finally { selecting.value = false }
}
async function search(): Promise<void> {
  if (searching.value) return
  searching.value = true
  try {
    if (startDate.value && endDate.value && startDate.value > endDate.value) throw new Error('开始日期不能晚于结束日期')
    const found = await searchInventoryCounts(documentNo.value.trim(), startDate.value, endDate.value)
    sources.value = found.items; hasMore.value = found.hasMore
    notify(found.items.length ? '' : '没有找到未审核盘点单')
  } catch (failure) { notify(reason(failure), true) }
  finally { searching.value = false }
}
async function scan(raw: string): Promise<void> {
  const taskId = draft.value?.task.id
  if (!taskId || validating.value) { notify(taskId ? '正在核验，请稍候' : '请先选择盘点单', true); playScanFeedback('error'); return }
  pendingWrites.value++
  try {
    await inventoryCountStore.scan(scope(), taskId, raw)
    // Feedback only after IndexedDB commits; the barcode's printed quantity is never read.
    playScanFeedback('success'); barcode.value = ''; notify('已保存本机，请填写实盘数量')
  } catch (failure) { notify(reason(failure), true); playScanFeedback('error') }
  finally {
    try { await refresh(taskId) } catch (failure) { notify(reason(failure), true) }
    pendingWrites.value--
  }
}
function inputQuantity(row: CountObservation, event: Event): void {
  if (!editRevisions.has(row.codeKey)) editRevisions.set(row.codeKey, row.revision)
  quantities[row.codeKey] = (event.target as HTMLInputElement).value
}
async function saveQuantity(row: CountObservation): Promise<void> {
  if (!draft.value || validating.value || pendingWrites.value) return
  const id = draft.value.task.id
  pendingWrites.value++
  try {
    await inventoryCountStore.quantity(scope(), id, row.code,
      quantities[row.codeKey] ?? '', editRevisions.get(row.codeKey) ?? row.revision)
    editRevisions.delete(row.codeKey); notify('数量已保存本机')
  } catch (failure) { notify(reason(failure), true) }
  finally { try { await refresh(id) } catch (failure) { notify(reason(failure), true) }; pendingWrites.value-- }
}
async function remove(row: CountObservation): Promise<void> {
  if (!draft.value || validating.value || pendingWrites.value || !window.confirm('移除此条本机采集记录？')) return
  const id = draft.value.task.id
  pendingWrites.value++
  try {
    await inventoryCountStore.remove(scope(), id, row.code, row.revision)
    editRevisions.delete(row.codeKey); delete quantities[row.codeKey]; notify('已移除')
  } catch (failure) { notify(reason(failure), true) }
  finally { try { await refresh(id) } catch (failure) { notify(reason(failure), true) }; pendingWrites.value-- }
}
async function validate(): Promise<void> {
  if (!draft.value || pendingWrites.value || validating.value) return
  if (dirty.value) { notify('请先保存修改的数量', true); return }
  if (!draft.value.observations.length) { notify('请先扫描标签', true); return }
  validating.value = true
  const id = draft.value.task.id, token = createUniqueId('check')
  try {
    const frozen = await inventoryCountStore.beginValidation(scope(), id, token)
    await refresh(id)
    const checked = await validateInventoryCount(frozen)
    await inventoryCountStore.applyValidation(scope(), frozen, token, checked.items)
    notify(checked.items.every(item => item.status === 'MATCHED') ? '已核验，采集记录保存在本机' : '部分标签需核对',
      checked.items.some(item => item.status !== 'MATCHED'))
  } catch (failure) { notify(reason(failure), true) }
  finally { validating.value = false; try { await refresh(id) } catch (failure) { notify(reason(failure), true) } }
}
async function closeTask(): Promise<void> {
  if (!mayLeave()) return
  ++refreshSequence; draft.value = null; editRevisions.clear()
  try { await listTasks() } catch (failure) { notify(reason(failure), true) }
}
async function back(): Promise<void> {
  if (draft.value) { await closeTask(); return }
  await router.replace({ name: route.query.origin === 'all-functions' ? 'all-functions' : 'home' })
}
const filled = computed(() => draft.value?.observations.filter(row => row.quantity !== null).length ?? 0)
const matchedTotals = computed(() => {
  const totals = new Map<number, Array<string | null>>()
  if (!draft.value?.task.validatedAt) return new Map<number, string>()
  for (const row of draft.value.observations) {
    if (row.check?.status !== 'MATCHED' || !row.check.sourceLineId) continue
    const values = totals.get(row.check.sourceLineId) ?? []
    values.push(row.quantity); totals.set(row.check.sourceLineId, values)
  }
  return new Map([...totals].map(([line, values]) => [line, sumCountQuantities(values)]))
})
function rowStatus(row: CountObservation): string {
  if (row.quantity === null) return '待填数量'
  if (!draft.value?.task.validatedAt || !row.check) return '待核验'
  return row.check.status === 'MATCHED' ? '已匹配' : row.check.message
}
function networkChanged(): void { online.value = navigator.onLine }
function shellReady(event: MessageEvent): void {
  if (event.source === window.parent && event.origin === 'http://127.0.0.1:18765' &&
      event.data?.source === 'label-system-pda' && event.data?.type === 'offline-collection-ready') {
    shellSupportsCollection.value = event.data.payload?.enabled === true
  }
}
function beforeUnload(event: BeforeUnloadEvent): void {
  if (dirty.value || pendingWrites.value) { event.preventDefault(); event.returnValue = '' }
}
onBeforeRouteLeave(() => mayLeave())
onMounted(async () => {
  stopScan = getPdaBridge().subscribeScan(event => { void scan(event.barcode) })
  stopBack = getPdaBridge().subscribeBack(() => { void back() })
  window.addEventListener('online', networkChanged); window.addEventListener('offline', networkChanged)
  window.addEventListener('beforeunload', beforeUnload)
  window.addEventListener('message', shellReady)
  try { await listTasks(); shellCollection(true) } catch (failure) { notify(reason(failure), true) }
})
onBeforeUnmount(() => {
  stopScan?.(); stopBack?.(); shellCollection(false)
  window.removeEventListener('online', networkChanged); window.removeEventListener('offline', networkChanged)
  window.removeEventListener('beforeunload', beforeUnload)
  window.removeEventListener('message', shellReady)
})
</script>

<template>
  <main class="count-page">
    <header><button type="button" class="back" aria-label="返回" @click="back">‹</button><h1>盘点单</h1>
      <span class="network">{{ online ? '本机采集' : '离线采集' }}</span></header>
    <p class="mode">采集模式 · 暂不上传ERP</p>
    <p v-if="!shellSupportsCollection" class="mode">离线续扫需更新PDA应用</p>
    <p v-if="message" class="message" :class="{ error }" role="status" aria-live="polite">{{ message }}</p>
    <template v-if="!draft">
      <section class="card">
        <h2>选择原盘点单</h2>
        <form @submit.prevent="search">
          <input v-model="documentNo" aria-label="盘点单号" placeholder="盘点单号" maxlength="100">
          <div class="dates"><label>开始日期<input v-model="startDate" type="date"></label><label>结束日期<input v-model="endDate" type="date"></label></div>
          <button type="submit" :disabled="searching || selecting">{{ searching ? '查询中…' : '查询未审核单据' }}</button>
        </form>
        <p v-if="hasMore" class="hint">结果超过100条，请缩小查询范围</p>
        <button v-for="source in sources" :key="source.id" class="source" type="button" :disabled="selecting" @click="choose(source.id)">
          <strong>{{ source.documentNo }}</strong><span>{{ source.documentDate?.slice(0, 10) }} · {{ source.partnerName || source.warehouseCode }} · {{ source.lineCount }}行</span>
        </button>
      </section>
      <section class="card">
        <h2>本机盘点记录</h2><p v-if="!tasks.length" class="hint">暂无记录</p>
        <button v-for="task in tasks" :key="task.id" class="source" type="button" @click="openTask(task.id)">
          <strong>{{ task.source.document.documentNo }}</strong><span>{{ task.source.accountId }} · {{ task.observationCount }}张 · 继续采集</span>
        </button>
      </section>
    </template>
    <template v-else>
      <section class="card">
        <h2>{{ draft.task.source.document.documentNo }}</h2>
        <p class="hint">{{ draft.task.source.accountId }} · {{ draft.task.source.document.warehouseCode }} · 已采集{{ draft.observations.length }}张 / 已填{{ filled }}张</p>
        <form class="scan" @submit.prevent="scan(barcode)"><input v-model="barcode" aria-label="扫描标签" placeholder="扫描或输入标签码" maxlength="100" :disabled="validating"><button type="submit" :disabled="validating">添加</button></form>
        <button type="button" :disabled="validating || !!pendingWrites" @click="validate">{{ validating ? '核验中…' : '联网核验' }}</button>
      </section>
      <section class="card">
        <h2>采集明细</h2><p v-if="!draft.observations.length" class="hint">请扫描标签</p>
        <article v-for="row in draft.observations.slice(0, visibleCount)" :key="row.codeKey" class="observation">
          <div class="code">{{ row.code }}</div><p class="hint">{{ rowStatus(row) }}<span v-if="draft.task.validatedAt && row.check?.balance !== null && row.check?.balance !== undefined"> · 当前余额{{ row.check.balance }}</span></p>
          <form class="quantity" @submit.prevent="saveQuantity(row)">
            <label>实盘数量<input :value="quantities[row.codeKey]" type="text" inputmode="decimal" maxlength="19" :disabled="validating || !!pendingWrites" @input="inputQuantity(row, $event)"></label>
            <button type="submit" :disabled="validating || !!pendingWrites">保存</button><button type="button" class="secondary" :disabled="validating || !!pendingWrites" @click="remove(row)">移除</button>
          </form>
        </article>
        <button v-if="draft.observations.length > visibleCount" type="button" class="secondary" @click="visibleCount += 30">显示更多</button>
      </section>
      <section class="card">
        <h2>原单明细</h2>
        <article v-for="line in draft.task.source.lines.slice(0, visibleLines)" :key="line.id" class="observation">
          <strong>{{ line.materialCode }}</strong><div>{{ line.materialName }}</div><p class="hint">{{ line.batchNo || '无批号' }} · {{ line.locationCode || '无货位' }} · 账面{{ line.bookQuantity }}</p>
          <p class="hint">已匹配采集量：{{ matchedTotals.get(line.id) ?? '—' }}</p>
        </article>
        <button v-if="draft.task.source.lines.length > visibleLines" class="secondary" type="button" @click="visibleLines += 30">显示更多</button>
      </section>
    </template>
  </main>
</template>

<style scoped>
.count-page{min-height:100dvh;padding:calc(16px + var(--app-safe-top,0px)) 14px calc(30px + env(safe-area-inset-bottom));background:#f3f6fa;color:#17365d}
header,.card,.mode,.message{width:min(100%,760px);margin-inline:auto;box-sizing:border-box}header{display:flex;align-items:center;gap:12px;margin-bottom:10px}h1{font-size:24px;margin:0}h2{font-size:18px;margin:0 0 14px}.network{margin-left:auto;font-size:13px}.back{font-size:32px;background:#fff;color:#17365d;min-width:44px;padding:0}.mode{font-size:13px;color:#8a5b1a}.card{background:#fff;padding:18px;border-radius:15px;margin-top:14px}.message{padding:12px;background:#eaf5ee;border-radius:10px;overflow-wrap:anywhere}.message.error{color:#a32626;background:#fff0ee}
input{box-sizing:border-box;min-width:0;width:100%;height:44px;padding:0 10px;border:1px solid #ccd8e6;border-radius:8px;font-size:16px;background:#fff;color:#17365d}button{min-height:44px;padding:9px 15px;border:0;border-radius:9px;background:#285d9d;color:#fff;font-size:15px}button:disabled{opacity:.55}.secondary{background:#eaf0f7;color:#34577d}form>button{margin-top:10px}.dates{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:10px}label{font-size:13px;display:grid;gap:5px}.source{display:flex;flex-direction:column;align-items:flex-start;gap:7px;width:100%;margin-top:10px;background:#f0f5fb;color:#17365d;text-align:left}.source span,.hint{font-size:13px;color:#687b91}.scan{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:8px;margin-bottom:12px}.scan button{margin:0}.observation{padding:14px 0;border-top:1px solid #e8edf4}.code{font:14px ui-monospace,monospace;overflow-wrap:anywhere}.quantity{display:flex;align-items:end;gap:8px}.quantity label{flex:1;min-width:0}.quantity button{flex-shrink:0}.quantity>button{margin:0}.hint{overflow-wrap:anywhere}
</style>
