import {
  assertCountSource, countCodeKey, countQuantity, countScopeKey,
  type CountCheck, type CountDraft, type CountObservation, type CountScope, type CountSource, type CountTask,
} from './inventory-count-model'

export class CountStoreError extends Error {
  constructor(public readonly code: string, message: string) { super(message) }
}
const plain = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T
const result = <T>(request: IDBRequest<T>): Promise<T> => new Promise((resolve, reject) => {
  request.onsuccess = () => resolve(request.result)
  request.onerror = () => reject(request.error ?? new Error('本地盘点读取失败'))
})
const done = (transaction: IDBTransaction): Promise<void> => new Promise((resolve, reject) => {
  transaction.oncomplete = () => resolve()
  transaction.onabort = transaction.onerror = () => reject(transaction.error ?? new Error('本地盘点保存失败'))
})

export class InventoryCountStore {
  private database: Promise<IDBDatabase> | null = null
  constructor(private readonly factory: IDBFactory = globalThis.indexedDB,
    private readonly name = 'label-system-inventory-counts-v1') {}

  private open(): Promise<IDBDatabase> {
    if (this.database) return this.database
    this.database = new Promise((resolve, reject) => {
      if (!this.factory) { reject(new Error('设备不支持本地盘点保存')); return }
      const request = this.factory.open(this.name, 1)
      request.onupgradeneeded = () => {
        const db = request.result
        const tasks = db.createObjectStore('tasks', { keyPath: 'id' })
        tasks.createIndex('binding', 'binding', { unique: true })
        tasks.createIndex('scopeKey', 'scopeKey')
        db.createObjectStore('observations', { keyPath: ['taskId', 'codeKey'] }).createIndex('taskId', 'taskId')
      }
      request.onsuccess = () => {
        request.result.onversionchange = () => { request.result.close(); this.database = null }
        resolve(request.result)
      }
      request.onerror = () => { this.database = null; reject(request.error ?? new Error('本地盘点库打开失败')) }
      request.onblocked = () => { this.database = null; reject(new Error('请关闭其他盘点页面后重试')) }
    })
    return this.database
  }

  async close(): Promise<void> { if (this.database) (await this.database).close(); this.database = null }

  private async mutate<T>(action: (tx: IDBTransaction) => Promise<T>): Promise<T> {
    const db = await this.open(), tx = db.transaction(['tasks', 'observations'], 'readwrite')
    const completion = done(tx)
    void completion.catch(() => undefined)
    try { const value = await action(tx); await completion; return value }
    catch (error) { try { tx.abort() } catch { /* already completed */ }; await completion.catch(() => undefined); throw error }
  }

  private async task(tx: IDBTransaction, scope: CountScope, id: string): Promise<CountTask> {
    const task = await result(tx.objectStore('tasks').get(id)) as CountTask | undefined
    if (!task || task.scopeKey !== countScopeKey(scope)) throw new Error('找不到当前账号的盘点记录')
    return task
  }

  async list(scope: CountScope): Promise<CountTask[]> {
    const db = await this.open()
    const tasks = await result(db.transaction('tasks').objectStore('tasks').index('scopeKey').getAll(countScopeKey(scope))) as CountTask[]
    return tasks.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
  }

  async create(scope: CountScope, source: CountSource, id: string): Promise<CountTask> {
    assertCountSource(source)
    const scopeKey = countScopeKey(scope)
    const binding = JSON.stringify([scopeKey, source.accountId, source.databaseName.toUpperCase(), source.document.id])
    return this.mutate(async tx => {
      const store = tx.objectStore('tasks')
      const existing = await result(store.index('binding').get(binding)) as CountTask | undefined
      if (existing) {
        if (existing.source.fingerprint !== source.fingerprint) {
          throw new CountStoreError('SOURCE_CHANGED', '原单已变化，本机采集记录已保留，请先核对')
        }
        return existing
      }
      const task: CountTask = { id, scopeKey, binding, source: plain(source), revision: 0,
        observationCount: 0, updatedAt: new Date().toISOString(), validatedAt: null, validationToken: null }
      await result(store.add(task))
      return task
    })
  }

  async read(scope: CountScope, id: string): Promise<CountDraft> {
    const db = await this.open(), tx = db.transaction(['tasks', 'observations'])
    const completion = done(tx)
    const task = await this.task(tx, scope, id)
    const observations = await result(tx.objectStore('observations').index('taskId').getAll(id)) as CountObservation[]
    await completion
    return { task, observations: observations.sort((a, b) => a.sequence - b.sequence) }
  }

  private touch(tx: IDBTransaction, task: CountTask): void {
    task.revision++
    task.updatedAt = new Date().toISOString()
    task.validatedAt = null
    task.validationToken = null
    tx.objectStore('tasks').put(task)
  }

  async scan(scope: CountScope, taskId: string, raw: string): Promise<void> {
    const codeKey = countCodeKey(raw)
    await this.mutate(async tx => {
      const task = await this.task(tx, scope, taskId), rows = tx.objectStore('observations')
      if (await result(rows.get([taskId, codeKey]))) throw new CountStoreError('DUPLICATE_SCAN', '标签已扫描')
      if (task.observationCount >= 1000) throw new Error('本次最多采集1000张标签')
      this.touch(tx, task)
      const observation: CountObservation = { taskId, codeKey, code: raw.trim(), quantity: null,
        revision: 1, sequence: task.revision, check: null }
      await result(rows.add(observation))
      task.observationCount++
      await result(tx.objectStore('tasks').put(task))
    })
  }

  async quantity(scope: CountScope, taskId: string, code: string, quantity: string, expectedRevision: number): Promise<void> {
    const normalized = countQuantity(quantity), codeKey = countCodeKey(code)
    await this.mutate(async tx => {
      const task = await this.task(tx, scope, taskId), rows = tx.objectStore('observations')
      const row = await result(rows.get([taskId, codeKey])) as CountObservation | undefined
      if (!row) throw new Error('标签记录不存在')
      if (row.revision !== expectedRevision) throw new CountStoreError('REVISION_CHANGED', '数量已变化，请刷新后再修改')
      row.quantity = normalized; row.revision++; row.check = null
      await result(rows.put(row)); this.touch(tx, task)
    })
  }

  async remove(scope: CountScope, taskId: string, code: string, expectedRevision: number): Promise<void> {
    await this.mutate(async tx => {
      const task = await this.task(tx, scope, taskId), rows = tx.objectStore('observations')
      const key = [taskId, countCodeKey(code)], row = await result(rows.get(key)) as CountObservation | undefined
      if (!row || row.revision !== expectedRevision) throw new Error('记录已变化，请刷新')
      await result(rows.delete(key)); task.observationCount--; this.touch(tx, task)
    })
  }

  async beginValidation(scope: CountScope, taskId: string, token: string): Promise<CountDraft> {
    return this.mutate(async tx => {
      const task = await this.task(tx, scope, taskId)
      task.validationToken = token; task.validatedAt = null
      await result(tx.objectStore('tasks').put(task))
      const observations = await result(tx.objectStore('observations').index('taskId').getAll(taskId)) as CountObservation[]
      return { task, observations }
    })
  }

  async applyValidation(scope: CountScope, draft: CountDraft, token: string, items: CountCheck[]): Promise<void> {
    await this.mutate(async tx => {
      const task = await this.task(tx, scope, draft.task.id)
      if (task.revision !== draft.task.revision || task.validationToken !== token) {
        throw new CountStoreError('REVISION_CHANGED', '采集记录有变化，请重新核验')
      }
      const checks = new Map(items.map(item => [countCodeKey(item.code), item]))
      if (checks.size !== items.length || checks.size !== draft.observations.length) throw new Error('核验结果不完整')
      for (const observation of draft.observations) {
        const check = checks.get(observation.codeKey)
        if (!check) throw new Error('核验结果不完整')
        await result(tx.objectStore('observations').put({ ...plain(observation), check: plain(check) }))
      }
      task.validationToken = null; task.validatedAt = new Date().toISOString()
      await result(tx.objectStore('tasks').put(task))
    })
  }
}

export const inventoryCountStore = new InventoryCountStore()
