<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import DateField from '@/components/DateField.vue'
import { getPdaBridge } from '@/bridge/pda-bridge'
import { readSession } from '@/services/auth'
import { getRuntimeConfig } from '@/services/runtime-config'
import { playScanFeedback } from '@/services/shell-config'
import { createUniqueId } from '@/utils/unique-id'
import { downloadInventoryCount, searchInventoryCounts, validateInventoryCount, type CountSummary } from '@/services/inventory-count-api'
import { inventoryCountStore } from '@/services/inventory-count-store'
import { sumCountQuantities, type CountDraft, type CountObservation, type CountScope, type CountTask } from '@/services/inventory-count-model'

type Page = 'header' | 'source' | 'records' | 'scan' | 'lines'
const router = useRouter(), route = useRoute()
const page = ref<Page>('header'), recordsReturn = ref<Page>('header')
const draft = ref<CountDraft | null>(null), tasks = ref<CountTask[]>([]), sources = ref<CountSummary[]>([])
const selectedSourceId = ref<number | null>(null)
const documentNo = ref(''), startDate = ref(''), endDate = ref(''), barcode = ref('')
const message = ref(''), error = ref(false), searching = ref(false), searched = ref(false), validating = ref(false)
const selecting = ref(false), confirming = ref(false), hasMore = ref(false), online = ref(navigator.onLine)
const shellSupportsCollection = ref(window.parent === window)
const visibleCount = ref(30), visibleLines = ref(30), visibleTasks = ref(30), pendingWrites = ref(0)
const editing = ref<{ row: CountObservation; quantity: string } | null>(null)
const editorViewport = ref<Record<string, string>>({})
const startDatePicker = ref<InstanceType<typeof DateField> | null>(null)
const endDatePicker = ref<InstanceType<typeof DateField> | null>(null)
let stopScan: (() => void) | undefined, stopBack: (() => void) | undefined, refreshSequence = 0, searchSequence = 0
let messageTimer: number | undefined, alive = true

const busy = computed(() => !!pendingWrites.value || validating.value || selecting.value)
const dirty = computed(() => !!editing.value && editing.value.quantity !== (editing.value.row.quantity ?? ''))
const canScan = computed(() => page.value === 'scan' && !!draft.value && !editing.value &&
  !validating.value && !selecting.value && !confirming.value)
const title = computed(() => ({ header: '盘点单', source: '选择原单', records: '采集记录', scan: '盘点单', lines: '原单明细' })[page.value])
const filled = computed(() => draft.value?.observations.filter(row => row.quantity !== null).length ?? 0)
const scanRows = computed(() => draft.value?.observations.slice(-visibleCount.value).reverse() ?? [])

function scope(): CountScope {
  const user = readSession()?.user
  if (!user) throw new Error('请联网重新登录，采集记录会保留')
  const config = getRuntimeConfig()
  return { apiBaseUrl: config.apiBaseUrl, environment: config.environment, userId: user.id }
}
function notify(text: string, failed = false): void {
  window.clearTimeout(messageTimer)
  message.value = text; error.value = failed
  if (text && !failed) messageTimer = window.setTimeout(() => { message.value = '' }, 3500)
}
function shellCollection(enabled: boolean): void {
  if (window.parent !== window) window.parent.postMessage(
    { source: 'label-system-h5', type: 'offline-collection', payload: { enabled } }, 'http://127.0.0.1:18765')
}
function reason(value: unknown): string { return value instanceof Error ? value.message : '操作失败，请重试' }
function confirm(text: string): boolean {
  confirming.value = true
  try { return window.confirm(text) } finally { confirming.value = false }
}
function mayLeave(): boolean {
  if (busy.value) { notify('正在保存或核验，请稍候', true); return false }
  return !dirty.value || confirm('有数量尚未保存，确定离开？')
}
function navigate(next: Page): void {
  page.value = next; notify('')
  window.scrollTo({ top: 0 })
}
async function listTasks(): Promise<void> { tasks.value = await inventoryCountStore.list(scope()) }
async function refresh(taskId: string): Promise<void> {
  const sequence = ++refreshSequence
  const next = await inventoryCountStore.read(scope(), taskId)
  if (alive && sequence === refreshSequence && draft.value?.task.id === taskId) draft.value = next
}
async function activateTask(id: string, next: Page): Promise<void> {
  const restored = await inventoryCountStore.read(scope(), id)
  ++refreshSequence
  draft.value = restored; editing.value = null; barcode.value = ''
  visibleCount.value = 30; visibleLines.value = 30
  navigate(next); shellCollection(true)
}
async function openTask(id: string): Promise<void> {
  if (!mayLeave()) return
  selecting.value = true
  try { await activateTask(id, 'scan'); notify('已恢复本机记录') }
  catch (failure) { notify(reason(failure), true) }
  finally { selecting.value = false }
}
async function openRecords(): Promise<void> {
  if (!mayLeave()) return
  recordsReturn.value = page.value
  selecting.value = true
  try { await listTasks(); visibleTasks.value = 30; navigate('records') }
  catch (failure) { notify(reason(failure), true) }
  finally { selecting.value = false }
}
function openSources(): void {
  if (!mayLeave()) return
  selectedSourceId.value = null; navigate('source')
}
async function choose(): Promise<void> {
  if (!selectedSourceId.value || !mayLeave()) return
  selecting.value = true
  try {
    const source = await downloadInventoryCount(selectedSourceId.value)
    const task = await inventoryCountStore.create(scope(), source, createUniqueId('count'))
    await activateTask(task.id, 'header')
  } catch (failure) { notify(reason(failure), true) }
  finally { selecting.value = false }
}
async function search(): Promise<void> {
  if (searching.value || selecting.value) return
  searching.value = true
  const sequence = ++searchSequence
  try {
    if (startDate.value && endDate.value && startDate.value > endDate.value) throw new Error('开始日期不能晚于结束日期')
    const found = await searchInventoryCounts(documentNo.value.trim(), startDate.value, endDate.value)
    if (!alive || page.value !== 'source' || sequence !== searchSequence) return
    sources.value = found.items; hasMore.value = found.hasMore; searched.value = true; selectedSourceId.value = null
    notify('')
  } catch (failure) {
    if (alive && page.value === 'source' && sequence === searchSequence) notify(reason(failure), true)
  } finally { searching.value = false }
}
function startScanning(): void {
  if (!draft.value) { notify('请先选择原单', true); return }
  if (mayLeave()) navigate('scan')
}
async function scan(raw: string): Promise<void> {
  if (!canScan.value) return
  const id = draft.value!.task.id
  pendingWrites.value++
  try {
    await inventoryCountStore.scan(scope(), id, raw)
    // The transaction must commit before a successful scan is acknowledged.
    playScanFeedback('success'); barcode.value = ''; notify('已保存本机')
  } catch (failure) { notify(reason(failure), true); playScanFeedback('error') }
  finally {
    try { await refresh(id) } catch (failure) { notify(reason(failure), true) }
    pendingWrites.value--
  }
}
function editRow(row: CountObservation): void {
  if (busy.value) return
  editing.value = { row: { ...row }, quantity: row.quantity ?? '' }; notify(''); resizeEditor()
}
function closeEditor(): void {
  if (!mayLeave()) return
  editing.value = null; notify('')
}
async function saveQuantity(): Promise<void> {
  if (!draft.value || !editing.value || busy.value) return
  const id = draft.value.task.id, entry = editing.value
  pendingWrites.value++
  try {
    await inventoryCountStore.quantity(scope(), id, entry.row.code, entry.quantity, entry.row.revision)
    editing.value = null; notify('数量已保存本机')
  } catch (failure) { notify(reason(failure), true) }
  finally { try { await refresh(id) } catch (failure) { notify(reason(failure), true) }; pendingWrites.value-- }
}
async function remove(): Promise<void> {
  if (!draft.value || !editing.value || busy.value || !confirm('移除此条本机采集记录？')) return
  const id = draft.value.task.id, row = editing.value.row
  pendingWrites.value++
  try {
    await inventoryCountStore.remove(scope(), id, row.code, row.revision)
    editing.value = null; notify('已移除')
  } catch (failure) { notify(reason(failure), true) }
  finally { try { await refresh(id) } catch (failure) { notify(reason(failure), true) }; pendingWrites.value-- }
}
async function validate(): Promise<void> {
  if (!draft.value || busy.value || editing.value) return
  if (!draft.value.observations.length) { notify('请先扫描标签', true); return }
  validating.value = true
  const id = draft.value.task.id, token = createUniqueId('check')
  try {
    const frozen = await inventoryCountStore.beginValidation(scope(), id, token)
    await refresh(id)
    const checked = await validateInventoryCount(frozen)
    await inventoryCountStore.applyValidation(scope(), frozen, token, checked.items)
    const failed = checked.items.some(item => item.status !== 'MATCHED')
    notify(failed ? '部分标签需核对' : '已核验，记录保存在本机', failed)
  } catch (failure) { notify(reason(failure), true) }
  finally { validating.value = false; try { await refresh(id) } catch (failure) { notify(reason(failure), true) } }
}
async function back(): Promise<void> {
  if (startDatePicker.value?.close() || endDatePicker.value?.close()) return
  if (editing.value) { closeEditor(); return }
  if (!mayLeave()) return
  if (page.value === 'source') { ++searchSequence; navigate('header'); return }
  if (page.value === 'records') { navigate(recordsReturn.value); return }
  if (page.value === 'lines') { navigate('scan'); return }
  if (page.value === 'scan') { navigate('header'); return }
  await router.replace({ name: route.query.origin === 'all-functions' ? 'all-functions' : 'home' })
}
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
function resizeEditor(): void {
  const viewport = window.visualViewport
  editorViewport.value = viewport ? { top: String(viewport.offsetTop) + 'px', height: String(viewport.height) + 'px' } : {}
}
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
onBeforeRouteUpdate((to, from) => to.params.code === from.params.code || mayLeave())
onMounted(async () => {
  stopScan = getPdaBridge().subscribeScan(event => {
    if (canScan.value && document.visibilityState !== 'hidden') void scan(event.barcode)
  })
  stopBack = getPdaBridge().subscribeBack(() => { void back() })
  window.addEventListener('online', networkChanged); window.addEventListener('offline', networkChanged)
  window.addEventListener('beforeunload', beforeUnload); window.addEventListener('message', shellReady)
  window.visualViewport?.addEventListener('resize', resizeEditor)
  window.visualViewport?.addEventListener('scroll', resizeEditor)
  try { await listTasks(); shellCollection(true) } catch (failure) { notify(reason(failure), true) }
})
onBeforeUnmount(() => {
  alive = false; stopScan?.(); stopBack?.(); shellCollection(false); window.clearTimeout(messageTimer)
  window.removeEventListener('online', networkChanged); window.removeEventListener('offline', networkChanged)
  window.removeEventListener('beforeunload', beforeUnload); window.removeEventListener('message', shellReady)
  window.visualViewport?.removeEventListener('resize', resizeEditor)
  window.visualViewport?.removeEventListener('scroll', resizeEditor)
})
</script>

<template>
  <main class="count-page">
    <header class="topbar-shell">
      <div class="topbar">
        <button class="back-button" type="button" aria-label="返回" @click="back">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg>
        </button>
        <h1>{{ title }}</h1>
        <button v-if="page === 'scan'" class="text-action" type="button" :disabled="busy" @click="openRecords">采集记录</button>
      </div>
    </header>

    <div class="page-content">
      <p v-if="message && !editing" class="message" :class="{ error }" role="status">{{ message }}</p>

      <section v-if="page === 'header'" class="card">
        <div class="form-grid">
          <div class="field full-width">
            <span><i>*</i>源单</span>
            <button class="source-button" type="button" :disabled="busy" @click="openSources">
              <span v-if="draft" class="selected-source"><strong>{{ draft.task.source.document.documentNo }}</strong><small>盘点单</small></span>
              <span v-else class="placeholder">请选择</span>
              <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9 6 6 6-6 6" /></svg>
            </button>
          </div>
          <div class="field"><span>日期</span><div class="readonly">{{ draft?.task.source.document.documentDate?.slice(0, 10) || '—' }}</div></div>
          <div class="field"><span>仓库</span><div class="readonly">{{ draft?.task.source.document.warehouseCode || '—' }}</div></div>
          <div class="field"><span>账套</span><div class="readonly">{{ draft?.task.source.accountId || '—' }}</div></div>
          <div class="field"><span>原单明细</span><div class="readonly">{{ draft ? draft.task.source.lines.length + '行' : '—' }}</div></div>
        </div>
        <p class="mode">采集模式 · 暂不上传ERP</p>
      </section>

      <template v-else-if="page === 'source'">
        <section class="card">
          <form class="query-form" @submit.prevent="search">
            <div class="form-grid">
              <label class="field"><span>开始日期</span><DateField ref="startDatePicker" v-model="startDate" aria-label="开始日期" /></label>
              <label class="field"><span>结束日期</span><DateField ref="endDatePicker" v-model="endDate" aria-label="结束日期" /></label>
            </div>
            <div class="field"><label for="count-source-no">单号</label><div class="search-row"><input id="count-source-no" v-model="documentNo" placeholder="输入单号" maxlength="100"><button class="primary-action" type="submit" :disabled="searching || selecting">{{ searching ? '查询中' : '查询' }}</button></div></div>
          </form>
        </section>
        <div class="section-heading"><h2>未审核单据</h2><span>{{ sources.length }}张</span></div>
        <p v-if="hasMore" class="hint">结果超过100条，请缩小查询范围</p>
        <button v-for="source in sources" :key="source.id" class="result-card" :class="{ selected: selectedSourceId === source.id }" :aria-pressed="selectedSourceId === source.id" type="button" :disabled="selecting || searching" @click="selectedSourceId = source.id">
          <span class="check" aria-hidden="true">{{ selectedSourceId === source.id ? '✓' : '' }}</span>
          <span class="result-main"><strong>{{ source.documentNo }}</strong><span>{{ source.warehouseCode }}</span><small>{{ source.documentDate?.slice(0, 10) }}</small></span><span class="badge">{{ source.lineCount }}行</span>
        </button>
        <p v-if="!sources.length" class="empty-state">{{ searched ? '没有找到未审核盘点单' : '请输入条件查询' }}</p>
      </template>

      <template v-else-if="page === 'records'">
        <div class="section-heading"><h2>本机记录</h2><span>{{ tasks.length }}张</span></div>
        <button v-for="task in tasks.slice(0, visibleTasks)" :key="task.id" class="task-card" type="button" :disabled="busy" @click="openTask(task.id)">
          <strong>{{ task.source.document.documentNo }}</strong><span>{{ task.source.accountId }} · {{ task.source.document.warehouseCode }}</span><span class="task-meta"><span>{{ task.observationCount }}张标签</span><span class="link-label">继续采集 ›</span></span>
        </button>
        <p v-if="!tasks.length" class="empty-state">暂无本机采集记录</p>
        <button v-if="tasks.length > visibleTasks" class="more-button" type="button" @click="visibleTasks += 30">显示更多</button>
      </template>

      <template v-else-if="page === 'scan' && draft">
        <section class="card scan-card">
          <div class="document-summary"><strong>{{ draft.task.source.document.documentNo }}</strong><span>{{ draft.task.source.document.warehouseCode }}</span></div>
          <div class="collection-status"><span>{{ online ? '本机采集' : '离线采集' }}</span><span>暂不上传ERP</span></div>
          <form class="search-row" @submit.prevent="scan(barcode)"><input v-model="barcode" aria-label="扫描标签" placeholder="扫描或输入标签码" maxlength="100" :disabled="!canScan"><button class="primary-action" type="submit" :disabled="!canScan">添加</button></form>
          <p v-if="!shellSupportsCollection" class="mode">离线续扫需更新PDA应用</p>
        </section>
        <div class="section-heading"><h2>扫码记录</h2><span>已填{{ filled }}张</span></div>
        <button v-for="row in scanRows" :key="row.codeKey" class="observation-card" type="button" :disabled="busy" @click="editRow(row)">
          <span class="code">{{ row.code }}</span>
          <span v-if="draft.task.validatedAt && row.check?.materialName" class="material-name">{{ row.check.materialName }}</span>
          <span class="row-summary"><span class="row-status">{{ rowStatus(row) }}</span><strong>{{ row.quantity === null ? '待填' : row.quantity }}<small> 实盘</small></strong><span class="chevron">›</span></span>
        </button>
        <p v-if="!draft.observations.length" class="empty-state">请扫描标签</p>
        <button v-if="draft.observations.length > visibleCount" class="more-button" type="button" @click="visibleCount += 30">显示更多</button>
      </template>

      <template v-else-if="page === 'lines' && draft">
        <section class="card document-summary"><strong>{{ draft.task.source.document.documentNo }}</strong><span>{{ draft.task.source.lines.length }}行</span></section>
        <article v-for="line in draft.task.source.lines.slice(0, visibleLines)" :key="line.id" class="card source-line">
          <strong>{{ line.materialCode }}</strong><span>{{ line.materialName }}</span><p class="hint">{{ line.batchNo || '无批号' }} · {{ line.locationCode || '无货位' }}</p>
          <dl><div><dt>账面</dt><dd>{{ line.bookQuantity }}</dd></div><div><dt>已匹配采集量</dt><dd>{{ matchedTotals.get(line.id) ?? '—' }}</dd></div></dl>
        </article>
        <button v-if="draft.task.source.lines.length > visibleLines" class="more-button" type="button" @click="visibleLines += 30">显示更多</button>
      </template>
    </div>

    <footer v-if="page === 'header' || page === 'source' || page === 'scan'" class="action-shell" :class="{ 'scan-actions': page === 'scan' }">
      <template v-if="page === 'header'"><button class="secondary-action" type="button" :disabled="busy" @click="openRecords">采集记录</button><button class="primary-action" type="button" :disabled="busy || !draft" @click="startScanning">下一步</button></template>
      <template v-else-if="page === 'source'"><span class="hint">{{ selectedSourceId ? '已选择1张' : '请选择原单' }}</span><button class="primary-action" type="button" :disabled="selecting || searching || !selectedSourceId" @click="choose">{{ selecting ? '读取中' : '确定' }}</button></template>
      <template v-else><div class="summary"><strong>{{ draft?.observations.length ?? 0 }}张</strong><span>{{ pendingWrites ? '保存中' : '本机采集' }}</span></div><button class="secondary-action" type="button" :disabled="busy" @click="navigate('lines')">原单明细</button><button class="primary-action" type="button" :disabled="busy || !draft?.observations.length" @click="validate">{{ validating ? '核验中' : '联网核验' }}</button></template>
    </footer>

    <section v-if="editing" class="editor-page" :style="editorViewport" role="dialog" aria-modal="true" aria-label="编辑明细">
      <header class="editor-header"><div class="topbar"><button class="back-button" type="button" aria-label="返回明细列表" :disabled="busy" @click="closeEditor"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m15 18-6-6 6-6" /></svg></button><h2>编辑明细</h2></div></header>
      <form id="count-quantity-form" class="editor-body" @submit.prevent="saveQuantity">
        <p v-if="message" class="message" :class="{ error }" role="status">{{ message }}</p>
        <section class="card editor-fields">
          <div class="field"><span>标签码</span><div class="readonly code">{{ editing.row.code }}</div></div>
          <div class="field"><span>状态</span><div class="readonly">{{ rowStatus(editing.row) }}</div></div>
          <div v-if="draft?.task.validatedAt && editing.row.check?.balance != null" class="field"><span>当前余额</span><div class="readonly">{{ editing.row.check.balance }}</div></div>
          <label class="field"><span><i>*</i>实盘数量</span><input v-model="editing.quantity" aria-label="实盘数量" type="text" inputmode="decimal" maxlength="19" placeholder="请输入" :disabled="busy"></label>
        </section>
      </form>
      <footer class="editor-actions"><button class="danger-action" type="button" :disabled="busy" @click="remove">移除</button><button class="primary-action" type="submit" form="count-quantity-form" :disabled="busy">{{ pendingWrites ? '保存中' : '保存' }}</button></footer>
    </section>
  </main>
</template>

<style scoped>
.count-page{min-height:100vh;min-height:100dvh;padding:var(--app-header-height) 0 calc(94px + env(safe-area-inset-bottom));background:#f3f6fa;color:#21324a}
.topbar-shell{position:fixed;z-index:20;top:0;right:0;left:0;height:var(--app-header-height);border-bottom:1px solid #e4ebf3;background:#fff;box-shadow:0 3px 12px rgba(23,54,93,.05)}
.topbar,.page-content,.action-shell{width:min(100%,760px);margin-inline:auto}.topbar{height:100%;display:grid;grid-template-columns:44px minmax(0,1fr) auto;align-items:center;gap:12px;padding:var(--app-safe-top) 18px 0}
.back-button{width:42px;height:42px;display:grid;place-items:center;border:0;border-radius:12px;background:#f3f6fa;color:#365678}.back-button svg,.source-button svg{width:23px;height:23px;fill:none;stroke:currentColor;stroke-linecap:round;stroke-linejoin:round;stroke-width:2}
h1,h2{color:#17365d;font-size:21px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.text-action{min-height:44px;padding:0 8px;border:0;background:transparent;color:#2368c4;font-size:13px;font-weight:700}
.page-content{display:grid;gap:14px;padding:16px}.card{padding:18px;border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff;box-shadow:0 8px 24px rgba(23,54,93,.05)}
.form-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:16px 12px}.field{min-width:0;display:grid;gap:8px}.field>span,.field>label{color:#677b94;font-size:13px;font-weight:650}.full-width{grid-column:1/-1}i{margin-right:4px;color:#d34c4c;font-style:normal}
input,.readonly{width:100%;min-width:0;min-height:44px;padding:11px 12px;border:1px solid #dce5ef;border-radius:10px;background:#f7f9fc;color:#21324a;font-size:15px;overflow-wrap:anywhere}input{height:44px;background:#fff}.readonly{border-color:transparent}
.source-button{width:100%;min-height:54px;display:flex;align-items:center;justify-content:space-between;gap:12px;padding:10px 12px;border:1px solid #dce5ef;border-radius:11px;background:#f7f9fc;text-align:left;color:#21324a}.source-button svg{flex-shrink:0}.selected-source{min-width:0;display:grid;gap:4px}.selected-source strong{overflow-wrap:anywhere;font-size:15px}.selected-source small,.placeholder{color:#8b98a8;font-size:13px}
.mode{margin-top:14px;color:#8a5b1a;font-size:12px}.query-form{display:grid;gap:16px}.search-row{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:9px}.search-row .primary-action{min-width:72px;height:44px}
.primary-action,.secondary-action,.danger-action{min-height:44px;padding:0 14px;border:0;border-radius:11px;font-size:15px;font-weight:750}.primary-action{background:#2368c4;color:#fff}.secondary-action{background:#e9f2fd;color:#2368c4}.danger-action{background:#fbecec;color:#b64141}button:disabled{opacity:.48}
.hint{color:#7b899b;font-size:13px;overflow-wrap:anywhere}.section-heading{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:2px}.section-heading h2{font-size:16px}.section-heading>span{color:#7d8a9b;font-size:13px}
.result-card{width:100%;display:grid;grid-template-columns:24px minmax(0,1fr) auto;align-items:center;gap:11px;padding:14px;border:1px solid rgba(23,54,93,.09);border-radius:15px;background:#fff;text-align:left;box-shadow:0 8px 24px rgba(23,54,93,.05)}.result-card.selected{border-color:#79a9df;background:#f5f9fe}.check{width:22px;height:22px;display:grid;place-items:center;border:1px solid #b8c6d6;border-radius:7px;color:#fff}.selected .check{border-color:#2368c4;background:#2368c4}.result-main{min-width:0;display:grid;gap:4px}.result-main strong{overflow-wrap:anywhere;color:#203552;font:700 14px ui-monospace,monospace}.result-main>span{color:#2c405c;font-size:13px}.result-main small{color:#7a8798;font-size:12px}.badge{padding:7px;border-radius:9px;background:#eaf3fd;color:#2368c4;font-size:12px;white-space:nowrap}
.task-card,.observation-card{width:100%;min-width:0;display:grid;gap:9px;padding:15px;border:1px solid rgba(23,54,93,.08);border-radius:16px;background:#fff;color:#21324a;text-align:left;box-shadow:0 8px 24px rgba(23,54,93,.05)}.task-card>strong{font-size:15px;overflow-wrap:anywhere}.task-card>span{color:#7b899b;font-size:13px}.task-meta{display:flex;justify-content:space-between;gap:12px}.link-label{color:#2368c4}
.document-summary{min-width:0;display:flex;align-items:center;justify-content:space-between;gap:12px;font-size:14px}.document-summary strong{overflow-wrap:anywhere}.document-summary>span{color:#7b899b;flex-shrink:0;font-size:13px}.collection-status{display:flex;justify-content:space-between;gap:12px;margin:10px 0 14px;color:#7b899b;font-size:12px}.code{overflow-wrap:anywhere;font:14px ui-monospace,monospace;line-height:1.6}.material-name{font-size:14px}.row-summary{display:grid;grid-template-columns:minmax(0,1fr) auto 12px;align-items:center;gap:10px}.row-status{font-size:12px;color:#7b899b;overflow-wrap:anywhere}.row-summary strong{font-size:16px;color:#2368c4}.row-summary small{font-size:12px;font-weight:400;color:#7b899b}.chevron{color:#8b98a8;font-size:23px}
.source-line{display:grid;gap:8px;font-size:14px}.source-line>strong,.source-line>span{overflow-wrap:anywhere}.source-line dl{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin:5px 0 0;padding:12px;border-radius:10px;background:#f7f9fc}.source-line dt{color:#8491a2;font-size:12px}.source-line dd{margin:6px 0 0;overflow-wrap:anywhere}
.empty-state{min-height:145px;display:grid;place-items:center;border:1px dashed #cbd7e4;border-radius:16px;color:#8b98a8;font-size:14px}.more-button{min-height:44px;border:0;border-radius:10px;background:#e9f2fd;color:#2368c4;font-size:14px}.message{padding:11px 13px;border-radius:10px;background:#eaf5ee;color:#197052;font-size:13px;overflow-wrap:anywhere}.message.error{background:#fff0ee;color:#a32626}
.action-shell{position:fixed;z-index:18;right:0;bottom:0;left:0;display:grid;grid-template-columns:1fr minmax(130px,45%);align-items:center;gap:12px;padding:10px 16px max(10px,env(safe-area-inset-bottom));border-top:1px solid #e1e8f0;background:rgba(255,255,255,.97)}.action-shell button{height:48px}.action-shell.scan-actions{grid-template-columns:minmax(0,1fr) auto auto;gap:8px}.summary{min-width:0;display:grid;gap:4px}.summary strong{font-size:16px;color:#17365d}.summary span{font-size:12px;color:#7b899b}
.editor-page{position:fixed;z-index:55;top:0;right:0;left:0;height:100vh;height:100dvh;display:flex;flex-direction:column;background:#f3f6fa}.editor-header{flex-shrink:0;height:var(--app-header-height);border-bottom:1px solid #e4ebf3;background:#fff}.editor-header .topbar{width:min(100%,760px);margin-inline:auto}.editor-body{flex:1;min-height:0;overflow-y:auto;overscroll-behavior:contain;scroll-padding:16px;padding:16px max(12px,calc((100% - 728px)/2)) 30px}.editor-fields{display:grid;gap:18px}.editor-body>.message{margin-bottom:14px}.editor-actions{flex-shrink:0;width:min(100%,760px);margin-inline:auto;display:grid;grid-template-columns:1fr 1fr;gap:12px;padding:10px 16px max(10px,env(safe-area-inset-bottom));border-top:1px solid #e1e8f0;background:#fff}.editor-actions button{height:48px}
@media(max-width:560px){.topbar{padding-right:14px;padding-left:14px}.page-content{padding:14px 12px}.card{padding:15px}.action-shell{padding-right:12px;padding-left:12px}.scan-actions button{padding-inline:10px;font-size:14px}}
</style>
